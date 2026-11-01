namespace Content.StationArchitect;

/// <summary>Actual connected room clusters and narrow service passages along their exposed boundaries.</summary>
public static class DesignClusterGeometry
{
    public sealed record MaintenanceObservation(string ClusterId, int BoundaryEdges, int CoveredEdges, int Connections)
    { public double Coverage => BoundaryEdges == 0 ? 1 : (double) CoveredEdges / BoundaryEdges; }

    public static List<List<DesignRoom>> Clusters(StationDesignProject project)
    {
        var definitions = LayoutGeometry.Definitions(project);
        var rooms = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && room.Cells.Count > 0 && definitions.ContainsKey(room.DefinitionPath)).DistinctBy(room => room.Id, StringComparer.Ordinal).OrderBy(room => room.Id, StringComparer.Ordinal).ToList();
        var remaining = rooms.ToDictionary(room => room.Id, StringComparer.Ordinal);
        var graph = rooms.ToDictionary(room => room.Id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var owners = rooms.ToDictionary(room => room.Id, room => room.OwnerId, StringComparer.Ordinal);
        var floor = new Dictionary<Cell, string>();
        foreach (var room in rooms) foreach (var cell in room.Cells) floor.TryAdd(cell, room.Id);
        void Link(string first, string second)
        {
            if (first == second || !owners.ContainsKey(first) || !owners.ContainsKey(second) || owners[first] != owners[second]) return;
            graph[first].Add(second); graph[second].Add(first);
        }
        foreach (var port in DesignMeasurements.ValidInterfaces(project).Where(port => port.Kind == "walking")) Link(port.From, port.To);
        foreach (var (cell, id) in floor) foreach (var direction in Cell.Directions)
            if (floor.TryGetValue(cell + direction, out var other)) Link(id, other);
        var result = new List<List<DesignRoom>>();
        while (remaining.Count > 0)
        {
            var first = remaining.First(); remaining.Remove(first.Key);
            var group = new List<DesignRoom> { first.Value }; var queue = new Queue<string>(); queue.Enqueue(first.Key);
            while (queue.TryDequeue(out var id)) foreach (var next in graph[id])
                if (remaining.Remove(next, out var room)) { group.Add(room); queue.Enqueue(next); }
            result.Add(group.OrderBy(room => room.Id, StringComparer.Ordinal).ToList());
        }
        return result;
    }

    private static List<(Cell Floor, Cell Normal)> ExposedEdges(List<DesignRoom> cluster, HashSet<Cell> allFloor) => cluster.SelectMany(room => room.Cells).Distinct()
        .SelectMany(cell => Cell.Directions.Select(normal => (Floor: cell, Normal: normal)))
        .Where(edge => !allFloor.Contains(edge.Floor + edge.Normal) && !allFloor.Contains(edge.Floor + edge.Normal + edge.Normal)).ToList();

    private static bool Covered((Cell Floor, Cell Normal) edge, HashSet<Cell> maintenance, int distance, int width) =>
        Enumerable.Range(distance, width).Any(offset => maintenance.Contains(edge.Floor + new Cell(edge.Normal.X * offset, edge.Normal.Y * offset)));

