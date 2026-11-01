namespace Content.StationArchitect;

internal sealed class GameflowWorkBudget(GameflowSettings settings)
{
    public int Remaining = settings.MaxTotalSearchVisits;
    public bool Exhausted;
    public bool Visit(ref int local)
    {
        if (Remaining <= 0 || local <= 0) { Exhausted = true; return false; }
        Remaining--; local--; return true;
    }
}

internal sealed record GameflowTerminal(string Id, Cell Cell);
internal sealed record GameflowSelection(List<GameflowTerminal> Terminals, HashSet<Cell> Targets);
internal sealed record GameflowPath(List<Cell> Cells, int Cost, List<string> Tubes);

/// <summary>Physical floor plus validated walking interfaces under one explicitly declared access assumption.</summary>
internal sealed class GameflowGraph
{
    public HashSet<Cell> Walk { get; } = [];
    public HashSet<Cell> PhysicalWalk { get; } = [];
    public HashSet<Cell> Doors { get; } = [];
    public List<HashSet<Cell>> DoorRuns { get; } = [];
    public Dictionary<string, HashSet<Cell>> RoomFloors { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<Cell>> Networks { get; } = new(StringComparer.Ordinal);
    public List<string> Assumptions { get; } = [];
    private readonly Dictionary<Cell, List<(Cell To, string Id)>> _tubes = [];
    private readonly StationDesignProject _project;
    private readonly GameflowAccessProfile _profile;
    private readonly GameflowSettings _settings;
    private readonly GameflowWorkBudget _budget;
    private readonly Dictionary<string, RoomDefinition> _definitions;

    public GameflowGraph(StationDesignProject project, GameflowAccessProfile profile, GameflowSettings settings, GameflowWorkBudget budget)
    {
        _project = project; _profile = profile; _settings = settings; _budget = budget;
        _definitions = RequirementCompiler.Rooms(project.Definition).ToDictionary(pair => pair.Path, pair => pair.Room, StringComparer.Ordinal);
        var rooms = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && _definitions.ContainsKey(room.DefinitionPath)).ToList();
        var interior = project.Layout.Envelope.ToHashSet();
        foreach (var room in rooms)
        {
            PhysicalWalk.UnionWith(room.Cells);
            var definition = _definitions[room.DefinitionPath];
            if ((!profile.AllowExterior && definition.Environment.Placement == "exterior") || !LevelAllowed(definition.Access.Level)) continue;
            RoomFloors[room.Id] = room.Cells.ToHashSet(); Walk.UnionWith(room.Cells);
        }
        Networks["publicHall"] = project.Layout.PublicCorridors.Where(cell => profile.AllowExterior || interior.Contains(cell)).ToHashSet();
        Networks["maintenance"] = project.Layout.Maintenance.Where(cell => profile.AllowExterior || interior.Contains(cell)).ToHashSet();
        PhysicalWalk.UnionWith(project.Layout.PublicCorridors); PhysicalWalk.UnionWith(project.Layout.Maintenance);
        foreach (var network in profile.Networks) if (Networks.TryGetValue(network, out var floor)) Walk.UnionWith(floor);
        bool EndpointAllowed(string endpoint) => endpoint switch
        {
            "$public" => profile.Networks.Contains("publicHall", StringComparer.Ordinal),
            "$maintenance" => profile.Networks.Contains("maintenance", StringComparer.Ordinal),
            "$exterior-maintenance" => profile.AllowExterior && profile.Networks.Contains("maintenance", StringComparer.Ordinal),
            _ => RoomFloors.ContainsKey(endpoint)
        };
        var validPorts = project.Layout.Interfaces.Where(port => port.Kind == "walking" && port.Type is "doorway" or "airlock" or "dockingPort" && DesignMeasurements.InterfaceTouches(project, port)).ToList();
        foreach (var port in validPorts)
        {
            PhysicalWalk.UnionWith(port.Cells);
            if (!EndpointAllowed(port.From) || !EndpointAllowed(port.To)) continue;
            var roomEndpoint = RoomFloors.ContainsKey(port.From) || RoomFloors.ContainsKey(port.To);
            if (!roomEndpoint && port.Access.Length > 0 && !LevelAllowed(port.Access)) continue;
            Walk.UnionWith(port.Cells); Doors.UnionWith(port.Cells); DoorRuns.Add(port.Cells.ToHashSet());
        }
        // Walking door hardware occupies its opening; ordinary machinery remains an obstruction.
        var doorHardware = validPorts.Select(port => port.Id).ToHashSet(StringComparer.Ordinal);
        var obstructions = project.Layout.Equipment.Where(entity => DesignSystems.BlocksCrew(project, entity) && !doorHardware.Contains(entity.InterfaceId)).SelectMany(entity => entity.Footprint).Concat(DesignTransport.EndpointFloorCells(project)).ToHashSet();
        Walk.ExceptWith(obstructions); PhysicalWalk.ExceptWith(obstructions);
        Walk.ExceptWith(project.Layout.Walls.Concat(project.Layout.Hull).Concat(project.Layout.Windows));
        PhysicalWalk.ExceptWith(project.Layout.Walls.Concat(project.Layout.Hull).Concat(project.Layout.Windows));
        foreach (var floor in RoomFloors.Values) floor.IntersectWith(Walk);
        foreach (var floor in Networks.Values) floor.IntersectWith(Walk);
        if (profile.AllowCrewTubes)
        {
            var validEdges = DesignTransport.CrewEdges(project).ToHashSet();
            foreach (var network in project.Layout.TransportNetworks.Where(network => network.Mode != "mail" && network.Terminals.Count == 2))
            {
                var a = network.Terminals[0].Position; var b = network.Terminals[1].Position;
                if (!validEdges.Contains((a, b)) && !validEdges.Contains((b, a))) continue;
                var from = network.Terminals[0].Footprint.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Where(Walk.Contains).Distinct().ToList();
                var to = network.Terminals[1].Footprint.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Where(Walk.Contains).Distinct().ToList();
                foreach (var entry in from) foreach (var exit in to) { AddTube(entry, exit, network.Id); AddTube(exit, entry, network.Id); }
            }
        }
        Assumptions.Add(profile.AccessPolicy == "allDoors" ? "Every physical walking door is assumed traversable; no actual job access or door-lock state is simulated." : "Walking doors respect authored access labels: " + string.Join(", ", profile.AllowedLevels) + ". Edit protection flags do not grant access.");
        Assumptions.Add("Traversed passage networks: " + string.Join(", ", profile.Networks) + "; exterior floor " + (profile.AllowExterior ? "included" : "excluded") + ". Empty space is never walkable.");
        Assumptions.Add(profile.AllowCrewTubes ? "Only independently validated crew-carrying tube hardware may add shortcut edges; mail never carries crew. Geometric chase loops still require continuous walking paths." : "Crew tube shortcuts and mail are excluded from traversal.");
        Assumptions.Add("Travel cost is tile steps plus declared door/crew-tube penalties. Furniture uses configured footprints; movement speed, tools, combat and runtime balance are not simulated.");
    }

