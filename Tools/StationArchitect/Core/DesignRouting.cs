namespace Content.StationArchitect;

/// <summary>Constructs physical interfaces and separated public/service networks from actual room floor.</summary>
public static class DesignRouting
{
    private sealed record PortCandidate(DesignRoom Room, Cell Wall, Cell Normal, List<Cell> Door, List<Cell> Throat);

    public static StationDesignProject Run(StationDesignProject source)
    {
        var project = ProgramJson.Clone(source);
        var compiled = RequirementCompiler.Compile(project.Definition);
        var diagnostics = new List<ProgramDiagnostic>(compiled.Diagnostics);
        if (!compiled.Success) { project.Report = new() { Stage = "circulation", Diagnostics = diagnostics }; return project; }
        diagnostics.AddRange(DesignTransport.ValidateConfiguration(project));
        if (diagnostics.Any(diagnostic => diagnostic.Severity == "error")) { project.Report = new() { Stage = "circulation", Diagnostics = diagnostics }; return project; }
        var definitions = RequirementCompiler.Rooms(project.Definition).ToDictionary(item => item.Path, item => item.Room, StringComparer.Ordinal);
        var rooms = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && room.Cells.Count > 0 && definitions.ContainsKey(room.DefinitionPath)).OrderBy(room => room.Id, StringComparer.Ordinal).ToList();
        if (rooms.Count == 0) { diagnostics.Add(new("layout", "missing-floor", "Grow rooms before constructing circulation.")); project.Report = new() { Stage = "circulation", Diagnostics = diagnostics }; return project; }
        var pinnedInterfaces = project.Layout.Equipment.Where(entity => entity.Pinned && entity.InterfaceId.Length > 0).Select(entity => entity.InterfaceId).ToHashSet(StringComparer.Ordinal);
        bool EditedNetworkPort(DesignInterface port) =>
            (project.Layout.PublicEdited && (port.From == "$public" || port.To == "$public") || project.Layout.MaintenanceEdited && (port.From == "$maintenance" || port.To == "$maintenance" || port.From == "$exterior-maintenance" || port.To == "$exterior-maintenance")) && DesignMeasurements.InterfaceTouches(project, port);
        project.Layout.Interfaces.RemoveAll(port => !port.Edited && !pinnedInterfaces.Contains(port.Id) && !EditedNetworkPort(port));
        project.Layout.Routes.RemoveAll(route => !route.Edited && !route.Locked);
        var retainedTransport = project.Layout.Routes.Where(route => route.Edited || route.Locked).Select(route => route.TransportNetworkId).ToHashSet(StringComparer.Ordinal);
        project.Layout.TransportNetworks.RemoveAll(network => !network.Edited && !network.Locked && !retainedTransport.Contains(network.Id));
        project.Layout.Utilities.RemoveAll(utility => !utility.Edited);
        if (!project.Layout.PublicEdited) project.Layout.PublicCorridors.Clear();
        if (!project.Layout.MaintenanceEdited) project.Layout.Maintenance.Clear();

        // Internal floor remains owned by rooms. Door tiles are structural, not anonymous department floor.
        foreach (var owner in rooms.GroupBy(room => room.OwnerId))
        {
            var list = owner.ToList();
            for (var a = 0; a < list.Count; a++) for (var b = a + 1; b < list.Count; b++)
            {
                var first = list[a]; var second = list[b];
                var parentChild = first.ParentInstanceId == second.Id || second.ParentInstanceId == first.Id;
                if (!parentChild && !definitions[first.DefinitionPath].AllowThroughTraffic && !definitions[second.DefinitionPath].AllowThroughTraffic) continue;
                if (!DesignNestedGeometry.EntryAllowed(project, first, second, definitions) || !DesignNestedGeometry.EntryAllowed(project, second, first, definitions)) continue;
                if (!EntryAllowed(definitions[first.DefinitionPath], second.OwnerId) || !EntryAllowed(definitions[second.DefinitionPath], first.OwnerId)) continue;
                var requirements = compiled.Connections.Where(connection => connection.Priority == "required" && connection.Kind == "walking" &&
                    (connection.From == first.DefinitionPath || connection.From == second.DefinitionPath || connection.To.Contains(first.DefinitionPath) || connection.To.Contains(second.DefinitionPath))).ToList();
                var width = Math.Max(1, requirements.Select(connection => connection.MinWidth).DefaultIfEmpty(1).Max());
                var type = requirements.Any(connection => connection.InterfaceTypes.Contains("airlock")) ? "airlock" : "doorway";
                AddSharedInterface(project, first, second, "walking", type, width, "");
            }
        }
        foreach (var connection in compiled.Connections.Where(connection => connection.Kind == "interaction" || connection.Modes.Contains("direct")))
        foreach (var from in Resolve(rooms, connection.From))
        foreach (var target in connection.To)
        foreach (var to in Resolve(rooms, target))
        {
            if (!RoomHierarchy.ConnectionScopeAllowed(connection, from.DefinitionPath, from.Id, from.ParentInstanceId, to.DefinitionPath, to.Id, to.ParentInstanceId)) continue;
            if (connection.Kind == "walking" && (!EntryAllowed(definitions[from.DefinitionPath], to.OwnerId) || !EntryAllowed(definitions[to.DefinitionPath], from.OwnerId) || !DesignNestedGeometry.EntryAllowed(project, from, to, definitions) || !DesignNestedGeometry.EntryAllowed(project, to, from, definitions))) continue;
            var type = connection.InterfaceTypes.FirstOrDefault(candidate => connection.Kind != "walking" || candidate is "doorway" or "airlock" or "dockingPort") ?? (connection.Kind == "interaction" ? "counter" : "doorway");
            AddSharedInterface(project, from, to, connection.Kind, type, Math.Max(1, connection.MinWidth), connection.Id);
        }

