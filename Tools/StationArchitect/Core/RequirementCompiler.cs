namespace Content.StationArchitect;

public sealed record FloorBudget(long Min, long Target, long Max);

/// <summary>A serializable, acyclic station → owner → room-template tree. Instances are added by later placement.</summary>
public sealed class RequirementNode
{
    public string Path { get; set; } = "";
    public string? ParentPath { get; set; }
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Role { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public List<string> SourceRefs { get; set; } = [];
    public AreaRequirement? Area { get; set; }
    public CountRequirement? Count { get; set; }
    public ShapeRequirement? Shape { get; set; }
    public SplitRequirement? Split { get; set; }
    public ExteriorRequirement? Exterior { get; set; }
    public AccessRequirement? Access { get; set; }
    public List<RoomContentRequirement> Contents { get; set; } = [];
    public RoomEnvironmentRequirement? Environment { get; set; }
    public RoomPlacementPolicy? Placement { get; set; }
    public RoomNestingRequirement? Nesting { get; set; }
    public AreaRequirement? ComplexArea { get; set; }
    public ShapeRequirement? ComplexShape { get; set; }
    public GameflowProgram? Gameflow { get; set; }
    public SystemsProgram? Systems { get; set; }
    public int? EffectiveMinArea { get; set; }
    public long ReservedContentArea { get; set; }
    public List<string> ChoiceIds { get; set; } = [];
    public bool? AllowThroughTraffic { get; set; }
    public int? EffectiveMinCount { get; set; }
    public List<string> ConnectionIds { get; set; } = [];
    public FloorBudget FloorBudget { get; set; } = new(0, 0, 0);
    public FloorBudget EffectiveFloorBudget { get; set; } = new(0, 0, 0);
    public List<RequirementNode> Children { get; set; } = [];
}

public sealed class CompiledConnection
{
    public bool LocalScope { get; set; }
    /// <summary>Globally unique: $station:ID or Department/Room:ID.</summary>
    public string Id { get; set; } = "";
    public string AuthoredId { get; set; } = "";
    public string From { get; set; } = "";
    public List<string> To { get; set; } = [];
    public string Selection { get; set; } = "all";
    public string Priority { get; set; } = "required";
    public double Weight { get; set; }
    public string Presence { get; set; } = "ifPresent";
    public List<string> Modes { get; set; } = [];
    public int? MaxDistance { get; set; }
    public string Description { get; set; } = "";
    public string Kind { get; set; } = "walking";
    public List<string> InterfaceTypes { get; set; } = [];
    public int MinWidth { get; set; }
}

public sealed class PresenceChoice
{
    public string ConnectionId { get; set; } = "";
    public string From { get; set; } = "";
    public List<string> Alternatives { get; set; } = [];
    public string Description { get; set; } = "";
}

public sealed class ProgramCompilation
{
    public bool Success => Tree != null && Diagnostics.All(d => d.Severity != "error");
    public List<ProgramDiagnostic> Diagnostics { get; set; } = [];
    public RequirementNode? Tree { get; set; }
    public List<CompiledConnection> Connections { get; set; } = [];
    public List<PresenceChoice> PresenceChoices { get; set; } = [];
    public List<RoomChoiceRequirement> Choices { get; set; } = [];
}

public static class RequirementCompiler
{
    public const string MaintenanceOwner = "_maintenance";
    private static readonly HashSet<string> SupportedModes = ["direct", "department", "publicHall", "maintenance", "controlledMaintenance", "utilityNetwork", "disposalShortcut", "mail"];

