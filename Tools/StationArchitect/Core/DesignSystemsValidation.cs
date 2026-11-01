namespace Content.StationArchitect;

public static partial class DesignSystems
{
    public static NativeSystemsReport Validate(StationDesignProject project, bool final = false)
        => ValidateCore(project, final, out _);

    private static NativeSystemsReport ValidateCore(StationDesignProject project, bool final, out NativeGraph? inspectedGraph)
    {
        inspectedGraph = null;
        TraceNative("validation.begin " + project.Stage);
        var report = new NativeSystemsReport(); report.Diagnostics.AddRange(ValidateDefinition(project.Definition));
        if (!report.Success) return report;
        var program = project.Definition.Systems;
        if (!program.Enabled) { report.Complete = true; return report; }
        if (project.Layout.SystemDevices == null || project.Layout.SystemNetworks == null || project.Layout.SystemControls == null || project.Layout.SystemDevices.Any(device => device == null) || project.Layout.SystemNetworks.Any(network => network == null || network.Segments == null || network.Terminals == null || network.Segments.Any(segment => segment == null) || network.Terminals.Any(terminal => terminal == null)) || project.Layout.SystemControls.Any(control => control == null || control.DeviceIds == null))
        { report.Diagnostics.Add(new("systems", "native-layout-shape", "Native runtime records and their collections cannot be null.")); return report; }
        if (project.Layout.SystemDevices.Count > program.Generation.MaxDevices || project.Layout.SystemNetworks.Count > 4096 || project.Layout.SystemNetworks.Sum(network => (long)network.Segments.Count) > program.Generation.MaxGeneratedSegments || project.Layout.SystemControls.Count > 4096)
        { report.Diagnostics.Add(new("systems", "native-layout-budget", "Actual native records exceed configured materialization budgets.")); return report; }
        bool Due(string stage) => final || project.Stage == "validation" || DesignStages.Index(stage) >= 0 && DesignStages.Index(project.Stage) >= DesignStages.Index(stage);
        if (!Due("systemsDevices")) return report;
        void Error(string path, string code, string message, string priority = "required") => report.Diagnostics.Add(new(path, code, message, priority == "required" ? "error" : "warning"));
        var profiles = program.Profiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        var scopeKey = ScopeResolver(project);
        TraceNative("validation.scope.ready");
        var resolveRoom = RoomResolver(project);
        var validInterfaces = DesignMeasurements.ValidInterfaces(project);
        var interfaceById = validInterfaces.GroupBy(port => port.Id).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var resolveEntity = EntityResolver(project, validInterfaces, resolveRoom);
        var placementValid = DesignMeasurements.EntityPlacementValidator(project);
        TraceNative("validation.inventories.ready interfaces=" + validInterfaces.Count);
        var targetRooms = program.Devices.ToDictionary(rule => rule.Id, rule => Targets(project, rule), StringComparer.Ordinal);
        var boundTargets = program.Devices.Where(rule => rule.BindInterfaces || rule.BindTransport).ToDictionary(rule => rule.Id, rule => (rule.BindTransport ? TransportTargets(project, rule) : InterfaceTargets(project, rule, validInterfaces, resolveRoom)), StringComparer.Ordinal);
        var roomInventory = project.Layout.Rooms.Concat(targetRooms.Values.SelectMany(rooms => rooms)).GroupBy(room => room.Id).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var rules = program.Devices.ToDictionary(rule => rule.Id, StringComparer.Ordinal);
        var validBindings = new Dictionary<string, (DesignSystemDevice Binding, DesignEntity Entity, DesignRoom Room, NativeSystemProfile Profile)>(StringComparer.Ordinal);
        var instances = new Dictionary<string, NativeInstance>(StringComparer.Ordinal); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in project.Layout.SystemDevices)
        {
            if (!ids.Add(device.Id) || !rules.TryGetValue(device.RequirementId, out var rule) || !profiles.TryGetValue(device.ProfileId, out var profile))
            { Error(device.Id, "native-device-reference", "Native device IDs must be unique and retain current authored rule/profile references."); continue; }
            var entity = resolveEntity(device);
            if (entity != null && !roomInventory.ContainsKey(entity.RoomId) && resolveRoom(entity.RoomId) is { } service) roomInventory[service.Id] = service;
            var room = entity == null ? null : roomInventory.GetValueOrDefault(entity.RoomId);
            var allowed = AllowedProfiles(project, rule);
            var selected = rule.BindTransport || rule.BindInterfaces ? boundTargets[rule.Id].Any(item => item.Id == entity?.Id) : targetRooms[rule.Id].Any(item => item.Id == room?.Id);
            if (entity == null || room == null || !selected || !allowed.Any(item => item.Id == profile.Id) || entity.Prototype != profile.Prototype || device.ScopeId != (rule.BindInterfaces || rule.BindTransport ? entity.Id : scopeKey(rule.Scope, room)))
            { Error(device.Id, "native-device-binding", "A native binding needs its actual selected hardware, matching profile prototype and fresh scope; saved role labels are not hardware evidence."); continue; }
            if (!rule.BindExisting && !rule.BindInterfaces && !rule.BindTransport && (entity.SystemRuleId != rule.Id || entity.RequirementId != "systems:" + rule.Id))
            { Error(device.Id, "native-device-identity", "Generated infrastructure must retain the exact system content requirement identity."); continue; }
            if (rule.BindExisting && (entity.SystemRuleId.Length > 0 || rule.ContentIds.Count > 0 && !rule.ContentIds.Contains(entity.RequirementId, StringComparer.Ordinal)))
            { Error(device.Id, "native-device-content", "This binding no longer identifies matching ordinary room content."); continue; }
            if (!DesignEquipmentGeometry.TryExpectedFootprint(project.Settings, entity.Prototype, entity.Position, entity.Rotation, out var expected) || entity.Footprint.Count != expected.Count || !entity.Footprint.ToHashSet().SetEquals(expected))
            { Error(device.Id, "native-device-footprint", "Native hardware must match its actual configured rotated footprint."); continue; }
            if (entity.Placement == "interface")
            {
                if (!interfaceById.TryGetValue(entity.InterfaceId, out var opening) || !entity.Footprint.All(opening.Cells.Contains))
                { Error(device.Id, "native-interface-hardware", "A bare-door receiver must identify a physically valid emitted doorway cell."); continue; }
            }
            else if (entity.Placement != "transport" && !placementValid(room, entity))
            { Error(device.Id, "native-device-placement", "Native equipment does not fit its actual room or service-zone floor/boundary and work reservation."); continue; }
            if (instances.TryGetValue(entity.Id, out var existing) && existing.Profile.Id != profile.Id)
            { Error(device.Id, "native-profile-conflict", "Two bindings assign different physical native profiles to the same entity."); continue; }
            instances[entity.Id] = new(entity.Id, profile, entity.Position, entity.Rotation);
            validBindings[device.Id] = (device, entity, room, profile);
        }
        TraceNative("validation.bindings.ready count=" + validBindings.Count);
        foreach (var rule in program.Devices)
        {
            var matched = validBindings.Values.Where(item => item.Binding.RequirementId == rule.Id).ToList();
            if (rule.BindInterfaces || rule.BindTransport)
            {
                foreach (var entity in boundTargets[rule.Id]) if (!matched.Any(item => item.Entity.Id == entity.Id)) Error(entity.Id, "native-hardware-unbound", "Selected emitted door/transport hardware lacks its native electrical receiver binding.");
                continue;
            }
            foreach (var scope in targetRooms[rule.Id].GroupBy(room => scopeKey(rule.Scope, room)).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal))
            {
                var members = matched.Where(item => item.Binding.ScopeId == scope.Key).ToList();
                if (members.Count < rule.Content.Count.Min || members.Count > rule.Content.Count.Max) Error(rule.Id + "@" + scope.Key, "native-device-count", $"Observed {members.Count} valid native devices; scope requires {rule.Content.Count.Min}–{rule.Content.Count.Max}.");
                var uncovered = UncoveredCells(project, rule, scope.Value, members.Select(item => item.Entity));
                if (uncovered.Count > 0) Error(scope.Value[0].Id, "native-service-coverage", $"{uncovered.Count} actual floor cells lack {rule.Id} coverage at the configured geodesic distance.");
                if (rule.BindExisting)
                {
                    var roomIds = scope.Value.Select(room => room.Id).ToHashSet(StringComparer.Ordinal); var variants = AllowedProfiles(project, rule).Select(profile => profile.Prototype).ToHashSet(StringComparer.Ordinal);
                    foreach (var entity in project.Layout.Equipment.Where(entity => entity.SystemRuleId.Length == 0 && roomIds.Contains(entity.RoomId) && variants.Contains(entity.Prototype) && (rule.ContentIds.Count == 0 || rule.ContentIds.Contains(entity.RequirementId, StringComparer.Ordinal))))
                        if (!members.Any(item => item.Entity.Id == entity.Id)) Error(entity.Id, "native-content-unbound", "Selected existing native hardware was omitted from the binding set.");
                }
            }
        }
        foreach (var entity in project.Layout.Equipment.Where(entity => entity.SystemRuleId.Length > 0))
            if (!validBindings.Values.Any(item => item.Entity.Id == entity.Id)) Error(entity.RoomId, "native-device-orphan", "Retained generated native hardware " + entity.Id + " has no valid current authored binding.");
        var supported = project.Layout.Rooms.SelectMany(room => room.Cells).Concat(project.Layout.PublicCorridors).Concat(project.Layout.Maintenance).Concat(project.Layout.Walls).Concat(project.Layout.Hull).Concat(project.Layout.Windows).Concat(project.Layout.Interfaces.SelectMany(port => port.Cells)).ToHashSet();
        var dockSpace = DesignDockGeometry.ReservedCells(project); var canvas = new GridBounds(0, 0, project.Settings.Width, project.Settings.Height);
        var networkRules = program.Networks.ToDictionary(rule => rule.Id, StringComparer.Ordinal); var networkIds = new HashSet<string>(StringComparer.Ordinal);
        var hardwarePorts = validBindings.Values.DistinctBy(item => item.Entity.Id).SelectMany(item => Ports(item.Profile, item.Entity.Position, item.Entity.Rotation, item.Entity.Id)).GroupBy(port => port.Position).ToDictionary(group => group.Key, group => group.ToList());
        var segmentMasks = new Dictionary<(Cell Cell, string Kind, string Group, int Layer, string Family), string>();
        foreach (var network in project.Layout.SystemNetworks)
        {
            if (!networkIds.Add(network.Id) || !networkRules.TryGetValue(network.RequirementId, out var rule)) { Error(network.Id, "native-network-reference", "Native network IDs and authored requirements must remain unique/current."); continue; }
            foreach (var segment in network.Segments)
            {
                if (!ids.Add(segment.Id) || !profiles.TryGetValue(segment.ProfileId, out var profile) || !rule.SegmentProfiles.Contains(segment.ProfileId, StringComparer.Ordinal) || profile.Kind == "device" || segment.Rotation % 90 != 0 || !canvas.Contains(segment.Position) || !rule.AllowSpace && !supported.Contains(segment.Position) || dockSpace.Contains(segment.Position))
                { Error(segment.Id, "native-segment-geometry", "A native segment needs unique identity, an allowed profile/rotation, actual supported placement and clear shuttle space."); continue; }
                var valid = true;
                foreach (var port in Ports(profile, segment.Position, segment.Rotation, segment.Id))
                {
                    if (!canvas.Contains(port.Position) || dockSpace.Contains(port.Position)) valid = false;
                    if ((hardwarePorts.GetValueOrDefault(port.Position) ?? []).Any(hardware => HardwarePortConflict(hardware.ProfilePort, port.ProfilePort)))
                    { Error(segment.Id, "native-hardware-overlap", "A segment duplicates a native port already supplied by actual hardware; only genuine same-tile adapters may coexist."); valid = false; }
                    var family = port.ProfilePort.Kind == "power" && port.ProfilePort.Connection == "terminal" ? "terminal" : "node";
                    var key = (port.Position, port.ProfilePort.Kind, port.ProfilePort.NodeGroup, port.ProfilePort.Kind == "gas" ? port.ProfilePort.PipeLayer : 0, family);
                    if (segmentMasks.TryGetValue(key, out var other) && other != segment.Id) { Error(segment.Id, "native-segment-overlap", "Duplicate native nodes occupy the same tier/layer cell as " + other + "."); valid = false; }
                    else segmentMasks[key] = segment.Id;
                }
                if (valid) instances[segment.Id] = new(segment.Id, profile, segment.Position, segment.Rotation);
            }
        }
        var graph = new NativeGraph(instances.Values.ToList(), report.Diagnostics);
        TraceNative("validation.graph.ready ports=" + graph.Ports.Count);
        inspectedGraph = graph;
        if (Due("power"))
            foreach (var item in validBindings.Values.Where(item => item.Profile.RequiresPower))
                if (!graph.Powered(item.Entity.Id)) Error(item.Room.Id, "native-device-unpowered", "Native device " + item.Entity.Id + " has no directed electrical path to a verified actual source/provider.");
        foreach (var rule in program.Networks)
        {
            TraceNative("validation.network " + rule.Id);
            var due = rule.Kind == "power" ? Due("power") : rule.Kind == "gas" ? Due("atmos") : Due("disposals"); if (!due) continue;
            var expected = new List<(string Scope, DesignSystemDevice Device, string Port, string Role, bool Required)>();
            foreach (var selector in rule.Terminals)
                foreach (var item in validBindings.Values.Where(item => item.Binding.RequirementId == selector.DeviceRuleId)) expected.Add((scopeKey(rule.Scope, item.Room), item.Binding, selector.PortId, selector.Role, selector.Required));
            foreach (var selector in rule.Terminals.Where(selector => selector.Required))
                if (!expected.Any(item => item.Device.RequirementId == selector.DeviceRuleId && item.Port == selector.PortId)) Error(rule.Id, "native-terminal-missing", "A required selected native terminal has no valid actual bound hardware.", rule.Priority);
            foreach (var scope in expected.GroupBy(item => item.Scope))
            {
                var network = project.Layout.SystemNetworks.FirstOrDefault(network => network.RequirementId == rule.Id && network.ScopeId == scope.Key);
                if (network == null) { Error(rule.Id + "@" + scope.Key, "native-network-missing", "The required actual native network is absent.", rule.Priority); continue; }
                foreach (var item in scope)
                {
                    var binding = validBindings[item.Device.Id]; var portId = binding.Entity.Id + ":" + item.Port;
                    var declared = network.Terminals.Any(terminal => terminal.DeviceId == item.Device.Id && terminal.PortId == item.Port && terminal.Role == item.Role);
                    if (!declared || !graph.Ports.TryGetValue(portId, out var port) || port.ProfilePort.Kind != rule.Kind)
                    { Error(network.Id, "native-terminal-binding", "Selected actual hardware must retain its declared native terminal and a matching-kind port. An optional selector permits absent hardware, not an unbound present device.", rule.Priority); continue; }
                    var connected = graph.Physical[portId].Count > 0; var reachable = rule.Kind == "power" ? graph.Power.Contains(portId) : rule.Kind == "gas" ? (item.Role is "inlet" or "outlet" ? graph.Sink.Contains(portId) : graph.Gas.Contains(portId)) : false;
                    var witness = rule.Kind == "disposal" ? DisposalWitness(graph, portId, scope.Where(other => other.Role == "outlet").Select(other => validBindings[other.Device.Id].Entity.Id).ToHashSet(StringComparer.Ordinal)) : graph.Witness(portId, rule.Kind, item.Role is "inlet" or "outlet");
                    if (rule.Kind == "disposal") { reachable = witness.Count > 0; connected = reachable; }
                    if (rule.Kind == "gas" && item.Role is "consumer" or "source" && graph.Pressure(portId) + .0001 < rule.MinPressureKpa) reachable = false;
                    if (binding.Profile.RequiresPower && Due("power") && !graph.Powered(binding.Entity.Id)) reachable = false;
                    report.Terminals.Add(new() { NetworkId = network.Id, DeviceId = item.Device.Id, PortId = item.Port, Role = item.Role, Connected = connected, SourceReachable = reachable, Witness = witness.Take(program.Generation.MaxWitnessSteps).ToList(), WitnessTruncated = witness.Count > program.Generation.MaxWitnessSteps });
                    if (!connected || !reachable) Error(binding.Room.Id, "native-terminal-unserved", $"{rule.Kind} terminal {item.Device.Id}/{item.Port} lacks actual directed source/sink reachability (including native adapters, power and pressure).", rule.Priority);
                }
            }
        }
        report.Metrics = new() { ["devices"] = validBindings.Count, ["segments"] = project.Layout.SystemNetworks.Sum(network => network.Segments.Count), ["verifiedTerminals"] = report.Terminals.Count(terminal => terminal.Connected && terminal.SourceReachable), ["startupStoredJoules"] = graph.Supplies.Sum(source => source.Energy), ["declaredSupplyWatts"] = graph.Supplies.Sum(source => source.Watts) };
        ValidateControls(project, validBindings, report, Due("atmos"), scopeKey);
        report.Diagnostics.Add(new("systems", "native-simulation-unverified", "Native ports, finite startup sources, scoped device lists and materialized wiring are inspected. Load draw, sustained generation, pressure regulation, timing and SS14 runtime operation have not been simulated.", "info"));
        report.Complete = report.Success && (final || Due("disposals"));
        TraceNative("validation.end terminals=" + report.Terminals.Count + " diagnostics=" + report.Diagnostics.Count);
        return report;
    }

    public static bool UtilityConnectionVerified(StationDesignProject project, string from, string to, string kind) => CreateUtilityVerifier(project)(from, to, kind);

    public static Func<string, string, string, bool> CreateUtilityVerifier(StationDesignProject project)
    {
        var inspect = CreateUtilityInspector(project);
        return (from, to, kind) => inspect(from, to, kind).Verified;
    }

    /// <summary>One fresh graph snapshot, reusable during a measurements pass; distance follows actual native port contacts, not saved utility route cells.</summary>
    public static Func<string, string, string, NativeUtilityConnection> CreateUtilityInspector(StationDesignProject project)
    {
        ValidateCore(project, false, out var graph);
        if (graph == null) return (_, _, _) => new(false, 0);
        var rooms = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var binding in project.Layout.SystemDevices)
        { var entity = FindEntity(project, binding); if (entity != null && graph.Instances.ContainsKey(entity.Id)) rooms[entity.Id] = entity.RoomId; }
        var undirected = graph.Edges.ToDictionary(pair => pair.Key, pair => pair.Value.ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var pair in graph.Edges) foreach (var next in pair.Value) undirected[next].Add(pair.Key);
        var cache = new Dictionary<(string From, string Kind), Dictionary<string, int>>();
        var predecessors = new Dictionary<(string From, string Kind), Dictionary<string, string>>();
        HashSet<string> RoomIds(string selector)
        {
            var selected = project.Layout.Rooms.Where(room => room.Id == selector || room.OwnerId == selector).Select(room => room.Id).ToHashSet(StringComparer.Ordinal);
            selected.Add(selector);
            foreach (var id in rooms.Values.Where(id => id.StartsWith(selector + "@", StringComparison.Ordinal))) selected.Add(id);
            return selected;
        }
        return (from, to, requestedKind) =>
        {
            var kind = requestedKind is "cable" or "power" ? "power" : requestedKind is "pipe" or "gas" ? "gas" : "";
            if (kind.Length == 0) return new(false, 0);
            bool Served(string id) => kind == "power" ? graph.Power.Contains(id) : graph.Gas.Contains(id) || graph.Sink.Contains(id);
            if (!cache.TryGetValue((from, kind), out var distance))
            {
                var sourceRooms = RoomIds(from);
                var roots = graph.Ports.Values.Where(port => port.ProfilePort.Kind == kind && Served(port.Id) && rooms.TryGetValue(port.InstanceId, out var room) && sourceRooms.Contains(room)).Select(port => port.Id).ToList();
                distance = roots.ToDictionary(id => id, _ => 0, StringComparer.Ordinal); var queue = new PriorityQueue<string, int>(); foreach (var root in roots) queue.Enqueue(root, 0);
                var previous = new Dictionary<string, string>(StringComparer.Ordinal);
                while (queue.TryDequeue(out var current, out var cost))
                {
                    if (distance[current] != cost) continue;
                    foreach (var next in undirected[current].Where(next => graph.Ports[next].ProfilePort.Kind == kind && Served(next)))
                    {
                        var nextCost = cost + graph.Ports[current].Position.Distance(graph.Ports[next].Position);
                        if (distance.TryGetValue(next, out var old) && old <= nextCost) continue;
                        distance[next] = nextCost; previous[next] = current; queue.Enqueue(next, nextCost);
                    }
                }
                cache[(from, kind)] = distance;
                predecessors[(from, kind)] = previous;
            }
            var targetRooms = RoomIds(to); var target = graph.Ports.Values.Where(port => port.ProfilePort.Kind == kind && Served(port.Id) && rooms.TryGetValue(port.InstanceId, out var room) && targetRooms.Contains(room) && distance.ContainsKey(port.Id)).OrderBy(port => distance[port.Id]).ThenBy(port => port.Id, StringComparer.Ordinal).FirstOrDefault();
            if (target == null) return new(false, 0);
            var path = new List<string> { target.Id }; while (predecessors[(from, kind)].TryGetValue(path[^1], out var parent)) path.Add(parent); path.Reverse();
            return new(true, distance[target.Id]) { PortIds = path, Cells = path.Select(id => graph.Ports[id].Position).ToList(), Connections = path.Select(id => graph.Ports[id].ProfilePort.Connection).ToList() };
        };
    }

    private static void ValidateControls(StationDesignProject project, Dictionary<string, (DesignSystemDevice Binding, DesignEntity Entity, DesignRoom Room, NativeSystemProfile Profile)> devices, NativeSystemsReport report, bool due, Func<string, DesignRoom, string> scopeKey)
    {
        if (!due) return;
        foreach (var rule in project.Definition.Systems.Controls)
        foreach (var controller in devices.Values.Where(item => item.Binding.RequirementId == rule.ControllerRuleId))
        {
            var scope = scopeKey(rule.Scope, controller.Room);
            var expected = devices.Values.Where(item => rule.DeviceRuleIds.Contains(item.Binding.RequirementId, StringComparer.Ordinal) && scopeKey(rule.Scope, item.Room) == scope).Select(item => item.Binding.Id).ToHashSet(StringComparer.Ordinal);
            var control = project.Layout.SystemControls.FirstOrDefault(control => control.RequirementId == rule.Id && control.ControllerDeviceId == controller.Binding.Id);
            if (control == null || expected.Count > rule.MaxDevices || control.DeviceIds.Count != control.DeviceIds.Distinct(StringComparer.Ordinal).Count() || !expected.SetEquals(control.DeviceIds) || !controller.Profile.RequiredComponents.Contains(rule.Component, StringComparer.Ordinal) && !controller.Profile.Components.ContainsKey(rule.Component))
                report.Diagnostics.Add(new(controller.Room.Id, "native-control-binding", "Controller " + controller.Entity.Id + " needs its actual supported component and exact scoped device UID binding list; same frequency alone does not bind devices.", rule.Priority == "required" ? "error" : "warning"));
        }
        foreach (var controller in devices.Values.Where(item => item.Profile.RequiredComponents.Contains("DeviceList", StringComparer.Ordinal)))
            if (!project.Layout.SystemControls.Any(control => control.ControllerDeviceId == controller.Binding.Id && control.DeviceIds.Count > 0))
                report.Diagnostics.Add(new(controller.Room.Id, "native-control-unbound", "Native controller " + controller.Entity.Id + " has no nonempty actual DeviceList; native empty allow-lists suppress broadcasts."));
        var alarms = devices.Values.Where(item => item.Profile.RequiredComponents.Contains("FireAlarm", StringComparer.Ordinal)).ToList();
        var closures = alarms.Count(alarm => project.Layout.SystemControls.Where(control => control.ControllerDeviceId == alarm.Binding.Id).SelectMany(control => control.DeviceIds).Any(id => devices.TryGetValue(id, out var target) && target.Profile.RequiredComponents.Contains("Firelock", StringComparer.Ordinal)));
        report.Metrics["fireDetectionControllers"] = alarms.Count(alarm => project.Layout.SystemControls.Any(control => control.ControllerDeviceId == alarm.Binding.Id && control.DeviceIds.Count > 0));
        report.Metrics["fireAutomaticClosureControllers"] = closures;
        if (closures < alarms.Count) report.Diagnostics.Add(new("systems.controls", "native-firelock-outputs", $"{alarms.Count - closures} fire alarm controllers have no actual Firelock output. Sensor/control wiring does not provide automatic boundary closure through ordinary airlocks.", "warning"));
        foreach (var control in project.Layout.SystemControls)
            if (!project.Definition.Systems.Controls.Any(rule => rule.Id == control.RequirementId) || !devices.ContainsKey(control.ControllerDeviceId) || control.DeviceIds.Any(id => !devices.ContainsKey(id)))
                report.Diagnostics.Add(new(control.Id, "native-control-reference", "A retained native control record has an unknown rule/controller/device and must be reconciled explicitly."));
    }

    private sealed record DisposalTrace(List<string> Witness, HashSet<string> Outlets);

    private static List<string> DisposalWitness(NativeGraph graph, string portId, HashSet<string> outlets) => TraceDisposal(graph, portId, outlets).Witness;

    private static DisposalTrace TraceDisposal(NativeGraph graph, string portId, HashSet<string> outlets)
    {
        var start = graph.Ports[portId]; if (outlets.Contains(start.InstanceId) && start.ProfilePort.Connection == "disposalEntry") return new([portId], [start.InstanceId]);
        var index = graph.Ports.Values.Where(port => port.ProfilePort.Kind == "disposal" && port.ProfilePort.Connection != "disposalUnit").GroupBy(port => port.Position).ToDictionary(group => group.Key, group => group.ToList());
        var entries = start.ProfilePort.Connection == "disposalUnit" ? (index.GetValueOrDefault(start.Position) ?? []).Where(port => port.ProfilePort.Connection == "disposalEntry").ToList() : [start];
        if (entries.Count != 1) return new([], []);
        var seed = (Id: entries[0].Id, Incoming: new Cell());
        var edges = new Dictionary<(string Id, Cell Incoming), List<(string Id, Cell Incoming)>>();
        var reverse = new Dictionary<(string Id, Cell Incoming), List<(string Id, Cell Incoming)>>();
        var success = new HashSet<(string Id, Cell Incoming)>(); var dead = new HashSet<(string Id, Cell Incoming)>();
        var pending = new Queue<(string Id, Cell Incoming)>(); pending.Enqueue(seed);
        while (pending.TryDequeue(out var state))
        {
            if (edges.ContainsKey(state)) continue;
            edges[state] = []; var current = graph.Ports[state.Id]; var exits = current.Directions;
            if (exits.Count == 0) { dead.Add(state); continue; }
            var first = exits[0]; var back = new Cell(-state.Incoming.X, -state.Incoming.Y);
            if (state.Incoming != new Cell() && first == back && exits.Count == 1)
            { if (outlets.Contains(current.InstanceId)) success.Add(state); else dead.Add(state); continue; }
            var choices = state.Incoming != new Cell() && first == back ? exits.Skip(1).ToList() : [first];
            foreach (var direction in choices)
            {
                var next = (index.GetValueOrDefault(current.Position + direction) ?? []).Where(port => port.Directions.Contains(new(-direction.X, -direction.Y))).ToList();
                if (next.Count != 1) { dead.Add(state); continue; }
                var target = (next[0].Id, direction); edges[state].Add(target); pending.Enqueue(target);
                if (!reverse.TryGetValue(target, out var parents)) reverse[target] = parents = []; parents.Add(state);
            }
        }
        var needed = edges.ToDictionary(pair => pair.Key, pair => pair.Value.Count); var confirmed = new Queue<(string Id, Cell Incoming)>(success);
        while (confirmed.TryDequeue(out var state))
            foreach (var parent in reverse.GetValueOrDefault(state) ?? []) if (--needed[parent] == 0 && !dead.Contains(parent) && success.Add(parent)) confirmed.Enqueue(parent);
        if (!success.Contains(seed)) return new([], []); // loops and any random branch leading to a dead end remain unverified
        var witness = new List<string>(); var cursor = seed;
        while (true) { witness.Add(cursor.Id); if (edges[cursor].Count == 0) break; cursor = edges[cursor][0]; }
        return new(witness, success.Select(state => graph.Ports[state.Id].InstanceId).Where(outlets.Contains).ToHashSet(StringComparer.Ordinal));
    }
}
