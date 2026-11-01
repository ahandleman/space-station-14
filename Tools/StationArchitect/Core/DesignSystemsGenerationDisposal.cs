namespace Content.StationArchitect;

public static partial class DesignSystemsGeneration
{
    /// <summary>Repairs automatic native disposal directions on a private draft clone, preserving topology and protected hardware.</summary>
    public static StationDesignProject NormalizeDisposal(StationDesignProject source)
    {
        var project = ProgramJson.Clone(source); var issues = new List<ProgramDiagnostic>();
        if (project.Definition.Systems.Enabled)
        {
            var profiles = project.Definition.Systems.Profiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
            NormalizeDisposal(project, profiles, DesignSystems.EntityResolver(project), issues);
        }
        return Finish(project, "disposals", issues);
    }

    /// <summary>
    /// Native tube adjacency alone does not establish disposal flow. Choose each
    /// automatic tube's preferred exit toward a real outlet, retaining every
    /// existing reciprocal contact. Thus a holder entering from an upstream
    /// branch always follows a strictly decreasing distance to the outlet.
    /// </summary>
    private static void NormalizeDisposal(StationDesignProject project, Dictionary<string, NativeSystemProfile> profiles,
        Func<DesignSystemDevice, DesignEntity?> resolveEntity, List<ProgramDiagnostic> issues)
    {
        var bindings = project.Layout.SystemDevices.ToDictionary(device => device.Id, StringComparer.Ordinal);
        var hardware = project.Layout.SystemDevices.Where(device => profiles.ContainsKey(device.ProfileId)).Select(device => (Device: device, Entity: resolveEntity(device)))
            .Where(item => item.Entity != null).SelectMany(item => DesignSystems.Ports(profiles[item.Device.ProfileId], item.Entity!.Position, item.Entity.Rotation, item.Entity.Id))
            .Where(port => port.ProfilePort.Kind == "disposal").DistinctBy(port => port.Id).ToList();
        foreach (var rule in project.Definition.Systems.Networks.Where(rule => rule.Kind == "disposal"))
        foreach (var network in project.Layout.SystemNetworks.Where(network => network.RequirementId == rule.Id))
        {
            var ports = project.Layout.SystemNetworks.SelectMany(owner => owner.Segments).Where(segment => profiles.ContainsKey(segment.ProfileId))
                .SelectMany(segment => DesignSystems.Ports(profiles[segment.ProfileId], segment.Position, segment.Rotation, segment.Id)).Concat(hardware)
                .Where(port => port.ProfilePort.Kind == "disposal" && port.ProfilePort.Connection != "disposalUnit").DistinctBy(port => port.Id).ToList();
            var at = ports.GroupBy(port => port.Position).ToDictionary(group => group.Key, group => group.ToList());
            var neighbors = ports.ToDictionary(port => port.Id, port => Cell.Directions.SelectMany(direction => at.GetValueOrDefault(port.Position + direction) ?? [])
                .Where(other => DesignSystems.Contacts(port, other)).OrderBy(other => other.Position.Y).ThenBy(other => other.Position.X).ThenBy(other => other.Id, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
            var roots = new HashSet<string>(StringComparer.Ordinal);
            foreach (var terminal in network.Terminals.Where(terminal => terminal.Role == "outlet"))
            {
                if (!bindings.TryGetValue(terminal.DeviceId, out var binding) || !profiles.TryGetValue(binding.ProfileId, out var profile) || resolveEntity(binding) is not { } entity ||
                    !rule.Terminals.Any(selector => selector.DeviceRuleId == binding.RequirementId && selector.PortId == terminal.PortId && selector.Role == "outlet")) continue;
                var port = DesignSystems.Ports(profile, entity.Position, entity.Rotation, entity.Id).FirstOrDefault(port => port.ProfilePort.Id == terminal.PortId && port.ProfilePort.Kind == "disposal");
                if (port != null && neighbors.ContainsKey(port.Id)) roots.Add(port.Id);
            }
            var distance = roots.ToDictionary(id => id, _ => 0, StringComparer.Ordinal); var pending = new Queue<string>(roots.Order(StringComparer.Ordinal));
            while (pending.TryDequeue(out var current)) foreach (var next in neighbors[current])
                if (distance.TryAdd(next.Id, distance[current] + 1)) pending.Enqueue(next.Id);
            if (network.Edited || network.Locked) continue;
            var variants = rule.SegmentProfiles.Where(profiles.ContainsKey).Select(id => profiles[id]).Where(profile => profile.Kind == "disposal" && profile.Ports.Count(port => port.Kind == "disposal") == 1)
                .SelectMany(profile => new[] { 0, 90, 180, 270 }.Select(rotation => (Profile: profile, Rotation: rotation))).ToList();
            foreach (var segment in network.Segments.Where(segment => !segment.Edited && !segment.Locked))
            {
                if (!profiles.TryGetValue(segment.ProfileId, out var oldProfile)) continue;
                var existing = DesignSystems.Ports(oldProfile, segment.Position, segment.Rotation, segment.Id).Where(port => port.ProfilePort.Kind == "disposal" && port.ProfilePort.Connection != "disposalUnit").ToList();
                if (existing.Count != 1 || !distance.TryGetValue(existing[0].Id, out var depth) || depth == 0)
                {
                    issues.Add(new(segment.Id, "system-disposal-direction", "This automatic tube has no supported single native tube port with a reciprocal physical path to the declared outlet; its geometry was retained.", rule.Priority == "required" ? "error" : "warning"));
                    continue;
                }
                var original = existing[0]; var contacts = neighbors[original.Id];
                var unitContacts = hardware.Where(port => port.ProfilePort.Connection == "disposalUnit" && DesignSystems.Contacts(original, port)).ToList();
                var candidates = variants.Select(variant => (variant.Profile, variant.Rotation, Port: DesignSystems.Ports(variant.Profile, segment.Position, variant.Rotation, segment.Id).Single(port => port.ProfilePort.Kind == "disposal")))
                    .Where(candidate => candidate.Port.Position == original.Position && candidate.Port.Directions.Count > 0 &&
                        contacts.All(other => DesignSystems.Contacts(candidate.Port, other)) && unitContacts.All(other => DesignSystems.Contacts(candidate.Port, other)) &&
                        contacts.Any(other => distance.TryGetValue(other.Id, out var nextDepth) && nextDepth < depth && other.Position - original.Position == candidate.Port.Directions[0]))
                    .OrderBy(candidate => candidate.Port.Directions.Count)
                    .ThenBy(candidate => candidate.Profile.Id == segment.ProfileId && candidate.Rotation == segment.Rotation ? 0 : 1)
                    .ThenBy(candidate => candidate.Profile.Id, StringComparer.Ordinal).ThenBy(candidate => candidate.Rotation).ToList();
                if (candidates.Count == 0)
                {
                    issues.Add(new(segment.Id, "system-disposal-direction", "No authored native tube profile/rotation preserves all actual contacts while preferring an exit toward the declared outlet. Add an appropriate junction profile or edit this branch; no protected geometry was changed.", rule.Priority == "required" ? "error" : "warning"));
                    continue;
                }
                segment.ProfileId = candidates[0].Profile.Id; segment.Rotation = candidates[0].Rotation;
            }
        }
    }
}