    public static ProgramCompilation Compile(StationProgramDocument document)
    {
        var result = new ProgramCompilation { Diagnostics = ProgramValidator.ValidateStructure(document) };
        if (result.Diagnostics.Any(d => d.Severity == "error")) return result;
        var rooms = Rooms(document).ToDictionary(r => r.Path, r => r.Room, StringComparer.Ordinal);
        var owners = document.Departments.Select(d => d.Id).Append(MaintenanceOwner).ToHashSet(StringComparer.Ordinal);
        var paths = owners.Concat(rooms.Keys).ToHashSet(StringComparer.Ordinal);
        ProgramValidator.GameflowReferences(document, rooms, owners, result.Diagnostics);
        foreach (var (path, room) in Rooms(document))
            foreach (var entryOwner in room.Access.EntryOwners)
                if (!owners.Contains(entryOwner)) result.Diagnostics.Add(new(path + ".access.entryOwners", "reference", $"Unknown eligible entry owner '{entryOwner}'."));
        AddChoices(document.Choices, rooms, result);
        foreach (var (path, room) in Rooms(document)) AddConnections(room.Connections, path, result, paths);
        AddConnections(document.Connections, null, result, paths);
        if (result.Diagnostics.Any(d => d.Severity == "error")) return result;

        var effectiveCounts = rooms.ToDictionary(r => r.Key, r => r.Value.Count.Min, StringComparer.Ordinal);
        bool Guaranteed(string path) => effectiveCounts.ContainsKey(path) ? RoomHierarchy.Multiplicity(path, effectiveCounts) > 0 :
            path == MaintenanceOwner ? document.Maintenance.Area.Min > 0 || effectiveCounts.Any(p => Owner(p.Key) == path && RoomHierarchy.Multiplicity(p.Key, effectiveCounts) > 0) :
            document.Departments.Single(d => d.Id == path).Area.Min > 0 || effectiveCounts.Any(p => Owner(p.Key) == path && RoomHierarchy.Multiplicity(p.Key, effectiveCounts) > 0);
        bool Promote(string path)
        {
            var changed = false;
            for (var current = path; rooms.ContainsKey(current); current = RoomHierarchy.ParentPath(current))
                if (effectiveCounts[current] == 0 && rooms[current].Count.Max > 0) { effectiveCounts[current] = 1; changed = true; }
            return changed;
        }
        int OwnerMaximum(string path) => path == MaintenanceOwner ? document.Maintenance.Area.Max : document.Departments.Single(d => d.Id == path).Area.Max;
        bool RoomCanExist(string path, RoomDefinition room)
        {
            for (var current = path; rooms.TryGetValue(current, out var ancestor); current = RoomHierarchy.ParentPath(current))
                if (ancestor.Count.Max == 0) return false;
            var owner = Owner(path);
            var hypothetical = new Dictionary<string, int>(effectiveCounts, StringComparer.Ordinal);
            for (var current = path; hypothetical.ContainsKey(current); current = RoomHierarchy.ParentPath(current)) hypothetical[current] = Math.Max(1, hypothetical[current]);
            return RoomHierarchy.MinimumFloor(owner, rooms, hypothetical) <= OwnerMaximum(owner);
        }
        bool CanExist(string path) => rooms.TryGetValue(path, out var room) ? RoomCanExist(path, room) :
            rooms.Any(p => Owner(p.Key) == path && RoomCanExist(p.Key, p.Value));
        bool EntryAllowed(string path, string opposite) => !rooms.TryGetValue(path, out var definition) || definition.Access.EntryOwners.Count == 0 || definition.Access.EntryOwners.Contains(Owner(opposite), StringComparer.Ordinal);
        bool RoutePossible(CompiledConnection connection, string target) => connection.Modes.Any(mode => mode switch
        {
            "department" => Owner(connection.From) == Owner(target),
            "direct" when connection.Kind == "walking" => EntryAllowed(connection.From, target) && EntryAllowed(target, connection.From),
            _ => true
        });
        var requirements = result.Connections.Where(c => c.Priority == "required" && c.Presence == "requireTarget").ToList();
        // Presence implications are monotone, and cycles are legal. Promote only certain choices;
        // unresolved OR alternatives remain explicit instead of pretending every alternative is mandatory.
        for (var pass = 0; pass <= rooms.Count; pass++)
        {
            var changed = false;
            foreach (var choice in result.Choices)
            {
                var required = choice.Rooms.Count(Guaranteed);
                var alternatives = choice.Rooms.Where(path => !Guaranteed(path) && CanExist(path)).ToList();
                if (choice.Count.Min > required && choice.Count.Min - required == alternatives.Count)
                    foreach (var path in alternatives) changed |= Promote(path);
            }
            foreach (var connection in requirements.Where(c => Guaranteed(c.From)))
            {
                var viable = connection.To.Where(t => CanExist(t) && RoutePossible(connection, t)).ToList();
                if (connection.Selection == "any" && viable.Any(Guaranteed)) continue;
                var targets = connection.Selection == "all" ? connection.To : viable.Count == 1 ? viable : [];
                foreach (var target in targets)
                    if (rooms.TryGetValue(target, out var room) && room.Count.Max > 0) changed |= Promote(target);
            }
            if (!changed) break;
        }
        foreach (var choice in result.Choices)
        {
            var required = choice.Rooms.Count(Guaranteed);
            var possible = choice.Rooms.Count(CanExist);
            if (required > choice.Count.Max || possible < choice.Count.Min)
                result.Diagnostics.Add(new("choices:" + choice.Id, "impossible-choice", $"Room choice permits {required}–{possible} active room templates, outside required bounds {choice.Count.Min}–{choice.Count.Max}."));
            else if (choice.Count.Min > required && choice.Rooms.Select(Owner).Distinct(StringComparer.Ordinal).Count() == 1)
            {
                var owner = Owner(choice.Rooms[0]);
                var baseline = RoomHierarchy.MinimumFloor(owner, rooms, effectiveCounts);
                var additional = choice.Rooms.Where(path => !Guaranteed(path) && CanExist(path)).Select(path => EffectiveMinimumArea(rooms[path])).Order().Take(choice.Count.Min - required).Sum(value => (long) value);
                if (baseline + additional > OwnerMaximum(owner))
                    result.Diagnostics.Add(new("choices:" + choice.Id, "impossible-choice", $"Even the smallest permitted choice requires {baseline + additional} owner-floor tiles, above {owner}'s maximum {OwnerMaximum(owner)}."));
            }
        }
        foreach (var connection in requirements)
        {
            if (!Guaranteed(connection.From)) continue;
            var viable = connection.To.Where(t => CanExist(t) && RoutePossible(connection, t)).ToList();
            if (connection.Selection == "all" && viable.Count != connection.To.Count || connection.Selection == "any" && viable.Count == 0)
                result.Diagnostics.Add(new(connection.Id, "impossible-presence", "An active required source requires a target that cannot be instantiated within its count/owner-floor bounds or reached by the allowed route modes."));
            else if (connection.Selection == "any" && !viable.Any(Guaranteed))
                result.PresenceChoices.Add(new() { ConnectionId = connection.Id, From = connection.From, Alternatives = viable, Description = "At least one alternative must be instantiated while this source exists; no branch has been chosen." });
            else if (connection.Selection == "all")
                foreach (var target in viable.Where(t => !rooms.ContainsKey(t) && !Guaranteed(t)))
                    result.PresenceChoices.Add(new() { ConnectionId = connection.Id, From = connection.From, Alternatives = rooms.Keys.Where(p => Owner(p) == target && CanExist(p)).ToList(), Description = "At least one room in the target owner must exist while this source exists." });
        }
        foreach (var connection in result.Connections.Where(c => c.Priority == "required" && c.Presence == "ifPresent" && Guaranteed(c.From)))
        {
            if (connection.Selection == "all")
            {
                foreach (var target in connection.To.Where(Guaranteed))
                    if (!RoutePossible(connection, target))
                        result.Diagnostics.Add(new(connection.Id, "impossible-route", $"The required route to '{target}' violates an owner boundary or direct-entry restriction in every allowed mode."));
            }
            else if (connection.To.Any(Guaranteed) && !connection.To.Any(t => CanExist(t) && RoutePossible(connection, t)))
                result.Diagnostics.Add(new(connection.Id, "impossible-route", "At least one required target is present, but no alternative can be reached using the allowed route modes."));
        }

        var tree = new RequirementNode { Path = "$station", Kind = "station", Name = document.Name, Description = document.Description, SourceRefs = [.. document.SourceRefs], Gameflow = ProgramJson.Clone(document.Gameflow), Systems = ProgramJson.Clone(document.Systems) };
        foreach (var department in document.Departments)
            tree.Children.Add(OwnerNode(department.Id, "department", department.Name, department.Description, department.Tags, department.Area, department.Shape, department.Split, department.Rooms, effectiveCounts, result.Diagnostics));
        tree.Children.Add(OwnerNode(MaintenanceOwner, "maintenance", "True maintenance rooms", "Enclosed maintenance rooms, separately from the connecting passage network.", [], document.Maintenance.Area, null, null, document.Maintenance.Rooms, effectiveCounts, result.Diagnostics));
        tree.FloorBudget = Sum(tree.Children.Select(n => n.FloorBudget));
        tree.EffectiveFloorBudget = Sum(tree.Children.Select(n => n.EffectiveFloorBudget));
        var nodes = RoomHierarchy.Nodes(tree).ToDictionary(n => n.Path, StringComparer.Ordinal);
        foreach (var owner in tree.Children)
        {
            owner.ParentPath = "$station";
        }
        foreach (var connection in result.Connections)
            foreach (var path in connection.To.Append(connection.From)) nodes[path].ConnectionIds.Add(connection.Id);
        foreach (var choice in result.Choices)
            foreach (var path in choice.Rooms) nodes[path].ChoiceIds.Add(choice.Id);
        foreach (var department in document.Departments) nodes[department.Id].SourceRefs = [.. department.SourceRefs];
        if (result.PresenceChoices.Count > 0)
            result.Diagnostics.Add(new("connections", "presence-choice", $"{result.PresenceChoices.Count} required optional-room choice(s) remain for placement. Interval budgets do not prove those alternatives fit simultaneously.", "warning"));
        result.Tree = tree;
        return result;
    }

