namespace Content.StationArchitect;

public static partial class DesignSystems
{
    /// <summary>Fresh native graph, searched within each authored route mode rather than filtering one unconstrained shortest witness.</summary>
    public static Func<DesignRoute, NativeUtilityConnection> CreateUtilityRouteInspector(StationDesignProject project, bool final = false, Func<DesignRoute, string>? utilityType = null)
    {
        ValidateCore(project, final, out var graph);
        if (graph == null) return _ => new(false, 0);
        var resolveEntity = EntityResolver(project);
        var roomOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var binding in project.Layout.SystemDevices)
            if (resolveEntity(binding) is { } entity && graph.Instances.ContainsKey(entity.Id)) roomOf[entity.Id] = entity.RoomId;
        var actualRooms = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned).ToList();
        var interfaces = DesignMeasurements.ValidInterfaces(project);
        var edges = graph.Edges.ToDictionary(pair => pair.Key, pair => pair.Value.ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var pair in graph.Edges) foreach (var next in pair.Value) edges[next].Add(pair.Key);
        var cache = new Dictionary<(string From, string To, string Kind, string Mode), NativeUtilityConnection>();
        HashSet<string> Selected(string endpoint)
        {
            var ids = actualRooms.Where(room => room.Id == endpoint || room.OwnerId == endpoint).Select(room => room.Id).ToHashSet(StringComparer.Ordinal);
            ids.Add(endpoint); ids.UnionWith(roomOf.Values.Where(id => id.StartsWith(endpoint + "@", StringComparison.Ordinal))); return ids;
        }
        return route =>
        {
            var requested = utilityType?.Invoke(route) ?? project.Layout.Utilities.FirstOrDefault(utility => utility.RequirementId == route.RequirementId && utility.From == route.From && utility.To == route.To)?.Type;
            var kind = requested == "cable" ? "power" : requested == "pipe" ? "gas" : "";
            var key = (route.From, route.To, kind, route.Mode);
            if (cache.TryGetValue(key, out var known)) return known;
            NativeUtilityConnection Missing() => cache[key] = new(false, 0);
            if (route.Kind != "utility" || kind.Length == 0 || route.Mode is not ("department" or "direct" or "publicHall" or "maintenance" or "utilityNetwork")) return Missing();
            var sourceIds = Selected(route.From); var targetIds = Selected(route.To);
            var sourceOwner = actualRooms.FirstOrDefault(room => room.Id == route.From)?.OwnerId ?? route.From;
            var targetOwner = actualRooms.FirstOrDefault(room => room.Id == route.To)?.OwnerId ?? route.To;
            if (route.Mode == "department" && sourceOwner != targetOwner) return Missing();
            var sourceFloor = actualRooms.Where(room => sourceIds.Contains(room.Id)).SelectMany(room => room.Cells).ToHashSet();
            var targetFloor = actualRooms.Where(room => targetIds.Contains(room.Id)).SelectMany(room => room.Cells).ToHashSet();
            var seam = sourceFloor.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Where(cell => !sourceFloor.Contains(cell) && !targetFloor.Contains(cell) &&
                Cell.Directions.Any(direction => sourceFloor.Contains(cell + direction) && targetFloor.Contains(cell - direction))).ToHashSet();
            var allowed = UtilitySupportCells(project, route, actualRooms, interfaces,
                graph.Ports.Values.Where(port => roomOf.ContainsKey(port.InstanceId)).Select(port => (Room: roomOf[port.InstanceId], port.Position)));
            bool Served(string id) => kind == "power" ? graph.Power.Contains(id) : graph.Gas.Contains(id) || graph.Sink.Contains(id);
            bool PortAllowed(string id) => graph.Ports[id].ProfilePort.Kind == kind && Served(id) && allowed.Contains(graph.Ports[id].Position) &&
                (route.Mode != "direct" || graph.Ports[id].ProfilePort.Connection is not ("rangeProvider" or "rangeReceiver"));
            bool Via(Cell cell) => route.Mode == "direct" ? seam.Contains(cell) : route.Mode == "maintenance" ? project.Layout.Maintenance.Contains(cell) : route.Mode == "publicHall" ? project.Layout.PublicCorridors.Contains(cell) : true;
            var targets = graph.Ports.Values.Where(port => PortAllowed(port.Id) && roomOf.TryGetValue(port.InstanceId, out var room) && targetIds.Contains(room)).Select(port => port.Id).ToHashSet(StringComparer.Ordinal);
            var distances = new Dictionary<(string Id, bool Via), int>(); var previous = new Dictionary<(string Id, bool Via), (string Id, bool Via)>();
            var pending = new PriorityQueue<(string Id, bool Via), int>();
            var roots = graph.Ports.Values.Where(port => PortAllowed(port.Id) && roomOf.TryGetValue(port.InstanceId, out var room) && sourceIds.Contains(room)).OrderBy(port => port.Id, StringComparer.Ordinal).ToList();
            foreach (var root in roots)
            { var start = (root.Id, Via(root.Position)); if (distances.TryAdd(start, 0)) pending.Enqueue(start, 0); }
            var remaining = project.Definition.Systems.Generation.MaxStatesPerConnection;
            while (remaining-- > 0 && pending.TryDequeue(out var current, out var cost))
            {
                if (distances[current] != cost) continue;
                if (current.Via && targets.Contains(current.Id))
                {
                    var path = new List<string> { current.Id }; var cursor = current;
                    while (previous.TryGetValue(cursor, out var parent)) { path.Add(parent.Id); cursor = parent; } path.Reverse();
                    return cache[key] = new(true, cost) { PortIds = path, Cells = path.Select(id => graph.Ports[id].Position).ToList(), Connections = path.Select(id => graph.Ports[id].ProfilePort.Connection).ToList() };
                }
                foreach (var next in edges[current.Id].Where(PortAllowed).Order(StringComparer.Ordinal))
                {
                    // A detour down an unrelated maintenance spur and back over the same port
                    // does not make the source-to-target circuit a maintenance route.
                    var cursor = current; var repeats = false;
                    while (true)
                    {
                        if (cursor.Id == next) { repeats = true; break; }
                        if (!previous.TryGetValue(cursor, out cursor)) break;
                    }
                    if (repeats) continue;
                    var state = (Id: next, Via: current.Via || Via(graph.Ports[next].Position));
                    var distance = cost + graph.Ports[current.Id].Position.Distance(graph.Ports[next].Position);
                    if (distances.TryGetValue(state, out var old) && old <= distance) continue;
                    distances[state] = distance; previous[state] = current; pending.Enqueue(state, distance);
                }
            }
            if (route.Mode is "direct" or "publicHall" or "maintenance")
            {
                var allowedPorts = graph.Ports.Keys.Where(PortAllowed).ToHashSet(StringComparer.Ordinal);
                var through = allowedPorts.Where(id => Via(graph.Ports[id].Position)).ToHashSet(StringComparer.Ordinal);
                var alternative = DisjointUtilityPath(graph, edges, roots.Select(port => port.Id).ToList(), targets, allowedPorts, through, Math.Max(0, remaining));
                if (alternative.Count > 0)
                    return cache[key] = new(true, alternative.Zip(alternative.Skip(1)).Sum(pair => graph.Ports[pair.First].Position.Distance(graph.Ports[pair.Second].Position)))
                    { PortIds = alternative, Cells = alternative.Select(id => graph.Ports[id].Position).ToList(), Connections = alternative.Select(id => graph.Ports[id].ProfilePort.Connection).ToList() };
            }
            return Missing();
        };
    }

    public static Func<DesignRoute, HashSet<Cell>> CreateUtilityRouteSupport(StationDesignProject project)
    {
        var rooms = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned).ToList();
        var interfaces = DesignMeasurements.ValidInterfaces(project); var resolveEntity = EntityResolver(project, interfaces);
        var profiles = project.Definition.Systems.Profiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        var hardware = project.Layout.SystemDevices.Select(device => (Device: device, Entity: resolveEntity(device)))
            .Where(item => item.Entity != null && profiles.ContainsKey(item.Device.ProfileId))
            .SelectMany(item => Ports(profiles[item.Device.ProfileId], item.Entity!.Position, item.Entity.Rotation, item.Entity.Id).Select(port => (Room: item.Entity.RoomId, port.Position))).ToList();
        return route => UtilitySupportCells(project, route, rooms, interfaces, hardware);
    }

    private static HashSet<Cell> UtilitySupportCells(StationDesignProject project, DesignRoute route, List<DesignRoom> actualRooms,
        List<DesignInterface> interfaces, IEnumerable<(string Room, Cell Position)> hardware)
    {
        var sourceOwner = actualRooms.FirstOrDefault(room => room.Id == route.From)?.OwnerId ?? route.From;
        var permittedRooms = actualRooms.Where(room => route.Mode == "direct" ? room.Id == route.From || room.OwnerId == route.From || room.Id == route.To || room.OwnerId == route.To : route.Mode != "department" || room.OwnerId == sourceOwner).ToList();
        var permittedIds = permittedRooms.Select(room => room.Id).ToHashSet(StringComparer.Ordinal); var positions = hardware.ToList();
        if (route.Mode is "maintenance" or "publicHall")
        {
            var prefix = route.Mode == "maintenance" ? "$maintenance" : "$public";
            permittedIds.UnionWith(positions.Select(item => item.Room).Where(id => id == prefix || id.StartsWith(prefix + "@", StringComparison.Ordinal) || route.Mode == "maintenance" && (id == "$exterior-maintenance" || id.StartsWith("$exterior-maintenance@", StringComparison.Ordinal))));
        }
        if (route.Mode == "utilityNetwork") permittedIds.UnionWith(positions.Select(item => item.Room).Where(id => id.StartsWith('$')));
        var floor = permittedRooms.SelectMany(room => room.Cells).ToHashSet();
        if (route.Mode == "utilityNetwork") floor.UnionWith(project.Layout.PublicCorridors.Concat(project.Layout.Maintenance));
        var allowed = floor.Concat(project.Layout.Walls.Concat(project.Layout.Hull).Where(cell => Cell.Directions.Count(direction => floor.Contains(cell + direction)) >= 2)).ToHashSet();
        if (route.Mode == "maintenance") allowed.UnionWith(project.Layout.Maintenance);
        if (route.Mode == "publicHall") allowed.UnionWith(project.Layout.PublicCorridors);
        allowed.UnionWith(interfaces.Where(port => route.Mode is "direct" or "department" ? permittedIds.Contains(port.From) && permittedIds.Contains(port.To) : true).SelectMany(port => port.Cells));
        allowed.UnionWith(positions.Where(item => permittedIds.Contains(item.Room)).Select(item => item.Position));
        return allowed;
    }

    private sealed class UtilityFlowEdge(int to, int reverse, int capacity)
    {
        public int To = to, Reverse = reverse, Capacity = capacity, Initial = capacity;
    }

    /// <summary>Two vertex-disjoint source/target legs prove a simple circuit through a required network port.</summary>
    private static List<string> DisjointUtilityPath(NativeGraph graph, Dictionary<string, HashSet<string>> edges,
        List<string> roots, HashSet<string> targets, HashSet<string> allowed, HashSet<string> via, int budget)
    {
        Dictionary<string, int> Distances(IEnumerable<string> seeds)
        {
            var found = seeds.Where(allowed.Contains).Distinct().ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
            var pending = new Queue<string>(found.Keys);
            while (pending.TryDequeue(out var id)) foreach (var next in edges[id].Where(allowed.Contains))
                if (found.TryAdd(next, found[id] + 1)) pending.Enqueue(next);
            return found;
        }
        var from = Distances(roots); var to = Distances(targets);
        var ids = allowed.Order(StringComparer.Ordinal).ToList(); var index = ids.Select((id, position) => (id, position)).ToDictionary(pair => pair.id, pair => pair.position, StringComparer.Ordinal);
        foreach (var middle in via.Where(id => from.ContainsKey(id) && to.ContainsKey(id)).OrderBy(id => from[id] + to[id]).ThenBy(id => id, StringComparer.Ordinal).Take(32))
        {
            if (budget <= 0) break;
            var super = ids.Count * 2; var sourceGroup = super + 1; var targetGroup = super + 2; var sink = super + 3;
            var network = Enumerable.Range(0, sink + 1).Select(_ => new List<UtilityFlowEdge>()).ToArray();
            void Add(int a, int b, int capacity)
            { var first = new UtilityFlowEdge(b, network[b].Count, capacity); var second = new UtilityFlowEdge(a, network[a].Count, 0); network[a].Add(first); network[b].Add(second); }
            foreach (var id in ids)
            {
                var position = index[id]; Add(position * 2, position * 2 + 1, id == middle ? 2 : 1);
                foreach (var next in edges[id].Where(allowed.Contains)) Add(position * 2 + 1, index[next] * 2, 2);
            }
            Add(super, sourceGroup, 1); Add(super, targetGroup, 1);
            foreach (var root in roots.Where(allowed.Contains)) Add(sourceGroup, index[root] * 2, 1);
            foreach (var target in targets.Where(allowed.Contains)) Add(targetGroup, index[target] * 2, 1);
            Add(index[middle] * 2 + 1, sink, 2);
            var flow = 0;
            while (flow < 2 && budget > 0)
            {
                var previous = Enumerable.Repeat((-1, -1), network.Length).ToArray(); previous[super] = (super, -1);
                var pending = new Queue<int>(); pending.Enqueue(super);
                while (pending.TryDequeue(out var current) && previous[sink].Item1 < 0 && budget-- > 0)
                    for (var edge = 0; edge < network[current].Count; edge++)
                    {
                        var candidate = network[current][edge]; if (candidate.Capacity == 0 || previous[candidate.To].Item1 >= 0) continue;
                        previous[candidate.To] = (current, edge); pending.Enqueue(candidate.To);
                    }
                if (previous[sink].Item1 < 0) break;
                for (var current = sink; current != super;)
                {
                    var (parent, edge) = previous[current]; var selected = network[parent][edge]; selected.Capacity--; network[current][selected.Reverse].Capacity++; current = parent;
                }
                flow++;
            }
            if (flow != 2) continue;
            List<string> Leg(int start)
            {
                var pending = new Queue<int>(); pending.Enqueue(start); var previous = new Dictionary<int, int> { [start] = -1 };
                while (pending.TryDequeue(out var current))
                {
                    if (current == sink) break;
                    foreach (var edge in network[current].Where(edge => edge.Initial > edge.Capacity))
                        if (previous.TryAdd(edge.To, current)) pending.Enqueue(edge.To);
                }
                if (!previous.ContainsKey(sink)) return [];
                var path = new List<string>(); var cursor = sink;
                while (cursor != start) { if (cursor < super && cursor % 2 == 0) path.Add(ids[cursor / 2]); cursor = previous[cursor]; }
                path.Reverse(); return path;
            }
            var first = Leg(sourceGroup); var second = Leg(targetGroup);
            var path = first.Concat(second.AsEnumerable().Reverse().Skip(1)).ToList();
            if (first.Count > 0 && second.Count > 0 && path.Distinct(StringComparer.Ordinal).Count() == path.Count) return path;
        }
        return [];
    }

}

