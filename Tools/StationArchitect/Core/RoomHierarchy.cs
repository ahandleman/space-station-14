namespace Content.StationArchitect;

/// <summary>Canonical definition and runtime identities; floor ownership remains exclusive at every depth.</summary>
public static class RoomHierarchy
{
    public const int MaxDepth = 12;
    public const int MaxTemplates = 4096;

    public static IEnumerable<(string Path, RoomDefinition Room)> Templates(StationProgramDocument document)
    {
        foreach (var owner in document.Departments ?? [])
            if (owner != null)
                foreach (var item in Walk(owner.Rooms, owner.Id)) yield return item;
        if (document.Maintenance != null)
            foreach (var item in Walk(document.Maintenance.Rooms, RequirementCompiler.MaintenanceOwner)) yield return item;
    }

    private static IEnumerable<(string Path, RoomDefinition Room)> Walk(List<RoomDefinition>? rooms, string parent, int depth = 1)
    {
        if (depth > MaxDepth) yield break;
        foreach (var room in rooms ?? [])
        {
            if (room == null) continue;
            var path = parent + "/" + room.Id;
            yield return (path, room);
            foreach (var child in Walk(room.Rooms, path, depth + 1)) yield return child;
        }
    }

    public static IEnumerable<RequirementNode> Nodes(RequirementNode root)
    {
        yield return root;
        foreach (var child in root.Children)
            foreach (var node in Nodes(child)) yield return node;
    }

    public static string ParentPath(string path) => path.LastIndexOf('/') is var index && index >= 0 ? path[..index] : "";
    public static string? ParentTemplatePath(string path) => path.Count(character => character == '/') > 1 ? ParentPath(path) : null;
    public static string LocalId(string path) => path[(path.LastIndexOf('/') + 1)..];
    public static string InstanceId(string templatePath, string ordinal, string parentInstanceId = "") => string.IsNullOrEmpty(parentInstanceId) ? templatePath + "#" + ordinal : parentInstanceId + "/" + LocalId(templatePath) + "#" + ordinal;
    public static string CountKey(string parentInstanceId, string childTemplatePath) => parentInstanceId + "/" + LocalId(childTemplatePath);
    public static string TemplatePath(string runtimeId) => string.Join('/', runtimeId.Split('/').Select(part => part.Split('#')[0]));
    public static string MeasurementKey(RoomMeasurement room) => InstanceId(room.Path, room.InstanceId, room.ParentInstanceId);
    public static bool TryParseInstanceId(string? id, out string templatePath, out string ordinal, out string parentInstanceId)
    {
        templatePath = ordinal = parentInstanceId = "";
        if (string.IsNullOrWhiteSpace(id) || id.Length > 2048) return false;
        var parts = id.Split('/');
        if (parts.Length is < 2 or > MaxDepth + 1 || parts[0].Contains('#')) return false;
        foreach (var part in parts.Skip(1))
        {
            var pair = part.Split('#');
            if (pair.Length != 2 || pair[0].Length == 0 || pair[1].Length is < 1 or > 64) return false;
        }
        templatePath = TemplatePath(id); ordinal = parts[^1].Split('#')[1];
        if (parts.Length > 2) parentInstanceId = id[..id.LastIndexOf('/')];
        return true;
    }
    public static bool ConnectionScopeAllowed(CompiledConnection connection, string fromPath, string fromId, string fromParent, string toPath, string toId, string toParent)
    {
        if (!connection.LocalScope) return true;
        var source = fromPath.Split('/'); var target = toPath.Split('/'); var common = 0;
        while (common < source.Length && common < target.Length && source[common] == target[common]) common++;
        // A shared department alone does not tie instances together. A shared room
        // template does, including a grandchild's references to its parent's siblings.
        if (common < 2) return true;
        return string.Join('/', fromId.Split('/').Take(common)) == string.Join('/', toId.Split('/').Take(common));
    }

