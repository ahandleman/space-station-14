namespace Content.StationArchitect;

/// <summary>Observations derived from the design's actual masks, entrances and placed entities.</summary>
public static class DesignMeasurements
{
    public sealed record AirCompartment(HashSet<Cell> Cells, bool ExposedToSpace);

    public static PressureState Build(StationDesignProject project, bool final = false, IReadOnlyDictionary<Cell, List<Cell>>? crewJumps = null)
    {
        var state = new PressureState();
        var entityPlacementValid = EntityPlacementValidator(project);
        var definitions = RequirementCompiler.Rooms(project.Definition).ToDictionary(item => item.Path, item => item.Room, StringComparer.Ordinal);
        var rooms = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && definitions.ContainsKey(room.DefinitionPath)).ToList();
        var envelope = project.Layout.Envelope.ToHashSet();
        var structural = project.Layout.Walls.Concat(project.Layout.Hull).Concat(project.Layout.Windows).ToHashSet();
        var exterior = ExteriorSpace(project);
        var validPorts = ValidInterfaces(project);
        structural.UnionWith(validPorts.Where(port => port.Type == "window").SelectMany(port => port.Cells));
        var allFloor = rooms.SelectMany(room => room.Cells).Concat(project.Layout.PublicCorridors).Concat(project.Layout.Maintenance).ToHashSet();
        var airCompartments = AirCompartments(project);
        var connections = RequirementCompiler.Compile(project.Definition).Connections.ToDictionary(connection => connection.Id, StringComparer.Ordinal);
        var inspectUtility = HasDueNativeUtilities(project, final) ? DesignSystems.CreateUtilityRouteInspector(project, final) : null;
        var circulated = final || DesignStages.Index(project.Stage) >= DesignStages.Index("circulation");
        crewJumps ??= circulated ? DesignCrewTraversal.Jumps(project) : new Dictionary<Cell, List<Cell>>();
        var nestedChecks = circulated ? DesignNestedGeometry.Validate(project, true, true, definitions, observeAll: true, crewJumps: crewJumps).ToList() : [];
        foreach (var room in rooms)
        {
            var floor = room.Cells.ToHashSet();
            var complex = definitions[room.DefinitionPath].Rooms.Count > 0 ? DesignNestedGeometry.ComplexCells(project, room) : floor;
            var bounds = Bounds(complex);
            var entrances = validPorts.Where(port => port.Kind == "walking" && (port.From == room.Id || port.To == room.Id)).ToList();
            var entryOwners = entrances.Select(port => port.From == room.Id ? port.To : port.From)
                .Where(id => id != "$public" && id != "$maintenance" && id != "$space")
                .Select(id => rooms.FirstOrDefault(other => other.Id == id)?.OwnerId)
                .Where(id => id != null).Select(id => id!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var measurement = new RoomMeasurement
            {
                Path = room.DefinitionPath, InstanceId = room.Instance, ParentInstanceId = room.ParentInstanceId, Area = floor.Count,
                ComplexArea = definitions[room.DefinitionPath].Rooms.Count > 0 ? complex.Count : null,
                SubtreeFloorArea = definitions[room.DefinitionPath].Rooms.Count > 0 || definitions[room.DefinitionPath].ComplexArea != null ? DesignNestedGeometry.Subtree(project, room).SelectMany(child => child.Cells).Distinct().LongCount() : null,
                ContainedInParent = room.ParentInstanceId.Length > 0 && circulated ? floor.Count > 0 && !nestedChecks.Any(issue => issue.Path == room.Id && issue.Code is "nested-parent" or "nested-containment" or "nested-enclosure" or "nested-internal-placement" || issue.Path == room.ParentInstanceId && issue.Code == "complex-intrusion") : null,
                ParentConnectionVerified = room.ParentInstanceId.Length > 0 && circulated ? floor.Count > 0 && !nestedChecks.Any(issue => issue.Path == room.Id && issue.Code is "nested-parent" or "nested-parent-connection") : null,
                ParentApproachVerified = room.ParentInstanceId.Length > 0 && circulated ? floor.Count > 0 && !nestedChecks.Any(issue => issue.Path == room.Id && issue.Code is "nested-parent" or "nested-approach-bypass" or "nested-external-entry") : null,
                Width = bounds.Width, Height = bounds.Height, Components = Components(floor),
                PublicEntries = entrances.Count(port => port.From == "$public" || port.To == "$public"),
                MaintenanceEntries = entrances.Count(port => port.From == "$maintenance" || port.To == "$maintenance"),
                EntryOwners = entryOwners,
                VerifiedApproachNetworks = circulated ? VerifiedApproachNetworks(project, room, definitions[room.DefinitionPath].Access.RequireNetworkApproach, crewJumps) : null,
                SpaceFrontage = Frontage(room, structural, exterior),
                ThroughTrafficPaths = project.Layout.Routes.Count(route => route.Kind == "walking" && route.Mode is not ("disposalShortcut" or "mail") && route.From != room.Id && route.To != room.Id && !rooms.Any(endpoint => (endpoint.Id == route.From || endpoint.Id == route.To) && DesignNestedGeometry.IsAncestor(project, room.Id, endpoint)) && route.Cells.Any(floor.Contains)),
                Contents = project.Layout.Equipment.Where(entity => entity.SystemRuleId.Length == 0 && entity.RoomId == room.Id && entityPlacementValid(room, entity))
                    .GroupBy(entity => (entity.RequirementId, entity.Prototype, entity.Placement, InterfaceType: validPorts.FirstOrDefault(port => port.Id == entity.InterfaceId && entity.Footprint.All(port.Cells.Contains) && (port.From == room.Id || port.To == room.Id))?.Type ?? ""))
                    .Select(group => new ContentMeasurement { Id = group.Key.RequirementId, Prototype = group.Key.Prototype, Placement = group.Key.Placement, InterfaceType = group.Key.InterfaceType, Count = group.Count() }).ToList(),
                Docks = DesignDockGeometry.Measure(project, room)
            };
            var zone = project.Layout.EnvironmentZones.FirstOrDefault(zone => zone.RoomId == room.Id && floor.IsSubsetOf(zone.Cells));
            if (zone != null)
            {
                var inside = floor.Count(envelope.Contains);
                measurement.Environment = new()
                {
                    Pressure = zone.Pressure,
                    Placement = inside == floor.Count ? "interior" : "exterior",
                    Sealed = airCompartments.Where(compartment => compartment.Cells.Overlaps(floor)).All(compartment => !compartment.ExposedToSpace),
                    TemperatureKelvin = zone.TemperatureKelvin
                };
            }
            state.Rooms.Add(measurement);
        }
        foreach (var owner in project.Definition.Departments.Select(department => department.Id).Append(RequirementCompiler.MaintenanceOwner))
        {
            var ownedRooms = rooms.Where(room => room.OwnerId == owner).ToList();
            var floor = ownedRooms.SelectMany(room => room.Cells).ToHashSet();
            var bounds = Bounds(floor);
            // Doors join owned rooms without counting their wall cells as owned usable floor.
            var connected = floor.Concat(project.Layout.Interfaces.Where(port => port.Kind == "walking" && ownedRooms.Any(room => room.Id == port.From) && ownedRooms.Any(room => room.Id == port.To)).SelectMany(port => port.Cells)).ToHashSet();
            state.Departments.Add(new()
            {
                Path = owner, Width = bounds.Width, Height = bounds.Height, Components = Components(connected),
                HallCrossings = OwnerCrossings(project, ownedRooms, "$public"),
                MaintenanceCrossings = OwnerCrossings(project, ownedRooms, "$maintenance")
            });
        }
        foreach (var route in project.Layout.Routes)
        {
            if (route.Mode is "mail" or "disposalShortcut")
            {
                if (!RouteValid(project, route, definitions, validPorts, connections.GetValueOrDefault(route.RequirementId), final: final)) continue;
                var measured = DesignTransport.Measure(project, route);
                if (measured != null) state.Connections.Add(measured);
                continue;
            }
            if (route.Mode == "utilityNetwork" && !NativeUtilityDue(project, route, final)) continue;
            if (route.RequirementId.Length == 0 || !connections.TryGetValue(route.RequirementId, out var requirement) || !RouteValid(project, route, definitions, validPorts, requirement, inspectUtility, final)) continue;
            var ports = validPorts.Where(port => route.Cells.Any(port.Cells.Contains)).ToList();
            var port = ports.Where(port => port.Kind == route.Kind && (requirement.InterfaceTypes.Count == 0 || requirement.InterfaceTypes.Contains(port.Type))).OrderByDescending(port => port.Cells.Distinct().Count()).FirstOrDefault();
            var fromRoom = rooms.FirstOrDefault(room => room.Id == route.From); var toRoom = rooms.FirstOrDefault(room => room.Id == route.To);
            var measuredFrom = fromRoom != null && requirement.From == fromRoom.OwnerId ? fromRoom.OwnerId : route.From;
            var measuredTo = toRoom != null && requirement.To.Contains(toRoom.OwnerId) ? toRoom.OwnerId : route.To;
            state.Connections.Add(new()
            {
                Id = route.RequirementId, SourceInstance = measuredFrom, TargetInstance = measuredTo, Kind = route.Kind,
                Mode = route.Mode, Distance = NativeUtilityDue(project, route, final) ? inspectUtility!(route).Distance : Math.Max(0, route.Cells.Count - 1),
                InterfaceType = route.Kind == "utility" ? project.Layout.Utilities.FirstOrDefault(utility => utility.RequirementId == route.RequirementId && utility.From == route.From && utility.To == route.To)?.Type ?? "" : port?.Type ?? "",
                Width = route.Kind == "utility" ? 1 : port?.Cells.Distinct().Count() ?? 0
            });
        }
        return state;
    }

