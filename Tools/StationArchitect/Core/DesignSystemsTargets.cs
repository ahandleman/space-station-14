namespace Content.StationArchitect;

public static partial class DesignSystems
{
    public static Func<string, DesignRoom, string> ScopeResolver(StationDesignProject project)
    {
        var compartments = DesignMeasurements.AirCompartments(project);
        var air = new Dictionary<Cell, string>(); foreach (var component in compartments) { var key = CompartmentKey(component.Cells); foreach (var cell in component.Cells) air[cell] = key; }
        var clusters = DesignClusterGeometry.Clusters(project).SelectMany(cluster => cluster.Select(room => (room.Id, Key: cluster.Select(member => member.Id).Order(StringComparer.Ordinal).First()))).ToDictionary(item => item.Id, item => item.Key, StringComparer.Ordinal);
        return (scope, room) => scope switch
        {
            "station" => "$station", "owner" => room.OwnerId, "cluster" => clusters.GetValueOrDefault(room.Id) ?? room.Id,
            "compartment" => room.Cells.Select(cell => air.GetValueOrDefault(cell)).FirstOrDefault(value => value != null) ?? room.Id,
            _ => room.Id
        };
    }

    private static string CompartmentKey(IEnumerable<Cell> cells)
    { var first = cells.OrderBy(cell => cell.Y).ThenBy(cell => cell.X).First(); return "$air:" + first.X + "," + first.Y; }

    private static List<DesignRoom> NetworkTargets(StationDesignProject project, IEnumerable<string> networks, bool split)
    {
        var requested = networks.Where(network => network is "publicHall" or "maintenance" or "exteriorMaintenance").Distinct(StringComparer.Ordinal).ToList();
        if (requested.Count == 0) return [];
        var result = new List<DesignRoom>(); var envelope = project.Layout.Envelope.ToHashSet();
        var compartments = split ? DesignMeasurements.AirCompartments(project) : [];
        foreach (var network in requested)
        {
            var id = network switch { "publicHall" => "$public", "maintenance" => "$maintenance", "exteriorMaintenance" => "$exterior-maintenance", _ => "" };
            if (id.Length == 0) continue;
            var cells = (id == "$public" ? project.Layout.PublicCorridors : project.Layout.Maintenance.Where(cell => id == "$exterior-maintenance" ? !envelope.Contains(cell) : envelope.Contains(cell))).ToHashSet();
            if (cells.Count == 0) continue;
            foreach (var mask in split ? compartments.Select(component => component.Cells.Intersect(cells).ToHashSet()).Where(mask => mask.Count > 0) : [cells])
                result.Add(new() { Id = split ? id + "@" + CompartmentKey(compartments.First(component => mask.Any(component.Cells.Contains)).Cells)[5..] : id, OwnerId = "$station", DefinitionPath = id, Cells = LayoutGeometry.Sorted(mask), Bounds = LayoutGeometry.Bounds(mask) });
        }
        return result;
    }

    public static DesignRoom? LookupTargetRoom(StationDesignProject project, string id) => project.Layout.Rooms.FirstOrDefault(room => room.Id == id && room.Enabled && !room.Orphaned) ??
        NetworkTargets(project, ["publicHall", "maintenance", "exteriorMaintenance"], id.Contains('@')).FirstOrDefault(room => room.Id == id);

