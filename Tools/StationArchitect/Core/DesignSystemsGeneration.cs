namespace Content.StationArchitect;

/// <summary>Places real authored native devices, then constructs networks from configured native port-bearing entities.</summary>
public static partial class DesignSystemsGeneration
{
    public static StationDesignProject RunDevices(StationDesignProject source)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew(); DesignSystems.TraceNative("devices.begin");
        var project = ProgramJson.Clone(source); var issues = new List<ProgramDiagnostic>(); var program = project.Definition.Systems;
        if (!program.Enabled) return Finish(project, "systemsDevices", issues);
        var profiles = program.Profiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        var protectedIds = project.Layout.SystemDevices.Where(device => device.Edited || device.Locked || project.Layout.Equipment.Any(entity => entity.Id == device.EntityId && entity.Pinned)).Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
        protectedIds.UnionWith(project.Layout.SystemNetworks.Where(network => network.Edited || network.Locked || network.Segments.Any(segment => segment.Edited || segment.Locked)).SelectMany(network => network.Terminals.Select(terminal => terminal.DeviceId)));
        protectedIds.UnionWith(project.Layout.SystemControls.Where(control => control.Edited || control.Locked).SelectMany(control => control.DeviceIds.Append(control.ControllerDeviceId)));
        var retainedEntities = project.Layout.SystemDevices.Where(device => protectedIds.Contains(device.Id)).Select(device => device.EntityId).ToHashSet(StringComparer.Ordinal);
        project.Layout.SystemDevices.RemoveAll(device => !protectedIds.Contains(device.Id));
        project.Layout.Equipment.RemoveAll(entity => entity.SystemRuleId.Length > 0 && !entity.Pinned && !retainedEntities.Contains(entity.Id));
        var placementBudgets = new Dictionary<(string Rule, string Scope), int>();
        foreach (var minimumPass in new[] { true, false })
        foreach (var rule in program.Devices)
        {
            if (!minimumPass && (rule.BindExisting || rule.BindInterfaces || rule.BindTransport)) continue;
            var ruleElapsed = System.Diagnostics.Stopwatch.StartNew(); DesignSystems.TraceNative("devices.rule.begin " + rule.Id + " devices=" + project.Layout.SystemDevices.Count);
            if (!profiles.TryGetValue(rule.ProfileId, out var profile)) { issues.Add(new(rule.Id, "system-device-profile", "No declared native profile exists for this device rule.")); continue; }
            if (rule.BindInterfaces || rule.BindTransport)
            {
                var variants = DesignSystems.AllowedProfiles(project, rule);
                foreach (var entity in rule.BindTransport ? DesignSystems.TransportTargets(project, rule) : DesignSystems.InterfaceTargets(project, rule))
                {
                    if (project.Layout.SystemDevices.Any(device => device.RequirementId == rule.Id && device.EntityId == entity.Id)) continue;
                    var variant = variants.FirstOrDefault(candidate => candidate.Prototype == entity.Prototype);
                    if (variant == null) { issues.Add(new(entity.Id, "system-interface-profile", "A selected physical doorway has no matching native receiver profile and was not silently excluded.")); continue; }
                    if (project.Layout.SystemDevices.Count >= program.Generation.MaxDevices) { issues.Add(new(rule.Id, "system-device-budget", "The native device budget prevents binding every selected doorway.")); break; }
                    var scope = DesignSystems.DeviceScopeKey(project, rule, entity); var binding = Bind(rule, scope, entity, 1); binding.ProfileId = variant.Id; project.Layout.SystemDevices.Add(binding);
                }
                DesignSystems.TraceNative("devices.rule.end " + rule.Id + " ms=" + ruleElapsed.ElapsedMilliseconds + " devices=" + project.Layout.SystemDevices.Count); continue;
            }
            foreach (var group in DesignSystems.TargetGroups(project, rule).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var rooms = group.Value; var roomIds = rooms.Select(room => room.Id).ToHashSet(StringComparer.Ordinal);
                var current = project.Layout.SystemDevices.Where(device => device.RequirementId == rule.Id && device.ScopeId == group.Key).ToList();
                var candidatesRemaining = placementBudgets.GetValueOrDefault((rule.Id, group.Key), program.Generation.MaxDeviceCandidatesPerScope);
                if (rule.BindExisting)
                {
                    var variants = DesignSystems.AllowedProfiles(project, rule);
                    foreach (var entity in project.Layout.Equipment.Where(entity => entity.SystemRuleId.Length == 0 && roomIds.Contains(entity.RoomId) && variants.Any(variant => variant.Prototype == entity.Prototype) &&
                        (rule.ContentIds.Count == 0 || rule.ContentIds.Contains(entity.RequirementId, StringComparer.Ordinal)) && !current.Any(device => device.EntityId == entity.Id)).OrderBy(entity => entity.Id, StringComparer.Ordinal))
                    {
                        if (project.Layout.SystemDevices.Count >= program.Generation.MaxDevices) { issues.Add(new(rule.Id, "system-device-budget", "The native binding budget was reached; some selected hardware remains explicitly unbound.")); break; }
                        var binding = Bind(rule, group.Key, entity, NextOrdinal(current, rule.Id, group.Key)); binding.ProfileId = variants.First(variant => variant.Prototype == entity.Prototype).Id;
                        current.Add(binding); project.Layout.SystemDevices.Add(binding);
                    }
                }
                else while (true)
                {
                    var hardware = current.Select(device => DesignSystems.FindEntity(project, device)).Where(entity => entity != null).Select(entity => entity!).ToList();
                    var uncovered = minimumPass ? [] : DesignSystems.UncoveredCells(project, rule, rooms, hardware);
                    var desired = minimumPass ? rule.Content.Count.Min : rule.Content.Count.Target;
                    if (current.Count >= desired && uncovered.Count == 0 || current.Count >= rule.Content.Count.Max) break;
                    if (project.Layout.SystemDevices.Count >= program.Generation.MaxDevices) { issues.Add(new(rule.Id, "system-device-budget", "The native device budget was reached before target/coverage requirements were met.")); break; }
                    var ordinal = NextOrdinal(current, rule.Id, group.Key);
                    var aim = uncovered.Count == 0 ? (Cell?) null : uncovered.OrderByDescending(cell => hardware.Count == 0 ? cell.Distance(LayoutGeometry.Bounds(rooms.SelectMany(room => room.Cells)).Center) : hardware.Min(entity => cell.Distance(entity.Position))).ThenBy(cell => cell.Y).ThenBy(cell => cell.X).First();
                    var entity = Place(project, rule, profile, rooms, group.Key, ordinal, ref candidatesRemaining, aim);
                    if (entity == null) break;
                    project.Layout.Equipment.Add(entity); var binding = Bind(rule, group.Key, entity, ordinal); project.Layout.SystemDevices.Add(binding); current.Add(binding);
                }
                placementBudgets[(rule.Id, group.Key)] = candidatesRemaining;
                if (minimumPass && !rule.BindExisting) continue;
                if (current.Count < rule.Content.Count.Min) issues.Add(new(rule.Id + "@" + group.Key, "system-device-fit", $"Only {current.Count} of at least {rule.Content.Count.Min} native devices fit/bind in this scope. Preserve equipment/work clearances or enlarge the selected rooms."));
                else if (current.Count < rule.Content.Count.Target) issues.Add(new(rule.Id + "@" + group.Key, "system-device-target", $"Placed/bound {current.Count}; preferred count is {rule.Content.Count.Target}.", "warning"));
                if (current.Count > rule.Content.Count.Max) issues.Add(new(rule.Id + "@" + group.Key, "system-device-count", "Protected native devices exceed the current maximum and were retained for explicit correction."));
                if (candidatesRemaining <= 0) issues.Add(new(rule.Id + "@" + group.Key, "system-device-budget", "The configured candidate placement budget was exhausted; no missing device was inferred present."));
                var missingCoverage = DesignSystems.UncoveredCells(project, rule, rooms, current.Select(device => DesignSystems.FindEntity(project, device)).Where(entity => entity != null).Select(entity => entity!));
                if (missingCoverage.Count > 0) issues.Add(new(rule.Id + "@" + group.Key, "system-device-coverage", $"{missingCoverage.Count} actual floor tiles remain beyond this rule's geodesic coverage after bounded placement up to Count.Max."));
            }
            DesignSystems.TraceNative("devices.rule.end " + rule.Id + " ms=" + ruleElapsed.ElapsedMilliseconds + " devices=" + project.Layout.SystemDevices.Count);
        }
        DesignSystems.TraceNative("devices.controls.begin elapsedMs=" + elapsed.ElapsedMilliseconds);
        project.Layout.SystemControls.RemoveAll(control => !control.Edited && !control.Locked);
        var scopeKey = DesignSystems.ScopeResolver(project);
        var resolveRoom = DesignSystems.RoomResolver(project);
        var resolveEntity = DesignSystems.EntityResolver(project, resolveRoom: resolveRoom);
        foreach (var rule in program.Controls)
        {
            var controllers = project.Layout.SystemDevices.Where(device => device.RequirementId == rule.ControllerRuleId).OrderBy(device => device.Id, StringComparer.Ordinal);
            foreach (var controller in controllers)
            {
                var entity = resolveEntity(controller); var room = entity == null ? null : resolveRoom(entity.RoomId);
                if (room == null) continue; var scope = scopeKey(rule.Scope, room); var id = "system-control:" + rule.Id + ":" + controller.Id;
                if (project.Layout.SystemControls.Any(control => control.Id == id)) continue;
                var devices = project.Layout.SystemDevices.Where(device => rule.DeviceRuleIds.Contains(device.RequirementId, StringComparer.Ordinal)).Where(device =>
                {
                    var hardware = resolveEntity(device); var owner = hardware == null ? null : resolveRoom(hardware.RoomId);
                    return owner != null && scopeKey(rule.Scope, owner) == scope;
                }).Select(device => device.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                if (devices.Count > rule.MaxDevices) { issues.Add(new(id, "system-control-limit", "The selected control scope exceeds MaxDevices; no truncated device list was published.")); continue; }
                project.Layout.SystemControls.Add(new() { Id = id, RequirementId = rule.Id, ControllerDeviceId = controller.Id, DeviceIds = devices });
            }
        }
        DesignSystems.TraceNative("devices.controls.end elapsedMs=" + elapsed.ElapsedMilliseconds); return Finish(project, "systemsDevices", issues);
    }

