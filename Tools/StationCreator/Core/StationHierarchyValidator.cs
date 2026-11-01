namespace Content.StationGeneration;

/// <summary>Checks saved hierarchy geometry without regenerating or trusting access declarations.</summary>
public static class StationHierarchyValidator
{
    public static List<Diagnostic> Validate(StationProject project)
    {
        var errors = new List<Diagnostic>();
        var plan = project.SpatialPlan;
        if (plan == null) return errors;
        var expected = project.Config.Departments.SelectMany(d => d.Subdepartments.Select(s => (Department: d, Subdepartment: s,
            Id: StationHierarchy.Path(d.Id, s.Id)))).ToDictionary(s => s.Id, StringComparer.Ordinal);
        if (plan.Hierarchy == null || plan.Hierarchy.Subdepartments == null || plan.Hierarchy.RoomFeatures == null || plan.Hierarchy.InternalCirculation == null)
            return [Error("missing", "The hierarchy masks and access plans are missing.")];
        var hierarchy = plan.Hierarchy;
        // Projects authored before room programs remain valid without subdivision
        // overlays. They acquire explicit internal circulation when rebuilt.
        if (expected.Count == 0 && hierarchy.Subdepartments.Count == 0 && hierarchy.RoomFeatures.Count == 0 && hierarchy.InternalCirculation.Count == 0)
            return errors;
        if (hierarchy.Subdepartments.Any(s => s == null || s.Cells == null) || hierarchy.RoomFeatures.Any(r => r == null || r.Cells == null || r.Walls == null || r.Doors == null || r.AccessRoutes == null || r.AccessRoutes.Any(a => a == null || a.Cells == null)) || hierarchy.InternalCirculation.Any(c => c == null || c.Cells == null))
            return [Error("malformed", "Hierarchy regions, partitions, routes and floor lists must be present.")];
        if (hierarchy.Subdepartments.GroupBy(s => s.Id).Any(g => g.Count() != 1) || hierarchy.RoomFeatures.GroupBy(r => r.Id).Any(g => g.Count() != 1) || hierarchy.InternalCirculation.GroupBy(c => c.Department).Any(g => g.Count() != 1))
            return [Error("duplicate-id", "Subdivision, room instance and circulation IDs must be unique.")];
        var parents = plan.Regions.ToDictionary(r => r.Id, r => r.Cells.ToHashSet(), StringComparer.Ordinal);
        var definitions = project.Config.Departments.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var zones = hierarchy.Subdepartments.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var allRoomFloors = new HashSet<Cell>();
        var allPartitions = hierarchy.RoomFeatures.SelectMany(r => r.Walls).ToHashSet();
        var allDoors = hierarchy.RoomFeatures.SelectMany(r => r.Doors).ToHashSet();
        var circulation = new Dictionary<string, HashSet<Cell>>(StringComparer.Ordinal);
        foreach (var parent in parents)
        {
            var declared = hierarchy.InternalCirculation.SingleOrDefault(c => c.Department == parent.Key);
            if (declared == null) { errors.Add(Error("circulation-missing", $"{parent.Key}: internal circulation was not planned.")); continue; }
            var roomMasks = hierarchy.RoomFeatures.Where(r => r.HostDepartment == parent.Key).ToList();
            var actual = parent.Value.Except(roomMasks.SelectMany(r => r.Cells.Concat(r.Walls))).ToHashSet();
            actual.UnionWith(roomMasks.SelectMany(r => r.Doors));
            var saved = declared.Cells.ToHashSet();
            if (declared.Cells.Count != saved.Count || !saved.SetEquals(actual))
                errors.Add(Error("circulation-mask", $"{parent.Key}: saved circulation must equal the actual parent floor remaining around room footprints and partitions."));
            circulation[parent.Key] = actual;
            if (!Connected(actual)) errors.Add(Error("circulation-connected", $"{parent.Key}: planned partitions disconnect the department's internal walking floor."));
            foreach (var entry in plan.Ports.Where(p => p.Department == parent.Key || p.OtherDepartment == parent.Key))
                if (!entry.Cells.SelectMany(Adjacent).Any(actual.Contains))
                    errors.Add(Error("circulation-entrance", $"{parent.Key}: a planned room blocks an existing department entrance."));
        }
        foreach (var missing in expected.Keys.Except(zones.Keys)) errors.Add(Error("missing-subdepartment", $"{missing}: no subdivision footprint was planned."));
        var occupied = new HashSet<Cell>();
        foreach (var zone in hierarchy.Subdepartments)
        {
            if (!expected.TryGetValue(zone.Id, out var specification)) { errors.Add(Error("unknown-subdepartment", $"{zone.Id}: no matching definition.")); continue; }
            var (department, sub, _) = specification;
            var cells = zone.Cells.ToHashSet();
            if (zone.Department != department.Id || !parents.TryGetValue(zone.HostDepartment, out var parent))
            { errors.Add(Error("subdepartment-host", $"{zone.Id}: its parent or physical host is invalid.")); continue; }
            if (cells.Count != zone.Cells.Count || !parent.IsSupersetOf(cells) || !cells.Contains(zone.Seed))
                errors.Add(Error("subdepartment-floor", $"{zone.Id}: subdivision ownership must use unique tiles in its physical host floor and contain its seed."));
            if (!Connected(cells)) errors.Add(Error("subdepartment-disconnected", $"{zone.Id}: the subdivision footprint must be connected."));
            if (occupied.Overlaps(cells)) errors.Add(Error("subdepartment-overlap", $"{zone.Id}: subdivision footprints overlap."));
            occupied.UnionWith(cells);
            var usable = cells.Count(p => !allPartitions.Contains(p));
            if (usable < sub.MinArea || usable > sub.MaxArea)
                errors.Add(Error("subdepartment-area", $"{zone.Id}: planned usable area {usable} is outside {sub.MinArea}–{sub.MaxArea}."));
            var satellite = zone.HostDepartment != zone.Department;
            if (satellite != zone.IsSatellite || satellite && (!department.AllowSplit || !sub.AllowSplit || sub.RequireInternalAccess || department.Grouping == "grouped" || definitions[zone.HostDepartment].Access != department.Access))
                errors.Add(Error("satellite-policy", $"{zone.Id}: its physical satellite placement conflicts with grouping, split, internal-access or host-access policy."));
            if (satellite) errors.Add(new("hierarchy", "satellite", $"{zone.Id}: occupies an explicit satellite within {zone.HostDepartment}'s floor allocation.", "info"));
            if (!circulation.TryGetValue(zone.HostDepartment, out var internalFloor)) continue;
            var entries = plan.Ports.Where(p => p.Department == zone.Department || p.OtherDepartment == zone.Department)
                .SelectMany(p => p.Cells).SelectMany(Adjacent).Where(internalFloor.Contains).ToHashSet();
            if (sub.RequireInternalAccess && (satellite || Route(entries, cells.Intersect(internalFloor).ToHashSet(), internalFloor) == null))
                errors.Add(Error("subdepartment-internal-access", $"{zone.Id}: must be reachable wholly through its parent department floor, without a hall or maintenance detour."));
        }
        foreach (var specification in expected.Values)
        foreach (var definition in specification.Subdepartment.Rooms)
        {
            var path = StationHierarchy.Path(specification.Department.Id, specification.Subdepartment.Id, definition.Id);
            var instances = hierarchy.RoomFeatures.Where(r => r.Definition == path).ToList();
            if (instances.Count < definition.MinCount || instances.Count > definition.MaxCount)
                errors.Add(Error("room-count", $"{path}: planned {instances.Count} instances, expected {definition.MinCount}–{definition.MaxCount}."));
        }
        foreach (var room in hierarchy.RoomFeatures)
        {
            if (!zones.TryGetValue(room.Subdepartment, out var zone) || !expected.TryGetValue(room.Subdepartment, out var specification))
            { errors.Add(Error("room-owner", $"{room.Id}: no matching subdivision footprint.")); continue; }
            var definition = specification.Subdepartment.Rooms.SingleOrDefault(r => StationHierarchy.Path(zone.Department, specification.Subdepartment.Id, r.Id) == room.Definition);
            if (definition == null) { errors.Add(Error("unknown-room", $"{room.Id}: no matching room or feature definition.")); continue; }
            var floor = room.Cells.ToHashSet();
            var walls = room.Walls.ToHashSet();
            var doors = room.Doors.ToHashSet();
            var zoneCells = zone.Cells.ToHashSet();
            if (room.Department != zone.Department || room.HostDepartment != zone.HostDepartment || room.Kind != definition.Kind || !room.Id.StartsWith(room.Definition + "#", StringComparison.Ordinal))
                errors.Add(Error("room-owner", $"{room.Id}: its definition, kind or physical host does not match the subdivision."));
            if (room.Cells.Count != floor.Count || !zoneCells.IsSupersetOf(floor) || !Connected(floor))
                errors.Add(Error("room-floor", $"{room.Id}: clear footprint must be unique, connected floor within its subdivision."));
            if (floor.Count < definition.MinArea || floor.Count > definition.MaxArea)
                errors.Add(Error("room-area", $"{room.Id}: clear footprint {floor.Count} is outside {definition.MinArea}–{definition.MaxArea}."));
            if (allRoomFloors.Overlaps(floor) || allPartitions.Overlaps(floor) || allDoors.Overlaps(floor))
                errors.Add(Error("room-overlap", $"{room.Id}: room floor overlaps another room, partition or doorway."));
            allRoomFloors.UnionWith(floor);
            if (!zoneCells.IsSupersetOf(walls) || !zoneCells.IsSupersetOf(doors) || walls.Count != room.Walls.Count || doors.Count != room.Doors.Count || walls.Overlaps(doors))
                errors.Add(Error("room-partition", $"{room.Id}: partitions and doors must be unique subdivision territory without overlaps."));
            if (definition.Kind == "feature" && walls.Count != 0)
                errors.Add(Error("room-partition", $"{room.Id}: equipment features reserve floor without enclosing room partitions."));
            if (definition.Kind == "room")
            {
                var surrounding = floor.SelectMany(Adjacent).Except(floor).ToHashSet();
                var legal = walls.Concat(doors).Concat(plan.Walls).Concat(plan.Hull).ToHashSet();
                if (!legal.IsSupersetOf(surrounding) || walls.Any(p => !Adjacent(p).Any(floor.Contains) && !floor.Any(f => Math.Abs(f.X - p.X) <= 1 && Math.Abs(f.Y - p.Y) <= 1)))
                    errors.Add(Error("room-partition", $"{room.Id}: planned enclosure is missing a wall or doorway along its clear floor boundary."));
            }
            if (!circulation.TryGetValue(room.HostDepartment, out var walkable)) continue;
            if (doors.Count == 0 || doors.Any(d => !walkable.Contains(d) || !Adjacent(d).Any(floor.Contains)))
                errors.Add(Error("room-door", $"{room.Id}: every planned doorway must join its footprint to actual internal walking floor."));
            IEnumerable<string> declaredOrigins = definition.AccessibleFrom.Count == 0 ? new[] { room.Subdepartment } : definition.AccessibleFrom;
            var origins = declaredOrigins.Distinct(StringComparer.Ordinal).ToList();
            foreach (var origin in origins)
            {
                var routes = room.AccessRoutes.Where(a => a.From == origin).ToList();
                if (routes.Count != 1) { errors.Add(Error("room-access", $"{room.Id}: expected one access route from {origin}.")); continue; }
                var route = routes[0];
                var network = AccessNetwork(project, origin, room.HostDepartment, circulation);
                var sources = ScopeCells(project, origin, circulation).Where(network.Contains).ToHashSet();
                if (route.Cells.Count == 0 || !sources.Contains(route.Cells[0]) || route.Cells[^1] != route.Door || !doors.Contains(route.Door) ||
                    route.Cells.Any(p => !network.Contains(p)) || route.Cells.Zip(route.Cells.Skip(1)).Any(pair => pair.First.Distance(pair.Second) != 1) || Route(sources, doors, network) == null)
                    errors.Add(Error("room-access", $"{room.Id}: {origin} does not have a valid contiguous route over the actual planned walking floor to a doorway."));
            }
            if (room.AccessRoutes.Any(a => !origins.Contains(a.From))) errors.Add(Error("room-access", $"{room.Id}: an access route has no matching required origin."));
        }
        ValidateRelations(project, circulation, errors);
        return errors;
    }