    public static List<MaintenanceObservation> MeasureMaintenance(StationDesignProject project)
    {
        var definitions = LayoutGeometry.Definitions(project);
        var allFloor = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned).SelectMany(room => room.Cells).ToHashSet();
        var maintenance = project.Layout.Maintenance.Concat(DesignMeasurements.ValidInterfaces(project).Where(port => port.Kind == "walking" && (port.From == "$maintenance" || port.To == "$maintenance" || port.From == "$exterior-maintenance" || port.To == "$exterior-maintenance")).SelectMany(port => port.Cells)).ToHashSet();
        var structure = project.Layout.Walls.Concat(project.Layout.Hull).Concat(project.Layout.Windows).Concat(DesignMeasurements.ValidInterfaces(project).Where(port => port.Kind != "utility").SelectMany(port => port.Cells)).ToHashSet();
        return Clusters(project).Where(cluster => cluster[0].OwnerId != RequirementCompiler.MaintenanceOwner && cluster.Any(room => definitions.TryGetValue(room.DefinitionPath, out var definition) && definition.Environment.Placement != "exterior"))
            .Select(cluster =>
            {
                var ids = cluster.Select(room => room.Id).ToHashSet(StringComparer.Ordinal);
                var edges = ExposedEdges(cluster, allFloor);
                var ports = DesignMeasurements.ValidInterfaces(project).Count(port => port.Kind == "walking" && (ids.Contains(port.From) && port.To == "$maintenance" || ids.Contains(port.To) && port.From == "$maintenance"));
                return new MaintenanceObservation(cluster[0].Id, edges.Count, edges.Count(edge => structure.Contains(edge.Floor + edge.Normal) && Covered(edge, maintenance, project.Settings.Packing.MaintenanceWrapDistance, project.Settings.MaintenanceWidth)), ports);
            }).ToList();
    }

    internal static void WrapMaintenance(StationDesignProject project, HashSet<Cell> target, Func<Cell, bool> passable, GridBounds bounds, List<ProgramDiagnostic> diagnostics)
    {
        if (target.Count == 0 || project.Settings.Packing.MaintenanceCoverage <= 0) return;
        var definitions = LayoutGeometry.Definitions(project);
        var allFloor = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned).SelectMany(room => room.Cells).ToHashSet();
        var width = project.Settings.MaintenanceWidth; var distance = project.Settings.Packing.MaintenanceWrapDistance;
        var centerCache = new Dictionary<Cell, bool>();
        bool Allowed(Cell cell)
        {
            if (!centerCache.TryGetValue(cell, out var result)) centerCache[cell] = result = passable(cell);
            return result;
        }
        // A closed public loop can split legal service space. Do not repeatedly search
        // impossible candidates on the other side of that physical barrier.
        var reachable = new HashSet<Cell>(); var pending = new Queue<Cell>();
        foreach (var center in target.SelectMany(cell => DesignRouting.Stamp(cell, width + 1)).Distinct().Where(Allowed))
            if (DesignRouting.Stamp(center, width).Any(target.Contains) && reachable.Add(center)) pending.Enqueue(center);
        while (pending.TryDequeue(out var current)) foreach (var direction in Cell.Directions)
        {
            var next = current + direction;
            if (bounds.Contains(next) && !reachable.Contains(next) && Allowed(next)) { reachable.Add(next); pending.Enqueue(next); }
        }
        foreach (var cluster in Clusters(project).Where(cluster => cluster[0].OwnerId != RequirementCompiler.MaintenanceOwner && cluster.All(room => definitions[room.DefinitionPath].Environment.Placement != "exterior")))
        {
            var edges = ExposedEdges(cluster, allFloor);
            var candidates = edges.Select(edge =>
            {
                // The first strip cell remains separated from the room by an actual wall tile.
                var offset = distance + (edge.Normal.X > 0 || edge.Normal.Y > 0 ? width / 2 : width - width / 2 - 1);
                return edge.Floor + new Cell(edge.Normal.X * offset, edge.Normal.Y * offset);
            }).Distinct().Where(reachable.Contains).OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToList();
            var influenced = candidates.ToDictionary(cell => cell, cell =>
            {
                var stamp = DesignRouting.Stamp(cell, width).ToHashSet();
                return Enumerable.Range(0, edges.Count).Where(index => Covered(edges[index], stamp, distance, width)).ToArray();
            });
            var wanted = (int) Math.Ceiling(edges.Count * project.Settings.Packing.MaintenanceCoverage);
            var attempted = new HashSet<Cell>();
            var coveredEdges = edges.Select(edge => Covered(edge, target, distance, width)).ToArray();
            for (var iteration = 0; iteration < Math.Min(96, candidates.Count) && coveredEdges.Count(covered => covered) < wanted; iteration++)
            {
                var targetBounds = DesignMeasurements.Bounds(target);
                int Nearness(Cell cell) => Math.Max(0, Math.Max(targetBounds.X - cell.X, cell.X - targetBounds.Right + 1)) + Math.Max(0, Math.Max(targetBounds.Y - cell.Y, cell.Y - targetBounds.Bottom + 1));
                var next = candidates.Where(cell => !attempted.Contains(cell) && !DesignRouting.Stamp(cell, width).All(target.Contains))
                    .OrderByDescending(cell => influenced[cell].Count(index => !coveredEdges[index]))
                    .ThenBy(Nearness).Cast<Cell?>().FirstOrDefault();
                if (next == null) break;
                attempted.Add(next.Value);
                var path = DesignRouting.Search(next.Value, cell => DesignRouting.Stamp(cell, width).Any(target.Contains), Allowed, bounds, target, 60000);
                if (path == null || path.Count > Math.Max(48, edges.Count)) continue;
                var added = path.SelectMany(cell => DesignRouting.Stamp(cell, width)).ToHashSet();
                if (DesignRouting.HasOpenSquare(target.Concat(added).ToHashSet(), project.Settings.MaintenanceMaxWidth + 1)) continue;
                target.UnionWith(added);
                for (var index = 0; index < edges.Count; index++) coveredEdges[index] = Covered(edges[index], target, distance, width);
            }
            var covered = edges.Count(edge => Covered(edge, target, distance, width));
            if (covered < wanted) diagnostics.Add(new(cluster[0].Id, "maintenance-coverage", $"Actual nearby maintenance covers {covered}/{edges.Count} exposed boundary edges; requested target is {project.Settings.Packing.MaintenanceCoverage:P0}. Public passages, space pockets or the width limit prevent further wrapping.", "warning"));
        }
    }
}
