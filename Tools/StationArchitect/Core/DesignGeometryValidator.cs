using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Content.StationArchitect;

/// <summary>Read-only checks over actual geometry. A previously saved acceptance report is never authoritative.</summary>
public static class DesignGeometryValidator
{
    public static DesignReport Validate(StationDesignProject project, bool final = false, bool includeGameflow = true, bool refreshReports = false)
    {
        DesignSystems.TraceNative("geometry.begin " + project.Stage);
        var report = new DesignReport { Stage = project.Stage };
        // Analysis is read-only by default. Coordinators may publish these independently
        // recomputed observations on their private draft clone.
        if (refreshReports)
        {
            if (includeGameflow) project.Gameflow = null;
            project.Systems = new();
        }
        var compiled = RequirementCompiler.Compile(project.Definition);
        report.Diagnostics.AddRange(compiled.Diagnostics);
        if (!compiled.Success) return report;
        report.Diagnostics.AddRange(DesignTransport.Validate(project));
        var definitions = RequirementCompiler.Rooms(project.Definition).ToDictionary(item => item.Path, item => item.Room, StringComparer.Ordinal);
        var stage = DesignStages.Index(project.Stage);
        var grown = final || stage >= DesignStages.Index("growth");
        var circulated = final || stage >= DesignStages.Index("circulation");
        var crewJumps = circulated ? DesignCrewTraversal.Jumps(project) : new Dictionary<Cell, List<Cell>>();
        if (grown) report.Diagnostics.AddRange(DesignDockGeometry.Validate(project, final || stage >= DesignStages.Index("contents")));
        var bounds = new GridBounds(0, 0, project.Settings.Width, project.Settings.Height);
        void Error(string path, string code, string message) => report.Diagnostics.Add(new(path, code, message));
        var rooms = project.Layout.Rooms.Where(room => room.Enabled).ToList();
        foreach (var stale in project.Layout.Rooms.Where(room => room.Orphaned || !room.Enabled && room.Cells.Count > 0))
            Error(stale.Id, "orphaned-instance", "Retained inactive or orphaned room floor must be reconciled explicitly before this design can be accepted.");
        if (rooms.Count > 4096 || project.Layout.Envelope.Count > 1048576)
        { Error("layout", "geometry-limit", "The design exceeds the supported room or envelope-cell bound."); return report; }
        var ids = new HashSet<string>(StringComparer.Ordinal); var ownership = new Dictionary<Cell, string>();
        var envelope = project.Layout.Envelope.ToHashSet();
        foreach (var room in rooms)
        {
            if (!ids.Add(room.Id)) Error(room.Id, "duplicate-instance", "Room-instance IDs must be unique.");
            if (room.Orphaned || !definitions.TryGetValue(room.DefinitionPath, out var definition))
            { Error(room.Id, "orphaned-instance", "This retained room has no current requirement definition. Reconcile it explicitly before export."); continue; }
            if (room.OwnerId != RequirementCompiler.Owner(room.DefinitionPath) || room.Id != RoomHierarchy.InstanceId(room.DefinitionPath, room.Instance, room.ParentInstanceId))
                Error(room.Id, "instance-reference", "The instance ID, owner and definition path do not agree.");
            var floor = room.Cells.ToHashSet();
            if (floor.Count != room.Cells.Count) Error(room.Id, "duplicate-floor", "Room floor contains duplicate cells.");
            foreach (var cell in floor)
            {
                if (!bounds.Contains(cell)) Error(room.Id, "outside-canvas", "Room floor extends beyond the configured canvas.");
                if (!ownership.TryAdd(cell, room.Id)) Error(room.Id, "room-overlap", $"Floor overlaps {ownership[cell]} at {cell.X},{cell.Y}.");
            }
            if (floor.Count == 0) { if (grown) Error(room.Id, "missing-floor", "This selected room has no grown floor."); continue; }
            if (DesignMeasurements.Components(floor) != 1) Error(room.Id, "room-connectivity", "A room instance must be one connected floor component.");
            if (definition.Environment.Placement == "interior" && floor.Any(cell => !envelope.Contains(cell))) Error(room.Id, "interior-placement", "Interior room floor is outside the station envelope.");
            if (definition.Environment.Placement == "exterior" && floor.Any(envelope.Contains)) Error(room.Id, "exterior-placement", "An exterior room must be outside the interior station envelope.");
            var minimum = RequirementCompiler.EffectiveMinimumArea(definition);
            if (floor.Count > definition.Area.Max || grown && floor.Count < minimum) Error(room.Id, "room-area", $"Owned usable floor {floor.Count} must be within {minimum}–{definition.Area.Max}.");
            if (grown)
            {
                var shapeFloor = definition.Rooms.Count > 0 ? DesignNestedGeometry.ComplexCells(project, room) : floor;
                var size = DesignMeasurements.Bounds(shapeFloor);
                var aspect = Math.Max((double) size.Width / size.Height, (double) size.Height / size.Width);
                var fill = (double) shapeFloor.Count / size.Area;
                var shape = definition.ComplexShape ?? definition.Shape;
                if (aspect > shape.MaxAspectRatio + 1e-9 || fill < shape.MinFillRatio - 1e-9)
                    Error(room.Id, "room-shape", $"Actual floor aspect {aspect:0.###} and fill {fill:0.###} violate the room's shape bounds.");
            }
        }
        report.Diagnostics.AddRange(DesignNestedGeometry.Validate(project, grown, circulated, definitions, crewJumps: crewJumps));
        var layers = new Dictionary<string, List<Cell>>
        {
            ["public"] = project.Layout.PublicCorridors, ["maintenance"] = project.Layout.Maintenance,
            ["walls"] = project.Layout.Walls, ["hull"] = project.Layout.Hull, ["windows"] = project.Layout.Windows
        };
        foreach (var (name, cells) in layers)
        {
            if (cells.Count != cells.Distinct().Count()) Error("layout." + name, "duplicate-layer", "A physical layer contains repeated cells.");
            foreach (var cell in cells)
            {
                if (!bounds.Contains(cell)) Error("layout." + name, "outside-canvas", "A physical layer extends beyond the canvas.");
                if (!ownership.TryAdd(cell, "$" + name)) Error("layout." + name, "layer-overlap", $"Physical {name} overlaps {ownership[cell]} at {cell.X},{cell.Y}.");
                if (name == "public" && !envelope.Contains(cell)) Error("layout.public", "public-exterior", "Public corridors must remain inside the station envelope.");
            }
        }
        if (project.Layout.ReservedSpace.Any(ownership.ContainsKey)) Error("layout.reservedSpace", "reserved-space-overlap", "A reserved exterior-space cut is occupied by physical floor or structural material.");
        var interfaces = new HashSet<string>(StringComparer.Ordinal); var interfaceCells = new HashSet<Cell>();
        foreach (var port in project.Layout.Interfaces)
        {
            if (!interfaces.Add(port.Id)) Error(port.Id, "duplicate-interface", "Interface IDs must be unique.");
            if (!DesignMeasurements.InterfaceTouches(project, port)) Error(port.Id, "interface-contact", "An interface must physically touch both declared endpoint floors.");
            if (port.Cells.Count == 0 || port.Cells.Any(cell => !bounds.Contains(cell))) Error(port.Id, "interface-geometry", "An interface needs actual in-bounds boundary cells.");
            if (port.Cells.Select(cell => cell.X).Distinct().Count() > 1 && port.Cells.Select(cell => cell.Y).Distinct().Count() > 1)
                Error(port.Id, "interface-straight", "A doorway or shared interface must form one straight boundary run.");
            if (port.Width != port.Cells.Distinct().Count()) Error(port.Id, "interface-width", "Declared interface width differs from its actual boundary run.");
            foreach (var cell in port.Cells)
            {
                if (port.Kind != "utility" && !interfaceCells.Add(cell)) Error(port.Id, "interface-overlap", "Two physical interfaces occupy the same boundary cell.");
                if (port.Kind != "utility" && ownership.ContainsKey(cell)) Error(port.Id, "interface-overlap", "An interface overlaps floor or structural material instead of replacing a boundary tile.");
            }
            if (port.Kind == "walking" && port.Type is "counter" or "window" or "pipe" or "cable") Error(port.Id, "nonwalking-interface", "This physical interface cannot carry crew movement.");
        }
        if (circulated)
        {
            CheckPassages(project, report);
            var validGateways = DesignMeasurements.ValidInterfaces(project).Where(port => port.Kind == "walking" && port.Type is "doorway" or "airlock" or "dockingPort").ToList();
            foreach (var cluster in DesignClusterGeometry.Clusters(project))
            foreach (var network in new[] { "$public", "$maintenance" })
            {
                if (network == "$public" && cluster[0].OwnerId == RequirementCompiler.MaintenanceOwner) continue;
                var eligible = cluster.Any(room => DesignNestedGeometry.EntryAllowed(project, room, null, definitions) && (network == "$public" ? definitions[room.DefinitionPath].Access.AllowPublicEntry : definitions[room.DefinitionPath].Access.AllowMaintenanceEntry));
                if (!eligible) continue;
                var clusterRoomIds = cluster.Select(room => room.Id).ToHashSet(StringComparer.Ordinal);
                bool MatchesNetwork(string endpoint) => endpoint == network || network == "$maintenance" && endpoint == "$exterior-maintenance";
                if (!validGateways.Any(port => clusterRoomIds.Contains(port.From) && MatchesNetwork(port.To) || clusterRoomIds.Contains(port.To) && MatchesNetwork(port.From)))
                    report.Diagnostics.Add(new(cluster[0].Id, "network-gateway", $"This eligible cluster has no actual direct {network[1..]} entrance. Automatic gateway placement remains unfinished; authored Required connections are checked separately.", "warning"));
            }
            if (project.Layout.PublicWaypoints.Count > 0)
            {
                var backbone = project.Layout.PublicBackbone; var publicFloor = project.Layout.PublicCorridors.ToHashSet();
                if (backbone.Count == 0 || backbone.Zip(backbone.Skip(1)).Any(pair => pair.First.Distance(pair.Second) != 1) || backbone.Any(cell => !DesignRouting.Stamp(cell, project.Settings.PublicWidth).All(publicFloor.Contains)))
                    Error("layout.public", "backbone-geometry", "The topology backbone is missing or no longer has a continuous full-width physical passage.");
                else if (project.Settings.Topology == "ring" && (backbone.Count < 8 || backbone[0] != backbone[^1]))
                    Error("layout.public", "ring-open", "The public ring backbone is not a closed physical loop.");
                else if (project.Settings.Topology == "ring" && (backbone.Take(backbone.Count - 1).Distinct().Count() != backbone.Count - 1 || Math.Abs(backbone.Zip(backbone.Skip(1)).Sum(pair => (long) pair.First.X * pair.Second.Y - (long) pair.Second.X * pair.First.Y)) < 2L * project.Settings.PublicWidth * project.Settings.PublicWidth))
                    Error("layout.public", "ring-degenerate", "The recorded ring must enclose area through a simple physical loop; an out-and-back walk is insufficient.");
            }
            var maintenance = project.Layout.Maintenance.ToHashSet();
            if (project.Layout.PublicCorridors.Any(cell => Cell.Directions.Any(direction => maintenance.Contains(cell + direction))))
                Error("layout", "network-separation", "Public and maintenance floors touch without a separating wall or controlled interface.");
            foreach (var room in rooms.Where(room => !room.Orphaned && definitions.ContainsKey(room.DefinitionPath)))
            foreach (var cell in room.Cells)
            foreach (var direction in Cell.Directions)
            {
                var neighbor = cell + direction;
                if (ownership.TryGetValue(neighbor, out var other) && (other == "$public" || other == "$maintenance" || rooms.Any(candidate => candidate.Id == other && candidate.OwnerId != room.OwnerId)))
                    Error(room.Id, "uncontrolled-entrance", $"Floor touches {other} directly at {neighbor.X},{neighbor.Y} without a boundary interface.");
            }
            var validRoutes = DesignMeasurements.ValidRoutes(project, final);
            foreach (var route in project.Layout.Routes)
                if (!validRoutes.Contains(route)) Error(route.Id, "route-geometry", "This route does not follow its endpoints, permitted floor, interfaces and actual route network.");
        }
        var equipmentCells = new Dictionary<Cell, List<DesignEntity>>();
        var workingCells = new Dictionary<Cell, string>();
        DesignSystems.TraceNative("geometry.equipment.begin count=" + project.Layout.Equipment.Count);
        var resolveServiceRoom = DesignSystems.RoomResolver(project);
        var resolveSystemContent = DesignSystems.ContentRequirementResolver(project);
        var entityPlacementValid = DesignMeasurements.EntityPlacementValidator(project);
        foreach (var entity in project.Layout.Equipment)
        {
            var room = rooms.FirstOrDefault(room => room.Id == entity.RoomId) ?? (entity.SystemRuleId.Length > 0 ? resolveServiceRoom(entity.RoomId) : null);
            if (room == null || !entityPlacementValid(room, entity)) Error(entity.Id, "equipment-placement", "Equipment does not fit the declared floor/boundary placement of its owning room.");
            if (!DesignEquipmentGeometry.TryExpectedFootprint(project.Settings, entity.Prototype, entity.Position, entity.Rotation, out var expected) || entity.Footprint.Count != expected.Count || !entity.Footprint.ToHashSet().SetEquals(expected))
                Error(entity.Id, "equipment-footprint", "Placed equipment does not match its configured prototype footprint and quarter-turn rotation.");
            if (room != null)
            {
                var requirement = resolveSystemContent(entity) ?? (definitions.TryGetValue(room.DefinitionPath, out var roomDefinition) ? roomDefinition.Contents.FirstOrDefault(content => content.Id == entity.RequirementId) : null);
                var work = entity.WorkArea.ToHashSet();
                var minimumWork = DesignSystems.MinimumWorkArea(project, entity, requirement?.MinArea ?? 0);
                if (work.Count != entity.WorkArea.Count || DesignSystems.BlocksCrew(project, entity) && !entity.Footprint.All(work.Contains) || work.Any(cell => !room.Cells.Contains(cell)) || work.Count < minimumWork)
                    Error(entity.Id, "equipment-working-area", "Equipment needs actual reserved working floor within its room, including its crew-blocking footprint and configured minimum area.");
                foreach (var cell in work)
                    if (!workingCells.TryAdd(cell, entity.Id)) Error(entity.Id, "equipment-working-overlap", $"Working floor overlaps the reservation of {workingCells[cell]}.");
            }
            foreach (var cell in entity.Footprint)
            {
                if (!equipmentCells.TryGetValue(cell, out var occupants)) equipmentCells[cell] = occupants = [];
                foreach (var other in occupants)
                    if (!DesignSystems.CanShareCell(project, entity, other)) Error(entity.Id, "equipment-overlap", $"Equipment footprint overlaps incompatible occupancy of {other.Id}.");
                occupants.Add(entity);
            }
        }
        foreach (var entity in project.Layout.Equipment)
            if (entity.WorkArea.Any(cell => equipmentCells.TryGetValue(cell, out var others) && others.Any(other => other.Id != entity.Id && DesignSystems.BlocksCrew(project, other))))
                Error(entity.Id, "equipment-working-obstruction", "Another entity occupies reserved working floor.");
        foreach (var group in project.Layout.Equipment.Where(entity => entity.InterfaceId.Length > 0).GroupBy(entity => entity.InterfaceId))
        {
            var port = project.Layout.Interfaces.FirstOrDefault(port => port.Id == group.Key);
            var covered = group.SelectMany(entity => entity.Footprint).ToHashSet();
            if (port == null || !port.Cells.All(covered.Contains)) Error(group.Key, "interface-equipment-width", "The physical interface is not covered by its associated equipment footprints.");
        }
        DesignSystems.TraceNative("geometry.measurements.begin");
        var observations = DesignMeasurements.Build(project, final, crewJumps);
        report.Pressure = PressureEvaluator.Evaluate(project.Definition, observations, final);
        if (!circulated)
        {
            var componentDiagnostics = report.Pressure.Diagnostics.Where(diagnostic => diagnostic.Code is "owner-components" or "owner-split").ToList();
            report.Pressure.Diagnostics.RemoveAll(diagnostic => componentDiagnostics.Contains(diagnostic));
            if (componentDiagnostics.Count > 0) report.Pressure.Diagnostics.Add(new("layout", "owner-connections-pending", "Owner floor components are measured and contribute split cost; their required door connections are evaluated at the circulation stage.", "info"));
        }
        if (!final && stage < DesignStages.Index("contents"))
        {
            var pending = report.Pressure.Diagnostics.Where(diagnostic => diagnostic.Severity == "warning" && diagnostic.Code is "environment-measurement" or "content-measurement" or "entry-owner-measurement" or "temperature-measurement").ToList();
            report.Pressure.Diagnostics.RemoveAll(diagnostic => pending.Contains(diagnostic));
            if (pending.Count > 0) report.Pressure.Diagnostics.Add(new("layout", "observations-pending", $"{pending.Count} environment, equipment or entrance observations are pending their later stage; unbuilt features are not counted as fulfilled.", "info"));
        }
        report.Diagnostics.AddRange(report.Pressure.Diagnostics);
        if (circulated) CheckRequiredRoutes(project, compiled, observations, report, final);
        if (project.Layout.EnvironmentZones.Count > 0)
        {
            foreach (var compartment in DesignMeasurements.AirCompartments(project))
            {
                var zones = project.Layout.EnvironmentZones.Where(zone => zone.Cells.Any(compartment.Cells.Contains)).ToList();
                if (zones.Select(zone => zone.Pressure).Distinct(StringComparer.Ordinal).Count() > 1 || zones.Where(zone => zone.PressureKpa != null).Select(zone => Math.Round(zone.PressureKpa!.Value, 2)).Distinct().Count() > 1 || zones.Where(zone => zone.TemperatureKelvin != null).Select(zone => Math.Round(zone.TemperatureKelvin!.Value, 2)).Distinct().Count() > 1)
                    Error("environment", "open-compartment-conflict", "An open air-connected compartment contains incompatible initial plans: " + string.Join("; ", zones.Take(8).Select(zone => $"{zone.Id} ({zone.Pressure}, {zone.PressureKpa?.ToString("0.###") ?? "unspecified"} kPa, {zone.TemperatureKelvin?.ToString("0.##") ?? "unspecified"} K)")) + ". Add an airtight separator or reconcile those plans.");
            }
            report.Diagnostics.Add(new("environment", "simulation-unverified", "Environment observations describe the explicit initial map plan. SS14 atmosphere, power and machine operation have not been simulated by this editor.", "info"));
        }
        report.Diagnostics = report.Diagnostics.Distinct().ToList();
        if (final && report.Success && !report.Pressure.Complete)
            report.Diagnostics.Add(new("$station", "soft-preferences", "All mandatory geometry checks passed. Some Desired/Avoided preferences remain unsatisfied or unproven; their pressure costs remain in the report.", "info"));
        report.Complete = report.Success && (final || report.Pressure.Complete);
        report.Metrics = new()
        {
            ["rooms"] = rooms.Count, ["roomFloor"] = rooms.Sum(room => room.Cells.Count),
            ["publicFloor"] = project.Layout.PublicCorridors.Count, ["maintenanceFloor"] = project.Layout.Maintenance.Count,
            ["interfaces"] = project.Layout.Interfaces.Count, ["routes"] = project.Layout.Routes.Count,
            ["equipment"] = project.Layout.Equipment.Count, ["pressureCost"] = report.Pressure.Cost
        };
        if (circulated)
        {
            var serviceComponents = DesignMaintenanceConnectivity.Measure(project);
            var componentPolicy = project.Definition.Maintenance.Connectivity;
            var componentCost = Math.Max(0, serviceComponents.Count - 1) * componentPolicy.ComponentPenalty;
            report.Metrics["maintenanceComponents"] = serviceComponents.Count;
            report.Metrics["maintenanceStationTransitions"] = serviceComponents.Sum(component => component.Transitions.Count);
            report.Metrics["maintenanceComponentPenalty"] = componentCost;
            report.Pressure.Terms.Add(new("maintenance", "components", serviceComponents.Count, 1, componentPolicy.ComponentPenalty, componentCost));
            report.Metrics["pressureCost"] = report.Pressure.Cost;
            var maintenance = DesignClusterGeometry.MeasureMaintenance(project);
            report.Metrics["physicalClusters"] = maintenance.Count;
            report.Metrics["maintenanceBoundaryEdges"] = maintenance.Sum(item => item.BoundaryEdges);
            report.Metrics["maintenanceCoveredEdges"] = maintenance.Sum(item => item.CoveredEdges);
            report.Metrics["maintenanceCoverage"] = maintenance.Sum(item => item.BoundaryEdges) == 0 ? 1 : (double) maintenance.Sum(item => item.CoveredEdges) / maintenance.Sum(item => item.BoundaryEdges);
            report.Metrics["maintenanceClusterConnections"] = maintenance.Sum(item => item.Connections);
            foreach (var item in maintenance)
            {
                if (item.Coverage + 1e-9 < project.Settings.Packing.MaintenanceCoverage)
                    report.Diagnostics.Add(new(item.ClusterId, "maintenance-coverage", $"Measured maintenance coverage is {item.Coverage:P0}; configured target is {project.Settings.Packing.MaintenanceCoverage:P0}.", "warning"));
                var cluster = DesignClusterGeometry.Clusters(project).First(group => group[0].Id == item.ClusterId);
                if (cluster.Any(room => definitions[room.DefinitionPath].Access.AllowMaintenanceEntry && DesignNestedGeometry.EntryAllowed(project, room, null, definitions)) && item.Connections < project.Settings.Packing.MaintenanceConnectionsPerCluster)
                    report.Diagnostics.Add(new(item.ClusterId, "maintenance-connections", $"This physical cluster has {item.Connections} maintenance entrances; configured target is {project.Settings.Packing.MaintenanceConnectionsPerCluster}.", "warning"));
            }
        }
        // Selection/import may regroup instances without changing their IDs or physical state.
        // Hash a private canonical view so list ordering does not invalidate a saved design.
        var canonicalLayout = ProgramJson.Clone(project.Layout);
        canonicalLayout.Rooms = canonicalLayout.Rooms.OrderBy(room => room.Id, StringComparer.Ordinal).ToList();
        report.Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { project.Definition, project.Settings, project.Selection, Layout = canonicalLayout }, ProgramJson.Options)))).ToLowerInvariant();
        if (includeGameflow && (final || stage >= DesignStages.Index("gameflow")))
        {
            var gameflow = GameflowEvaluator.Evaluate(project);
            if (refreshReports) project.Gameflow = gameflow;
            report.Diagnostics = report.Diagnostics.Concat(gameflow.Diagnostics).Distinct().ToList();
            report.Metrics["gameflowCost"] = gameflow.TotalCost;
            report.Metrics["gameflowObservedRules"] = gameflow.Rules.Count(rule => rule.Observed);
            report.Metrics["gameflowRules"] = gameflow.Rules.Count;
            report.Metrics["generationCost"] = (report.Pressure?.Cost ?? 0) + gameflow.TotalCost;
            report.Complete &= report.Success && gameflow.Complete;
        }
        var systems = DesignSystems.Validate(project, final);
        if (refreshReports) project.Systems = systems;
        report.Diagnostics = report.Diagnostics.Concat(systems.Diagnostics).Distinct().ToList();
        foreach (var (key, value) in systems.Metrics) report.Metrics["systems." + key] = value;
        report.Complete &= report.Success && systems.Complete;
        DesignSystems.TraceNative("geometry.end " + project.Stage);
        return report;
    }

    private static void CheckPassages(StationDesignProject project, DesignReport report)
    {
        foreach (var (name, list, width) in new[] { ("public", project.Layout.PublicCorridors, project.Settings.PublicWidth), ("maintenance", project.Layout.Maintenance, project.Settings.MaintenanceWidth) })
        {
            var cells = list.ToHashSet();
            var connected = new HashSet<Cell>(cells);
            if (name == "maintenance") connected.UnionWith(project.Layout.Interfaces.Where(port => port.Kind == "walking" && port.Type == "airlock" &&
                (port.From == "$maintenance" && port.To == "$exterior-maintenance" || port.To == "$maintenance" && port.From == "$exterior-maintenance") && DesignMeasurements.InterfaceTouches(project, port)).SelectMany(port => port.Cells));
            if (name == "maintenance" && project.Definition.Maintenance.Connectivity.Policy == "controlled")
            {
                var measured = DesignMaintenanceConnectivity.Measure(project); var policy = project.Definition.Maintenance.Connectivity;
                if (measured.Count > policy.MaxComponents) report.Diagnostics.Add(new("layout.maintenance", "network-connectivity", $"Maintenance has {measured.Count} separate physical floor components; configured maximum is {policy.MaxComponents}."));
                foreach (var component in measured.Where(component => component.Transitions.Count < policy.MinTransitionsPerComponent))
                    report.Diagnostics.Add(new(component.Id, "maintenance-station-access", $"This physical maintenance component has {component.Transitions.Count} verified station transitions; configured minimum is {policy.MinTransitionsPerComponent}."));
            }
            else if (cells.Count > 0 && DesignMeasurements.Components(connected) != 1) report.Diagnostics.Add(new("layout." + name, "network-connectivity", "The passage network has disconnected floor components."));
            foreach (var cell in cells)
            {
                var covered = false;
                for (var y = 1 - width; y <= 0 && !covered; y++) for (var x = 1 - width; x <= 0 && !covered; x++)
                    covered = Enumerable.Range(0, width).All(dy => Enumerable.Range(0, width).All(dx => cells.Contains(cell + new Cell(x + dx, y + dy))));
                if (!covered) { report.Diagnostics.Add(new("layout." + name, "passage-min-width", $"A {name} passage narrows below {width} clear tiles near {cell.X},{cell.Y}.")); break; }
            }
            if (name == "maintenance" && DesignRouting.HasOpenSquare(cells, project.Settings.MaintenanceMaxWidth + 1))
                report.Diagnostics.Add(new("layout.maintenance", "passage-max-width", $"Maintenance contains an open {project.Settings.MaintenanceMaxWidth + 1}-tile square; enlarge a neighboring room or declare an enclosed maintenance room instead."));
        }
    }

    private static void CheckRequiredRoutes(StationDesignProject project, ProgramCompilation compiled, PressureState state, DesignReport report, bool final)
    {
        var activeTemplates = state.Rooms.Where(room => room.Area > 0).GroupBy(room => room.Path).ToDictionary(group => group.Key, group => group.Select(RoomHierarchy.MeasurementKey).ToList(), StringComparer.Ordinal);
        foreach (var owner in project.Definition.Departments.Select(department => department.Id).Append(RequirementCompiler.MaintenanceOwner))
            if (state.Rooms.Any(room => RequirementCompiler.Owner(room.Path) == owner && room.Area > 0)) activeTemplates[owner] = [owner];
        var pendingNative = compiled.Connections.Where(connection => connection.Priority == "required" && DesignMeasurements.NativeUtilityNetworkPending(project, connection)).Select(connection => connection.Id).ToHashSet(StringComparer.Ordinal);
        if (!final && pendingNative.Count > 0) report.Diagnostics.Add(new("systems", "native-utility-pending", "Required utilityNetwork circuits await their native power/atmos stage and actual source-served port evidence; planning lines are not counted as fulfilled.", "info"));
        foreach (var connection in compiled.Connections.Where(connection => connection.Priority == "required" && (final || !pendingNative.Contains(connection.Id))))
        foreach (var source in activeTemplates.GetValueOrDefault(connection.From) ?? [])
        {
            var groups = connection.Selection == "all" ? connection.To.Select(target => new List<string> { target }).ToList() : [connection.To];
            foreach (var group in groups)
            {
                var targets = group.SelectMany(target => activeTemplates.GetValueOrDefault(target) ?? []).ToHashSet(StringComparer.Ordinal);
                if (targets.Count == 0 && connection.Presence == "ifPresent") continue;
                if (!state.Connections.Any(measurement => measurement.Id == connection.Id && measurement.SourceInstance == source && targets.Contains(measurement.TargetInstance) && connection.Modes.Contains(measurement.Mode) && measurement.Kind == connection.Kind &&
                    (connection.MaxDistance == null || measurement.Distance <= connection.MaxDistance) && (connection.InterfaceTypes.Count == 0 || connection.InterfaceTypes.Contains(measurement.InterfaceType)) && measurement.Width >= connection.MinWidth))
                    report.Diagnostics.Add(new(connection.Id + "@" + source, "required-route", $"Required {connection.Kind} connection to [{string.Join(", ", group)}] has no valid physical route/interface."));
            }
        }
    }
}
