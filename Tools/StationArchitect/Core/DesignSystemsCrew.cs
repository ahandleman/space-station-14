namespace Content.StationArchitect;

public static partial class DesignSystems
{
    public static bool NativeProfileCarriesCrew(NativeSystemProfile profile)
    {
        if (profile == null || profile.RequiredComponents == null || profile.Components == null || profile.Ports == null) return false;
        if (profile.RequiredComponents.Contains("MailingUnit", StringComparer.Ordinal) || profile.Components.ContainsKey("MailingUnit")) return false;
        if (!profile.Ports.Any(port => port is { Kind: "disposal", Connection: "disposalUnit" }) ||
            !profile.RequiredComponents.Contains("DisposalUnit", StringComparer.Ordinal) && !profile.Components.ContainsKey("DisposalUnit")) return false;
        if (profile.Components.TryGetValue("DisposalUnit", out var unit) && unit.ValueKind == System.Text.Json.JsonValueKind.Object &&
            unit.TryGetProperty("whitelist", out var whitelist) && whitelist.ValueKind == System.Text.Json.JsonValueKind.Object &&
            whitelist.TryGetProperty("components", out var components) && components.ValueKind == System.Text.Json.JsonValueKind.Array &&
            components.GetArrayLength() == 1 && components[0].ValueKind == System.Text.Json.JsonValueKind.String && components[0].GetString() == "Item" &&
            (!whitelist.TryGetProperty("tags", out var tags) || tags.ValueKind == System.Text.Json.JsonValueKind.Array && tags.GetArrayLength() == 0)) return false;
        return profile.CarriesCrew ?? true;
    }

    /// <summary>
    /// Directed crew movement through freshly verified native waste tubes. Uses
    /// actual walkable interaction/ejection floor, not the blocked machine tile.
    /// Every possible native disposal branch must terminate at a declared outlet.
    /// </summary>
    public static List<(Cell From, Cell To)> NativeCrewEdges(StationDesignProject project)
    {
        if (!project.Definition.Systems.Enabled || project.Layout.SystemDevices.Count == 0 ||
            !project.Definition.Systems.Profiles.Any(NativeProfileCarriesCrew)) return [];
        ValidateCore(project, true, out var graph);
        if (graph == null) return [];
        var resolveRoom = RoomResolver(project); var resolveEntity = EntityResolver(project, resolveRoom: resolveRoom);
        var bindings = project.Layout.SystemDevices.ToDictionary(binding => binding.Id, StringComparer.Ordinal);
        var blocked = project.Layout.Equipment.Where(entity => BlocksCrew(project, entity)).SelectMany(entity => entity.Footprint)
            .Concat(DesignTransport.EndpointFloorCells(project)).ToHashSet();
        var edges = new HashSet<(Cell From, Cell To)>();
        foreach (var network in project.Layout.SystemNetworks)
        {
            var rule = project.Definition.Systems.Networks.FirstOrDefault(rule => rule.Id == network.RequirementId && rule.Kind == "disposal");
            if (rule == null) continue;
            var endpoints = network.Terminals.Where(terminal => bindings.ContainsKey(terminal.DeviceId)).Select(terminal =>
            {
                var binding = bindings[terminal.DeviceId]; var entity = resolveEntity(binding);
                return (Terminal: terminal, Binding: binding, Entity: entity);
            }).Where(item => item.Entity != null && graph.Instances.ContainsKey(item.Entity.Id) &&
                rule.Terminals.Any(selector => selector.DeviceRuleId == item.Binding.RequirementId && selector.PortId == item.Terminal.PortId && selector.Role == item.Terminal.Role)).ToList();
            var outlets = endpoints.Where(item => item.Terminal.Role == "outlet").Select(item => item.Entity!.Id).ToHashSet(StringComparer.Ordinal);
            // A real carrier may enter a shared tube even if its native binding
            // was selected only for power rather than declared as a waste inlet.
            foreach (var inlet in project.Layout.SystemDevices.Select(binding => (Binding: binding, Entity: resolveEntity(binding)))
                .Where(item => item.Entity != null && graph.Instances.ContainsKey(item.Entity.Id)))
            {
                var entity = inlet.Entity!; var profile = graph.Instances[entity.Id].Profile;
                if (!NativeProfileCarriesCrew(profile) || !graph.Powered(entity.Id)) continue;
                foreach (var sourcePort in profile.Ports.Where(port => port.Kind == "disposal" && port.Connection == "disposalUnit"))
                {
                    var portId = entity.Id + ":" + sourcePort.Id;
                    if (!graph.Ports.ContainsKey(portId)) continue;
                    var trace = TraceDisposal(graph, portId, outlets);
                    if (trace.Witness.Count == 0 || resolveRoom(entity.RoomId) is not { } sourceRoom) continue;
                    var sourceFloor = sourceRoom.Cells.ToHashSet();
                    var from = entity.Footprint.Concat(entity.Footprint.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)))
                        .Where(cell => sourceFloor.Contains(cell) && !blocked.Contains(cell)).Distinct().ToList();
                    foreach (var outlet in endpoints.Where(item => item.Terminal.Role == "outlet" && trace.Outlets.Contains(item.Entity!.Id)))
                    {
                        if (resolveRoom(outlet.Entity!.RoomId) is not { } targetRoom) continue;
                        var floor = targetRoom.Cells.ToHashSet();
                        var to = outlet.Entity.Footprint.Concat(outlet.Entity.Footprint.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)))
                            .Where(cell => floor.Contains(cell) && !blocked.Contains(cell)).Distinct().ToList();
                        foreach (var start in from) foreach (var end in to) if (start != end) edges.Add((start, end));
                    }
                }
            }
        }
        return edges.OrderBy(edge => edge.From.Y).ThenBy(edge => edge.From.X).ThenBy(edge => edge.To.Y).ThenBy(edge => edge.To.X).ToList();
    }
}
