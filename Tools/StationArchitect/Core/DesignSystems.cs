namespace Content.StationArchitect;

public sealed class SystemPortGeometry
{
    public string Id { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public NativeSystemPort ProfilePort { get; set; } = new();
    public Cell Position { get; set; }
    public List<Cell> Directions { get; set; } = [];
}

/// <summary>Native infrastructure geometry. Rules and profiles are configuration; observed edges come from placed ports.</summary>
public static partial class DesignSystems
{
    public static List<DesignRoom> Targets(StationDesignProject project, SystemDeviceRule rule)
    {
        var definitions = RoomHierarchy.Templates(project.Definition).ToDictionary(item => item.Path, item => item.Room, StringComparer.Ordinal);
        var result = rule.Selector.Networks.Count == 0 || rule.Selector.Networks.Contains("rooms") ? project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && room.Cells.Count > 0 && definitions.TryGetValue(room.DefinitionPath, out var definition) &&
            Matches(rule.Selector, room, definition)).ToList() : [];
        result.AddRange(NetworkTargets(project, rule.Selector.Networks, rule.Scope == "compartment").Where(room => Matches(rule.Selector, room, TargetDefinition(project, room))));
        return result.OrderBy(room => room.Id, StringComparer.Ordinal).ToList();
    }

    public static bool Matches(SystemRoomSelector selector, DesignRoom room, RoomDefinition definition) =>
        (selector.IncludeNested || room.ParentInstanceId.Length == 0) &&
        (selector.Paths.Count == 0 || selector.Paths.Any(path => path.EndsWith("/*", StringComparison.Ordinal) ? room.DefinitionPath.StartsWith(path[..^1], StringComparison.Ordinal) : path == room.DefinitionPath)) &&
        (selector.Tags.Count == 0 || selector.Tags.Any(definition.Tags.Contains)) &&
        (selector.Roles.Count == 0 || selector.Roles.Contains(definition.Role)) &&
        (selector.Pressures.Count == 0 || selector.Pressures.Contains(definition.Environment.Pressure)) &&
        (selector.Placements.Count == 0 || selector.Placements.Contains(definition.Environment.Placement));

    public static string ScopeKey(StationDesignProject project, string scope, DesignRoom room)
    {
        if (scope == "station") return "$station";
        if (scope == "owner") return room.OwnerId;
        if (scope == "cluster") return DesignClusterGeometry.Clusters(project).FirstOrDefault(cluster => cluster.Any(member => member.Id == room.Id))?.Select(member => member.Id).Order(StringComparer.Ordinal).FirstOrDefault() ?? room.Id;
        if (scope == "compartment")
        {
            var component = DesignMeasurements.AirCompartments(project).FirstOrDefault(component => room.Cells.Any(component.Cells.Contains));
            if (component != null && component.Cells.Count > 0)
            { var first = component.Cells.OrderBy(cell => cell.Y).ThenBy(cell => cell.X).First(); return "$air:" + first.X + "," + first.Y; }
        }
        return room.Id;
    }

