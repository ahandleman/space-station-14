namespace Content.StationArchitect;

public static partial class DesignSystemsGeneration
{
    /// <summary>Required room utilities receive actual native branches inside their authored spatial mode.</summary>
    private static void SatisfyUtilityRequirements(StationDesignProject project, string kind, Dictionary<string, NativeSystemProfile> profiles,
        Func<DesignSystemDevice, DesignEntity?> resolveEntity, ref int remaining, List<ProgramDiagnostic> issues)
    {
        if (kind is not ("power" or "gas")) return;
        var type = kind == "power" ? "cable" : "pipe";
        var compilation = RequirementCompiler.Compile(project.Definition);
        var rooms = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && room.Cells.Count > 0).ToList();
        var devices = project.Layout.SystemDevices.ToDictionary(device => device.Id, StringComparer.Ordinal);
        var rules = project.Definition.Systems.Networks.Where(rule => rule.Kind == kind).ToDictionary(rule => rule.Id, StringComparer.Ordinal);
        var support = DesignSystems.CreateUtilityRouteSupport(project);
        var inspect = DesignSystems.CreateUtilityRouteInspector(project, final: true, utilityType: _ => type);
        List<DesignRoom> Resolve(string path) => rooms.Where(room => room.Id == path || room.DefinitionPath == path || room.OwnerId == path).ToList();
        HashSet<string> Capabilities(NativeSystemsReport report) => report.Terminals.Where(terminal => terminal.Connected && terminal.SourceReachable)
            .Select(terminal => terminal.NetworkId + "|" + terminal.DeviceId + "|" + terminal.PortId + "|" + terminal.Role).ToHashSet(StringComparer.Ordinal);
        foreach (var requirement in compilation.Connections.Where(connection => connection.Kind == "utility" && connection.Priority == "required" &&
            (connection.InterfaceTypes.Count == 0 || connection.InterfaceTypes.Contains(type))))
        {
            var modes = requirement.Modes.Where(mode => mode is "department" or "direct" or "publicHall" or "maintenance" or "utilityNetwork").ToList();
            if (modes.Count == 0) continue;
            var sources = Resolve(requirement.From);
            var sourceGroups = sources.Any(room => room.OwnerId == requirement.From) ? new List<List<DesignRoom>> { sources } : sources.Select(room => new List<DesignRoom> { room }).ToList();
            var targetGroups = requirement.Selection == "all" ? requirement.To.Select(Resolve).ToList() : new List<List<DesignRoom>> { requirement.To.SelectMany(Resolve).DistinctBy(room => room.Id).ToList() };
            foreach (var sourceGroup in sourceGroups)
            foreach (var targets in targetGroups)
            {
                if (targets.Count == 0 && requirement.Presence == "ifPresent") continue;
                var requests = sourceGroup.SelectMany(from => targets.SelectMany(to => modes.Where(mode => RoomHierarchy.ConnectionScopeAllowed(requirement, from.DefinitionPath, from.Id, from.ParentInstanceId, to.DefinitionPath, to.Id, to.ParentInstanceId) &&
                    (mode != "department" || from.OwnerId == to.OwnerId)).Select(mode => new DesignRoute
                    { Id = requirement.Id + "@" + from.Id + ":" + to.Id + ":" + mode, RequirementId = requirement.Id, From = from.Id, To = to.Id, Mode = mode, Kind = "utility" }))).ToList();
                bool Fulfilled(NativeUtilityConnection proof) => proof.Verified && proof.Cells.Count > 0 && (requirement.MaxDistance == null || proof.Distance <= requirement.MaxDistance) && requirement.MinWidth <= 1;
                var already = requests.Select(request => (Request: request, Proof: inspect(request))).FirstOrDefault(item => Fulfilled(item.Proof));
                if (already.Request != null) { Record(project, already.Request, already.Proof); continue; }
                var before = Capabilities(DesignSystems.Validate(project, true));
                var previousUtilities = project.Layout.Routes.Where(route => route.Kind == "utility" && project.Layout.Utilities.Any(utility => utility.RequirementId == route.RequirementId && utility.From == route.From && utility.To == route.To && utility.Type == type))
                    .Where(route => inspect(route).Verified).ToList();
                var repaired = false; var attempts = 0;
                foreach (var request in requests)
                {
                    var restriction = support(request);
                    foreach (var network in project.Layout.SystemNetworks.Where(network => rules.ContainsKey(network.RequirementId) && !network.Edited && !network.Locked).ToList())
                    {
                        var points = new List<(DesignSystemDevice Device, DesignEntity Entity, SystemPortGeometry Port)>();
                        foreach (var terminal in network.Terminals)
                        {
                            if (!devices.TryGetValue(terminal.DeviceId, out var device) || resolveEntity(device) is not { } entity || !profiles.TryGetValue(device.ProfileId, out var profile)) continue;
                            var port = DesignSystems.Ports(profile, entity.Position, entity.Rotation, device.Id).FirstOrDefault(port => port.ProfilePort.Id == terminal.PortId && port.ProfilePort.Kind == kind);
                            if (port != null && restriction.Contains(port.Position) && (request.Mode != "direct" || port.ProfilePort.Connection is not ("rangeProvider" or "rangeReceiver"))) points.Add((device, entity, port));
                        }
                        var pairs = points.Where(point => point.Entity.RoomId == request.From).SelectMany(from => points.Where(to => to.Entity.RoomId == request.To && to.Port.ProfilePort.NodeGroup == from.Port.ProfilePort.NodeGroup &&
                            (kind != "gas" || to.Port.ProfilePort.PipeLayer == from.Port.ProfilePort.PipeLayer)).Select(to => (From: from.Port, To: to.Port)))
                            .OrderBy(pair => pair.From.Position.Distance(pair.To.Position)).ThenBy(pair => pair.From.Id, StringComparer.Ordinal).ThenBy(pair => pair.To.Id, StringComparer.Ordinal);
                        foreach (var pair in pairs)
                        {
                            if (attempts++ >= 32 || remaining <= 0) break;
                            var path = Route(project, rules[network.RequirementId], network, pair.From, [pair.To], profiles, false, resolveEntity, ref remaining, restriction);
                            if (path == null) continue;
                            var trial = ProgramJson.Clone(project); var target = trial.Layout.SystemNetworks.Single(candidate => candidate.Id == network.Id);
                            foreach (var part in path)
                            {
                                var same = trial.Layout.SystemNetworks.SelectMany(owner => owner.Segments).FirstOrDefault(segment => segment.Position == part.Position && SameSegment(segment, part));
                                if (same != null) continue;
                                target.Segments.RemoveAll(segment => !segment.Edited && !segment.Locked && segment.Position == part.Position && profiles.TryGetValue(segment.ProfileId, out var old) && Conflict(old, part.Profile));
                                target.Segments.Add(new() { Id = network.Id + "/" + part.Profile.Id + "@" + part.Position.X + "," + part.Position.Y + ":" + part.Rotation, ProfileId = part.Profile.Id, Position = part.Position, Rotation = part.Rotation });
                            }
                            if (trial.Layout.SystemNetworks.Sum(owner => owner.Segments.Count) > project.Definition.Systems.Generation.MaxGeneratedSegments) continue;
                            var nextInspect = DesignSystems.CreateUtilityRouteInspector(trial, final: true, utilityType: _ => type); var proof = nextInspect(request);
                            if (!Fulfilled(proof) || previousUtilities.Any(route => !nextInspect(route).Verified) || !before.IsSubsetOf(Capabilities(DesignSystems.Validate(trial, true)))) continue;
                            project.Layout.SystemNetworks = trial.Layout.SystemNetworks; Record(project, request, proof);
                            inspect = DesignSystems.CreateUtilityRouteInspector(project, final: true, utilityType: _ => type); repaired = true; break;
                        }
                        if (repaired || attempts >= 32 || remaining <= 0) break;
                    }
                    if (repaired || attempts >= 32 || remaining <= 0) break;
                }
                if (!repaired) issues.Add(new(requirement.Id, "native-utility-route", "Required " + type + " continuity inside [" + string.Join(", ", modes) + "] has no verified native branch from [" + string.Join(", ", sourceGroup.Select(room => room.Id)) + "]. Check compatible bound terminal ports, protected segments, the authored route mode and native search budgets."));
            }
        }

        void Record(StationDesignProject target, DesignRoute request, NativeUtilityConnection proof)
        {
            var existing = target.Layout.Routes.FirstOrDefault(route => route.Id == request.Id);
            if (existing == null) { request.Cells = [.. proof.Cells]; target.Layout.Routes.Add(request); }
            else if (!existing.Edited && !existing.Locked) existing.Cells = [.. proof.Cells];
            var utility = target.Layout.Utilities.FirstOrDefault(item => item.RequirementId == request.RequirementId && item.From == request.From && item.To == request.To && item.Type == type);
            if (utility == null) target.Layout.Utilities.Add(new() { Id = "utility:" + request.Id, RequirementId = request.RequirementId, From = request.From, To = request.To, Type = type, Cells = [.. proof.Cells] });
            else if (!utility.Edited) utility.Cells = [.. proof.Cells];
        }
    }
}