    /// <summary>Actual gas-connected floor, including open serving counters; closed doors/windows remain airtight barriers.</summary>
    public static List<AirCompartment> AirCompartments(StationDesignProject project)
    {
        var floor = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned).SelectMany(room => room.Cells)
            .Concat(project.Layout.PublicCorridors).Concat(project.Layout.Maintenance).ToHashSet();
        var ports = ValidInterfaces(project);
        floor.UnionWith(ports.Where(port => port.Kind == "interaction" && port.Type == "counter").SelectMany(port => port.Cells));
        var airtight = project.Layout.Walls.Concat(project.Layout.Hull).Concat(project.Layout.Windows)
            .Concat(ports.Where(port => port.Kind != "utility" && port.Type is "doorway" or "airlock" or "window" or "dockingPort").SelectMany(port => port.Cells)).ToHashSet();
        var remaining = new HashSet<Cell>(floor); var result = new List<AirCompartment>();
        while (remaining.Count > 0)
        {
            var first = remaining.First(); remaining.Remove(first);
            var cells = new HashSet<Cell> { first }; var queue = new Queue<Cell>(); queue.Enqueue(first); var exposed = false;
            while (queue.TryDequeue(out var cell)) foreach (var direction in Cell.Directions)
            {
                var next = cell + direction;
                if (remaining.Remove(next)) { cells.Add(next); queue.Enqueue(next); }
                else if (!floor.Contains(next) && !airtight.Contains(next)) exposed = true;
            }
            result.Add(new(cells, exposed));
        }
        return result;
    }

    /// <summary>For each configured network, removes that network's floor and searches all remaining physical station approaches.</summary>
    public static List<string> VerifiedApproachNetworks(StationDesignProject project, DesignRoom room, IEnumerable<string> requiredNetworks, IReadOnlyDictionary<Cell, List<Cell>>? crewJumps = null)
    {
        var networks = requiredNetworks.ToList(); if (networks.Count == 0) return [];
        var rooms = project.Layout.Rooms.Where(candidate => candidate.Enabled && !candidate.Orphaned).ToList();
        var blocked = project.Layout.Equipment.Where(entity => DesignSystems.BlocksCrew(project, entity)).SelectMany(entity => entity.Footprint).ToHashSet();
        var target = room.Cells.Except(blocked).ToHashSet();
        var floor = rooms.SelectMany(candidate => candidate.Cells).Concat(project.Layout.PublicCorridors).Concat(project.Layout.Maintenance)
            .Concat(project.Layout.Interfaces.Where(port => port.Kind == "walking" && port.Type is "doorway" or "airlock" or "dockingPort" && InterfaceTouches(project, port)).SelectMany(port => port.Cells)).Except(blocked).ToHashSet();
        var externalApproaches = rooms.Where(candidate => candidate.OwnerId != room.OwnerId).SelectMany(candidate => candidate.Cells).Concat(project.Layout.PublicCorridors).ToHashSet();
        var verified = new List<string>();
        var jumps = crewJumps ?? DesignCrewTraversal.Jumps(project);
        foreach (var network in networks)
        {
            var removed = (network == "maintenance" ? project.Layout.Maintenance : project.Layout.PublicCorridors).ToHashSet();
            var allowed = floor.Except(removed).ToHashSet();
            var reached = new HashSet<Cell>(); var queue = new Queue<Cell>();
            foreach (var source in externalApproaches.Where(allowed.Contains)) if (reached.Add(source)) queue.Enqueue(source);
            var bypass = false;
            while (queue.TryDequeue(out var cell))
            {
                if (target.Contains(cell)) { bypass = true; break; }
                foreach (var direction in Cell.Directions)
                {
                    var next = cell + direction;
                    if (allowed.Contains(next) && reached.Add(next)) queue.Enqueue(next);
                }
                if (jumps.TryGetValue(cell, out var exits)) foreach (var next in exits)
                    if (allowed.Contains(next) && reached.Add(next)) queue.Enqueue(next);
            }
            if (!bypass && target.Count > 0) verified.Add(network);
        }
        return verified;
    }

    private static int OwnerCrossings(StationDesignProject project, List<DesignRoom> owned, string network)
    {
        // A coherent owner with outward entrances is not split merely because it has a hallway door.
        if (owned.Count < 2) return 0;
        var cells = owned.SelectMany(room => room.Cells).ToHashSet();
        var doors = project.Layout.Interfaces.Where(port => port.Kind == "walking" && owned.Any(room => room.Id == port.From) && owned.Any(room => room.Id == port.To)).SelectMany(port => port.Cells);
        if (Components(cells.Concat(doors).ToHashSet()) <= 1) return 0;
        return project.Layout.Routes.Count(route => route.Kind == "walking" && owned.Any(room => room.Id == route.From) && owned.Any(room => room.Id == route.To) && route.Mode == (network == "$public" ? "publicHall" : "maintenance"));
    }

    public static GridBounds Bounds(IEnumerable<Cell> cells)
    {
        var list = cells as ICollection<Cell> ?? cells.ToList();
        if (list.Count == 0) return new(0, 0, 0, 0);
        var minX = list.Min(cell => cell.X); var minY = list.Min(cell => cell.Y);
        return new(minX, minY, list.Max(cell => cell.X) - minX + 1, list.Max(cell => cell.Y) - minY + 1);
    }

    public static int Components(HashSet<Cell> cells)
    {
        var remaining = new HashSet<Cell>(cells); var count = 0;
        while (remaining.Count > 0)
        {
            count++; var queue = new Queue<Cell>(); var first = remaining.First(); remaining.Remove(first); queue.Enqueue(first);
            while (queue.TryDequeue(out var cell)) foreach (var direction in Cell.Directions) if (remaining.Remove(cell + direction)) queue.Enqueue(cell + direction);
        }
        return count;
    }

    public static HashSet<Cell> ExteriorSpace(StationDesignProject project)
    {
        var barriers = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned).SelectMany(room => room.Cells)
            .Concat(project.Layout.PublicCorridors).Concat(project.Layout.Maintenance)
            .Concat(project.Layout.Walls).Concat(project.Layout.Hull).Concat(project.Layout.Windows)
            .Concat(project.Layout.Interfaces.SelectMany(port => port.Cells)).ToHashSet();
        var bounds = Bounds(barriers.Concat(project.Layout.Envelope)).Expand(3);
        // Unmaterialized cells export as SS14 Space tiles, including sealed vacuum courtyards.
        // Envelope membership is a planning limit, not a hidden floor assignment.
        return bounds.Cells().Where(cell => !barriers.Contains(cell)).ToHashSet();
    }

    public static List<Cell> SpaceBoundaryCells(StationDesignProject project, DesignRoom room)
    {
        var structural = project.Layout.Walls.Concat(project.Layout.Hull).Concat(project.Layout.Windows).Concat(project.Layout.Interfaces.SelectMany(port => port.Cells)).ToHashSet();
        var exterior = ExteriorSpace(project); var result = new HashSet<Cell>();
        foreach (var floor in room.Cells) foreach (var direction in Cell.Directions)
        {
            var boundary = floor + direction;
            if (structural.Contains(boundary) && exterior.Contains(boundary + direction)) result.Add(boundary);
        }
        return result.OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToList();
    }

    public static int SpaceFrontage(StationDesignProject project, DesignRoom room)
    {
        var structural = project.Layout.Walls.Concat(project.Layout.Hull).Concat(project.Layout.Windows).Concat(project.Layout.Interfaces.Where(port => port.Type == "window" && InterfaceTouches(project, port)).SelectMany(port => port.Cells)).ToHashSet();
        var exterior = ExteriorSpace(project); return Frontage(room, structural, exterior);
    }

    private static int Frontage(DesignRoom room, HashSet<Cell> structural, HashSet<Cell> exterior)
    {
        var count = 0;
        foreach (var floor in room.Cells) foreach (var direction in Cell.Directions)
            if (structural.Contains(floor + direction) && exterior.Contains(floor + direction + direction)) count++;
        return count;
    }

    public static bool InterfaceTouches(StationDesignProject project, DesignInterface port)
    {
        if (port.Cells.Count == 0 || port.Cells.Distinct().Count() != port.Cells.Count || Components(port.Cells.ToHashSet()) != 1) return false;
        var from = EndpointCells(project, port.From); var to = EndpointCells(project, port.To);
        if (port.From == "$maintenance" && port.To == "$exterior-maintenance" || port.To == "$maintenance" && port.From == "$exterior-maintenance")
        {
            var envelope = project.Layout.Envelope.ToHashSet();
            var interior = project.Layout.Maintenance.Where(envelope.Contains).ToHashSet();
            var exterior = project.Layout.Maintenance.Where(cell => !envelope.Contains(cell)).ToHashSet();
            from = port.From == "$maintenance" ? interior : exterior;
            to = port.To == "$maintenance" ? interior : exterior;
        }
        return port.Cells.All(cell => Cell.Directions.Any(direction => from.Contains(cell + direction) && to.Contains(cell - direction)));
    }

    public static List<DesignInterface> ValidInterfaces(StationDesignProject project)
    {
        var endpoints = project.Layout.Interfaces.SelectMany(port => new[] { port.From, port.To }).Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, id => EndpointCells(project, id), StringComparer.Ordinal);
        var envelope = project.Layout.Envelope.ToHashSet();
        var interiorMaintenance = project.Layout.Maintenance.Where(envelope.Contains).ToHashSet();
        var exteriorMaintenance = project.Layout.Maintenance.Where(cell => !envelope.Contains(cell)).ToHashSet();
        var result = new List<DesignInterface>();
        foreach (var port in project.Layout.Interfaces)
        {
            if (port.Cells.Count == 0 || port.Cells.Distinct().Count() != port.Cells.Count || Components(port.Cells.ToHashSet()) != 1) continue;
            var from = endpoints[port.From]; var to = endpoints[port.To];
            if (port.From == "$maintenance" && port.To == "$exterior-maintenance") { from = interiorMaintenance; to = exteriorMaintenance; }
            if (port.To == "$maintenance" && port.From == "$exterior-maintenance") { from = exteriorMaintenance; to = interiorMaintenance; }
            if (port.Cells.All(cell => Cell.Directions.Any(direction => from.Contains(cell + direction) && to.Contains(cell - direction)))) result.Add(port);
        }
        return result;
    }

    public static HashSet<Cell> EndpointCells(StationDesignProject project, string id) => id switch
    {
        "$public" => project.Layout.PublicCorridors.ToHashSet(),
        "$maintenance" => project.Layout.Maintenance.ToHashSet(),
        "$exterior-maintenance" => ExteriorMaintenance(project),
        "$space" => ExteriorSpace(project),
        _ => project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && (room.Id == id || room.OwnerId == id)).SelectMany(room => room.Cells).ToHashSet()
    };

    private static HashSet<Cell> ExteriorMaintenance(StationDesignProject project)
    {
        var envelope = project.Layout.Envelope.ToHashSet();
        return project.Layout.Maintenance.Where(cell => !envelope.Contains(cell)).ToHashSet();
    }

    /// <summary>One fresh geometry snapshot for a complete validation/measurement operation.</summary>
    public static Func<DesignRoom, DesignEntity, bool> EntityPlacementValidator(StationDesignProject project)
    {
        var definitions = RoomHierarchy.Templates(project.Definition).ToDictionary(item => item.Path, item => item.Room, StringComparer.Ordinal);
        var reserved = DesignDockGeometry.ReservedCells(project);
        var structural = project.Layout.Walls.Concat(project.Layout.Hull).Concat(project.Layout.Windows).Concat(project.Layout.Interfaces.SelectMany(port => port.Cells)).ToHashSet();
        HashSet<Cell>? exterior = null;
        var floors = new Dictionary<string, HashSet<Cell>>(StringComparer.Ordinal);
        var boundaries = new Dictionary<string, HashSet<Cell>>(StringComparer.Ordinal);
        var spaceBoundaries = new Dictionary<string, HashSet<Cell>>(StringComparer.Ordinal);
        var verifiedDocks = new Dictionary<string, bool>(StringComparer.Ordinal);
        return (room, entity) =>
        {
            if (!DesignEquipmentGeometry.TryExpectedFootprint(project.Settings, entity.Prototype, entity.Position, entity.Rotation, out var expected) || entity.Footprint.Count != expected.Count || !entity.Footprint.ToHashSet().SetEquals(expected)) return false;
            if (definitions.TryGetValue(room.DefinitionPath, out var definition) && definition.Exterior.Docks.Any(rule => rule.ContentId == entity.RequirementId))
            {
                if (!verifiedDocks.TryGetValue(entity.Id, out var verified))
                    verifiedDocks[entity.Id] = verified = project.Layout.Docks.Any(dock => dock.RoomId == room.Id && dock.ContentId == entity.RequirementId && dock.EntityId == entity.Id && DesignDockGeometry.ValidateDock(project, dock, hardwareRequired: true).Count == 0);
                return verified;
            }
            if (entity.Footprint.Any(reserved.Contains)) return false;
            if (!floors.TryGetValue(room.Id, out var floor)) floors[room.Id] = floor = room.Cells.ToHashSet();
            if (entity.Placement == "floor") return entity.Footprint.All(floor.Contains);
            if (entity.Placement == "spaceBoundary")
            {
                if (!spaceBoundaries.TryGetValue(room.Id, out var boundary))
                {
                    exterior ??= ExteriorSpace(project); boundary = [];
                    foreach (var cell in floor) foreach (var direction in Cell.Directions)
                        if (structural.Contains(cell + direction) && exterior.Contains(cell + direction + direction)) boundary.Add(cell + direction);
                    spaceBoundaries[room.Id] = boundary;
                }
                return entity.Footprint.All(boundary.Contains);
            }
            if (!boundaries.TryGetValue(room.Id, out var ordinaryBoundary))
                boundaries[room.Id] = ordinaryBoundary = floor.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Except(floor).ToHashSet();
            return entity.Footprint.All(ordinaryBoundary.Contains);
        };
    }

    public static bool EntityPlacementValid(StationDesignProject project, DesignRoom room, DesignEntity entity) => EntityPlacementValidator(project)(room, entity);

    public static bool RouteValid(StationDesignProject project, DesignRoute route)
    {
        var definitions = RequirementCompiler.Rooms(project.Definition).ToDictionary(item => item.Path, item => item.Room, StringComparer.Ordinal);
        var requirement = RequirementCompiler.Compile(project.Definition).Connections.FirstOrDefault(connection => connection.Id == route.RequirementId);
        return RouteValid(project, route, definitions, ValidInterfaces(project), requirement, NativeUtilityDue(project, route) ? DesignSystems.CreateUtilityRouteInspector(project) : null);
    }

    public static HashSet<DesignRoute> ValidRoutes(StationDesignProject project, bool final = false)
    {
        var definitions = RequirementCompiler.Rooms(project.Definition).ToDictionary(item => item.Path, item => item.Room, StringComparer.Ordinal);
        var ports = ValidInterfaces(project);
        var requirements = RequirementCompiler.Compile(project.Definition).Connections.ToDictionary(connection => connection.Id, StringComparer.Ordinal);
        var inspectUtility = HasDueNativeUtilities(project, final) ? DesignSystems.CreateUtilityRouteInspector(project, final) : null;
        return project.Layout.Routes.Where(route => RouteValid(project, route, definitions, ports, requirements.GetValueOrDefault(route.RequirementId), inspectUtility, final)).ToHashSet();
    }

    private static bool RouteValid(StationDesignProject project, DesignRoute route, Dictionary<string, RoomDefinition> definitions, List<DesignInterface> validPorts, CompiledConnection? requirement = null, Func<DesignRoute, NativeUtilityConnection>? inspectUtility = null, bool final = false)
    {
        if (route.Mode == "utilityNetwork" && route.Kind != "utility") return false;
        var sourceRoom = project.Layout.Rooms.FirstOrDefault(room => room.Id == route.From);
        var targetRoom = project.Layout.Rooms.FirstOrDefault(room => room.Id == route.To);
        if (requirement != null && sourceRoom != null && targetRoom != null && !RoomHierarchy.ConnectionScopeAllowed(requirement, sourceRoom.DefinitionPath, sourceRoom.Id, sourceRoom.ParentInstanceId, targetRoom.DefinitionPath, targetRoom.Id, targetRoom.ParentInstanceId)) return false;
        if (route.Mode is "mail" or "disposalShortcut") return DesignTransport.RouteValid(project, route);
        if (NativeUtilityDue(project, route, final)) return NativeUtilityValid(project, route, inspectUtility ?? DesignSystems.CreateUtilityRouteInspector(project, final));
        if (route.Cells.Count == 0 || route.Cells.Zip(route.Cells.Skip(1)).Any(pair => pair.First.Distance(pair.Second) > 1)) return false;
        if (route.Kind == "walking" && route.Cells.Any(project.Layout.Equipment.Where(entity => DesignSystems.BlocksCrew(project, entity)).SelectMany(entity => entity.Footprint).Concat(DesignTransport.EndpointFloorCells(project)).ToHashSet().Contains)) return false;
        var from = EndpointCells(project, route.From); var to = EndpointCells(project, route.To);
        if (!from.Contains(route.Cells[0]) || !to.Contains(route.Cells[^1])) return false;
        var sourceOwner = project.Layout.Rooms.FirstOrDefault(room => room.Id == route.From)?.OwnerId ?? route.From;
        var targetOwner = project.Layout.Rooms.FirstOrDefault(room => room.Id == route.To)?.OwnerId ?? route.To;
        if (route.Mode == "department" && sourceOwner != targetOwner) return false;
        if (route.Mode == "controlledMaintenance") return DesignControlledMaintenance.RouteValid(project, route);
        if (route.Kind == "utility")
        {
            if (!project.Layout.Utilities.Any(utility => utility.RequirementId == route.RequirementId && utility.From == route.From && utility.To == route.To && utility.Cells.SequenceEqual(route.Cells))) return false;
            var utilityFloor = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && (route.Mode != "department" || room.OwnerId == sourceOwner)).SelectMany(room => room.Cells).ToHashSet();
            if (route.Mode == "utilityNetwork") utilityFloor.UnionWith(project.Layout.PublicCorridors.Concat(project.Layout.Maintenance));
            var utilityPorts = validPorts.Where(port => port.Kind is "walking" or "utility").ToList();
            var allowedUtility = utilityFloor.Concat(utilityPorts.SelectMany(port => port.Cells)).ToHashSet();
            allowedUtility.UnionWith(project.Layout.Walls.Concat(project.Layout.Hull).Where(cell => Cell.Directions.Count(direction => utilityFloor.Contains(cell + direction)) >= 2));
            if (route.Mode == "maintenance") { if (!route.Cells.Any(project.Layout.Maintenance.Contains)) return false; allowedUtility.UnionWith(project.Layout.Maintenance); }
            if (route.Mode == "publicHall") { if (!route.Cells.Any(project.Layout.PublicCorridors.Contains)) return false; allowedUtility.UnionWith(project.Layout.PublicCorridors); }
            if (route.Mode is "mail" or "disposalShortcut") return false;
            if (route.Mode == "direct" && !utilityPorts.Any(port => (port.From == route.From && port.To == route.To || port.From == route.To && port.To == route.From) && route.Cells.Any(port.Cells.Contains))) return false;
            return route.Cells.All(allowedUtility.Contains);
        }
        var allowed = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && definitions.ContainsKey(room.DefinitionPath) &&
                (room.Id == route.From || room.Id == route.To || room.OwnerId == route.From || room.OwnerId == route.To || definitions[room.DefinitionPath].AllowThroughTraffic || project.Layout.Rooms.Any(endpoint => (endpoint.Id == route.From || endpoint.Id == route.To) && DesignNestedGeometry.IsAncestor(project, room.Id, endpoint))) &&
                (route.Mode != "department" || room.OwnerId == sourceOwner))
            .SelectMany(room => room.Cells).ToHashSet();
        var ports = validPorts.Where(port => port.Kind == route.Kind).ToList();
        allowed.UnionWith(ports.SelectMany(port => port.Cells));
        if (route.Mode == "publicHall") { if (!route.Cells.Any(project.Layout.PublicCorridors.Contains)) return false; allowed.UnionWith(project.Layout.PublicCorridors); }
        if (route.Mode == "maintenance") { if (!route.Cells.Any(project.Layout.Maintenance.Contains)) return false; allowed.UnionWith(project.Layout.Maintenance); }
        if (route.Mode is "mail" or "disposalShortcut") return false;
        if (route.Mode == "direct" && !ports.Any(port => (port.From == route.From && port.To == route.To || port.From == route.To && port.To == route.From) && route.Cells.Any(port.Cells.Contains))) return false;
        return route.Cells.All(allowed.Contains);
    }
    private static string UtilityType(StationDesignProject project, DesignRoute route) => project.Layout.Utilities.FirstOrDefault(utility => utility.RequirementId == route.RequirementId && utility.From == route.From && utility.To == route.To)?.Type ?? "";

    public static bool NativeUtilityNetworkPending(StationDesignProject project, CompiledConnection requirement)
    {
        if (requirement.Kind != "utility" || !requirement.Modes.Contains("utilityNetwork")) return false;
        var types = requirement.InterfaceTypes.Where(type => type is "cable" or "pipe").ToList();
        if (types.Count == 0) types = ["cable", "pipe"];
        return !project.Definition.Systems.Enabled || !types.Any(type => project.Definition.Systems.Networks.Any(network => network.Kind == (type == "cable" ? "power" : "gas")) &&
            DesignStages.Index(project.Stage) >= DesignStages.Index(type == "cable" ? "power" : "atmos"));
    }

    private static bool NativeUtilityDue(StationDesignProject project, DesignRoute route, bool final = false)
    {
        if (route.Kind != "utility" || !project.Definition.Systems.Enabled) return false;
        var kind = UtilityType(project, route) switch { "cable" => "power", "pipe" => "gas", _ => "" };
        var stage = kind == "power" ? "power" : "atmos";
        return kind.Length > 0 && project.Definition.Systems.Networks.Any(network => network.Kind == kind) && (final || DesignStages.Index(project.Stage) >= DesignStages.Index(stage));
    }

    private static bool HasDueNativeUtilities(StationDesignProject project, bool final = false) => project.Layout.Routes.Any(route => NativeUtilityDue(project, route, final));

    private static bool NativeUtilityValid(StationDesignProject project, DesignRoute route, Func<DesignRoute, NativeUtilityConnection> inspect)
    {
        var observed = inspect(route);
        return observed.Verified && observed.Cells.Count > 0;
    }
}