    public static Dictionary<string, List<DesignRoom>> TargetGroups(StationDesignProject project, SystemDeviceRule rule)
    {
        var targets = Targets(project, rule);
        if (rule.Scope != "compartment") return targets.GroupBy(room => ScopeKey(project, rule.Scope, room), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var compartments = DesignMeasurements.AirCompartments(project);
        return targets.GroupBy(room =>
        {
            var component = compartments.FirstOrDefault(component => room.Cells.Any(component.Cells.Contains));
            return component == null ? room.Id : CompartmentKey(component.Cells);
        }, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
    }

    public static RoomContentRequirement? ContentRequirement(StationDesignProject project, DesignEntity entity)
    {
        if (entity.SystemRuleId.Length == 0) return null;
        var rule = project.Definition.Systems.Devices.FirstOrDefault(rule => rule.Id == entity.SystemRuleId);
        if (!project.Definition.Systems.Enabled || rule == null || rule.BindExisting || rule.BindInterfaces || rule.BindTransport || !AllowedProfiles(project, rule).Any(profile => profile.Prototype == entity.Prototype) || entity.RequirementId != "systems:" + rule.Id || !Targets(project, rule).Any(room => room.Id == entity.RoomId)) return null;
        return rule.Content;
    }

    public static Func<DesignEntity, RoomContentRequirement?> ContentRequirementResolver(StationDesignProject project)
    {
        if (!project.Definition.Systems.Enabled) return _ => null;
        var rules = project.Definition.Systems.Devices.Where(rule => !rule.BindExisting && !rule.BindInterfaces && !rule.BindTransport)
            .ToDictionary(rule => rule.Id, rule => (Rule: rule, Rooms: Targets(project, rule).Select(room => room.Id).ToHashSet(StringComparer.Ordinal),
                Prototypes: AllowedProfiles(project, rule).Select(profile => profile.Prototype).ToHashSet(StringComparer.Ordinal)), StringComparer.Ordinal);
        return entity => entity.SystemRuleId.Length > 0 && rules.TryGetValue(entity.SystemRuleId, out var selected) && entity.RequirementId == "systems:" + selected.Rule.Id &&
            selected.Prototypes.Contains(entity.Prototype) && selected.Rooms.Contains(entity.RoomId) ? selected.Rule.Content : null;
    }

    public static List<NativeSystemProfile> AllowedProfiles(StationDesignProject project, SystemDeviceRule rule) => project.Definition.Systems.Profiles.Where(profile => profile.Id == rule.ProfileId || rule.ProfileIds.Contains(profile.Id)).ToList();

    public static List<SystemPortGeometry> Ports(NativeSystemProfile profile, Cell position, int rotation, string instanceId) => profile.Ports.Select(port => new SystemPortGeometry
    {
        Id = instanceId + ":" + port.Id, InstanceId = instanceId, ProfilePort = port,
        Position = position + Rotate(port.Offset, rotation), Directions = port.Directions.Select(direction => Rotate(Direction(direction), rotation)).ToList()
    }).ToList();

    public static Cell Direction(string direction) => direction.ToLowerInvariant() switch
    { "south" => new(0, -1), "east" => new(1, 0), "north" => new(0, 1), "west" => new(-1, 0), _ => new() };

    public static Cell Rotate(Cell direction, int rotation)
    {
        if (rotation % 90 != 0) return new();
        for (var quarter = 0; quarter < (rotation % 360 + 360) % 360 / 90; quarter++) direction = new(-direction.Y, direction.X);
        return direction;
    }

    public static bool Contacts(SystemPortGeometry first, SystemPortGeometry second)
    {
        var a = first.ProfilePort; var b = second.ProfilePort;
        if (first.InstanceId == second.InstanceId || a.Kind != b.Kind || a.NodeGroup != b.NodeGroup) return false;
        var delta = second.Position - first.Position; var distance = delta.Distance(new());
        if (a.Kind == "power")
        {
            if (a.Connection == "rangeProvider" && b.Connection == "rangeReceiver" || a.Connection == "rangeReceiver" && b.Connection == "rangeProvider")
                return (double)delta.X * delta.X + (double)delta.Y * delta.Y <= Math.Pow(Math.Min(a.Range, b.Range), 2);
            if (a.Connection == "cable" && b.Connection == "cable") return distance <= 1;
            if (a.Connection == "cable" && b.Connection is "cableDevice" or "terminal" || b.Connection == "cable" && a.Connection is "cableDevice" or "terminal") return distance == 0;
            if (a.Connection == "terminal" && b.Connection == "terminalPort") return distance == 1 && first.Directions.Contains(delta);
            if (b.Connection == "terminal" && a.Connection == "terminalPort") return distance == 1 && second.Directions.Contains(new(-delta.X, -delta.Y));
            return false;
        }
        if (a.Kind == "gas")
        {
            // Native PortPipeNode/PortablePipeNode explicitly join same-tile portable
            // connectors; this bridge does not apply the ordinary pipe-layer check.
            if (a.Connection == "pipePort" && b.Connection == "portable" || b.Connection == "pipePort" && a.Connection == "portable") return distance == 0;
            if (a.PipeLayer != b.PipeLayer) return false;
            return distance == 1 && first.Directions.Contains(delta) && second.Directions.Contains(new(-delta.X, -delta.Y));
        }
        if (a.Kind == "disposal")
        {
            if (a.Connection == "disposalUnit" && b.Connection == "disposalEntry" || b.Connection == "disposalUnit" && a.Connection == "disposalEntry") return distance == 0;
            if (a.Connection == "disposalUnit" || b.Connection == "disposalUnit") return false;
            return distance == 1 && first.Directions.Contains(delta) && second.Directions.Contains(new(-delta.X, -delta.Y));
        }
        return false;
    }
}