    private static void ValidateRelations(StationProject project, Dictionary<string, HashSet<Cell>> circulation, List<Diagnostic> errors)
    {
        foreach (var edge in project.Config.HierarchyRelations)
        {
            var fromFootprint = ScopeFootprint(project, edge.From);
            var toFootprint = ScopeFootprint(project, edge.To);
            if (fromFootprint.Count == 0 || toFootprint.Count == 0) continue; // Missing programs have their own diagnostics.
            var adjacent = fromFootprint.Any(p => Adjacent(p).Any(toFootprint.Contains));
            if (edge.Relation == "adjacency")
            {
                // A shared planned partition is a legitimate room boundary.
                var partitions = project.SpatialPlan!.Hierarchy.RoomFeatures.SelectMany(r => r.Walls).Concat(project.SpatialPlan.Walls).ToHashSet();
                adjacent |= fromFootprint.Any(p => Adjacent(p).Any(q => partitions.Contains(q) && Adjacent(q).Any(toFootprint.Contains)));
                if (edge.Priority == "required" && !adjacent) errors.Add(Error("required-adjacency", $"{edge.From} and {edge.To} do not share a planned boundary."));
                if (edge.Priority == "avoided" && edge.StrictAvoidance && adjacent) errors.Add(Error("avoided-adjacency", $"{edge.From} and {edge.To} share a prohibited boundary."));
                continue;
            }
            var modes = edge.AllowedModes.Where(m => m is "publicHall" or "staffPassage" or "maintenance").ToHashSet();
            if (modes.Count == 0 || edge.Relation == "transport")
            {
                if (edge.Priority == "required") errors.Add(Error("unsupported-fulfillment", $"{edge.From} → {edge.To}: its required transport or network mode has no physical implementation."));
                continue;
            }
            var fromHost = ScopeHost(project, edge.From);
            var toHost = ScopeHost(project, edge.To);
            var network = new HashSet<Cell>();
            if (fromHost == toHost && circulation.TryGetValue(fromHost, out var own)) network.UnionWith(own);
            else
            {
                network.UnionWith(circulation.Values.SelectMany(c => c));
                if (modes.Contains("publicHall"))
                {
                    network.UnionWith(project.SpatialPlan!.PublicCorridors);
                    network.UnionWith(project.SpatialPlan.Ports.Where(p => p.Kind == "public").SelectMany(p => p.Cells));
                }
                if (modes.Contains("staffPassage")) network.UnionWith(project.SpatialPlan!.Ports.Where(p => p.Kind == "adjacency").SelectMany(p => p.Cells));
                if (modes.Contains("maintenance"))
                {
                    network.UnionWith(project.SpatialPlan!.Maintenance);
                    network.UnionWith(project.SpatialPlan.Ports.Where(p => p.Kind == "maintenance").SelectMany(p => p.Cells));
                }
            }
            var path = Route(ScopeCells(project, edge.From, circulation).Where(network.Contains).ToHashSet(), ScopeCells(project, edge.To, circulation).Where(network.Contains).ToHashSet(), network);
            if (edge.Priority == "required" && (path == null || edge.MaxDistance is int max && path.Count - 1 > max))
                errors.Add(Error("required-route", $"{edge.From} → {edge.To}: no permitted physical route within its required distance."));
            else if (edge.Priority == "desired" && (path == null || edge.MaxDistance is int desired && path.Count - 1 > desired))
                errors.Add(new("hierarchy", "desired-route", $"{edge.From} → {edge.To}: desired route or distance was not achieved (weight {edge.Weight}).", "warning"));
            if (edge.Priority == "avoided" && edge.StrictAvoidance && path != null && (edge.MaxDistance == null || path.Count - 1 <= edge.MaxDistance))
                errors.Add(Error("avoided-route", $"{edge.From} → {edge.To}: its prohibited route exists."));
        }
    }

