namespace Content.StationArchitect;

/// <summary>
/// Potential owned passage proof for planning. It uses exclusive floor components
/// and possible physical door runs; it never creates floor or a built interface.
/// </summary>
public static class DesignOwnedTransitPlanning
{
    public static int CountOwnedTransitions(StationDesignProject project,
        IEnumerable<DesignCirculationReservation> serviceGuides,
        IEnumerable<DesignCirculationReservation> publicPaths)
    {
        var definitions = RoomHierarchy.Templates(project.Definition).ToDictionary(item => item.Path, item => item.Room, StringComparer.Ordinal);
        var rooms = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && definitions.ContainsKey(room.DefinitionPath)).ToList();
        var roomById = rooms.ToDictionary(room => room.Id, StringComparer.Ordinal);
        HashSet<Cell> Floor(DesignRoom room) => (!room.FloorLocked && !room.FloorEdited && room.GrowthReservation.Count > 0 ? room.GrowthReservation : room.Cells).ToHashSet();
        var floors = rooms.ToDictionary(room => room.Id, Floor, StringComparer.Ordinal);
        var occupied = floors.Values.SelectMany(cells => cells).ToHashSet();
        var forbidden = project.Layout.ReservedSpace.Concat(DesignDockGeometry.ReservedCells(project))
            .Concat(project.Layout.Equipment.Where(entity => DesignSystems.BlocksCrew(project, entity)).SelectMany(entity => entity.Footprint))
            .Concat(DesignTransport.EndpointFloorCells(project)).ToHashSet();
        var transit = rooms.Where(room => definitions[room.DefinitionPath].AllowThroughTraffic).ToList();
        var cellRegions = new Dictionary<string, Dictionary<Cell, int>>(StringComparer.Ordinal);
        var graph = new Dictionary<int, HashSet<int>>();
        foreach (var room in transit)
        {
            var index = new Dictionary<Cell, int>();
            var floor = floors[room.Id];
            foreach (var seed in floor.OrderBy(cell => cell.Y).ThenBy(cell => cell.X))
            {
                if (forbidden.Contains(seed) || index.ContainsKey(seed)) continue;
                var region = graph.Count; graph[region] = [];
                var pending = new Queue<Cell>(); pending.Enqueue(seed); index[seed] = region;
                while (pending.TryDequeue(out var cell))
                    foreach (var direction in Cell.Directions)
                    {
                        var next = cell + direction;
                        if (floor.Contains(next) && !forbidden.Contains(next) && index.TryAdd(next, region)) pending.Enqueue(next);
                    }
            }
            cellRegions[room.Id] = index;
        }
        var required = RequirementCompiler.Compile(project.Definition).Connections.Where(connection => connection.Priority == "required" && connection.Kind == "walking").ToList();
        bool Allowed(RoomDefinition room, string owner) => room.Access.EntryOwners.Count == 0 || room.Access.EntryOwners.Contains(owner, StringComparer.Ordinal);
        for (var a = 0; a < transit.Count; a++) for (var b = a + 1; b < transit.Count; b++)
        {
            var first = transit[a]; var second = transit[b];
            if (first.OwnerId != second.OwnerId || !Allowed(definitions[first.DefinitionPath], second.OwnerId) || !Allowed(definitions[second.DefinitionPath], first.OwnerId) ||
                !DesignNestedGeometry.EntryAllowed(project, first, second, definitions) || !DesignNestedGeometry.EntryAllowed(project, second, first, definitions)) continue;
            var width = Math.Max(1, required.Where(connection => connection.From == first.DefinitionPath || connection.From == second.DefinitionPath ||
                connection.To.Contains(first.DefinitionPath) || connection.To.Contains(second.DefinitionPath)).Select(connection => connection.MinWidth).DefaultIfEmpty(1).Max());
            foreach (var cell in cellRegions[first.Id].Keys) foreach (var normal in Cell.Directions)
            {
                var wall = cell + normal; var tangent = new Cell(-normal.Y, normal.X);
                var run = Enumerable.Range(0, width).Select(index => wall + new Cell(tangent.X * index, tangent.Y * index)).ToList();
                if (run.Any(part => occupied.Contains(part) || forbidden.Contains(part) || !cellRegions[first.Id].ContainsKey(part - normal) || !cellRegions[second.Id].ContainsKey(part + normal))) continue;
                var from = cellRegions[first.Id][cell]; var to = cellRegions[second.Id][wall + normal];
                // Each standing strip must itself belong to the same floor component.
                if (run.Any(part => cellRegions[first.Id][part - normal] != from || cellRegions[second.Id][part + normal] != to)) continue;
                graph[from].Add(to); graph[to].Add(from);
            }
        }
        HashSet<int> StandingRegions(DesignCirculationReservation guide, bool publicEntry)
        {
            if (!roomById.TryGetValue(guide.RoomId, out var room) || !cellRegions.TryGetValue(room.Id, out var index) ||
                !DesignNestedGeometry.EntryAllowed(project, room, null, definitions) || guide.Width < 1 || guide.BoundaryCells.Count != guide.Width ||
                guide.BoundaryCells.Distinct().Count() != guide.Width || !Cell.Directions.Contains(guide.Outward) ||
                (publicEntry ? !definitions[room.DefinitionPath].Access.AllowPublicEntry : !definitions[room.DefinitionPath].Access.AllowMaintenanceEntry)) return [];
            var standing = guide.BoundaryCells.Select(cell => cell - guide.Outward).ToList();
            var tangent = new Cell(-guide.Outward.Y, guide.Outward.X);
            if (!guide.BoundaryCells.Any(start => Enumerable.Range(0, guide.Width).All(offset => guide.BoundaryCells.Contains(start + new Cell(tangent.X * offset, tangent.Y * offset)))) ||
                guide.BoundaryCells.Any(cell => occupied.Contains(cell) || forbidden.Contains(cell) || !guide.ApproachCells.Contains(cell + guide.Outward)) || standing.Any(cell => !index.ContainsKey(cell))) return [];
            return standing.Select(cell => index[cell]).ToHashSet();
        }
        var targets = publicPaths.Where(path => path.Network == "publicHall").SelectMany(path => StandingRegions(path, true)).ToHashSet();
        var reached = targets.ToHashSet(); var queue = new Queue<int>(targets);
        while (queue.TryDequeue(out var region)) foreach (var next in graph[region]) if (reached.Add(next)) queue.Enqueue(next);
        return serviceGuides.Where(guide => guide.Network == "maintenance").DistinctBy(guide => guide.Id)
            .Count(guide => StandingRegions(guide, false).Any(reached.Contains));
    }
}