    public static IEnumerable<(string Path, RoomDefinition Room)> Rooms(StationProgramDocument document) => RoomHierarchy.Templates(document);

    public static string Owner(string path) => path.Split('/')[0];

    public static long ReservedContentArea(RoomDefinition room) => room.Contents.Sum(content => (long) content.Count.Min * content.MinArea);
    public static int EffectiveMinimumArea(RoomDefinition room) => (int) Math.Max(room.Area.Min, ReservedContentArea(room));

    private static RequirementNode OwnerNode(string path, string kind, string name, string description, List<string> tags,
        AreaRequirement area, ShapeRequirement? shape, SplitRequirement? split, List<RoomDefinition> rooms,
        Dictionary<string, int> effectiveCounts, List<ProgramDiagnostic> diagnostics)
    {
        var node = new RequirementNode { Path = path, Kind = kind, Name = name.Length == 0 ? path : name, Description = description, Tags = [.. tags], Area = ProgramJson.Clone(area), Shape = shape == null ? null : ProgramJson.Clone(shape), Split = split == null ? null : ProgramJson.Clone(split) };
        foreach (var room in rooms)
        {
            node.Children.Add(RoomNode(room, path, effectiveCounts, diagnostics));
        }
        node.FloorBudget = Sum(node.Children.Select(n => n.FloorBudget));
        var effective = Sum(node.Children.Select(n => n.EffectiveFloorBudget));
        var min = Math.Max(area.Min, effective.Min); var max = Math.Min(area.Max, effective.Max);
        node.EffectiveFloorBudget = new(min, Math.Clamp((long) area.Target, Math.Min(min, max), Math.Max(min, max)), max);
        if (min > max)
            diagnostics.Add(new(path + ".area", "impossible-area", $"Department room floor permits {effective.Min}–{effective.Max} tiles, which cannot meet the owner area range {area.Min}–{area.Max}. Change room counts, room areas or the owner budget."));
        else if (area.Target < effective.Min || area.Target > effective.Max)
            diagnostics.Add(new(path + ".area.target", "unreachable-target", $"The soft area target {area.Target} is outside room capacity {effective.Min}–{effective.Max}; the effective target is clamped, without changing the authored program.", "warning"));
        return node;
    }