    public static Func<string, DesignRoom?> RoomResolver(StationDesignProject project)
    {
        var rooms = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned)
            .Concat(NetworkTargets(project, ["publicHall", "maintenance", "exteriorMaintenance"], false))
            .Concat(NetworkTargets(project, ["publicHall", "maintenance", "exteriorMaintenance"], true))
            .GroupBy(room => room.Id).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        return id => rooms.GetValueOrDefault(id);
    }

    public static RoomDefinition TargetDefinition(StationDesignProject project, DesignRoom room) => RoomHierarchy.Templates(project.Definition).FirstOrDefault(item => item.Path == room.DefinitionPath).Room ?? new()
    {
        Id = room.DefinitionPath, Role = "filler", AllowThroughTraffic = true, Tags = ["circulation", room.DefinitionPath.TrimStart('$')],
        Environment = new() { Pressure = room.DefinitionPath == "$exterior-maintenance" ? "vacuum" : "standard", Placement = room.DefinitionPath == "$exterior-maintenance" ? "exterior" : "interior" }
    };

    public static HashSet<Cell> UncoveredCells(StationDesignProject project, SystemDeviceRule rule, IEnumerable<DesignRoom> scopeRooms, IEnumerable<DesignEntity> entities)
    {
        if (rule.MaxCoverageDistance == null) return [];
        var floor = scopeRooms.SelectMany(room => room.Cells).ToHashSet();
        var connectedFloor = new HashSet<Cell>(floor);
        if (rule.Scope == "compartment") connectedFloor.UnionWith(project.Layout.Interfaces.Where(port => port.Kind == "interaction" && port.Type == "counter" && DesignMeasurements.InterfaceTouches(project, port) &&
            port.Cells.Any(cell => Cell.Directions.Count(direction => floor.Contains(cell + direction)) >= 2)).SelectMany(port => port.Cells));
        var distance = new Dictionary<Cell, int>(); var pending = new Queue<Cell>();
        foreach (var entity in entities)
        foreach (var cell in (entity.Placement == "floor" ? entity.Footprint : entity.Footprint.SelectMany(cell => Cell.Directions.Select(direction => cell + direction))).Distinct().Where(floor.Contains))
            if (distance.TryAdd(cell, 0)) pending.Enqueue(cell);
        while (pending.TryDequeue(out var cell))
            foreach (var direction in Cell.Directions)
            {
                var next = cell + direction; var length = distance[cell] + 1;
                if (length <= rule.MaxCoverageDistance && connectedFloor.Contains(next) && distance.TryAdd(next, length)) pending.Enqueue(next);
            }
        return floor.Except(distance.Keys).ToHashSet();
    }

    public static string InterfaceEntityId(string portId, Cell cell) => "$interface:" + portId + ":" + cell.X + "," + cell.Y;

    public static string InterfacePrototype(StationDesignProject project, DesignInterface port) => port.Type switch
    {
        "dockingPort" => project.Settings.Export.DockingPortPrototype,
        _ => port.From is "$space" or "$exterior-maintenance" || port.To is "$space" or "$exterior-maintenance" ? project.Settings.Export.SpaceAirlockPrototype : project.Settings.Export.DoorPrototype
    };

    public static int InterfaceRotation(DesignInterface port)
    { var normal = port.ToApproach - port.FromApproach; return normal.X > 0 ? 90 : normal.Y > 0 ? 180 : normal.X < 0 ? 270 : 0; }

    public static List<DesignEntity> InterfaceTargets(StationDesignProject project, SystemDeviceRule rule, IReadOnlyList<DesignInterface>? validatedInterfaces = null, Func<string, DesignRoom?>? resolveRoom = null)
    {
        resolveRoom ??= RoomResolver(project);
        var targets = Targets(project, rule).Select(room => room.Id).ToHashSet(StringComparer.Ordinal);
        var allRooms = rule.Selector.Paths.Count == 0 && rule.Selector.Tags.Count == 0 && rule.Selector.Roles.Count == 0 && rule.Selector.Pressures.Count == 0 && rule.Selector.Placements.Count == 0 && rule.Selector.Networks.Count == 0;
        var result = new Dictionary<string, DesignEntity>(StringComparer.Ordinal);
        foreach (var port in (validatedInterfaces ?? DesignMeasurements.ValidInterfaces(project)).Where(port => port.Kind == "walking" && (rule.Selector.InterfaceTypes.Count == 0 || rule.Selector.InterfaceTypes.Contains(port.Type))))
        {
            bool Selected(string id) => targets.Contains(id) || targets.Any(target => target.StartsWith(id + "@", StringComparison.Ordinal));
            if (!allRooms && !Selected(port.From) && !Selected(port.To)) continue;
            foreach (var cell in port.Cells)
            {
                var existing = project.Layout.Equipment.FirstOrDefault(entity => entity.InterfaceId == port.Id && entity.Footprint.Contains(cell));
                var owner = project.Layout.Rooms.Any(room => room.Id == port.From && room.Enabled && !room.Orphaned) ? port.From : project.Layout.Rooms.Any(room => room.Id == port.To && room.Enabled && !room.Orphaned) ? port.To : resolveRoom(port.From) != null ? port.From : resolveRoom(port.To) != null ? port.To : port.From;
                var entity = existing ?? new DesignEntity { Id = InterfaceEntityId(port.Id, cell), RoomId = owner, Prototype = InterfacePrototype(project, port), Position = cell, Rotation = InterfaceRotation(port), Placement = "interface", Footprint = [cell], InterfaceId = port.Id };
                result.TryAdd(entity.Id, entity);
            }
        }
        return result.Values.OrderBy(entity => entity.Id, StringComparer.Ordinal).ToList();
    }

    public static DesignEntity? FindEntity(StationDesignProject project, DesignSystemDevice device) => project.Layout.Equipment.FirstOrDefault(entity => entity.Id == device.EntityId) ??
        project.Definition.Systems.Devices.Where(rule => rule.Id == device.RequirementId).SelectMany(rule => rule.BindTransport ? TransportTargets(project, rule) : rule.BindInterfaces ? InterfaceTargets(project, rule) : []).FirstOrDefault(entity => entity.Id == device.EntityId);

    public static Func<DesignSystemDevice, DesignEntity?> EntityResolver(StationDesignProject project, IReadOnlyList<DesignInterface>? validatedInterfaces = null, Func<string, DesignRoom?>? resolveRoom = null)
    {
        resolveRoom ??= RoomResolver(project);
        if (validatedInterfaces == null && project.Definition.Systems.Devices.Any(rule => rule.BindInterfaces)) validatedInterfaces = DesignMeasurements.ValidInterfaces(project);
        var entities = project.Layout.Equipment.GroupBy(entity => entity.Id).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var rule in project.Definition.Systems.Devices.Where(rule => rule.BindInterfaces || rule.BindTransport))
            foreach (var entity in rule.BindTransport ? TransportTargets(project, rule) : InterfaceTargets(project, rule, validatedInterfaces, resolveRoom)) entities.TryAdd(entity.Id, entity);
        return device => entities.GetValueOrDefault(device.EntityId);
    }

    public static string DeviceScopeKey(StationDesignProject project, SystemDeviceRule rule, DesignEntity entity) => rule.BindInterfaces || rule.BindTransport ? entity.Id :
        LookupTargetRoom(project, entity.RoomId) is { } room ? ScopeKey(project, rule.Scope, room) : entity.RoomId;

    public static List<DesignEntity> TransportTargets(StationDesignProject project, SystemDeviceRule rule)
    {
        var selected = Targets(project, rule).Select(room => room.Id).ToHashSet(StringComparer.Ordinal);
        var connections = RequirementCompiler.Compile(project.Definition).Connections.ToDictionary(connection => connection.Id, StringComparer.Ordinal);
        var result = new List<DesignEntity>();
        foreach (var network in project.Layout.TransportNetworks)
        {
            if (!connections.TryGetValue(network.RequirementId, out var requirement)) continue;
            var route = new DesignRoute { RequirementId = network.RequirementId, From = network.From, To = network.To, Mode = network.Mode, Kind = requirement.Kind, Cells = [.. network.Cells], TransportNetworkId = network.Id };
            if (!DesignTransport.RouteValid(project, route)) continue;
            result.AddRange(network.Terminals.Where(terminal => selected.Contains(terminal.RoomId)).Select(terminal => new DesignEntity
            { Id = terminal.Id, RoomId = terminal.RoomId, Prototype = terminal.Prototype, Position = terminal.Position, Rotation = terminal.Rotation, Footprint = [.. terminal.Footprint], Placement = "transport" }));
        }
        return result;
    }
}