    private bool LevelAllowed(string level) => _profile.AccessPolicy == "allDoors" || _profile.AllowedLevels.Contains(level, StringComparer.Ordinal);
    private void AddTube(Cell from, Cell to, string id)
    { if (!_tubes.TryGetValue(from, out var edges)) _tubes[from] = edges = []; edges.Add((to, id)); }

    public GameflowSelection Select(GameflowSelector selector)
    {
        var terminals = new List<GameflowTerminal>(); var targets = new HashSet<Cell>();
        var hasRooms = selector.Paths.Count + selector.Tags.Count + selector.Owners.Count + selector.Roles.Count > 0;
        foreach (var room in _project.Layout.Rooms.Where(room => RoomFloors.ContainsKey(room.Id)))
        {
            var definition = _definitions[room.DefinitionPath];
            var ownerTags = _project.Definition.Departments.FirstOrDefault(owner => owner.Id == room.OwnerId)?.Tags ?? [];
            var tests = new List<bool>();
            if (selector.Paths.Count > 0) tests.Add(selector.Paths.Any(path => path == room.Id || path == room.DefinitionPath || path == room.OwnerId));
            if (selector.Tags.Count > 0) tests.Add(selector.Tags.Any(tag => definition.Tags.Contains(tag, StringComparer.Ordinal) || ownerTags.Contains(tag, StringComparer.Ordinal)));
            if (selector.Owners.Count > 0) tests.Add(selector.Owners.Contains(room.OwnerId, StringComparer.Ordinal));
            if (selector.Roles.Count > 0) tests.Add(selector.Roles.Contains(definition.Role, StringComparer.Ordinal));
            var selected = hasRooms ? selector.Match == "all" ? tests.All(value => value) : tests.Any(value => value) : selector.Networks.Count == 0;
            if (!selected || RoomFloors[room.Id].Count == 0) continue;
            var center = RoomFloors[room.Id].OrderBy(cell => cell.Distance(room.Seed)).ThenBy(cell => cell.Y).ThenBy(cell => cell.X).First();
            terminals.Add(new(room.Id, center)); targets.Add(center);
        }
        foreach (var network in selector.Networks)
        {
            if (!_profile.Networks.Contains(network, StringComparer.Ordinal) || !Networks.TryGetValue(network, out var floor) || floor.Count == 0) continue;
            targets.UnionWith(floor);
            var alias = network == "publicHall" ? "$public" : "$maintenance";
            var entries = _project.Layout.Interfaces.Where(port => port.Kind == "walking" && (port.From == alias || port.To == alias) && port.Cells.Any(Doors.Contains))
                .Select(port => port.From == alias ? port.FromApproach : port.ToApproach).Where(floor.Contains).Distinct().ToList();
            var supplement = new List<Cell> { floor.OrderBy(cell => cell.X).ThenBy(cell => cell.Y).First(), floor.OrderBy(cell => cell.X).ThenByDescending(cell => cell.Y).First(),
                    floor.OrderByDescending(cell => cell.X).ThenBy(cell => cell.Y).First(), floor.OrderByDescending(cell => cell.X).ThenByDescending(cell => cell.Y).First(),
                    floor.OrderBy(cell => cell.Y).ThenBy(cell => cell.X).First(), floor.OrderBy(cell => cell.Y).ThenByDescending(cell => cell.X).First(),
                    floor.OrderByDescending(cell => cell.Y).ThenBy(cell => cell.X).First(), floor.OrderByDescending(cell => cell.Y).ThenByDescending(cell => cell.X).First() };
            supplement = supplement.Distinct().Except(entries).ToList();
            while (supplement.Count > 0 && entries.Count < _settings.MaxTerminals)
            {
                var next = supplement.OrderByDescending(cell => entries.Count == 0 ? 0 : entries.Min(entry => entry.Distance(cell))).ThenBy(cell => cell.Y).ThenBy(cell => cell.X).First();
                entries.Add(next); supplement.Remove(next);
            }
            terminals.AddRange(entries.Distinct().Select(cell => new GameflowTerminal(network + "@" + cell.X + "," + cell.Y, cell)));
        }
        return new(terminals.DistinctBy(terminal => terminal.Cell).OrderBy(terminal => terminal.Id, StringComparer.Ordinal).ToList(), targets);
    }