    private static DesignSystemDevice Bind(SystemDeviceRule rule, string scope, DesignEntity entity, int ordinal) => new()
    { Id = DeviceId(rule.Id, scope, ordinal), RequirementId = rule.Id, EntityId = entity.Id, ProfileId = rule.ProfileId, ScopeId = scope };
    private static string DeviceId(string rule, string scope, int ordinal) => "system-device:" + rule + ":" + scope + "#" + ordinal;
    private static int NextOrdinal(List<DesignSystemDevice> current, string rule, string scope)
    { var next = 1; while (current.Any(device => device.Id == DeviceId(rule, scope, next))) next++; return next; }

    private static DesignEntity? Place(StationDesignProject project, SystemDeviceRule rule, NativeSystemProfile profile, List<DesignRoom> rooms, string scope, int ordinal, ref int candidatesRemaining, Cell? aim = null)
    {
        if (rule.Content.InterfaceType.Length > 0) return null; // native boundary openings must bind real ordinary content/interfaces
        var candidate = new DesignEntity { Prototype = profile.Prototype, SystemRuleId = rule.Id, Placement = rule.Content.Placement };
        var occupied = project.Layout.Equipment.Where(entity => !DesignSystems.CanShareCell(project, candidate, entity)).SelectMany(entity => entity.Footprint).ToHashSet();
        var workOccupied = project.Layout.Equipment.SelectMany(entity => entity.WorkArea.Concat(DesignSystems.BlocksCrew(project, entity) ? entity.Footprint : [])).ToHashSet();
        if (DesignSystems.BlocksCrew(project, candidate)) occupied.UnionWith(project.Layout.Equipment.SelectMany(entity => entity.WorkArea).Concat(DesignTransport.EndpointFloorCells(project))
            .Concat(project.Layout.Interfaces.SelectMany(port => port.Cells.Concat([port.FromApproach, port.ToApproach]))).Concat(project.Layout.Routes.Where(route => route.Kind == "walking").SelectMany(route => route.Cells)));
        var dockSpace = DesignDockGeometry.ReservedCells(project); occupied.UnionWith(dockSpace);
        var boundary = project.Layout.Walls.Concat(project.Layout.Hull).Concat(project.Layout.Windows).ToHashSet();
        var exterior = rule.Content.Placement == "spaceBoundary" ? rooms.SelectMany(room => DesignMeasurements.SpaceBoundaryCells(project, room)).ToHashSet() : boundary;
        var portExitsFit = PortExitValidator(project, rule, profile);
        var scopeCenter = aim ?? LayoutGeometry.Bounds(rooms.SelectMany(room => room.Cells)).Center;
        foreach (var room in rooms.OrderBy(room => room.Seed.Distance(scopeCenter)).ThenBy(room => room.Id, StringComparer.Ordinal))
        {
            var floor = room.Cells.ToHashSet(); var positions = rule.Content.Placement == "floor" ? floor : boundary.Where(cell => Cell.Directions.Any(direction => floor.Contains(cell + direction))).ToHashSet();
            foreach (var position in positions.OrderBy(cell => cell.Distance(scopeCenter)).ThenBy(cell => LayoutGeometry.StableOrder(rule.Id + scope + ordinal + cell, project.Settings.Seed)))
            foreach (var rotation in new[] { 0, 90, 180, 270 })
            {
                if (candidatesRemaining-- <= 0) return null;
                if (!DesignEquipmentGeometry.TryExpectedFootprint(project.Settings, profile.Prototype, position, rotation, out var footprint) || footprint.Any(occupied.Contains)) continue;
                if (rule.Content.Placement == "floor" ? footprint.Any(cell => !floor.Contains(cell)) : footprint.Any(cell => !boundary.Contains(cell) || !Cell.Directions.Any(direction => floor.Contains(cell + direction)) || !exterior.Contains(cell))) continue;
                if (!portExitsFit(position, rotation)) continue;
                var required = DesignSystems.BlocksCrew(project, candidate) ? Math.Max(rule.Content.MinArea, footprint.Count) : rule.Content.MinArea;
                var reserved = footprint.Where(cell => floor.Contains(cell) && !workOccupied.Contains(cell)).ToHashSet(); var queue = new Queue<Cell>(reserved);
                if (DesignSystems.BlocksCrew(project, candidate) && !footprint.All(reserved.Contains)) continue;
                if (queue.Count == 0) foreach (var cell in footprint.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Where(floor.Contains).Where(cell => !workOccupied.Contains(cell)).Distinct()) { reserved.Add(cell); queue.Enqueue(cell); }
                while (reserved.Count < required && queue.TryDequeue(out var cell)) foreach (var direction in Cell.Directions)
                { var next = cell + direction; if (reserved.Count < required && floor.Contains(next) && !workOccupied.Contains(next) && reserved.Add(next)) queue.Enqueue(next); }
                if (reserved.Count < required) continue;
                return new() { Id = "systems:" + rule.Id + ":" + scope + "#" + ordinal, RoomId = room.Id, SystemRuleId = rule.Id, RequirementId = "systems:" + rule.Id, Prototype = profile.Prototype, Position = position, Rotation = rotation, Placement = rule.Content.Placement, Footprint = footprint, WorkArea = LayoutGeometry.Sorted(reserved.Take(required)) };
            }
        }
        return null;
    }