    private static HashSet<Cell> AccessNetwork(StationProject project, string origin, string targetHost, Dictionary<string, HashSet<Cell>> circulation)
    {
        var network = circulation.TryGetValue(targetHost, out var own) ? own.ToHashSet() : [];
        if (ScopeHost(project, origin) == targetHost) return network;
        network.UnionWith(circulation.Values.SelectMany(c => c));
        network.UnionWith(project.SpatialPlan!.PublicCorridors);
        network.UnionWith(project.SpatialPlan.Ports.Where(p => p.Kind is "public" or "adjacency").SelectMany(p => p.Cells));
        return network;
    }

    private static string ScopeHost(StationProject project, string path) => path.Contains('/')
        ? project.SpatialPlan!.Hierarchy.Subdepartments.FirstOrDefault(s => s.Id == string.Join('/', path.Split('/').Take(2)))?.HostDepartment ?? path.Split('/')[0]
        : path;

    private static HashSet<Cell> ScopeFootprint(StationProject project, string path)
    {
        var hierarchy = project.SpatialPlan!.Hierarchy;
        return path.Split('/').Length switch
        {
            1 => project.SpatialPlan.Regions.FirstOrDefault(r => r.Id == path)?.Cells.ToHashSet() ?? [],
            2 => hierarchy.Subdepartments.FirstOrDefault(s => s.Id == path)?.Cells.ToHashSet() ?? [],
            _ => hierarchy.RoomFeatures.Where(r => r.Definition == path).SelectMany(r => r.Cells).ToHashSet()
        };
    }

