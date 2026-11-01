namespace Content.StationArchitect;

/// <summary>Resolves preferred counts and mandatory room-choice/presence requirements without changing authored definitions.</summary>
public static class InstanceSelection
{
    public static StationDesignProject Resolve(StationDesignProject source)
    {
        if (source?.Definition == null || source.Settings == null || source.Selection?.RoomCounts == null || source.Selection.AutoRoomCounts == null || source.Selection.ParentRoomCounts == null || source.Layout?.Rooms == null)
            throw new ArgumentException("A design needs non-null definition, settings, selection maps and room layout.");
        var project = ProgramJson.Clone(source);
        var compiled = RequirementCompiler.Compile(project.Definition);
        var diagnostics = new List<ProgramDiagnostic>(compiled.Diagnostics);
        if (!compiled.Success)
        {
            project.Report = new() { Stage = "selection", Diagnostics = diagnostics };
            ProjectTree.Refresh(project); return project;
        }
        var definitions = RequirementCompiler.Rooms(project.Definition).ToDictionary(pair => pair.Path, pair => pair.Room, StringComparer.Ordinal);
        var minima = RoomHierarchy.Nodes(compiled.Tree!).Where(node => node.Kind == "room").ToDictionary(node => node.Path, node => node.EffectiveMinCount ?? node.Count!.Min, StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (path, definition) in definitions)
        {
            var explicitCount = project.Selection.RoomCounts.TryGetValue(path, out var chosen);
            var desired = explicitCount ? chosen : project.Selection.AutoRoomCounts.GetValueOrDefault(path, Math.Max(minima[path], definition.Count.Target));
            if (desired < minima[path] || desired > definition.Count.Max)
                diagnostics.Add(new(path + ".count", "selection-count", $"Selected count {desired} must be between required minimum {minima[path]} and maximum {definition.Count.Max}."));
            counts[path] = Math.Clamp(desired, 0, 64);
        }
        foreach (var path in project.Selection.RoomCounts.Keys.Concat(project.Selection.AutoRoomCounts.Keys).Distinct(StringComparer.Ordinal).Where(path => !definitions.ContainsKey(path)))
            diagnostics.Add(new(path, "selection-reference", "This count selection refers to a removed room template; remove the stale override explicitly."));
        if (project.Settings.MaxRoomInstances is < 1 or > 4096)
            diagnostics.Add(new("settings.maxRoomInstances", "selection-limit", "Maximum room instances must be between 1 and 4096."));
        long RequestedTotal() => RoomHierarchy.RequestedTotal(project, definitions, counts);
        if (RequestedTotal() > project.Settings.MaxRoomInstances)
            diagnostics.Add(new("selection", "selection-limit", $"Requested selected instances exceed the configured limit of {project.Settings.MaxRoomInstances}. The existing draft was retained."));
        if (diagnostics.Any(item => item.Severity == "error"))
        {
            project.Report = new() { Stage = "selection", Diagnostics = diagnostics };
            ProjectTree.Refresh(project); return project;
        }
        var promoted = new HashSet<string>(StringComparer.Ordinal);
        bool Exists(string path) => counts.ContainsKey(path) ? RoomHierarchy.Multiplicity(path, counts) > 0 : counts.Keys.Any(candidate => RequirementCompiler.Owner(candidate) == path && RoomHierarchy.Multiplicity(candidate, counts) > 0);
        int OwnerMax(string owner) => owner == RequirementCompiler.MaintenanceOwner ? project.Definition.Maintenance.Area.Max : project.Definition.Departments.Single(department => department.Id == owner).Area.Max;
        bool Viable(string path)
        {
            if (!definitions.TryGetValue(path, out var definition)) return definitions.Keys.Any(candidate => RequirementCompiler.Owner(candidate) == path && Viable(candidate));
            var hypothetical = new Dictionary<string, int>(counts, StringComparer.Ordinal);
            for (var current = path; definitions.TryGetValue(current, out var ancestor); current = RoomHierarchy.ParentPath(current))
            {
                if (ancestor.Count.Max == 0 || hypothetical[current] == 0 && project.Selection.RoomCounts.ContainsKey(current)) return false;
                hypothetical[current] = Math.Max(1, hypothetical[current]);
            }
            var owner = RequirementCompiler.Owner(path);
            return RoomHierarchy.MinimumFloor(owner, definitions, hypothetical) <= OwnerMax(owner);
        }
        double Preference(string path) => definitions[path].Count.Target * definitions[path].Count.Weight;
        string? Candidate(IEnumerable<string> paths) => paths.SelectMany(path => definitions.ContainsKey(path) ? new[] { path } : definitions.Keys.Where(candidate => RequirementCompiler.Owner(candidate) == path))
            .Where(path => !Exists(path) && Viable(path) && !project.Selection.RoomCounts.ContainsKey(path)).OrderByDescending(Preference).ThenBy(path => path, StringComparer.Ordinal).FirstOrDefault();
        bool Activate(string path)
        {
            if (Exists(path)) return false;
            if (!definitions.ContainsKey(path))
            {
                var candidate = Candidate([path]); return candidate != null && Activate(candidate);
            }
            if (!Viable(path) || project.Selection.RoomCounts.ContainsKey(path)) return false;
            var changed = false;
            for (var current = path; definitions.TryGetValue(current, out var definition); current = RoomHierarchy.ParentPath(current))
                if (counts[current] == 0) { counts[current] = Math.Max(1, definition.Count.Target); promoted.Add(current); changed = true; }
            return changed;
        }
        for (var pass = 0; pass <= definitions.Count; pass++)
        {
            var changed = false;
            foreach (var choice in compiled.Choices)
            {
                var active = choice.Rooms.Count(Exists);
                var preferred = Math.Clamp(choice.Count.Target, choice.Count.Min, choice.Count.Max);
                foreach (var path in choice.Rooms.Where(Exists).Where(path => minima[path] == 0 && !promoted.Contains(path) && !project.Selection.RoomCounts.ContainsKey(path)).OrderBy(Preference).ThenByDescending(path => path, StringComparer.Ordinal))
                {
                    if (active <= choice.Count.Max) break;
                    counts[path] = 0; active--; changed = true;
                }
                while (active < preferred)
                {
                    var candidate = Candidate(choice.Rooms);
                    if (candidate == null || !Activate(candidate)) break;
                    active++; changed = true;
                }
            }
            foreach (var connection in compiled.Connections.Where(connection => connection.Priority == "required" && connection.Presence == "requireTarget" && Exists(connection.From)))
            {
                if (connection.Selection == "all")
                    foreach (var path in connection.To) changed |= Activate(path);
                else if (!connection.To.Any(Exists))
                {
                    var candidate = Candidate(connection.To);
                    if (candidate != null) changed |= Activate(candidate);
                }
            }
            if (!changed) break;
        }
        foreach (var choice in compiled.Choices)
        {
            var active = choice.Rooms.Count(Exists);
            if (active < choice.Count.Min || active > choice.Count.Max) diagnostics.Add(new("choices:" + choice.Id, "selection-choice", $"Selected {active} room types; this choice requires {choice.Count.Min}–{choice.Count.Max}. Explicit count overrides are preserved."));
        }
        foreach (var owner in compiled.Tree!.Children)
        {
            var floor = RoomHierarchy.MinimumFloor(owner.Path, definitions, counts);
            if (project.Selection.ParentRoomCounts.Count == 0 && floor > owner.Area!.Max)
                diagnostics.Add(new(owner.Path + ".area", "selection-area", $"Selected room minima require {floor} floor tiles, above owner maximum {owner.Area.Max}. Reduce optional counts or increase the budget."));
        }
        if (RequestedTotal() > project.Settings.MaxRoomInstances)
        {
            diagnostics.Add(new("selection", "selection-limit", $"Required room implications exceed the configured instance limit of {project.Settings.MaxRoomInstances}; the existing draft was retained."));
            project.Report = new() { Stage = "selection", Diagnostics = diagnostics }; ProjectTree.Refresh(project); return project;
        }
        foreach (var connection in compiled.Connections.Where(connection => connection.Priority == "required" && connection.Presence == "requireTarget" && Exists(connection.From)))
            if (connection.Selection == "all" ? connection.To.Any(path => !Exists(path)) : !connection.To.Any(Exists))
                diagnostics.Add(new(connection.Id, "selection-presence", "An active required source has no selected required target. Explicit room-count overrides are preserved."));
        var result = new List<DesignRoom>();
        var existing = project.Layout.Rooms;
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var validCountKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in counts.Keys.Where(path => RoomHierarchy.ParentTemplatePath(path) == null).Order(StringComparer.Ordinal))
            Materialize(path, "");
        foreach (var key in project.Selection.ParentRoomCounts.Keys.Where(key => !validCountKeys.Contains(key)))
            diagnostics.Add(new(key, "selection-reference", "This per-parent count refers to a missing parent instance or child template."));
        foreach (var owner in compiled.Tree.Children)
        {
            var floor = result.Where(room => room.OwnerId == owner.Path).Sum(room => (long) RequirementCompiler.EffectiveMinimumArea(definitions[room.DefinitionPath]));
            if (floor > owner.Area!.Max) diagnostics.Add(new(owner.Path, "selection-area", $"Selected per-parent room minima require {floor} tiles, above owner maximum {owner.Area.Max}."));
        }
        var capacityCache = new Dictionary<string, (long Min, long Max)>(StringComparer.Ordinal);
        foreach (var room in result) Capacity(room);
        List<DesignRoom> Actual(string path) => result.Where(room => definitions.ContainsKey(path) ? room.DefinitionPath == path : room.OwnerId == path).ToList();
        foreach (var choice in compiled.Choices)
        {
            var selected = choice.Rooms.Count(path => Actual(path).Count > 0);
            if (selected < choice.Count.Min || selected > choice.Count.Max) diagnostics.Add(new("choices:" + choice.Id, "selection-choice", $"After per-parent overrides, {selected} actual room templates are selected; the choice requires {choice.Count.Min} to {choice.Count.Max}."));
        }
        foreach (var connection in compiled.Connections.Where(connection => connection.Priority == "required" && connection.Presence == "requireTarget"))
        foreach (var sourceRoom in Actual(connection.From))
        {
            bool Available(string path) => Actual(path).Any(target => RoomHierarchy.ConnectionScopeAllowed(connection,
                sourceRoom.DefinitionPath, sourceRoom.Id, sourceRoom.ParentInstanceId, target.DefinitionPath, target.Id, target.ParentInstanceId));
            if (connection.Selection == "all" ? connection.To.Any(path => !Available(path)) : !connection.To.Any(Available))
                diagnostics.Add(new(sourceRoom.Id, "selection-presence", $"Required connection {connection.Id} has no selected target in this source instance's permitted parent scope after per-parent overrides."));
        }
        if (diagnostics.Any(item => item.Severity == "error"))
        {
            project.Report = new() { Stage = "selection", Diagnostics = diagnostics.Distinct().ToList() }; ProjectTree.Refresh(project); return project;
        }
        foreach (var room in existing.Where(room => !usedIds.Contains(room.Id) && (Protected(room) || !definitions.ContainsKey(room.DefinitionPath))))
        {
            room.Enabled = false; room.Orphaned = true; result.Add(room);
            diagnostics.Add(new(room.Id, "orphaned-instance", "This unselected instance or removed definition retains its generated geometry until explicitly removed or restored."));
        }
        foreach (var duplicate in result.GroupBy(room => room.Id, StringComparer.Ordinal).Where(group => group.Count() > 1))
            diagnostics.Add(new(duplicate.Key, "duplicate-instance", "Room instance IDs must be unique; fix the imported project before generating."));
        project.Layout.Rooms = result;
        project.Stage = "selection";
        project.Report = new() { Stage = "selection", Complete = diagnostics.All(item => item.Severity != "error"), Diagnostics = diagnostics.Distinct().ToList() };
        ProjectTree.Refresh(project);
        return project;

