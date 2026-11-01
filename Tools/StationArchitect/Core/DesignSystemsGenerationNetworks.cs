namespace Content.StationArchitect;

public static partial class DesignSystemsGeneration
{
    private sealed record RoutedPort(DesignSystemDevice Device, SystemPortGeometry Port, string Role, DesignRoom Room);
    private sealed class NativePart(NativeSystemProfile profile, Cell position, int rotation, string entry)
    {
        public NativeSystemProfile Profile { get; } = profile;
        public Cell Position { get; } = position;
        public int Rotation { get; } = rotation;
        public string Entry { get; } = entry;
        public string Key { get; } = profile.Id + ":" + position.X + "," + position.Y + ":" + rotation + ":" + entry;
        private List<SystemPortGeometry>? _ports;
        public List<SystemPortGeometry> Ports => _ports ??= DesignSystems.Ports(Profile, Position, Rotation, "candidate:" + Key);
    }

    // A route to a power source follows native edges backwards. A powered receiver
    // cannot supply another provider merely because their physical ranges overlap.
    private static bool FlowContact(SystemPortGeometry first, SystemPortGeometry second, bool reverse, IEnumerable<SystemPortGeometry> terminals)
    {
        if (first.ProfilePort.Connection == (reverse ? "rangeProvider" : "rangeReceiver") &&
            second.ProfilePort.Connection == (reverse ? "rangeReceiver" : "rangeProvider")) return false;
        return DesignSystems.ContextualContact(first, second, terminals);
    }

    private static string PortShape(IEnumerable<SystemPortGeometry> ports, bool ordered) => string.Join(";", ports.Select(port => port.ProfilePort.Id + "@" + port.Position.X + "," + port.Position.Y + ":" +
        string.Join("/", (ordered ? port.Directions.AsEnumerable() : port.Directions.OrderBy(direction => direction.Y).ThenBy(direction => direction.X)).Select(direction => direction.X + "," + direction.Y))));

    private static bool SameSegment(DesignSystemSegment segment, NativePart part) => segment.ProfileId == part.Profile.Id &&
        (segment.Rotation == part.Rotation || PortShape(DesignSystems.Ports(part.Profile, new(), segment.Rotation, "shape"), part.Profile.Kind == "disposal") ==
            PortShape(DesignSystems.Ports(part.Profile, new(), part.Rotation, "shape"), part.Profile.Kind == "disposal"));

