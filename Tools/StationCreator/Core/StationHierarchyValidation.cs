using System.Text.RegularExpressions;

namespace Content.StationGeneration;

public static partial class StationHierarchyValidation
{
    public static List<Diagnostic> Validate(StationGenerationConfig config)
    {
        var diagnostics = new List<Diagnostic>();
        void Error(string code, string message) => diagnostics.Add(new("hierarchy", code, message));
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var access = new List<(string Owner, string Origin)>();
        if (config.Departments == null || config.HierarchyRelations == null)
        {
            Error("hierarchy-null", "Departments and hierarchy relations must be lists.");
            return diagnostics;
        }
        if (config.Departments.Count > 64)
            Error("hierarchy-limit", "At most 64 departments can be authored.");
        if (config.Departments.Count(d => d?.TopologyHub == true) > 1)
            Error("hierarchy-topology-hub", "Select at most one department as the topology hub.");
        foreach (var department in config.Departments)
        {
            if (department == null) { Error("hierarchy-null", "Department entries must be objects."); continue; }
            Register(department.Id, "department", 1);
            Text(department.Id, "", department.Description);
            if (department.PlacementOrder is < -10000 or > 10000)
                Error("hierarchy-placement-order", $"{department.Id}: placement order must be between -10000 and 10000.");
            if (department.Grouping is not ("grouped" or "preferGrouped" or "independent"))
                Error("hierarchy-grouping", $"{department.Id}: choose grouped, preferGrouped or independent.");
            if (department.Subdepartments == null) { Error("hierarchy-null", $"{department.Id}: subdepartments must be a list."); continue; }
            if (department.Subdepartments.Count > 64)
                Error("hierarchy-limit", $"{department.Id}: at most 64 subdepartments can be authored.");
            foreach (var sub in department.Subdepartments)
            {
                if (sub == null) { Error("hierarchy-null", $"{department.Id}: subdepartment entries must be objects."); continue; }
                var subPath = StationHierarchy.Path(department.Id, sub.Id);
                Register(subPath, "subdepartment", 2);
                Bounds(subPath, sub.MinArea, sub.MaxArea);
                if (sub.TargetArea < sub.MinArea || sub.TargetArea > sub.MaxArea)
                    Error("hierarchy-area", $"{subPath}: target area must be between minimum and maximum area.");
                Text(subPath, sub.Name, sub.Description);
                if (sub.Rooms == null) { Error("hierarchy-null", $"{subPath}: rooms must be a list."); continue; }
                if (sub.Rooms.Count > 64) Error("hierarchy-limit", $"{subPath}: at most 64 room/feature definitions can be authored.");
                long required = 0;
                foreach (var room in sub.Rooms)
                {
                    if (room == null) { Error("hierarchy-null", $"{subPath}: room entries must be objects."); continue; }
                    var roomPath = StationHierarchy.Path(department.Id, sub.Id, room.Id);
                    Register(roomPath, "room/feature", 3);
                    Bounds(roomPath, room.MinArea, room.MaxArea);
                    Text(roomPath, room.Name, room.Description);
                    if (room.Kind is not ("room" or "feature")) Error("hierarchy-kind", $"{roomPath}: kind must be room or feature.");
                    if (room.MinCount < 0 || room.MaxCount < room.MinCount || room.MaxCount > 32)
                        Error("hierarchy-count", $"{roomPath}: counts must satisfy 0 ≤ minimum ≤ maximum ≤ 32.");
                    required += Math.Max(0, room.MinCount) * (long) Math.Max(0, room.MinArea);
                    if (room.AccessibleFrom == null) Error("hierarchy-null", $"{roomPath}: accessibleFrom must be a list.");
                    else foreach (var origin in room.AccessibleFrom)
                    {
                        if (origin == roomPath) Error("hierarchy-access", $"{roomPath}: a room cannot use itself as an access origin.");
                        access.Add((roomPath, origin));
                    }
                }
                if (required > sub.MaxArea) Error("hierarchy-capacity", $"{subPath}: required room floor exceeds the subdepartment maximum area.");
            }
            if (StationHierarchy.RequiredFloorArea(department) > department.MaxArea)
                Error("hierarchy-capacity", $"{department.Id}: maximum area cannot fit the required rooms and internal circulation; increase it or reduce the program.");
        }
        foreach (var item in access)
            if (item.Origin == null || !paths.Contains(item.Origin)) Error("hierarchy-reference", $"{item.Owner}: unknown access origin '{item.Origin}'.");

        var relationIds = new HashSet<string>(StringComparer.Ordinal);
        var relationKeys = new HashSet<string>(StringComparer.Ordinal);
        if (config.HierarchyRelations.Count > 2048) Error("hierarchy-limit", "At most 2048 hierarchy relations can be authored.");
        foreach (var relation in config.HierarchyRelations)
        {
            if (relation == null) { Error("hierarchy-null", "Hierarchy relation entries must be objects."); continue; }
            if (!string.IsNullOrEmpty(relation.Id) && !relationIds.Add(relation.Id)) Error("hierarchy-duplicate", $"Duplicate hierarchy relation id '{relation.Id}'.");
            if (!paths.Contains(relation.From ?? "") || !paths.Contains(relation.To ?? "")) Error("hierarchy-reference", $"Hierarchy relation '{relation.Id}' names an unknown department, subdepartment or room.");
            if (relation.From == relation.To) Error("hierarchy-relation", "A hierarchy relation must connect distinct scopes.");
            if (relation.Priority is not ("required" or "desired" or "avoided")) Error("hierarchy-priority", "Relation priority must be required, desired or avoided.");
            if (!double.IsFinite(relation.Weight) || relation.Weight <= 0 || relation.Weight > 1000) Error("hierarchy-weight", "Relation weights must be finite and greater than zero, up to 1000.");
            if (relation.Relation is not ("walking" or "adjacency" or "delivery" or "transport")) Error("hierarchy-relation", "Unknown hierarchy relation kind.");
            if (relation.Purpose is not ("crew" or "items" or "patients" or "waste")) Error("hierarchy-purpose", "Unknown hierarchy relation purpose.");
            if (relation.Direction is not ("both" or "forward")) Error("hierarchy-direction", "Relation direction must be both or forward.");
            if (relation.MaxDistance is <= 0 or > 100000) Error("hierarchy-distance", "Maximum distance must be positive and no greater than 100000.");
            if (relation.AllowedModes == null || relation.AllowedModes.Count == 0 || relation.AllowedModes.Any(m => m is not ("publicHall" or "staffPassage" or "maintenance" or "disposalShortcut" or "mail")))
                Error("hierarchy-modes", "Choose at least one supported connection mode.");
            if (relation.AllowedModes?.Contains("mail") == true && relation.Purpose != "items") Error("hierarchy-modes", "Mail can satisfy item relations only.");
            if (relation.AllowedModes?.Contains("disposalShortcut") == true && relation.Purpose is "patients" or "waste") Error("hierarchy-modes", "Shortcut tubes cannot satisfy patient or waste relations.");
            Text(relation.Id, "", relation.Description);
            var endpoints = relation.Direction == "both" && string.CompareOrdinal(relation.From, relation.To) > 0 ? $"{relation.To}|{relation.From}" : $"{relation.From}|{relation.To}";
            var key = $"{endpoints}|{relation.Priority}|{relation.Relation}|{relation.Purpose}|{relation.Direction}|{string.Join(',', (relation.AllowedModes ?? []).Order(StringComparer.Ordinal))}";
            if (!relationKeys.Add(key)) Error("hierarchy-duplicate", "Duplicate hierarchy dependency; edit its weight rather than adding it twice.");
        }
        // Access dependencies between rooms must not require entering each other first.
        var roomAccess = access.Where(a => a.Origin != null && a.Origin.Count(c => c == '/') == 2).ToList();
        var incoming = roomAccess.SelectMany(a => new[] { a.Owner, a.Origin }).Distinct(StringComparer.Ordinal)
            .ToDictionary(p => p, _ => 0, StringComparer.Ordinal);
        var outgoing = roomAccess.GroupBy(a => a.Owner, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(a => a.Origin).ToList(), StringComparer.Ordinal);
        foreach (var edge in roomAccess) incoming[edge.Origin]++;
        var ready = new Queue<string>(incoming.Where(p => p.Value == 0).Select(p => p.Key));
        var removed = 0;
        while (ready.TryDequeue(out var path))
        {
            removed++;
            foreach (var target in outgoing.GetValueOrDefault(path) ?? [])
                if (--incoming[target] == 0) ready.Enqueue(target);
        }
        if (removed != incoming.Count) Error("hierarchy-cycle", "Room access dependencies contain a cycle; provide an independent access origin.");
        foreach (var required in config.HierarchyRelations.Where(r => r?.Priority == "required"))
        foreach (var avoided in config.HierarchyRelations.Where(r => r?.Priority == "avoided" && r.StrictAvoidance))
        {
            var sameEndpoints = required.From == avoided.From && required.To == avoided.To ||
                (required.Direction == "both" || avoided.Direction == "both") && required.From == avoided.To && required.To == avoided.From;
            if (sameEndpoints && required.Relation == avoided.Relation && required.Purpose == avoided.Purpose &&
                required.AllowedModes?.Count > 0 && required.AllowedModes.All(m => avoided.AllowedModes?.Contains(m, StringComparer.Ordinal) == true))
                Error("hierarchy-conflict", $"Required relation '{required.Id}' contradicts strictly avoided relation '{avoided.Id}'.");
        }
        return diagnostics;

        void Register(string path, string kind, int depth)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Split('/').Length != depth || path.Split('/').Any(p => !IdPattern().IsMatch(p)))
                Error("hierarchy-id", $"Invalid {kind} id '{path}'; use 1–48 letters, numbers, spaces, underscores or hyphens per path component.");
            if (!paths.Add(path ?? "")) Error("hierarchy-duplicate", $"Duplicate hierarchy path '{path}'.");
        }
        void Bounds(string path, int minimum, int maximum)
        {
            if (minimum < 4 || maximum < minimum || maximum > 100000)
                Error("hierarchy-area", $"{path}: areas must satisfy 4 ≤ minimum ≤ maximum ≤ 100000.");
        }
        void Text(string path, string? name, string? description)
        {
            if (name == null || description == null || name.Length > 120 || description.Length > 2000)
                Error("hierarchy-text", $"{path}: name and description must be text, with at most 120 and 2000 characters respectively.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 _-]{0,47}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