    private static RequirementNode RoomNode(RoomDefinition room, string parentPath, Dictionary<string, int> effectiveCounts, List<ProgramDiagnostic> diagnostics)
    {
        var path = parentPath + "/" + room.Id;
        var count = effectiveCounts[path];
        var node = new RequirementNode
        {
            Path = path, ParentPath = parentPath, Kind = "room", Name = room.Name.Length == 0 ? room.Id : room.Name,
            Description = room.Description, Role = room.Role, Tags = [.. room.Tags],
            Area = ProgramJson.Clone(room.Area), Count = ProgramJson.Clone(room.Count), Shape = ProgramJson.Clone(room.Shape),
            Exterior = ProgramJson.Clone(room.Exterior), Access = ProgramJson.Clone(room.Access), AllowThroughTraffic = room.AllowThroughTraffic,
            SourceRefs = [.. room.SourceRefs], Contents = ProgramJson.Clone(room.Contents), Environment = ProgramJson.Clone(room.Environment),
            Placement = ProgramJson.Clone(room.Placement), Nesting = ProgramJson.Clone(room.Nesting),
            ComplexArea = room.ComplexArea == null ? null : ProgramJson.Clone(room.ComplexArea), ComplexShape = room.ComplexShape == null ? null : ProgramJson.Clone(room.ComplexShape),
            EffectiveMinArea = EffectiveMinimumArea(room), ReservedContentArea = ReservedContentArea(room), EffectiveMinCount = count
        };
        foreach (var child in room.Rooms) node.Children.Add(RoomNode(child, path, effectiveCounts, diagnostics));
        var children = Sum(node.Children.Select(child => child.FloorBudget));
        var effective = Sum(node.Children.Select(child => child.EffectiveFloorBudget));
        node.FloorBudget = new(
            RoomHierarchy.SaturatingMultiply(room.Count.Min, RoomHierarchy.SaturatingAdd(room.Area.Min, children.Min)),
            RoomHierarchy.SaturatingMultiply(room.Count.Target, RoomHierarchy.SaturatingAdd(room.Area.Target, children.Target)),
            RoomHierarchy.SaturatingMultiply(room.Count.Max, RoomHierarchy.SaturatingAdd(room.Area.Max, children.Max)));
        var min = RoomHierarchy.SaturatingAdd(EffectiveMinimumArea(room), effective.Min);
        var target = RoomHierarchy.SaturatingAdd(Math.Max(room.Area.Target, EffectiveMinimumArea(room)), effective.Target);
        var max = RoomHierarchy.SaturatingAdd(room.Area.Max, effective.Max);
        if (room.ComplexArea is { } complex)
        {
            min = Math.Max(min, complex.Min); max = Math.Min(max, complex.Max);
            if (min > max) diagnostics.Add(new(path + ".complexArea", "impossible-complex-area", "The recursive room floor capacity cannot meet its complex-area bounds."));
            target = Math.Clamp((long) complex.Target, Math.Min(min, max), Math.Max(min, max));
        }
        node.EffectiveFloorBudget = new(RoomHierarchy.SaturatingMultiply(count, min), RoomHierarchy.SaturatingMultiply(Math.Max(count, room.Count.Target), target), RoomHierarchy.SaturatingMultiply(room.Count.Max, max));
        return node;
    }