    public static IEnumerable<DesignRoom> Descendants(StationDesignProject project, DesignRoom parent, bool includeSelf = false)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { parent.Id };
        var pending = new Queue<DesignRoom>(); pending.Enqueue(parent);
        if (includeSelf) yield return parent;
        while (pending.TryDequeue(out var next))
        foreach (var child in project.Layout.Rooms.Where(room => room.ParentInstanceId == next.Id && seen.Add(room.Id)))
        {
            yield return child;
            pending.Enqueue(child);
        }
    }

    public static long Multiplicity(string path, IReadOnlyDictionary<string, int> localCounts)
    {
        long result = 1;
        for (var current = path; localCounts.TryGetValue(current, out var count); current = ParentPath(current))
            result = SaturatingMultiply(result, Math.Max(0, count));
        return result;
    }

    public static long MinimumFloor(string owner, IReadOnlyDictionary<string, RoomDefinition> definitions, IReadOnlyDictionary<string, int> localCounts) =>
        definitions.Where(pair => RequirementCompiler.Owner(pair.Key) == owner).Aggregate(0L, (sum, pair) => SaturatingAdd(sum, SaturatingMultiply(Multiplicity(pair.Key, localCounts), RequirementCompiler.EffectiveMinimumArea(pair.Value))));

    /// <summary>Bounded preview of raw counts, using the same stable ordinal reuse as selection.</summary>
    public static long RequestedTotal(StationDesignProject project, IReadOnlyDictionary<string, RoomDefinition> definitions, IReadOnlyDictionary<string, int> localCounts)
    {
        long total = 0; var stop = Math.Clamp(project.Settings.MaxRoomInstances, 1, 4096);
        foreach (var path in definitions.Keys.Where(path => ParentTemplatePath(path) == null)) Visit(path, "");
        return total;

        void Visit(string path, string parent)
        {
            if (total > stop) return;
            var count = parent.Length > 0 && project.Selection.ParentRoomCounts.TryGetValue(CountKey(parent, path), out var selected) ? selected : localCounts[path];
            total = SaturatingAdd(total, Math.Max(0, count));
            if (total > stop || count <= 0) return;
            var ordinals = project.Layout.Rooms.Where(room => room.DefinitionPath == path && room.ParentInstanceId == parent)
                .OrderBy(room => int.TryParse(room.Instance, out var number) ? number : int.MaxValue).ThenBy(room => room.Id, StringComparer.Ordinal)
                .Take(count).Select(room => room.Instance).ToList();
            var used = ordinals.ToHashSet(StringComparer.Ordinal);
            for (var number = 1; ordinals.Count < count; number++)
            {
                var ordinal = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (used.Add(ordinal)) ordinals.Add(ordinal);
            }
            foreach (var ordinal in ordinals)
            foreach (var child in definitions[path].Rooms) Visit(path + "/" + child.Id, InstanceId(path, ordinal, parent));
        }
    }

    public static string ResolveReference(string scope, string reference, IReadOnlySet<string> paths)
    {
        if (paths.Contains(reference)) return reference;
        if (reference == "..") return ParentPath(scope);
        if (reference.StartsWith("./", StringComparison.Ordinal)) return scope + reference[1..];
        if (reference.StartsWith("../", StringComparison.Ordinal))
        {
            var basis = scope;
            while (reference.StartsWith("../", StringComparison.Ordinal)) { basis = ParentPath(basis); reference = reference[3..]; }
            return basis.Length == 0 ? reference : basis + "/" + reference;
        }
        if (!reference.Contains('/'))
        {
            var sibling = ParentPath(scope) + "/" + reference;
            if (paths.Contains(sibling)) return sibling;
            var child = scope + "/" + reference;
            if (paths.Contains(child)) return child;
            return sibling;
        }
        return reference;
    }

    public static long SaturatingAdd(long first, long second) => first >= long.MaxValue - second ? long.MaxValue : first + second;
    public static long SaturatingMultiply(long first, long second) => first == 0 || second == 0 ? 0 : first > long.MaxValue / second ? long.MaxValue : first * second;
}