    public GameflowPath? Search(Cell from, HashSet<Cell> targets, HashSet<Cell>? forbidden = null, bool allowTubes = true)
    {
        if (!Walk.Contains(from) || targets.Count == 0 || forbidden?.Contains(from) == true) return null;
        var queue = new PriorityQueue<Cell, (int Cost, int Y, int X)>(); var costs = new Dictionary<Cell, int> { [from] = 0 };
        var previous = new Dictionary<Cell, (Cell From, string Tube)>(); queue.Enqueue(from, (0, from.Y, from.X));
        var visits = _settings.MaxSearchVisits;
        while (queue.TryDequeue(out var cell, out var priority))
        {
            if (costs[cell] != priority.Cost) continue;
            if (!_budget.Visit(ref visits)) return null;
            if (targets.Contains(cell))
            {
                var path = new List<Cell> { cell }; var tubes = new List<string>(); var current = cell;
                while (previous.TryGetValue(current, out var edge)) { if (edge.Tube.Length > 0) tubes.Add(edge.Tube); current = edge.From; path.Add(current); }
                path.Reverse(); return new(path, costs[cell], tubes.Distinct(StringComparer.Ordinal).ToList());
            }
            foreach (var direction in Cell.Directions) Try(cell + direction, 1 + (Doors.Contains(cell + direction) ? _profile.DoorPenalty : 0), "");
            if (allowTubes && _profile.AllowCrewTubes && _tubes.TryGetValue(cell, out var shortcuts)) foreach (var edge in shortcuts) Try(edge.To, _profile.CrewTubeCost, edge.Id);
            void Try(Cell next, int stepCost, string tube)
            {
                if (!Walk.Contains(next) || forbidden?.Contains(next) == true) return;
                var cost = costs[cell] + stepCost;
                if (costs.TryGetValue(next, out var old) && old <= cost) return;
                costs[next] = cost; previous[next] = (cell, tube); queue.Enqueue(next, (cost, next.Y, next.X));
            }
        }
        return null;
    }

    public static HashSet<Cell> PathHalo(IEnumerable<Cell> path, int separation, Cell from, HashSet<Cell> targets, int endpointRadius)
    {
        var blocked = new HashSet<Cell>();
        foreach (var cell in path) for (var y = -separation; y <= separation; y++) for (var x = -separation + Math.Abs(y); x <= separation - Math.Abs(y); x++) blocked.Add(cell + new Cell(x, y));
        blocked.RemoveWhere(cell => cell.Distance(from) <= endpointRadius || targets.Any(target => cell.Distance(target) <= endpointRadius));
        return blocked;
    }

    public List<Cell>? EnclosedBlocked(List<Cell> first, List<Cell> second)
    {
        var boundary = first.Concat(second).ToHashSet(); var bounds = LayoutGeometry.Bounds(boundary).Expand(1);
        if ((long) bounds.Width * bounds.Height > _settings.MaxGraphCells) { _budget.Exhausted = true; return null; }
        var outside = new HashSet<Cell>(); var queue = new Queue<Cell>(); var seed = new Cell(bounds.X, bounds.Y); outside.Add(seed); queue.Enqueue(seed);
        var visits = _settings.MaxSearchVisits;
        while (queue.TryDequeue(out var cell))
        {
            if (!_budget.Visit(ref visits)) return null;
            foreach (var direction in Cell.Directions)
            {
                var next = cell + direction;
                if (bounds.Contains(next) && !boundary.Contains(next) && outside.Add(next)) queue.Enqueue(next);
            }
        }
        return bounds.Cells().Where(cell => !boundary.Contains(cell) && !outside.Contains(cell) && !PhysicalWalk.Contains(cell)).ToList();
    }
}