    public static StationDesignProject Run(StationDesignProject source, string kind)
    {
        if (kind is not ("power" or "gas" or "disposal")) throw new ArgumentException("Native system kind is power, gas or disposal.");
        var project = ProgramJson.Clone(source); var issues = new List<ProgramDiagnostic>(); var program = project.Definition.Systems;
        var stage = kind == "gas" ? "atmos" : kind == "disposal" ? "disposals" : "power";
        if (!program.Enabled) return Finish(project, stage, issues);
        var profiles = program.Profiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        var scopeKey = DesignSystems.ScopeResolver(project);
        var resolveRoom = DesignSystems.RoomResolver(project);
        var resolveEntity = DesignSystems.EntityResolver(project, resolveRoom: resolveRoom);
        var remaining = program.Generation.MaxTotalSearchStates;
        var failedRoutes = new List<(ProgramDiagnostic Issue, string NetworkId, string DeviceId, string PortId)>();
        var rebuiltRules = program.Networks.Where(rule => rule.Kind == kind).Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);
        // Do not borrow an old automatic wire which a later scope is about to
        // delete. Protected segments and whole protected networks remain available.
        foreach (var old in project.Layout.SystemNetworks.Where(network => rebuiltRules.Contains(network.RequirementId) && !network.Edited && !network.Locked))
            old.Segments.RemoveAll(segment => !segment.Edited && !segment.Locked);
        foreach (var rule in program.Networks.Where(rule => rule.Kind == kind))
        {
            var terminals = new List<RoutedPort>();
            foreach (var selector in rule.Terminals)
            {
                var count = 0;
                foreach (var device in project.Layout.SystemDevices.Where(device => device.RequirementId == selector.DeviceRuleId))
                {
                    var entity = resolveEntity(device); var room = entity == null ? null : resolveRoom(entity.RoomId);
                    if (entity == null || room == null || !profiles.TryGetValue(device.ProfileId, out var profile)) continue;
                    var port = DesignSystems.Ports(profile, entity.Position, entity.Rotation, device.Id).FirstOrDefault(port => port.ProfilePort.Id == selector.PortId && port.ProfilePort.Kind == kind);
                    if (port == null) continue; terminals.Add(new(device, port, selector.Role, room)); count++;
                }
                if (count == 0 && selector.Required) issues.Add(new(rule.Id, "system-terminal-missing", $"Required terminal {selector.DeviceRuleId}/{selector.PortId} has no actual bound native device."));
            }
            foreach (var scope in terminals.GroupBy(terminal => scopeKey(rule.Scope, terminal.Room)).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                var id = "system-network:" + rule.Id + ":" + scope.Key;
                var previous = project.Layout.SystemNetworks.FirstOrDefault(network => network.Id == id);
                if (previous is { Edited: true } or { Locked: true }) continue;
                var network = new DesignSystemNetwork { Id = id, RequirementId = rule.Id, ScopeId = scope.Key, Segments = previous?.Segments.Where(segment => segment.Edited || segment.Locked).Select(segment => ProgramJson.Clone(segment)).ToList() ?? [] };
                var points = scope.DistinctBy(terminal => terminal.Device.Id + ":" + terminal.Port.ProfilePort.Id).ToList();
                network.Terminals = points.Select(terminal => new DesignSystemTerminal { DeviceId = terminal.Device.Id, PortId = terminal.Port.ProfilePort.Id, Role = terminal.Role }).ToList();
                if (previous != null) project.Layout.SystemNetworks.Remove(previous); project.Layout.SystemNetworks.Add(network);
                foreach (var selector in rule.Terminals.Where(selector => selector.Required))
                    if (!points.Any(point => point.Device.RequirementId == selector.DeviceRuleId && point.Port.ProfilePort.Id == selector.PortId)) issues.Add(new(id, "system-terminal-scope", $"Required {selector.DeviceRuleId}/{selector.PortId} is absent from this network scope."));
                foreach (var group in points.GroupBy(point => point.Port.ProfilePort.NodeGroup))
                {
                    var ordered = group.OrderBy(point => point.Role is "source" or "outlet" ? 0 : 1).ThenBy(point => point.Device.Id, StringComparer.Ordinal).ToList();
                    if (ordered.Count == 0) continue; var root = ordered[0];
                    // Two adjacent unary canister connectors can face one another
                    // and close off the entire nascent network. Establish an
                    // outward trunk before adding the remaining portable banks.
                    // Connect nearby non-portable terminals before adding banks.
                    // For an entirely portable network, use its furthest endpoint
                    // first so adjacent unary connectors do not close the trunk.
                    if (root.Port.ProfilePort.Connection == "portable" && ordered.Count > 2)
                    {
                        ordered = new[] { root }.Concat(ordered.Skip(1).OrderBy(point => point.Port.ProfilePort.Connection == "portable" ? 1 : 0)
                            .ThenBy(point => (point.Port.ProfilePort.Connection == "portable" ? -1 : 1) * point.Port.Position.Distance(root.Port.Position))
                            .ThenBy(point => point.Device.Id, StringComparer.Ordinal)).ToList();
                    }
                    // Multiple declared supplies are physical terminals, not an
                    // already-connected virtual bus. In particular, isolated gas
                    // canisters cannot contribute pressure to the station pipe.
                    var tree = new List<SystemPortGeometry> { root.Port };
                    var reverseFlow = root.Role == "source";
                    var terminalContext = project.Layout.SystemNetworks.SelectMany(owner => owner.Segments)
                        .Where(segment => profiles.TryGetValue(segment.ProfileId, out var profile) && profile.Ports.Any(port => port.Connection == "terminal"))
                        .SelectMany(segment => DesignSystems.Ports(profiles[segment.ProfileId], segment.Position, segment.Rotation, segment.Id))
                        .Concat(points.Select(point => point.Port)).Where(port => port.ProfilePort.Connection == "terminal").ToList();
                    var retainedPorts = network.Segments.Where(segment => profiles.ContainsKey(segment.ProfileId)).SelectMany(segment => DesignSystems.Ports(profiles[segment.ProfileId], segment.Position, segment.Rotation, segment.Id)).ToList();
                    var expanded = true; while (expanded)
                    { expanded = false; foreach (var port in retainedPorts.Where(port => !tree.Any(old => old.Id == port.Id)).ToList()) if (tree.Any(old => FlowContact(port, old, reverseFlow, terminalContext))) { tree.Add(port); expanded = true; } }
                    var routedCount = 0;
                    foreach (var terminal in ordered.Where(point => point != root))
                    {
                        if (project.Layout.SystemNetworks.Sum(owner => owner.Segments.Count) >= program.Generation.MaxGeneratedSegments) { issues.Add(new(id, "system-segment-limit", "The configured native segment budget was reached; no truncated connection was claimed.")); break; }
                        if (tree.Any(port => FlowContact(terminal.Port, port, reverseFlow, terminalContext))) { tree.Add(terminal.Port); continue; }
                        var path = Route(project, rule, network, terminal.Port, tree, profiles, reverseFlow, resolveEntity, ref remaining);
                        if (++routedCount % 32 == 0) DesignSystems.TraceNative("network.progress " + id + " routed=" + routedCount + " segments=" + network.Segments.Count + " statesRemaining=" + remaining + " ms=" + elapsed.ElapsedMilliseconds);
                        if (path == null)
                        {
                            failedRoutes.Add((new(id, remaining <= 0 ? "system-routing-budget" : "system-native-route", $"No configured native segment/rotation path connects {terminal.Device.Id}/{terminal.Port.ProfilePort.Id}. Check node groups, layers, terminal adapters, provider ranges, free supported tiles and routing budgets.", rule.Priority == "required" ? "error" : "warning"), id, terminal.Device.Id, terminal.Port.ProfilePort.Id));
                            continue;
                        }
                        foreach (var part in path)
                        {
                            // Scope is service ownership, not electrical insulation.
                            // Reuse one identical physical segment/UID across scopes.
                            var existing = project.Layout.SystemNetworks.SelectMany(owner => owner.Segments).FirstOrDefault(segment => segment.Position == part.Position && SameSegment(segment, part));
                            if (existing == null)
                            {
                                var replaced = network.Segments.Where(segment => !segment.Edited && !segment.Locked && segment.Position == part.Position && profiles.TryGetValue(segment.ProfileId, out var other) && Conflict(other, part.Profile)).Select(segment => segment.Id).ToHashSet(StringComparer.Ordinal);
                                network.Segments.RemoveAll(segment => replaced.Contains(segment.Id));
                                tree.RemoveAll(port => replaced.Contains(port.InstanceId));
                                terminalContext.RemoveAll(port => replaced.Contains(port.InstanceId));
                                existing = new() { Id = id + "/" + part.Profile.Id + "@" + part.Position.X + "," + part.Position.Y + ":" + part.Rotation, ProfileId = part.Profile.Id, Position = part.Position, Rotation = part.Rotation };
                                network.Segments.Add(existing);
                            }
                            var newPorts = DesignSystems.Ports(part.Profile, part.Position, part.Rotation, existing.Id);
                            tree.AddRange(newPorts.Where(port => !tree.Any(old => old.Id == port.Id)));
                            terminalContext.AddRange(newPorts.Where(port => port.ProfilePort.Connection == "terminal"));
                        }
                        tree.Add(terminal.Port);
                    }
                }
                DesignSystems.TraceNative("network.end " + id + " terminals=" + points.Count + " segments=" + network.Segments.Count + " statesRemaining=" + remaining + " ms=" + elapsed.ElapsedMilliseconds);
            }
        }
        if (kind == "disposal") NormalizeDisposal(project, profiles, resolveEntity, issues);
        SatisfyUtilityRequirements(project, kind, profiles, resolveEntity, ref remaining, issues);
        var result = Finish(project, stage, issues);
        // A later scope may complete a physical connection which an earlier
        // bounded search missed. Keep failures only when fresh native proof is absent.
        foreach (var failure in failedRoutes.Where(failure => !result.Systems!.Terminals.Any(terminal => terminal.NetworkId == failure.NetworkId && terminal.DeviceId == failure.DeviceId && terminal.PortId == failure.PortId && terminal.Connected && terminal.SourceReachable)))
        { result.Systems!.Diagnostics.Add(failure.Issue); result.Report!.Diagnostics.Add(failure.Issue); }
        return result;
    }

    private static List<NativePart>? Route(StationDesignProject project, SystemNetworkRule rule, DesignSystemNetwork network, SystemPortGeometry start, List<SystemPortGeometry> goals,
        Dictionary<string, NativeSystemProfile> profiles, bool reverseFlow, Func<DesignSystemDevice, DesignEntity?> resolveEntity, ref int remaining, HashSet<Cell>? supportRestriction = null)
    {
        var allowed = rule.SegmentProfiles.Where(profiles.ContainsKey).Select(id => profiles[id]).Where(profile => profile.Kind != "device" && profile.Ports.Any(port => port.Kind == rule.Kind)).ToList();
        if (allowed.Count == 0 || remaining <= 0) return null;
        var canvas = new GridBounds(0, 0, project.Settings.Width, project.Settings.Height);
        var support = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned).SelectMany(room => room.Cells).Concat(project.Layout.PublicCorridors).Concat(project.Layout.Maintenance)
            .Concat(project.Layout.Walls).Concat(project.Layout.Hull).Concat(project.Layout.Windows).Concat(project.Layout.Interfaces.SelectMany(port => port.Cells)).ToHashSet();
        if (supportRestriction != null) support.IntersectWith(supportRestriction);
        var blocked = DesignDockGeometry.ReservedCells(project); blocked.UnionWith(project.Layout.ReservedSpace);
        if (rule.Kind == "disposal") blocked.UnionWith(project.Layout.TransportNetworks.SelectMany(transport => transport.Segments.Select(segment => segment.Position)));
        var placed = project.Layout.SystemNetworks.SelectMany(owner => owner.Segments.Select(segment => (Owner: owner, Segment: segment))).Where(item => profiles.ContainsKey(item.Segment.ProfileId)).GroupBy(item => item.Segment.Position).ToDictionary(group => group.Key, group => group.ToList());
        var placedPorts = placed.Values.SelectMany(value => value).ToDictionary(item => item.Segment.Id,
            item => DesignSystems.Ports(profiles[item.Segment.ProfileId], item.Segment.Position, item.Segment.Rotation, item.Segment.Id), StringComparer.Ordinal);
        var hardwarePorts = project.Layout.SystemDevices.Select(device => (Device: device, Entity: resolveEntity(device))).Where(item => item.Entity != null && profiles.ContainsKey(item.Device.ProfileId))
            .SelectMany(item => DesignSystems.Ports(profiles[item.Device.ProfileId], item.Entity!.Position, item.Entity.Rotation, item.Entity.Id)).GroupBy(port => port.Position).ToDictionary(group => group.Key, group => group.ToList());
        var deviceIndex = project.Layout.SystemDevices.ToDictionary(device => device.Id, StringComparer.Ordinal);
        var portableAdapters = network.Terminals.Where(terminal => deviceIndex.ContainsKey(terminal.DeviceId)).Select(terminal => (Terminal: terminal, Device: deviceIndex[terminal.DeviceId]))
            .Select(item => (item.Terminal, item.Device, Entity: resolveEntity(item.Device))).Where(item => item.Entity != null && profiles.ContainsKey(item.Device.ProfileId))
            .SelectMany(item => DesignSystems.Ports(profiles[item.Device.ProfileId], item.Entity!.Position, item.Entity.Rotation, item.Entity.Id)
                .Where(port => port.ProfilePort.Id == item.Terminal.PortId && port.ProfilePort.Connection == "portable" && port.ProfilePort.NodeGroup == start.ProfilePort.NodeGroup))
            .Select(port => port.Position).ToHashSet();
        // Straight pipes and omnidirectional cables have equivalent rotations. Keep
        // disposal exit order, however: the native transport uses that ordering.
        var variants = allowed.SelectMany(profile => new[] { 0, 90, 180, 270 }
            .Select(rotation => (Profile: profile, Rotation: rotation, Ports: DesignSystems.Ports(profile, new(), rotation, "variant")))
            .DistinctBy(variant => PortShape(variant.Ports, profile.Kind == "disposal"))).ToList();
        var terminalIndex = placedPorts.Values.SelectMany(ports => ports).Concat(hardwarePorts.Values.SelectMany(ports => ports))
            .Where(port => port.ProfilePort.Connection == "terminal").GroupBy(port => port.Position).ToDictionary(group => group.Key, group => group.ToList());
        var goalIndex = goals.GroupBy(port => port.Position).ToDictionary(group => group.Key, group => group.ToList());
        var rangeGoals = goals.Where(port => port.ProfilePort.Connection is "rangeProvider" or "rangeReceiver").ToList();
        IEnumerable<SystemPortGeometry> NearbyTerminals(SystemPortGeometry first, SystemPortGeometry second) =>
            (terminalIndex.GetValueOrDefault(first.Position) ?? []).Concat(terminalIndex.GetValueOrDefault(second.Position) ?? []);
        bool Contact(SystemPortGeometry first, SystemPortGeometry second, IEnumerable<SystemPortGeometry>? pathTerminals = null) =>
            FlowContact(first, second, reverseFlow, pathTerminals == null ? NearbyTerminals(first, second) : NearbyTerminals(first, second).Concat(pathTerminals));
        bool Goal(SystemPortGeometry port) => (port.ProfilePort.Connection is "rangeProvider" or "rangeReceiver" ? rangeGoals :
            Cell.Directions.Append(new()).SelectMany(direction => goalIndex.GetValueOrDefault(port.Position + direction) ?? [])).Any(other => Contact(port, other));
        // Manhattan distance to the connected tree is a lower bound, independent
        // of walls and equipment. Scale it by the largest native contact/transfer
        // jump so custom multi-port profiles cannot make the heuristic overestimate.
        var width = project.Settings.Width; var height = project.Settings.Height;
        var distance = Enumerable.Repeat(width + height, width * height).ToArray();
        var useHeuristic = goals.All(goal => canvas.Contains(goal.Position));
        foreach (var goal in goals.Where(goal => canvas.Contains(goal.Position))) distance[goal.Position.Y * width + goal.Position.X] = 0;
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var index = y * width + x;
            if (x > 0) distance[index] = Math.Min(distance[index], distance[index - 1] + 1);
            if (y > 0) distance[index] = Math.Min(distance[index], distance[index - width] + 1);
        }
        for (var y = height - 1; y >= 0; y--) for (var x = width - 1; x >= 0; x--)
        {
            var index = y * width + x;
            if (x + 1 < width) distance[index] = Math.Min(distance[index], distance[index + 1] + 1);
            if (y + 1 < height) distance[index] = Math.Min(distance[index], distance[index + width] + 1);
        }
        var maximumContact = Math.Max(1, (int)Math.Ceiling(2 * variants.SelectMany(variant => variant.Ports).Concat(goals).Append(start).Max(port => port.ProfilePort.Range)));
        var maximumTransfer = variants.Max(variant => variant.Ports.Max(first => variant.Ports.Max(second => first.Position.Distance(second.Position))));
        var maximumStep = maximumContact + maximumTransfer;
        int Estimate(NativePart part) => !useHeuristic ? 0 : (Math.Max(0, Reachable(part, reverseFlow).Min(port => distance[port.Position.Y * width + port.Position.X]) - maximumContact) + maximumStep - 1) / maximumStep;
        var queue = new PriorityQueue<NativePart, (int Estimate, int Cost, int Y, int X)>(); var costs = new Dictionary<string, int>(StringComparer.Ordinal); var parents = new Dictionary<string, string?>(StringComparer.Ordinal); var parts = new Dictionary<string, NativePart>(StringComparer.Ordinal);
        var pathTerminals = new Dictionary<string, List<SystemPortGeometry>>(StringComparer.Ordinal);
        foreach (var part in Next(start, 1)) Enqueue(part, null, 1);
        var local = project.Definition.Systems.Generation.MaxStatesPerConnection;
        while (queue.TryDequeue(out var part, out var priority) && local-- > 0 && remaining-- > 0)
        {
            if (costs[part.Key] != priority.Cost) continue;
            var ports = Reachable(part, reverseFlow);
            if (ports.Any(Goal))
            {
                var path = new List<NativePart>(); string? key = part.Key;
                while (key != null) { path.Add(parts[key]); key = parents[key]; }
                path.Reverse();
                var context = path.SelectMany(item => item.Ports).Concat(goals).Append(start).Where(port => port.ProfilePort.Connection == "terminal").ToList();
                var previous = new List<SystemPortGeometry> { start }; var valid = true;
                foreach (var item in path)
                {
                    var entry = item.Ports.First(port => port.ProfilePort.Id == item.Entry);
                    if (!previous.Any(port => Contact(port, entry, context))) { valid = false; break; }
                    previous = Reachable(item, reverseFlow);
                }
                if (valid && previous.Any(port => goals.Any(goal => Contact(port, goal, context)))) return path;
            }
            if (priority.Cost >= rule.MaxLength) continue;
            foreach (var port in ports) foreach (var next in Next(port, priority.Cost + 1)) Enqueue(next, part.Key, priority.Cost + 1);
        }
        return null;

        void Enqueue(NativePart part, string? parent, int cost)
        {
            if (costs.TryGetValue(part.Key, out var old) && old <= cost) return;
            costs[part.Key] = cost; parts[part.Key] = part; parents[part.Key] = parent;
            var ownTerminals = part.Ports.Where(port => port.ProfilePort.Connection == "terminal").ToList();
            var inherited = parent == null ? [] : pathTerminals.GetValueOrDefault(parent) ?? [];
            pathTerminals[part.Key] = ownTerminals.Count == 0 ? inherited : inherited.Concat(ownTerminals).ToList();
            queue.Enqueue(part, (cost + Estimate(part), cost, part.Position.Y, part.Position.X));
        }
        IEnumerable<NativePart> Next(SystemPortGeometry current, int nextCost)
        {
            foreach (var variant in variants) foreach (var entry in variant.Ports.Where(port => port.ProfilePort.Kind == rule.Kind && port.ProfilePort.NodeGroup == current.ProfilePort.NodeGroup)) foreach (var delta in Cell.Directions.Append(new()))
            {
                var position = current.Position + delta - entry.Position;
                if (!canvas.Contains(position) || blocked.Contains(position) || (!rule.AllowSpace || supportRestriction != null) && !support.Contains(position)) continue;
                var candidate = new NativePart(variant.Profile, position, variant.Rotation, entry.ProfilePort.Id);
                if (costs.TryGetValue(candidate.Key, out var knownCost) && knownCost <= nextCost) continue;
                var candidatePorts = candidate.Ports;
                // A selected portable canister needs a same-tile native connector.
                // Letting an earlier branch build a tee/cross here strands that
                // supply: a unary GasPort cannot later preserve all those exits.
                if (candidatePorts.Any(port => port.ProfilePort.Kind == "gas" && portableAdapters.Contains(port.Position) && port.ProfilePort.Connection != "pipePort")) continue;
                if (candidatePorts.Any(port => (hardwarePorts.GetValueOrDefault(port.Position) ?? []).Any(hardware => DesignSystems.HardwarePortConflict(hardware.ProfilePort, port.ProfilePort)))) continue;
                var parentKey = current.InstanceId.StartsWith("candidate:", StringComparison.Ordinal) ? current.InstanceId[10..] : "";
                var context = (pathTerminals.GetValueOrDefault(parentKey) ?? []).Concat(candidatePorts.Where(port => port.ProfilePort.Connection == "terminal"));
                if (!Contact(current, candidatePorts.First(port => port.ProfilePort.Id == candidate.Entry), context)) continue;
                if (candidatePorts.Any(port => !canvas.Contains(port.Position) || blocked.Contains(port.Position) || supportRestriction != null && !supportRestriction.Contains(port.Position))) continue;
                var collisions = placed.GetValueOrDefault(position)?.Where(item => Conflict(profiles[item.Segment.ProfileId], candidate.Profile)).ToList() ?? [];
                if (collisions.Any(item => (item.Owner != network || item.Segment.Edited || item.Segment.Locked) && !SameSegment(item.Segment, candidate))) continue;
                var preserve = true;
                foreach (var old in collisions)
                {
                    var oldPorts = placedPorts[old.Segment.Id];
                    var neighbors = Cell.Directions.Append(new()).SelectMany(direction => placed.GetValueOrDefault(position + direction) ?? []).Where(item => item.Segment.Id != old.Segment.Id)
                        .SelectMany(item => placedPorts[item.Segment.Id]).Concat(Cell.Directions.Append(new()).SelectMany(direction => goalIndex.GetValueOrDefault(position + direction) ?? []));
                    if (neighbors.Where(port => oldPorts.Any(oldPort => DesignSystems.Contacts(oldPort, port))).Any(port => !candidatePorts.Any(candidatePort => DesignSystems.Contacts(candidatePort, port)))) { preserve = false; break; }
                }
                if (preserve) yield return candidate;
            }
        }
    }

    private static List<SystemPortGeometry> Reachable(NativePart part, bool reverse)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { part.Entry }; var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var transfer in part.Profile.Transfers.Where(transfer => !transfer.RequiresPower))
            {
                var from = reverse ? transfer.ToPort : transfer.FromPort; var to = reverse ? transfer.FromPort : transfer.ToPort;
                if (ids.Contains(from)) changed |= ids.Add(to); if (transfer.Bidirectional && ids.Contains(to)) changed |= ids.Add(from);
            }
        }
        return part.Ports.Where(port => ids.Contains(port.ProfilePort.Id)).ToList();
    }

    private static bool Conflict(NativeSystemProfile first, NativeSystemProfile second) => first.Ports.Any(a => second.Ports.Any(b => a.Kind == b.Kind && a.NodeGroup == b.NodeGroup &&
        (a.Kind != "gas" || a.PipeLayer == b.PipeLayer) && (a.Kind != "power" || (a.Connection == "terminal") == (b.Connection == "terminal"))));

}