    // Orient unary pipe/disposal endpoints and terminal offsets toward an actual
    // supported first native segment. A nearby tile on the wrong side of a dock
    // or reserved void is not a usable port exit. This is local feasibility only;
    // the subsequent network stage still proves the complete connection.
    private static Func<Cell, int, bool> PortExitValidator(StationDesignProject project, SystemDeviceRule deviceRule, NativeSystemProfile deviceProfile)
    {
        var profiles = project.Definition.Systems.Profiles.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var selected = project.Definition.Systems.Networks.SelectMany(network => network.Terminals
            .Where(terminal => terminal.DeviceRuleId == deviceRule.Id)
            .Select(terminal => (Network: network, Port: deviceProfile.Ports.FirstOrDefault(port => port.Id == terminal.PortId && port.Kind == network.Kind))))
            .Where(item => item.Port != null && (item.Port.Connection is "terminalPort" || item.Port.Kind is "gas" or "disposal" && item.Port.Directions.Count > 0))
            .Select(item => (item.Network, Port: item.Port!, Segments: item.Network.SegmentProfiles.Where(profiles.ContainsKey).Select(id => profiles[id])
                .Where(profile => profile.Kind != "device").SelectMany(profile => new[] { 0, 90, 180, 270 }
                    .Select(rotation => DesignSystems.Ports(profile, new(), rotation, "exit-candidate"))).ToList())).ToList();
        if (selected.Count == 0) return (_, _) => true;
        var canvas = new GridBounds(0, 0, project.Settings.Width, project.Settings.Height);
        var support = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned).SelectMany(room => room.Cells)
            .Concat(project.Layout.PublicCorridors).Concat(project.Layout.Maintenance).Concat(project.Layout.Walls).Concat(project.Layout.Hull)
            .Concat(project.Layout.Windows).Concat(project.Layout.Interfaces.SelectMany(port => port.Cells)).ToHashSet();
        var blocked = DesignDockGeometry.ReservedCells(project); blocked.UnionWith(project.Layout.ReservedSpace);
        var transport = project.Layout.TransportNetworks.SelectMany(network => network.Segments.Select(segment => segment.Position)).ToHashSet();
        return (position, rotation) =>
        {
            var ports = DesignSystems.Ports(deviceProfile, position, rotation, "device-candidate");
            foreach (var item in selected)
            {
                var port = ports.First(value => value.ProfilePort.Id == item.Port.Id);
                var fit = false;
                foreach (var segment in item.Segments)
                {
                    foreach (var entry in segment.Where(candidate => candidate.ProfilePort.Kind == port.ProfilePort.Kind && candidate.ProfilePort.NodeGroup == port.ProfilePort.NodeGroup))
                    foreach (var delta in Cell.Directions.Append(new()))
                    {
                        var origin = port.Position + delta - entry.Position;
                        if (!canvas.Contains(origin) || IsBlocked(origin) || !item.Network.AllowSpace && !support.Contains(origin)) continue;
                        if (segment.Any(candidate => !canvas.Contains(origin + candidate.Position) || IsBlocked(origin + candidate.Position))) continue;
                        var actual = new SystemPortGeometry { Id = entry.Id, InstanceId = entry.InstanceId, ProfilePort = entry.ProfilePort, Position = origin + entry.Position, Directions = entry.Directions };
                        if (DesignSystems.Contacts(port, actual)) { fit = true; break; }
                    }
                    if (fit) break;
                }
                if (!fit) return false;
                bool IsBlocked(Cell cell) => blocked.Contains(cell) || item.Network.Kind == "disposal" && transport.Contains(cell);
            }
            return true;
        };
    }

    private static StationDesignProject Finish(StationDesignProject project, string stage, List<ProgramDiagnostic> issues)
    {
        project.Stage = stage; DesignSystems.TraceNative("generation.validation.begin " + stage); project.Systems = DesignSystems.Validate(project); DesignSystems.TraceNative("generation.validation.end " + stage); project.Systems.Diagnostics = project.Systems.Diagnostics.Concat(issues).Distinct().ToList();
        project.Report = new() { Stage = stage, Complete = false, Diagnostics = [.. project.Systems.Diagnostics], Metrics = project.Systems.Metrics.ToDictionary(pair => "systems." + pair.Key, pair => pair.Value) };
        return project;
    }
}