    private static FloorBudget Sum(IEnumerable<FloorBudget> budgets)
    {
        long min = 0, target = 0, max = 0;
        foreach (var budget in budgets) { min = RoomHierarchy.SaturatingAdd(min, budget.Min); target = RoomHierarchy.SaturatingAdd(target, budget.Target); max = RoomHierarchy.SaturatingAdd(max, budget.Max); }
        return new(min, target, max);
    }

    private static void AddChoices(List<RoomChoiceRequirement> choices, Dictionary<string, RoomDefinition> rooms, ProgramCompilation result)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var choice in choices)
        {
            if (choice == null) { result.Diagnostics.Add(new("choices", "missing", "Null room choices are not allowed.")); continue; }
            var path = "choices:" + choice.Id;
            ProgramValidator.Text(choice.Description, path + ".description", 4096, result.Diagnostics);
            if (string.IsNullOrWhiteSpace(choice.Id) || choice.Id.Length > 64 || choice.Id.Contains('/') || choice.Id.Contains(':') || !ids.Add(choice.Id))
                result.Diagnostics.Add(new(path, "id", "Choice IDs must be unique, 1–64 characters and contain neither '/' nor ':'."));
            if (choice.Rooms == null || choice.Rooms.Count is < 1 or > 4096)
            { result.Diagnostics.Add(new(path + ".rooms", "count", "A room choice needs 1–4096 canonical room references.")); continue; }
            foreach (var room in choice.Rooms)
                if (room == null || !rooms.ContainsKey(room)) result.Diagnostics.Add(new(path + ".rooms", "reference", $"Unknown choice room '{room}'."));
            if (choice.Rooms.Distinct(StringComparer.Ordinal).Count() != choice.Rooms.Count) result.Diagnostics.Add(new(path + ".rooms", "duplicate", "Choice room references must be unique."));
            ProgramValidator.BoundedCount(choice.Count, path + ".count", choice.Rooms.Count, result.Diagnostics);
            result.Choices.Add(ProgramJson.Clone(choice));
        }
    }

    private static void AddConnections(List<ConnectionRequirement> connections, string? localPath, ProgramCompilation result, HashSet<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connection in connections)
        {
            var scope = localPath ?? "$station";
            if (connection == null) { result.Diagnostics.Add(new(scope + ".connections", "missing", "Null connection definitions are not allowed.")); continue; }
            var key = scope + ":" + connection.Id;
            void Error(string code, string message) => result.Diagnostics.Add(new(key, code, message));
            if (string.IsNullOrWhiteSpace(connection.Id) || connection.Id.Length > 64 || connection.Id.Contains('/') || connection.Id.Contains(':')) Error("id", "Connection IDs must contain 1–64 characters and cannot contain '/' or ':'.");
            if (!seen.Add(connection.Id ?? "")) Error("duplicate", "Connection IDs must be unique within their authoring scope.");
            var source = localPath != null && string.IsNullOrEmpty(connection.From) ? localPath : connection.From ?? "";
            if (localPath != null && source != localPath) Error("source", "Room-local connections always originate from their owning room; omit from or use its canonical path.");
            if (!paths.Contains(source)) Error("reference", $"Unknown source '{source}'.");
            var targets = new List<string>();
            if (connection.To == null || connection.To.Count is < 1 or > 256) Error("count", "A connection needs 1–256 targets.");
            else foreach (var target in connection.To)
            {
                var canonical = target == null ? "" : localPath != null ? RoomHierarchy.ResolveReference(localPath, target, paths) : target;
                targets.Add(canonical);
                if (!paths.Contains(canonical)) Error("reference", $"Unknown target '{canonical}'.");
                if (canonical == source) Error("self", "A connection must target a different room or owner.");
            }
            if (targets.Distinct(StringComparer.Ordinal).Count() != targets.Count) Error("duplicate", "Connection target references must be unique.");
            if (connection.Selection is not ("all" or "any")) Error("enum", "Connection selection is all or any.");
            if (connection.Priority is not ("required" or "desired" or "avoided")) Error("enum", "Connection priority is required, desired or avoided.");
            if (connection.Presence is not ("ifPresent" or "requireTarget")) Error("enum", "Connection presence is ifPresent or requireTarget.");
            if (connection.Priority == "avoided" && connection.Presence == "requireTarget") Error("presence", "Avoidance cannot require its own target to be instantiated; use ifPresent.");
            if (connection.Modes == null || connection.Modes.Count == 0 || connection.Modes.Any(m => m == null || !SupportedModes.Contains(m)) || connection.Modes.Distinct(StringComparer.Ordinal).Count() != connection.Modes.Count)
                Error("mode", "Use unique allowed connection modes: direct, department, publicHall, maintenance, controlledMaintenance, utilityNetwork, disposalShortcut, mail.");
            if (connection.Kind != "utility" && connection.Modes?.Contains("utilityNetwork") == true)
                Error("mode-kind", "utilityNetwork is a physical utility-only circuit mode; it cannot fulfill crew movement or interaction.");
            if (connection.Kind != "walking" && connection.Modes?.Contains("controlledMaintenance") == true)
                Error("mode-kind", "controlledMaintenance is a crew walking mode; utilities and interactions need their own physical networks/interfaces.");
            if (connection.MaxDistance is < 1 or > 1000000) Error("range", "Maximum distance must be null or 1–1000000 tiles.");
            if (connection.Kind is not ("walking" or "interaction" or "utility")) Error("enum", "Connection kind is walking, interaction or utility.");
            if (connection.Kind == "walking" && connection.Modes is { Count: > 0 } && connection.Modes.All(mode => mode == "mail")) Error("mode", "Mailing units accept items, not crew. A crew connection needs another mode; use a utility connection for mail delivery.");
            if (connection.Kind == "interaction" && connection.Modes != null && connection.Modes.Any(mode => mode != "direct")) Error("mode", "Interaction connections use direct interfaces, not traversable route networks.");
            if (connection.InterfaceTypes == null || connection.InterfaceTypes.Count > 7 || connection.InterfaceTypes.Any(type => type is not ("doorway" or "airlock" or "counter" or "window" or "dockingPort" or "pipe" or "cable")) || connection.InterfaceTypes.Distinct(StringComparer.Ordinal).Count() != connection.InterfaceTypes.Count)
                Error("interface", "Use unique interface types: doorway, airlock, counter, window, dockingPort, pipe, cable; [] leaves the interface unconstrained.");
            if (connection.MinWidth is < 0 or > 1000) Error("range", "Minimum interface width must be 0–1000 tiles.");
            if (connection.Kind == "walking" && connection.InterfaceTypes is { Count: > 0 } && connection.InterfaceTypes.All(type => type is "counter" or "window" or "pipe" or "cable")) Error("interface", "A walking connection needs a traversable interface alternative.");
            ProgramValidator.Weight(connection.Weight, key + ".weight", result.Diagnostics);
            ProgramValidator.Text(connection.Description, key + ".description", 4096, result.Diagnostics);
            result.Connections.Add(new()
            {
                Id = key, AuthoredId = connection.Id ?? "", From = source, To = targets,
                LocalScope = localPath != null,
                Selection = connection.Selection ?? "", Priority = connection.Priority ?? "", Presence = connection.Presence ?? "",
                Weight = connection.Weight, Modes = connection.Modes == null ? [] : [.. connection.Modes], MaxDistance = connection.MaxDistance, Description = connection.Description ?? ""
                , Kind = connection.Kind ?? "", InterfaceTypes = connection.InterfaceTypes == null ? [] : [.. connection.InterfaceTypes], MinWidth = connection.MinWidth
            });
        }
    }
}