        // Reserve only the public cycle first. Service access precedes the public
        // branches so those branches cannot box a whole department out of maintenance.
        // Delay secondary service entries and wrapping until public access also exists.
        var publicApproaches = new HashSet<Cell>();
        var publicAnchors = new HashSet<Cell>();
        BuildNetwork(project, rooms, definitions, "$public", project.Settings.PublicWidth, diagnostics, backboneOnly: true, reservedApproaches: publicApproaches, publicAnchors: publicAnchors);
        BuildNetwork(project, rooms, definitions, "$maintenance", project.Settings.MaintenanceWidth, [], primaryOnly: true, reservedApproaches: publicApproaches, publicAnchors: publicAnchors);
        var networkDiagnostics = new List<ProgramDiagnostic>();
        BuildNetwork(project, rooms, definitions, "$public", project.Settings.PublicWidth, networkDiagnostics, skipBackbone: true);
        var canNegotiate = !project.Layout.PublicEdited && !project.Layout.MaintenanceEdited && !project.Layout.Interfaces.Any(port => port.Edited) && !project.Layout.Routes.Any(route => route.Edited || route.Locked) && !project.Layout.Equipment.Any(entity => entity.Pinned) && !project.Layout.Docks.Any(dock => dock.Edited || dock.Locked) && !project.Layout.CirculationReservations.Any(reservation => reservation.Edited || reservation.Locked);
        for (var attempt = 0; attempt < 2 && canNegotiate; attempt++)
        {
            var trialReservations = publicApproaches.ToHashSet();
            if (!ReserveStrandedPublicPaths(project, rooms, definitions, trialReservations)) break;
            // A legal doorway alone can still be isolated by a service branch. Negotiate
            // a real full-width escape, then commit only if existing network and
            // authored route capabilities survive. Failed trials leave the draft intact.
            var previousMaintenance = GatewayClusters(project, "$maintenance");
            var previousPublic = GatewayClusters(project, "$public");
            var previousRequired = RequiredNetworkCapabilities(project, rooms, definitions, compiled.Connections);
            var trial = ProgramJson.Clone(project);
            trial.Layout.Maintenance.Clear();
            trial.Layout.Interfaces.RemoveAll(port => port.From is "$maintenance" or "$exterior-maintenance" || port.To is "$maintenance" or "$exterior-maintenance");
            var trialRooms = trial.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && room.Cells.Count > 0 && definitions.ContainsKey(room.DefinitionPath)).OrderBy(room => room.Id, StringComparer.Ordinal).ToList();
            var trialDiagnostics = new List<ProgramDiagnostic>();
            BuildNetwork(trial, trialRooms, definitions, "$maintenance", trial.Settings.MaintenanceWidth, [], primaryOnly: true, reservedApproaches: trialReservations, publicAnchors: publicAnchors);
            BuildNetwork(trial, trialRooms, definitions, "$public", trial.Settings.PublicWidth, trialDiagnostics, skipBackbone: true);
            var nextMaintenance = GatewayClusters(trial, "$maintenance");
            var nextPublic = GatewayClusters(trial, "$public");
            var nextRequired = RequiredNetworkCapabilities(trial, trialRooms, definitions, compiled.Connections);
            if (!previousMaintenance.IsSubsetOf(nextMaintenance) || !previousPublic.IsSubsetOf(nextPublic) || !previousRequired.IsSubsetOf(nextRequired) || nextPublic.Count <= previousPublic.Count)
            {
                networkDiagnostics.Add(new("layout", "network-negotiation-retained", $"The public escape trial was not committed: maintenance gateways {previousMaintenance.Count}→{nextMaintenance.Count}, public gateways {previousPublic.Count}→{nextPublic.Count}, feasible required network paths {previousRequired.Count}→{nextRequired.Count}. The previous physical networks were retained.", "warning"));
                break;
            }
            project = trial; rooms = trialRooms; publicApproaches = trialReservations; networkDiagnostics = trialDiagnostics;
        }
        BuildNetwork(project, rooms, definitions, "$maintenance", project.Settings.MaintenanceWidth, networkDiagnostics);
        DesignMaintenanceConnectivity.AddTransitions(project, networkDiagnostics);
        diagnostics.AddRange(networkDiagnostics);
        RebuildBoundaries(project);
        foreach (var connection in compiled.Connections)
        {
            if (connection.Priority == "avoided") continue; // Separation is measured independently; no avoided route is deliberately built.
            foreach (var from in SourceRooms(rooms, definitions, connection.From))
            {
                var groups = connection.Selection == "all" ? connection.To.Select(path => new List<string> { path }).ToList() : [connection.To];
                foreach (var group in groups)
                {
                    var targets = group.SelectMany(path => Resolve(rooms, path).OrderByDescending(room => definitions[room.DefinitionPath].AllowThroughTraffic).ThenBy(room => room.Seed.Distance(from.Seed)).Take(path.Contains('/') ? int.MaxValue : 3)).OrderBy(room => room.Seed.Distance(from.Seed)).ThenBy(room => room.Id, StringComparer.Ordinal).ToList();
                    if (targets.Count == 0 && connection.Presence == "ifPresent") continue;
                    var priorNetworks = project.Layout.TransportNetworks.Select(network => network.Id).ToHashSet(StringComparer.Ordinal);
                    DesignRoute? best = null;
                    foreach (var to in targets)
                    foreach (var mode in connection.Modes.OrderBy(ModeOrder))
                    {
                        var route = FindRequirementRoute(project, from, to, connection, mode, definitions);
                        if (route != null && (best == null || route.Cells.Count < best.Cells.Count)) best = route;
                    }
                    project.Layout.TransportNetworks.RemoveAll(network => !network.Edited && !network.Locked && !priorNetworks.Contains(network.Id) && network.Id != best?.TransportNetworkId);
                    if (best != null)
                    {
                        if (!project.Layout.Routes.Any(route => route.RequirementId == best.RequirementId && route.From == best.From && route.To == best.To && route.Kind == best.Kind)) project.Layout.Routes.Add(best);
                        if (best.Kind == "utility" && best.Mode is not ("mail" or "disposalShortcut") && !project.Layout.Utilities.Any(utility => utility.RequirementId == best.RequirementId && utility.From == best.From && utility.To == best.To))
                            project.Layout.Utilities.Add(new() { Id = "utility:" + best.Id, RequirementId = best.RequirementId, From = best.From, To = best.To, Type = connection.InterfaceTypes.FirstOrDefault() ?? "cable", Cells = [.. best.Cells] });
                    }
                    else if (connection.Priority == "required")
                    {
                        diagnostics.Add(new(connection.Id + "@" + from.Id, "unrouted", $"No physical {connection.Kind} route from {from.Id} to [{string.Join(", ", group)}] satisfies its allowed modes, hardware profiles and interfaces."));
                    }
                }
            }
        }
        project.Stage = "circulation";
        project.Report = new() { Stage = "circulation", Diagnostics = diagnostics, Complete = false };
        return project;
    }

    private static int ModeOrder(string mode) => mode switch { "direct" => 0, "department" => 1, "publicHall" => 2, "maintenance" => 3, "controlledMaintenance" => 4, _ => 5 };
    private static HashSet<string> GatewayClusters(StationDesignProject project, string network)
    {
        var ports = DesignMeasurements.ValidInterfaces(project).Where(port => port.Kind == "walking").ToList();
        return DesignClusterGeometry.Clusters(project).Where(cluster =>
        {
            var ids = cluster.Select(room => room.Id).ToHashSet(StringComparer.Ordinal);
            return ports.Any(port => port.From == network && ids.Contains(port.To) || port.To == network && ids.Contains(port.From));
        }).Select(cluster => cluster[0].Id).ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> RequiredNetworkCapabilities(StationDesignProject project, List<DesignRoom> rooms, Dictionary<string, RoomDefinition> definitions, List<CompiledConnection> requirements)
    {
        var fulfilled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requirement in requirements.Where(requirement => requirement.Priority == "required" && requirement.Modes.Any(mode => mode is "publicHall" or "maintenance")))
        foreach (var from in SourceRooms(rooms, definitions, requirement.From))
        {
            var groups = requirement.Selection == "all" ? requirement.To.Select(path => new List<string> { path }).ToList() : [requirement.To];
            for (var index = 0; index < groups.Count; index++)
            {
                var targets = groups[index].SelectMany(path => Resolve(rooms, path)).ToList();
                if (targets.Any(to => requirement.Modes.Where(mode => mode is "direct" or "department" or "publicHall" or "maintenance").Any(mode => FindRequirementRoute(project, from, to, requirement, mode, definitions) != null)))
                    fulfilled.Add(requirement.Id + "@" + from.Id + ":" + index);
            }
        }
        return fulfilled;
    }
    private static bool EntryAllowed(RoomDefinition definition, string owner) => definition.Access.EntryOwners.Count == 0 || definition.Access.EntryOwners.Contains(owner, StringComparer.Ordinal);
    private static IEnumerable<DesignRoom> Resolve(IEnumerable<DesignRoom> rooms, string path) => rooms.Where(room => room.DefinitionPath == path || room.OwnerId == path);
    private static IEnumerable<DesignRoom> SourceRooms(IEnumerable<DesignRoom> rooms, Dictionary<string, RoomDefinition> definitions, string path)
    {
        var matches = Resolve(rooms, path).OrderByDescending(room => definitions[room.DefinitionPath].Role == "filler").ThenByDescending(room => definitions[room.DefinitionPath].AllowThroughTraffic).ThenBy(room => room.Id, StringComparer.Ordinal);
        return path.Contains('/') ? matches : matches.Take(1);
    }

    private static void AddSharedInterface(StationDesignProject project, DesignRoom first, DesignRoom second, string kind, string type, int width, string requirement)
    {
        var existing = project.Layout.Interfaces.FirstOrDefault(port => port.Kind == kind && (port.From == first.Id && port.To == second.Id || port.From == second.Id && port.To == first.Id));
        if (existing?.Edited == true) return;
        if (existing != null && existing.Width >= width && (requirement.Length == 0 || existing.Type == type)) return;
        var firstFloor = first.Cells.ToHashSet(); var secondFloor = second.Cells.ToHashSet();
        var occupied = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned).SelectMany(room => room.Cells).ToHashSet();
        var groups = new Dictionary<(Cell Normal, int Axis), List<Cell>>();
        foreach (var floor in firstFloor) foreach (var normal in Cell.Directions)
        {
            var wall = floor + normal;
            if (occupied.Contains(wall) || !secondFloor.Contains(wall + normal)) continue;
            var key = (normal, normal.X != 0 ? wall.X : wall.Y);
            if (!groups.TryGetValue(key, out var candidates)) groups[key] = candidates = [];
            candidates.Add(wall);
        }
        foreach (var group in groups.OrderBy(pair => pair.Key.Normal.X).ThenBy(pair => pair.Key.Normal.Y).ThenBy(pair => pair.Key.Axis))
        {
            var normal = group.Key.Normal; var tangent = new Cell(-normal.Y, normal.X);
            var cells = group.Value.ToHashSet();
            foreach (var wall in cells.OrderBy(cell => cell.Y).ThenBy(cell => cell.X))
            {
                var run = Enumerable.Range(0, width).Select(index => wall + new Cell(tangent.X * index, tangent.Y * index)).ToList();
                if (!run.All(cells.Contains)) continue;
                if (kind != "utility" && project.Layout.Interfaces.Any(port => port != existing && port.Kind != "utility" && port.Cells.Any(run.Contains))) continue;
                if (existing != null && !existing.Edited) project.Layout.Interfaces.Remove(existing);
                project.Layout.Interfaces.Add(new()
                {
                    Id = "interface:" + first.Id + ":" + second.Id + ":" + kind, RequirementId = requirement,
                    From = first.Id, To = second.Id, Kind = kind, Type = type, Width = width,
                    Access = first.OwnerId == second.OwnerId ? first.OwnerId : "shared", Cells = run,
                    FromApproach = wall - normal, ToApproach = wall + normal
                });
                return;
            }
        }
    }

    private static bool ReserveStrandedPublicPaths(StationDesignProject project, List<DesignRoom> rooms, Dictionary<string, RoomDefinition> definitions, HashSet<Cell> reservations)
    {
        var publicFloor = project.Layout.PublicCorridors.ToHashSet();
        if (publicFloor.Count == 0) return false;
        var floor = rooms.SelectMany(room => room.Cells).ToHashSet();
        var structure = floor.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Except(floor).ToHashSet();
        var reservedSpace = project.Layout.ReservedSpace.Concat(DesignDockGeometry.ReservedCells(project))
            .Concat(project.Layout.CirculationReservations.Where(reservation => reservation.Network == "maintenance").SelectMany(reservation => reservation.BoundaryCells.Concat(reservation.ApproachCells)))
            .SelectMany(cell => Cell.Directions.Select(direction => cell + direction).Append(cell)).ToHashSet();
        var maintenance = project.Layout.Maintenance.SelectMany(cell => Cell.Directions.Select(direction => cell + direction).Append(cell)).ToHashSet();
        var envelope = project.Layout.Envelope.ToHashSet();
        var bounds = new GridBounds(0, 0, project.Settings.Width, project.Settings.Height);
        var passageBounds = DesignNetworkGeometry.PassageBounds(project);
        var width = project.Settings.PublicWidth;
        var passableCache = new Dictionary<Cell, bool>();
        bool Passable(Cell cell)
        {
            if (!passableCache.TryGetValue(cell, out var allowed)) passableCache[cell] = allowed = Stamp(cell, width).All(part => passageBounds.Contains(part) && !floor.Contains(part) && !structure.Contains(part) && !reservedSpace.Contains(part) && (!project.Layout.EnvelopeEdited || envelope.Contains(part)));
            return allowed;
        }
        int Cost(Cell cell) => 1 + (Stamp(cell, width).Any(maintenance.Contains) ? 20 : 0);
        var before = reservations.Count;
        foreach (var cluster in DesignClusterGeometry.Clusters(project).Where(cluster => cluster[0].OwnerId != RequirementCompiler.MaintenanceOwner))
        {
            var ids = cluster.Select(room => room.Id).ToHashSet(StringComparer.Ordinal);
            if (project.Layout.Interfaces.Any(port => port.Kind == "walking" && (port.To == "$public" && ids.Contains(port.From) || port.From == "$public" && ids.Contains(port.To)) && DesignMeasurements.InterfaceTouches(project, port))) continue;
            var eligible = cluster.Where(room => definitions[room.DefinitionPath].Access.AllowPublicEntry && DesignNestedGeometry.EntryAllowed(project, room, null, definitions)).ToList();
            if (eligible.Any(room => definitions[room.DefinitionPath].AllowThroughTraffic)) eligible = eligible.Where(room => definitions[room.DefinitionPath].AllowThroughTraffic).ToList();
            PortCandidate? chosen = null; List<Cell>? best = null; var bestCost = int.MaxValue;
            foreach (var candidate in eligible.SelectMany(room => Ports(project, room, floor, width)).Where(port => Passable(DesignMeasurements.Bounds(port.Throat).Center)).OrderBy(port => publicFloor.Min(cell => cell.Distance(port.Wall))).Take(48))
            {
                var anchor = DesignMeasurements.Bounds(candidate.Throat).Center;
                var path = Search(anchor, cell => Stamp(cell, width).Any(publicFloor.Contains), Passable, bounds, publicFloor, 100000, Cost);
                if (path == null) continue;
                var cost = path.Sum(Cost);
                if (cost >= bestCost) continue;
                chosen = candidate; best = path; bestCost = cost;
            }
            if (chosen == null || best == null) continue;
            reservations.UnionWith(best.SelectMany(cell => Stamp(cell, width)).Concat(chosen.Throat).Concat(chosen.Door));
        }
        return reservations.Count > before;
    }

    private static void BuildNetwork(StationDesignProject project, List<DesignRoom> rooms, Dictionary<string, RoomDefinition> definitions, string network, int width, List<ProgramDiagnostic> diagnostics, bool backboneOnly = false, bool primaryOnly = false, bool skipBackbone = false, HashSet<Cell>? reservedApproaches = null, HashSet<Cell>? publicAnchors = null)
    {
        var edited = network == "$public" ? project.Layout.PublicEdited : project.Layout.MaintenanceEdited;
        var floor = rooms.SelectMany(room => room.Cells).ToHashSet();
        var reservedSpace = project.Layout.ReservedSpace.Concat(DesignDockGeometry.ReservedCells(project)).ToHashSet();
        var reservedHalo = reservedSpace.SelectMany(cell => Cell.Directions.Select(direction => cell + direction).Append(cell)).ToHashSet();
        var envelope = project.Layout.Envelope.ToHashSet();
        var bounds = new GridBounds(0, 0, project.Settings.Width, project.Settings.Height);
        var passageBounds = DesignNetworkGeometry.PassageBounds(project);
        var structural = floor.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Except(floor).ToHashSet();
        var other = (network == "$public" ? project.Layout.Maintenance : project.Layout.PublicCorridors).ToHashSet();
        var otherHalo = other.SelectMany(cell => Cell.Directions.Select(direction => cell + direction).Append(cell)).ToHashSet();
        var networkKey = network == "$public" ? "publicHall" : "maintenance";
        var plannedEntrances = project.Layout.CirculationReservations.Where(reservation => reservation.Network == networkKey).ToList();
        var oppositeApproaches = project.Layout.CirculationReservations.Where(reservation => reservation.Network != networkKey).SelectMany(reservation => reservation.ApproachCells.Concat(reservation.BoundaryCells));
        otherHalo.UnionWith(oppositeApproaches.SelectMany(cell => Cell.Directions.Select(direction => cell + direction).Append(cell)));
        if (network == "$maintenance" && reservedApproaches != null)
            otherHalo.UnionWith(reservedApproaches.SelectMany(cell => Cell.Directions.Select(direction => cell + direction).Append(cell)));
        var target = new HashSet<Cell>(network == "$public" ? project.Layout.PublicCorridors : project.Layout.Maintenance);
        bool NetworkCenterAllowed(Cell center) => Stamp(center, width).All(cell => passageBounds.Contains(cell) && !floor.Contains(cell) && !structural.Contains(cell) && !otherHalo.Contains(cell) && !reservedHalo.Contains(cell) && (network != "$public" || !project.Layout.EnvelopeEdited || envelope.Contains(cell)));
        var controlled = project.Definition.Maintenance.Connectivity.Policy == "controlled";
        if (controlled && !edited && project.Layout.CirculationReservations.Any(reservation => reservation.Id == "planning:public-backbone"))
        {
            var planned = plannedEntrances.SelectMany(reservation => reservation.ApproachCells).ToHashSet();
            var available = bounds.Cells().Where(NetworkCenterAllowed).SelectMany(center => Stamp(center, width)).ToHashSet();
            if (planned.IsSubsetOf(available) && (network != "$maintenance" || !HasOpenSquare(planned, project.Settings.MaintenanceMaxWidth + 1)))
            {
                target.UnionWith(planned);
                if (!project.Layout.EnvelopeEdited)
                {
                    var exteriorFloor = rooms.Where(room => definitions[room.DefinitionPath].Environment.Placement == "exterior").SelectMany(room => room.Cells).ToHashSet();
                    envelope.UnionWith(planned.Except(exteriorFloor));
                    envelope.UnionWith(planned.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Except(exteriorFloor));
                }
            }
            else diagnostics.Add(new("layout." + network[1..], "planning-network-changed", "The reserved network no longer fits actual grown floor/clearances; its invalid cells were not materialized.", "warning"));
        }
        HashSet<Cell>? reachableCenters = null;
        if (network == "$public" && !edited && !skipBackbone)
        {
            project.Layout.PublicBackbone.Clear();
            bool PassableCenter(Cell center) => NetworkCenterAllowed(center);
            var anchors = new List<Cell>();
            foreach (var desired in project.Layout.PublicWaypoints)
            {
                var anchor = bounds.Cells().Where(cell => cell.Distance(desired) <= 24 && PassableCenter(cell)).OrderBy(cell => cell.Distance(desired)).ThenBy(cell => cell.Y).ThenBy(cell => cell.X).Cast<Cell?>().FirstOrDefault();
                if (anchor == null) { diagnostics.Add(new("layout.public", "backbone-waypoint", "A topology waypoint has no legal full-width public passage nearby.")); continue; }
                anchors.Add(anchor.Value);
            }
            if (anchors.Count > 0)
            {
                project.Layout.PublicBackbone.Add(anchors[0]); target.UnionWith(Stamp(anchors[0], width));
                var edges = Enumerable.Range(1, Math.Max(0, anchors.Count - 1)).Select(index => (From: anchors[index - 1], To: anchors[index], Closing: false)).ToList();
                if (project.Settings.Topology == "ring" && anchors.Count >= 3) edges.Add((anchors[^1], anchors[0], true));
                foreach (var edge in edges)
                {
                    var previousCenterline = project.Layout.PublicBackbone.ToHashSet();
                    bool PassableEdge(Cell cell) => PassableCenter(cell) && (cell == edge.From || cell == edge.To || !previousCenterline.Contains(cell)) && (!edge.Closing || cell.Distance(edge.From) <= width * 2 || cell.Distance(edge.To) <= width * 2 || !Stamp(cell, width).Any(target.Contains));
                    var path = Search(edge.From, cell => cell == edge.To, PassableEdge, bounds, [edge.To], 100000);
                    if (path == null) { diagnostics.Add(new("layout.public", "backbone-route", "The requested topology backbone cannot be built without crossing room floor, reserved space or another passage.")); break; }
                    project.Layout.PublicBackbone.AddRange(path.Skip(1)); target.UnionWith(path.SelectMany(cell => Stamp(cell, width)));
                }
                if (!project.Layout.EnvelopeEdited)
                {
                    var exteriorFloor = rooms.Where(room => definitions[room.DefinitionPath].Environment.Placement == "exterior").SelectMany(room => room.Cells).ToHashSet();
                    envelope.UnionWith(target.Except(exteriorFloor));
                    envelope.UnionWith(target.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Except(exteriorFloor));
                }
            }
        }
        var groups = new List<List<DesignRoom>>();
        foreach (var owner in DesignClusterGeometry.Clusters(project).OrderBy(group => group[0].Id, StringComparer.Ordinal))
        {
            var candidates = owner.Where(room => DesignNestedGeometry.EntryAllowed(project, room, null, definitions) && (network == "$public" ? definitions[room.DefinitionPath].Access.AllowPublicEntry : definitions[room.DefinitionPath].Access.AllowMaintenanceEntry)).ToList();
            if (network == "$public" && owner[0].OwnerId == RequirementCompiler.MaintenanceOwner) continue;
            if (owner[0].OwnerId == RequirementCompiler.MaintenanceOwner)
                groups.AddRange(candidates.Select(room => new List<DesignRoom> { room }));
            else if (candidates.Count > 0)
            {
                var transit = candidates.Where(room => definitions[room.DefinitionPath].AllowThroughTraffic).ToList();
                if (transit.Count > 0) candidates = transit;
                groups.Add(candidates.OrderByDescending(room => definitions[room.DefinitionPath].Role == "filler").ThenBy(room => room.Id, StringComparer.Ordinal).ToList());
            }
        }
        if (backboneOnly)
        {
            var reachablePublic = target.Count == 0 ? null : DesignNetworkGeometry.ReachableFromFloor(DesignNetworkGeometry.LegalCenters(project, "publicHall"), target, width);
            if (!edited && reservedApproaches != null)
                foreach (var group in groups)
                {
                    var candidate = group.SelectMany(room => Ports(project, room, floor, width))
                        .Where(port => port.Throat.All(cell => passageBounds.Contains(cell) && !floor.Contains(cell) && !structural.Contains(cell) && !otherHalo.Contains(cell) && !reservedHalo.Contains(cell) && (!project.Layout.EnvelopeEdited || envelope.Contains(cell))))
                        .Where(port => reachablePublic == null || reachablePublic.Contains(DesignMeasurements.Bounds(port.Throat).Center))
                        .OrderByDescending(port => plannedEntrances.Any(reservation => reservation.RoomId == port.Room.Id && reservation.Outward == port.Normal && reservation.BoundaryCells.ToHashSet().SetEquals(port.Door)))
                        .ThenBy(port => target.Count == 0 ? port.Wall.Distance(new(project.Settings.Width / 2, project.Settings.Height / 2)) : target.Min(cell => cell.Distance(port.Wall))).FirstOrDefault();
                    if (candidate != null)
                    { reservedApproaches.UnionWith(candidate.Throat.Concat(candidate.Door)); publicAnchors?.Add(DesignMeasurements.Bounds(candidate.Throat).Center); }
                    else diagnostics.Add(new(group[0].Id, "public-planning-connectivity", "No eligible full-width public gateway reaches the backbone before service routing. Preserve a connected public planning artery when packing this cluster.", "warning"));
                }
            project.Layout.PublicCorridors = Sort(target);
            if (!project.Layout.EnvelopeEdited) project.Layout.Envelope = Sort(envelope);
            return;
        }
        var guardPublicAccess = network == "$maintenance" && primaryOnly && !controlled && !edited && publicAnchors is { Count: > 0 } && project.Layout.PublicCorridors.Count > 0;
        var publicCapacity = guardPublicAccess ? DesignNetworkGeometry.LegalCenters(project, "publicHall", oppositeFloorOverride: Array.Empty<Cell>()) : [];
        var scheduled = groups.OrderBy(group => group[0].Seed.X).ThenBy(group => group[0].Seed.Y).SelectMany(group => Enumerable.Range(0, !primaryOnly && network == "$maintenance" && group[0].OwnerId != RequirementCompiler.MaintenanceOwner ? Math.Max(1, project.Settings.Packing.MaintenanceConnectionsPerCluster) : 1).Select(index => (Rooms: group, Index: index))).ToList();
        foreach (var item in scheduled)
        {
            var group = item.Rooms;
            var groupFloor = group.SelectMany(room => room.Cells).ToHashSet();
            var previousPorts = project.Layout.Interfaces.Where(port => port.Kind == "walking" && (port.From == network && group.Any(room => room.Id == port.To) || port.To == network && group.Any(room => room.Id == port.From)) && port.Cells.Count > 0 && port.Cells.All(cell => Cell.Directions.Any(direction => groupFloor.Contains(cell + direction) && target.Contains(cell - direction)))).ToList();
            if (previousPorts.Count > item.Index) continue;
            if (edited)
            {
                if (project.Layout.Interfaces.Any(port => port.Edited && (port.From == network && group.Any(room => room.Id == port.To) || port.To == network && group.Any(room => room.Id == port.From))))
                { diagnostics.Add(new(group[0].OwnerId, "edited-entrance", "An edited network entrance no longer touches both endpoint floors; its geometry was preserved for correction.", "warning")); continue; }
                var port = group.SelectMany(room => Ports(project, room, floor, width))
                    .FirstOrDefault(candidate => candidate.Door.All(cell => target.Contains(cell + candidate.Normal)) && !candidate.Door.Any(reservedSpace.Contains) && previousPorts.All(previous => previous.Cells.All(cell => cell.Distance(candidate.Wall) > width * 2)));
                if (port == null) { diagnostics.Add(new(group[0].OwnerId, "edited-network-frontage", "The painted passage floor has no legal full-width entrance into an eligible room. Its floor was preserved.", "warning")); continue; }
                var portDefinition = definitions[port.Room.DefinitionPath];
                project.Layout.Interfaces.Add(new()
                {
                    Id = "entrance:" + port.Room.Id + ":" + network[1..] + (item.Index == 0 ? "" : ":" + (item.Index + 1)), From = port.Room.Id, To = network,
                    Kind = "walking", Type = network == "$maintenance" && (portDefinition.Environment.Placement == "exterior" || port.Room.OwnerId == RequirementCompiler.MaintenanceOwner) ? "airlock" : "doorway",
                    Width = width, Cells = port.Door, Access = portDefinition.Access.Level,
                    FromApproach = port.Wall - port.Normal, ToApproach = port.Wall + port.Normal
                });
                continue;
            }
            var candidates = group.SelectMany(room => Ports(project, room, floor, width)).Where(port => port.Throat.All(cell => passageBounds.Contains(cell) && !floor.Contains(cell) && !structural.Contains(cell) && !otherHalo.Contains(cell) && !reservedHalo.Contains(cell)) && previousPorts.All(previous => previous.Cells.All(cell => cell.Distance(port.Wall) > width * 2))).ToList();
            if (network == "$public" && project.Layout.EnvelopeEdited) candidates = candidates.Where(port => port.Throat.All(envelope.Contains)).ToList();
            if (candidates.Count == 0) { diagnostics.Add(new(group[0].OwnerId, "network-frontage", $"No {(item.Index > 0 ? "additional " : "")}full-width {network[1..]} entrance fits this owner/room's actual boundary.", "warning")); continue; }
            PortCandidate? selected = null; List<Cell>? path = null;
            var publicBefore = guardPublicAccess ? DesignNetworkGeometry.WithoutOppositeFloor(publicCapacity, target, project.Settings.PublicWidth) : [];
            var publicReachedBefore = guardPublicAccess ? DesignNetworkGeometry.ReachableFromFloor(publicBefore, project.Layout.PublicCorridors, project.Settings.PublicWidth) : [];
            var protectedPublicGoals = guardPublicAccess ? publicAnchors!.Where(publicReachedBefore.Contains).ToHashSet() : [];
            if (target.Count > 0 && reachableCenters == null)
            {
                reachableCenters = []; var queue = new Queue<Cell>();
                foreach (var seed in target.SelectMany(cell => Stamp(cell, width + 1)).Distinct().Where(NetworkCenterAllowed))
                    if (Stamp(seed, width).Any(target.Contains) && reachableCenters.Add(seed)) queue.Enqueue(seed);
                while (queue.TryDequeue(out var cell)) foreach (var direction in Cell.Directions)
                {
                    var next = cell + direction;
                    if (bounds.Contains(next) && !reachableCenters.Contains(next) && NetworkCenterAllowed(next)) { reachableCenters.Add(next); queue.Enqueue(next); }
                }
            }
            foreach (var candidate in candidates.OrderByDescending(port => plannedEntrances.Any(reservation => reservation.RoomId == port.Room.Id && reservation.Outward == port.Normal && reservation.BoundaryCells.ToHashSet().SetEquals(port.Door)))
                .ThenBy(port => target.Count == 0 ? port.Wall.Distance(new(project.Settings.Width / 2, project.Settings.Height / 2)) : target.Min(cell => cell.Distance(port.Wall))).Take(48))
            {
                var anchor = DesignMeasurements.Bounds(candidate.Throat).Center;
                if (!NetworkCenterAllowed(anchor)) continue;
                var isolatedSeed = network == "$maintenance" && controlled && reachableCenters != null && !reachableCenters.Contains(anchor);
                if (reachableCenters != null && !reachableCenters.Contains(anchor) && !isolatedSeed) continue;
                var localPublicEscape = new HashSet<Cell>();
                for (var retry = 0; retry < (guardPublicAccess ? 4 : 1); retry++)
                {
                    bool Passable(Cell cell) => NetworkCenterAllowed(cell) && !Stamp(cell, width).Any(localPublicEscape.Contains);
                    if (!Passable(anchor)) break;
                    var trial = target.Count == 0 || isolatedSeed ? new List<Cell> { anchor } : Search(anchor, cell => Stamp(cell, width).Any(target.Contains), Passable, bounds, target, 50000);
                    if (trial == null) break;
                    if (path != null && trial.Count >= path.Count) break;
                    var stamped = trial.SelectMany(cell => Stamp(cell, width)).Concat(candidate.Throat).ToHashSet();
                    var proposed = target.Concat(stamped).ToHashSet();
                    if (network == "$maintenance" && HasOpenSquare(proposed, project.Settings.MaintenanceMaxWidth + 1)) break;
                    if (isolatedSeed && DesignMeasurements.Components(proposed) > project.Definition.Maintenance.Connectivity.MaxComponents) break;
                    if (isolatedSeed && item.Index > 0 && !DesignMaintenanceConnectivity.AdditionalComponentCanReachStation(project, proposed, new()
                    {
                        Id = "preview:secondary:" + candidate.Room.Id, From = candidate.Room.Id, To = "$maintenance", Kind = "walking",
                        Type = definitions[candidate.Room.DefinitionPath].Environment.Placement == "exterior" ? "airlock" : "doorway",
                        Access = definitions[candidate.Room.DefinitionPath].Access.Level, Width = width, Cells = candidate.Door,
                        FromApproach = candidate.Wall - candidate.Normal, ToApproach = candidate.Wall + candidate.Normal
                    })) break;
                    if (protectedPublicGoals.Count > 0)
                    {
                        var publicAfter = DesignNetworkGeometry.WithoutOppositeFloor(publicCapacity, proposed, project.Settings.PublicWidth);
                        var reached = DesignNetworkGeometry.ReachableFromFloor(publicAfter, project.Layout.PublicCorridors, project.Settings.PublicWidth);
                        var lost = protectedPublicGoals.Where(goal => !reached.Contains(goal)).OrderBy(goal => goal.Y).ThenBy(goal => goal.X).ToList();
                        if (lost.Count > 0)
                        {
                            var escape = DesignNetworkGeometry.PathToFloor(project, lost[0], project.Layout.PublicCorridors, project.Settings.PublicWidth, publicBefore);
                            if (escape == null) break;
                            localPublicEscape.UnionWith(DesignNetworkGeometry.Halo(escape.SelectMany(cell => Stamp(cell, project.Settings.PublicWidth))));
                            continue;
                        }
                    }
                    if (path == null || trial.Count < path.Count) { selected = candidate; path = trial; }
                    break;
                }
                if (path is { Count: <= 3 }) break;
            }
            if (selected == null || path == null) { diagnostics.Add(new(group[0].OwnerId, "network-route", $"The separated {network[1..]} network cannot reach an {(item.Index > 0 ? "additional " : "")}eligible entrance without crossing another floor or violating passage width.", "warning")); continue; }
            target.UnionWith(path.SelectMany(cell => Stamp(cell, width)));
            target.UnionWith(selected.Throat);
            var definition = definitions[selected.Room.DefinitionPath];
            var exterior = definition.Environment.Placement == "exterior";
            if (!project.Layout.EnvelopeEdited && (network == "$public" || !exterior))
            {
                var added = path.SelectMany(cell => Stamp(cell, width)).Concat(selected.Throat).ToHashSet();
                var exteriorFloor = rooms.Where(room => definitions[room.DefinitionPath].Environment.Placement == "exterior").SelectMany(room => room.Cells).ToHashSet();
                envelope.UnionWith(added.Except(exteriorFloor));
                envelope.UnionWith(added.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Except(exteriorFloor));
            }
            project.Layout.Interfaces.Add(new()
            {
                Id = "entrance:" + selected.Room.Id + ":" + network[1..] + (item.Index == 0 ? "" : ":" + (item.Index + 1)), From = selected.Room.Id, To = network,
                Kind = "walking", Type = network == "$maintenance" && (exterior || selected.Room.OwnerId == RequirementCompiler.MaintenanceOwner) ? "airlock" : "doorway",
                Width = width, Cells = selected.Door, Access = definition.Access.Level,
                FromApproach = selected.Wall - selected.Normal, ToApproach = selected.Wall + selected.Normal
            });
        }
        if (network == "$maintenance" && !edited && !primaryOnly)
            DesignClusterGeometry.WrapMaintenance(project, target, NetworkCenterAllowed, bounds, diagnostics);
        if (network == "$public") project.Layout.PublicCorridors = Sort(target);
        else project.Layout.Maintenance = Sort(target);
        if (!project.Layout.EnvelopeEdited) project.Layout.Envelope = Sort(envelope);
    }

    private static IEnumerable<PortCandidate> Ports(StationDesignProject project, DesignRoom room, HashSet<Cell> allFloor, int width)
    {
        var own = room.Cells.ToHashSet();
        foreach (var normal in Cell.Directions)
        {
            var tangent = new Cell(-normal.Y, normal.X);
            foreach (var floor in own.OrderBy(cell => cell.Y).ThenBy(cell => cell.X))
            {
                var wall = floor + normal;
                var door = Enumerable.Range(0, width).Select(index => wall + new Cell(tangent.X * index, tangent.Y * index)).ToList();
                if (!door.All(cell => own.Contains(cell - normal) && !allFloor.Contains(cell) && !allFloor.Contains(cell + normal))) continue;
                var throat = new List<Cell>();
                for (var distance = 1; distance <= width; distance++)
                    throat.AddRange(door.Select(cell => cell + new Cell(normal.X * distance, normal.Y * distance)));
                if (project.Layout.Interfaces.Any(port => port.Kind != "utility" && port.Cells.Any(door.Contains))) continue;
                yield return new(room, wall, normal, door, throat.Distinct().ToList());
            }
        }
    }

    private static DesignRoute? FindRequirementRoute(StationDesignProject project, DesignRoom from, DesignRoom to, CompiledConnection requirement, string mode, Dictionary<string, RoomDefinition> definitions)
    {
        if (!RoomHierarchy.ConnectionScopeAllowed(requirement, from.DefinitionPath, from.Id, from.ParentInstanceId, to.DefinitionPath, to.Id, to.ParentInstanceId)) return null;
        if (mode == "utilityNetwork" && requirement.Kind != "utility") return null;
        if (mode == "controlledMaintenance")
        {
            var controlled = requirement.Kind == "walking" ? DesignControlledMaintenance.FindRoute(project, from, to) : null;
            if (controlled == null || requirement.MaxDistance != null && controlled.Count - 1 > requirement.MaxDistance) return null;
            var usedPorts = DesignMeasurements.ValidInterfaces(project).Where(port => port.Kind == "walking" && port.Cells.Any(controlled.Contains)).ToList();
            if (requirement.InterfaceTypes.Count > 0 && !usedPorts.Any(port => requirement.InterfaceTypes.Contains(port.Type) && port.Width >= requirement.MinWidth) || requirement.MinWidth > 0 && !usedPorts.Any(port => port.Width >= requirement.MinWidth)) return null;
            return new() { Id = requirement.Id + "@" + from.Id + ":" + to.Id + ":" + mode, RequirementId = requirement.Id, From = from.Id, To = to.Id, Kind = "walking", Mode = mode, Cells = controlled, InterfaceIds = usedPorts.Select(port => port.Id).ToList() };
        }
        if (requirement.Kind == "walking" && (!DesignNestedGeometry.EntryAllowed(project, from, to, definitions) || !DesignNestedGeometry.EntryAllowed(project, to, from, definitions)) && mode is "direct" or "disposalShortcut") return null;
        if (mode is "mail" or "disposalShortcut") return DesignTransport.TryBuild(project, from, to, requirement, mode);
        if (mode == "department" && from.OwnerId != to.OwnerId) return null;
        var rooms = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && definitions.ContainsKey(room.DefinitionPath)).ToList();
        var allowed = rooms.Where(room => requirement.Kind == "utility" || room.Id == from.Id || room.Id == to.Id || definitions[room.DefinitionPath].AllowThroughTraffic || DesignNestedGeometry.IsAncestor(project, room.Id, from) || DesignNestedGeometry.IsAncestor(project, room.Id, to))
            .Where(room => mode != "department" || room.OwnerId == from.OwnerId).SelectMany(room => room.Cells).ToHashSet();
        var ports = project.Layout.Interfaces.Where(port => port.Kind == requirement.Kind && DesignMeasurements.InterfaceTouches(project, port)).ToList();
        if (requirement.Kind == "utility") ports = project.Layout.Interfaces.Where(port => port.Kind is "walking" or "utility" && DesignMeasurements.InterfaceTouches(project, port)).ToList();
        if (mode == "direct") ports = ports.Where(port => port.From == from.Id && port.To == to.Id || port.From == to.Id && port.To == from.Id).ToList();
        allowed.UnionWith(ports.SelectMany(port => port.Cells));
        if (mode == "publicHall") allowed.UnionWith(project.Layout.PublicCorridors);
        if (mode == "maintenance") allowed.UnionWith(project.Layout.Maintenance);
        if (mode == "utilityNetwork") allowed.UnionWith(project.Layout.PublicCorridors.Concat(project.Layout.Maintenance));
        if (requirement.Kind == "utility")
        {
            // Utility runs can pass through the wall seam between owned rooms, but not through unbuilt space.
            var ownFloor = mode == "utilityNetwork" ? rooms.SelectMany(room => room.Cells).Concat(project.Layout.PublicCorridors).Concat(project.Layout.Maintenance).ToHashSet() : rooms.Where(room => room.OwnerId == from.OwnerId || room.Id == to.Id).SelectMany(room => room.Cells).ToHashSet();
            allowed.UnionWith(project.Layout.Walls.Concat(project.Layout.Hull).Where(cell => Cell.Directions.Count(direction => ownFloor.Contains(cell + direction)) >= 2));
        }
        if (mode == "direct" && ports.Count == 0) return null;
        var start = from.Cells.OrderBy(cell => cell.Distance(to.Seed)).First(); var target = to.Cells.ToHashSet();
        var path = Search(start, target.Contains, allowed.Contains, DesignMeasurements.Bounds(allowed).Expand(1), target, 100000);
        if (path == null || requirement.MaxDistance != null && path.Count - 1 > requirement.MaxDistance) return null;
        if (mode == "publicHall" && !path.Any(project.Layout.PublicCorridors.Contains) || mode == "maintenance" && !path.Any(project.Layout.Maintenance.Contains)) return null;
        var used = ports.Where(port => path.Any(port.Cells.Contains)).ToList();
        if (requirement.InterfaceTypes.Count > 0 && requirement.Kind != "utility" && !used.Any(port => requirement.InterfaceTypes.Contains(port.Type) && port.Width >= requirement.MinWidth)) return null;
        if (requirement.MinWidth > 0 && requirement.Kind != "utility" && !used.Any(port => port.Width >= requirement.MinWidth)) return null;
        return new()
        {
            Id = requirement.Id + "@" + from.Id + ":" + to.Id + ":" + mode,
            RequirementId = requirement.Id, From = from.Id, To = to.Id, Kind = requirement.Kind, Mode = mode,
            Cells = path, InterfaceIds = used.Select(port => port.Id).ToList()
        };
    }

    public static void RebuildBoundaries(StationDesignProject project)
    {
        var definitions = RequirementCompiler.Rooms(project.Definition).ToDictionary(item => item.Path, item => item.Room, StringComparer.Ordinal);
        var interior = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && definitions.ContainsKey(room.DefinitionPath) && definitions[room.DefinitionPath].Environment.Placement != "exterior").SelectMany(room => room.Cells).ToHashSet();
        var allFloor = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned).SelectMany(room => room.Cells).Concat(project.Layout.PublicCorridors).Concat(project.Layout.Maintenance).ToHashSet();
        var envelope = project.Layout.Envelope.ToHashSet();
        interior.UnionWith(project.Layout.PublicCorridors);
        interior.UnionWith(project.Layout.Maintenance.Where(envelope.Contains));
        var ports = project.Layout.Interfaces.Where(port => port.Kind != "utility").SelectMany(port => port.Cells).ToHashSet();
        var dockingSpace = DesignDockGeometry.ReservedCells(project);
        var structural = interior.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Except(allFloor).Except(ports).Except(dockingSpace).ToHashSet();
        // Shared open counters and doors need end caps, including when a port sits at a room corner.
        foreach (var port in project.Layout.Interfaces.Where(port => port.Kind != "utility" && port.Cells.Count > 0))
        {
            Cell tangent;
            if (port.Cells.Select(cell => cell.X).Distinct().Count() == 1 && port.Cells.Count > 1) tangent = new(0, 1);
            else if (port.Cells.Select(cell => cell.Y).Distinct().Count() == 1 && port.Cells.Count > 1) tangent = new(1, 0);
            else
            {
                var from = DesignMeasurements.EndpointCells(project, port.From); var to = DesignMeasurements.EndpointCells(project, port.To); var cell = port.Cells[0];
                var normal = Cell.Directions.FirstOrDefault(direction => from.Contains(cell - direction) && to.Contains(cell + direction));
                if (normal == default) continue;
                tangent = new(-normal.Y, normal.X);
            }
            foreach (var cap in port.Cells.SelectMany(cell => new[] { cell + tangent, cell - tangent }).Except(port.Cells))
                if (!allFloor.Contains(cap) && !ports.Contains(cap) && !project.Layout.ReservedSpace.Contains(cap) && !dockingSpace.Contains(cap)) structural.Add(cap);
        }
        project.Layout.Walls = Sort(structural); project.Layout.Hull.Clear(); project.Layout.Windows.Clear();
        var exterior = DesignMeasurements.ExteriorSpace(project);
        var hull = structural.Where(cell => Cell.Directions.Any(direction => exterior.Contains(cell + direction))).ToHashSet();
        project.Layout.Hull = Sort(hull); project.Layout.Walls = Sort(structural.Except(hull));
        foreach (var room in project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && definitions.ContainsKey(room.DefinitionPath)))
        {
            var required = definitions[room.DefinitionPath].Exterior.MinSpaceFrontage;
            if (required == 0) continue;
            var candidates = room.Cells.SelectMany(cell => Cell.Directions.Select(direction => (Wall: cell + direction, Beyond: cell + direction + direction)))
                .Where(pair => hull.Contains(pair.Wall) && exterior.Contains(pair.Beyond)).Select(pair => pair.Wall).Distinct().OrderBy(cell => cell.Y).ThenBy(cell => cell.X).Take(required).ToList();
            foreach (var cell in candidates) { project.Layout.Hull.Remove(cell); project.Layout.Windows.Add(cell); }
        }
        project.Layout.Windows = Sort(project.Layout.Windows);
    }

    internal static List<Cell> Stamp(Cell center, int width)
    {
        var cells = new List<Cell>();
        for (var y = -width / 2; y < width - width / 2; y++) for (var x = -width / 2; x < width - width / 2; x++) cells.Add(center + new Cell(x, y));
        return cells;
    }

    internal static bool HasOpenSquare(HashSet<Cell> cells, int width) => cells.Any(cell => Enumerable.Range(0, width).All(y => Enumerable.Range(0, width).All(x => cells.Contains(cell + new Cell(x, y)))));
    internal static List<Cell> Sort(IEnumerable<Cell> cells) => cells.Distinct().OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToList();

    internal static List<Cell>? Search(Cell start, Func<Cell, bool> goal, Func<Cell, bool> passable, GridBounds bounds, ICollection<Cell> targets, int budget, Func<Cell, int>? stepCost = null)
    {
        if (!bounds.Contains(start)) return null;
        var queue = new PriorityQueue<Cell, (int Cost, int Y, int X)>();
        var costs = new Dictionary<Cell, int> { [start] = 0 }; var previous = new Dictionary<Cell, Cell>();
        var targetBounds = DesignMeasurements.Bounds(targets);
        int Heuristic(Cell cell) => Math.Max(0, Math.Max(targetBounds.X - cell.X, cell.X - targetBounds.Right + 1)) + Math.Max(0, Math.Max(targetBounds.Y - cell.Y, cell.Y - targetBounds.Bottom + 1));
        queue.Enqueue(start, (Heuristic(start), start.Y, start.X));
        while (queue.TryDequeue(out var cell, out _) && budget-- > 0)
        {
            if (goal(cell))
            {
                var path = new List<Cell> { cell };
                while (previous.TryGetValue(cell, out var parent)) { path.Add(parent); cell = parent; }
                path.Reverse(); return path;
            }
            foreach (var direction in Cell.Directions)
            {
                var next = cell + direction;
                if (!bounds.Contains(next) || !passable(next)) continue;
                var cost = costs[cell] + (stepCost?.Invoke(next) ?? 1);
                if (costs.TryGetValue(next, out var existing) && existing <= cost) continue;
                costs[next] = cost; previous[next] = cell; queue.Enqueue(next, (cost + Heuristic(next), next.Y, next.X));
            }
        }
        return null;
    }
}