    private static IEnumerable<Cell> ScopeCells(StationProject project, string path, Dictionary<string, HashSet<Cell>> circulation)
    {
        var parts = path.Split('/');
        if (parts.Length == 1)
        {
            if (!circulation.TryGetValue(path, out var floor)) return [];
            var starts = project.SpatialPlan!.Ports.Where(p => p.Department == path || p.OtherDepartment == path)
                .SelectMany(p => p.Cells).SelectMany(Adjacent).Where(floor.Contains).Distinct().ToList();
            return starts.Count > 0 ? starts : floor;
        }
        if (parts.Length == 2) return project.SpatialPlan!.Hierarchy.Subdepartments.FirstOrDefault(s => s.Id == path)?.Cells ?? [];
        return project.SpatialPlan!.Hierarchy.RoomFeatures.Where(r => r.Definition == path).SelectMany(r => r.Doors);
    }

    private static List<Cell>? Route(HashSet<Cell> starts, HashSet<Cell> goals, HashSet<Cell> network)
    {
        var previous = new Dictionary<Cell, Cell>();
        var queue = new Queue<Cell>();
        foreach (var start in starts.Where(network.Contains).OrderBy(p => p.Y).ThenBy(p => p.X)) { previous[start] = start; queue.Enqueue(start); }
        while (queue.TryDequeue(out var cell))
        {
            if (goals.Contains(cell))
            {
                var path = new List<Cell> { cell };
                while (previous[cell] != cell) { cell = previous[cell]; path.Add(cell); }
                path.Reverse(); return path;
            }
            foreach (var next in Adjacent(cell)) if (network.Contains(next) && previous.TryAdd(next, cell)) queue.Enqueue(next);
        }
        return null;
    }

    private static bool Connected(HashSet<Cell> cells) => cells.Count > 0 && Flood(cells.First(), cells).Count == cells.Count;
    private static HashSet<Cell> Flood(Cell start, HashSet<Cell> cells)
    {
        var seen = new HashSet<Cell> { start }; var queue = new Queue<Cell>(); queue.Enqueue(start);
        while (queue.TryDequeue(out var cell)) foreach (var next in Adjacent(cell)) if (cells.Contains(next) && seen.Add(next)) queue.Enqueue(next);
        return seen;
    }
    private static IEnumerable<Cell> Adjacent(Cell cell) => Cell.Directions.Select(d => cell + d);
    private static Diagnostic Error(string code, string message) => new("hierarchy", code, message);
}
