namespace Content.StationArchitect;

public static partial class ProgramValidator
{
    internal static void Gameflow(GameflowProgram? program, List<ProgramDiagnostic> errors)
    {
        void Error(string path, string message) => errors.Add(new("gameflow." + path, "gameflow-schema", message));
        if (program == null) { Error("", "A gameflow program is required; use empty rules to disable its pressures."); return; }
        if (program.AccessProfiles == null || program.AccessProfiles.Count > 32) Error("accessProfiles", "Use at most 32 access profiles.");
        else
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var profile in program.AccessProfiles)
            {
                if (profile == null) { Error("accessProfiles", "Null access profiles are not allowed."); continue; }
                var path = "gameflow.accessProfiles." + profile.Id;
                Id(profile.Id, path, errors); if (!ids.Add(profile.Id ?? "")) Error("accessProfiles", "Access profile IDs must be unique.");
                Text(profile.Description, path + ".description", 4096, errors);
                if (profile.AccessPolicy is not ("levels" or "allDoors")) Error("accessProfiles", "Access policy is levels or allDoors; edit protection flags never imply gameplay permissions.");
                GameflowStrings(profile.AllowedLevels, path + ".allowedLevels", 128, errors);
                GameflowStrings(profile.Networks, path + ".networks", 2, errors);
                if (profile.Networks?.Any(value => value is not ("publicHall" or "maintenance")) == true) Error("accessProfiles", "Traversal networks are publicHall or maintenance.");
                if (profile.DoorPenalty is < 0 or > 100 || profile.CrewTubeCost is < 1 or > 1000) Error("accessProfiles", "Door penalties must be 0–100 and crew tube costs 1–1000.");
            }
        }
        if (program.Rules == null || program.Rules.Count > 128) Error("rules", "Use at most 128 gameflow rules.");
        else
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in program.Rules)
            {
                if (rule == null) { Error("rules", "Null gameflow rules are not allowed."); continue; }
                var path = "gameflow.rules." + rule.Id;
                Id(rule.Id, path, errors); if (!ids.Add(rule.Id ?? "")) Error("rules", "Gameflow rule IDs must be unique.");
                Text(rule.Description, path + ".description", 4096, errors); Sources(rule.SourceRefs, path + ".sourceRefs", errors);
                if (rule.Kind is not ("chaseLoops" or "escapeRoutes" or "travelDistance" or "travelBalance" or "chokepoints")) Error("rules", "Unknown gameflow metric kind.");
                if (rule.Priority is not ("required" or "desired")) Error("rules", "Gameflow priority is required or desired; upper bounds express avoided outcomes.");
                if (rule.Aggregation is not ("min" or "max" or "mean")) Error("rules", "Travel/chokepoint aggregation is min, max or mean.");
                Weight(rule.Weight, path + ".weight", errors); Id(rule.AccessProfile, path + ".accessProfile", errors);
                GameflowSelector(rule.From, path + ".from", errors); GameflowSelector(rule.To, path + ".to", errors);
                if (rule.Bounds == null) Error("rules", "A gameflow bounds object is required.");
                else
                {
                    var values = new[] { rule.Bounds.Min, rule.Bounds.Target, rule.Bounds.Max }.Where(value => value.HasValue).Select(value => value!.Value).ToList();
                    if (values.Any(value => !double.IsFinite(value) || value < 0 || value > 1000000) ||
                        rule.Bounds.Min > rule.Bounds.Max || rule.Bounds.Min > rule.Bounds.Target || rule.Bounds.Target > rule.Bounds.Max)
                        Error("rules", "Present bounds must be finite, nonnegative, ordered min ≤ target ≤ max and at most 1000000.");
                }
                if (rule.Routes == null) Error("rules", "A route measurement policy is required.");
                else if (rule.Routes.MinSeparation is < 2 or > 32 || rule.Routes.EndpointRadius is < 1 or > 64 || rule.Routes.MinPathLength is < 8 or > 4096 ||
                    rule.Routes.MinLoopLength is < 24 or > 8192 || rule.Routes.MinEnclosedBlockedArea is < 4 or > 65536 ||
                    !double.IsFinite(rule.Routes.MaxDetourRatio) || rule.Routes.MaxDetourRatio is < 1 or > 100 ||
                    rule.Routes.ChokepointWidth is < 1 or > 12 || rule.Routes.DeduplicationRadius is < 1 or > 64)
                    Error("rules", "Route policy bounds must retain meaningful separated paths and enclosed obstacles; corridor grid squares are not chase loops.");
            }
        }
        var settings = program.Settings;
        if (settings == null) Error("settings", "Gameflow analysis settings are required.");
        else
        {
            if (settings.MaxGraphCells is < 64 or > 262144 || settings.MaxSearchVisits is < 64 or > 1000000 || settings.MaxTotalSearchVisits is < 64 or > 10000000 ||
                settings.MaxPairTests is < 1 or > 512 || settings.MaxTerminals is < 2 or > 128 || settings.MaxWitnesses is < 1 or > 128 || settings.MaxWitnessCells is < 64 or > 16384 || settings.MaxIndependentRoutes is < 1 or > 16 ||
                settings.RepairBudget is < 0 or > 8 || settings.CandidateBudget is < 1 or > 64 || settings.MaxRepairLength is < 2 or > 256)
                Error("settings", "Gameflow graph, search, witness and physical-repair budgets exceed their bounded ranges.");
            GameflowStrings(settings.RepairNetworks, "gameflow.settings.repairNetworks", 2, errors);
            if (settings.RepairNetworks?.Any(value => value is not ("publicHall" or "maintenance")) == true) Error("settings", "Repair networks are publicHall or maintenance.");
        }
    }

    internal static void GameflowReferences(StationProgramDocument document, Dictionary<string, RoomDefinition> rooms, HashSet<string> owners, List<ProgramDiagnostic> errors)
    {
        var profiles = document.Gameflow.AccessProfiles.Select(profile => profile.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var rule in document.Gameflow.Rules)
        {
            var path = "gameflow.rules." + rule.Id;
            if (!profiles.Contains(rule.AccessProfile)) errors.Add(new(path, "gameflow-reference", "The rule's explicit access profile does not exist."));
            foreach (var selector in new[] { rule.From, rule.To })
            {
                foreach (var target in selector.Paths)
                    if (!rooms.ContainsKey(target) && !rooms.ContainsKey(RoomHierarchy.TemplatePath(target)) && !owners.Contains(target)) errors.Add(new(path, "gameflow-reference", $"Unknown room, instance-template or owner path '{target}'."));
                foreach (var owner in selector.Owners)
                    if (!owners.Contains(owner)) errors.Add(new(path, "gameflow-reference", $"Unknown selector owner '{owner}'."));
            }
        }
    }

    private static void GameflowSelector(GameflowSelector? selector, string path, List<ProgramDiagnostic> errors)
    {
        if (selector == null) { errors.Add(new(path, "gameflow-schema", "A selector object is required.")); return; }
        GameflowStrings(selector.Paths, path + ".paths", 256, errors); GameflowStrings(selector.Tags, path + ".tags", 128, errors);
        GameflowStrings(selector.Owners, path + ".owners", 64, errors); GameflowStrings(selector.Roles, path + ".roles", 2, errors); GameflowStrings(selector.Networks, path + ".networks", 2, errors);
        if (selector.Match is not ("any" or "all") || selector.Roles?.Any(value => value is not ("activity" or "filler")) == true || selector.Networks?.Any(value => value is not ("publicHall" or "maintenance")) == true)
            errors.Add(new(path, "gameflow-schema", "Selector match is any/all, roles activity/filler, and networks publicHall/maintenance."));
    }

    private static void GameflowStrings(List<string>? values, string path, int maximum, List<ProgramDiagnostic> errors)
    {
        if (values == null || values.Count > maximum || values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Contains('\0')) || values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            errors.Add(new(path, "gameflow-schema", $"Use at most {maximum} distinct nonempty bounded strings."));
    }
}