        (long Min, long Max) Capacity(DesignRoom room)
        {
            if (capacityCache.TryGetValue(room.Id, out var cached)) return cached;
            var definition = definitions[room.DefinitionPath];
            long min = RequirementCompiler.EffectiveMinimumArea(definition), max = definition.Area.Max;
            foreach (var child in result.Where(child => child.ParentInstanceId == room.Id))
            {
                var childCapacity = Capacity(child);
                min = RoomHierarchy.SaturatingAdd(min, childCapacity.Min); max = RoomHierarchy.SaturatingAdd(max, childCapacity.Max);
            }
            if (definition.ComplexArea is { } complex)
            {
                var availableMin = min; var availableMax = max;
                min = Math.Max(min, complex.Min); max = Math.Min(max, complex.Max);
                if (min > max) diagnostics.Add(new(room.Id, "selection-complex-area", $"Selected children permit {availableMin} to {availableMax} usable complex tiles, incompatible with this instance's required complex bounds {complex.Min} to {complex.Max}. Count overrides and authored requirements were retained."));
            }
            return capacityCache[room.Id] = (min, max);
        }

        void Materialize(string path, string parentId)
        {
            var count = counts[path];
            if (parentId.Length > 0)
            {
                var key = RoomHierarchy.CountKey(parentId, path); validCountKeys.Add(key);
                if (project.Selection.ParentRoomCounts.TryGetValue(key, out var requested)) count = requested;
                if (count < minima[path] || count > definitions[path].Count.Max)
                { diagnostics.Add(new(key, "selection-count", $"Per-parent selected count {count} must be between {minima[path]} and {definitions[path].Count.Max}.")); return; }
            }
            if (result.Count + (long) count > project.Settings.MaxRoomInstances)
            { diagnostics.Add(new("selection", "selection-limit", "Per-parent room counts exceed the configured instance limit; the existing draft was retained.")); return; }
            var used = new HashSet<string>(StringComparer.Ordinal);
            var selected = new List<DesignRoom>();
            var rooms = existing.Where(room => room.DefinitionPath == path && room.ParentInstanceId == parentId).OrderBy(room => int.TryParse(room.Instance, out var number) ? number : int.MaxValue).ThenBy(room => room.Id, StringComparer.Ordinal).ToList();
            foreach (var existingRoom in rooms.Take(count))
            {
                var room = ProgramJson.Clone(existingRoom);
                room.Enabled = true; room.Orphaned = false; room.OwnerId = RequirementCompiler.Owner(path);
                selected.Add(room); used.Add(room.Instance);
            }
            for (var number = 1; selected.Count < count; number++)
            {
                var instance = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!used.Add(instance)) continue;
                selected.Add(new() { Id = RoomHierarchy.InstanceId(path, instance, parentId), DefinitionPath = path, OwnerId = RequirementCompiler.Owner(path), Instance = instance, ParentInstanceId = parentId });
            }
            foreach (var room in selected)
            {
                result.Add(room); usedIds.Add(room.Id);
                foreach (var child in definitions[path].Rooms) Materialize(path + "/" + child.Id, room.Id);
            }
        }
    }

    private static bool Protected(DesignRoom room) => room.Pinned || room.FloorLocked || room.FloorEdited || room.Cells.Count > 0;
}
