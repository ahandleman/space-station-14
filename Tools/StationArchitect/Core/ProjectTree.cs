namespace Content.StationArchitect;

/// <summary>Builds a fresh runtime hierarchy from authored requirements and persistent instance geometry.</summary>
public static class ProjectTree
{
    public static DesignTreeNode Build(StationDesignProject project)
    {
        var compilation = RequirementCompiler.Compile(project.Definition);
        // Form edits routinely pass through an invalid scalar range. Preserve the authored
        // hierarchy so the next field can be edited; compilation errors stay attached.
        var requirementTree = compilation.Tree ?? DraftRequirements(project.Definition);
        var root = new DesignTreeNode { Id = "$station", DefinitionPath = "$station", Kind = "station", Name = project.Definition.Name };
        root.Requirements = Copy(requirementTree);
        var diagnostics = compilation.Diagnostics.Concat(project.Report?.Diagnostics ?? []).Distinct().ToList();
        root.Diagnostics.AddRange(diagnostics);
        var instances = project.Layout.Rooms ?? [];
        foreach (var owner in requirementTree.Children)
        {
            var placement = project.Layout.Departments.FirstOrDefault(item => item.OwnerId == owner.Path);
            var ownerNode = new DesignTreeNode
            {
                Id = owner.Path, ParentId = root.Id, DefinitionPath = owner.Path, Kind = owner.Kind,
                Name = owner.Name, Requirements = Copy(owner), Pinned = placement?.Pinned == true, Locked = placement?.Locked == true
            };
            foreach (var template in owner.Children)
                ownerNode.Children.Add(BuildTemplate(template, ownerNode.Id, ""));
            Aggregate(ownerNode);
            var ownerArea = owner.Area ?? new AreaRequirement { Min = 0, Target = 0, Max = 0 };
            ownerNode.SelectionFloorBudget = new(Math.Max(ownerArea.Min, ownerNode.SelectionFloorBudget.Min), ownerArea.Target, Math.Min(ownerArea.Max, ownerNode.SelectionFloorBudget.Max));
            ownerNode.Diagnostics.AddRange(diagnostics.Where(item => item.Path == owner.Path || item.Path.StartsWith(owner.Path + ".", StringComparison.Ordinal)));
            root.Children.Add(ownerNode);
        }
        var known = RoomHierarchy.Nodes(requirementTree).Select(node => node.Path).ToHashSet(StringComparer.Ordinal);
        var activeIds = Flatten(root).Where(node => node.Kind == "instance").Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var orphans = instances.Where(room => room.Orphaned || !known.Contains(room.DefinitionPath) || !activeIds.Contains(room.Id) && (room.Cells.Count > 0 || room.Pinned || room.FloorLocked)).ToList();
        if (orphans.Count > 0)
        {
            var branch = new DesignTreeNode { Id = "$orphaned", ParentId = root.Id, Kind = "orphaned", Name = "Preserved stale geometry", Orphaned = true };
            foreach (var room in orphans.OrderBy(room => room.Id, StringComparer.Ordinal))
                branch.Children.Add(new() { Id = room.Id, ParentId = branch.Id, DefinitionPath = room.DefinitionPath, Kind = "instance", Name = room.Id, InstanceId = room.Id, FloorArea = room.Cells.Distinct().LongCount(), OwnFloorArea = room.Cells.Distinct().LongCount(), Pinned = room.Pinned, Locked = room.FloorLocked, Orphaned = true,
                    Diagnostics = [new(room.Id, "orphaned-instance", "This preserved instance has no active selection. Resolve or remove it explicitly before final validation.")] });
            Aggregate(branch);
            root.Children.Add(branch);
        }
        Aggregate(root);
        return root;

        DesignTreeNode BuildTemplate(RequirementNode template, string parentNodeId, string parentInstanceId)
        {
            var templateNode = new DesignTreeNode
            {
                Id = parentInstanceId.Length == 0 ? template.Path : RoomHierarchy.CountKey(parentInstanceId, template.Path),
                ParentId = parentNodeId, DefinitionPath = template.Path, Kind = "template", Name = template.Name, Requirements = Copy(template)
            };
            foreach (var room in instances.Where(room => room.DefinitionPath == template.Path && room.ParentInstanceId == parentInstanceId && room.Enabled && !room.Orphaned).OrderBy(room => InstanceOrder(room.Instance)).ThenBy(room => room.Id, StringComparer.Ordinal))
            {
                var areaRequirement = template.Area ?? new AreaRequirement { Min = 0, Target = 0, Max = 0 };
                var min = template.EffectiveMinArea ?? areaRequirement.Min;
                var preferred = room.TargetAreaOverride ?? room.PlannedArea ?? areaRequirement.Target;
                var target = min <= areaRequirement.Max ? Math.Clamp(preferred, min, areaRequirement.Max) : preferred;
                var instanceNode = new DesignTreeNode
                {
                    Id = room.Id, ParentId = templateNode.Id, DefinitionPath = template.Path, Kind = "instance", Name = template.Name + " " + room.Instance,
                    Requirements = Copy(template), InstanceId = room.Id, OwnFloorArea = room.Cells.Distinct().LongCount(), Pinned = room.Pinned, Locked = room.FloorLocked,
                    Diagnostics = diagnostics.Where(item => item.Path == room.Id || item.Path.StartsWith(room.Id + "/", StringComparison.Ordinal)).ToList()
                };
                foreach (var child in template.Children) instanceNode.Children.Add(BuildTemplate(child, instanceNode.Id, room.Id));
                Aggregate(instanceNode);
                instanceNode.FloorArea += instanceNode.OwnFloorArea; instanceNode.SelectedCount++;
                var childBudget = instanceNode.SelectionFloorBudget;
                instanceNode.SelectionFloorBudget = new(RoomHierarchy.SaturatingAdd(min, childBudget.Min), RoomHierarchy.SaturatingAdd(target, childBudget.Target), RoomHierarchy.SaturatingAdd(areaRequirement.Max, childBudget.Max));
                if (template.ComplexArea is { } complex)
                {
                    var budget = instanceNode.SelectionFloorBudget;
                    var lower = Math.Max(budget.Min, complex.Min); var upper = Math.Min(budget.Max, complex.Max);
                    instanceNode.SelectionFloorBudget = new(lower, Math.Clamp((long) complex.Target, Math.Min(lower, upper), Math.Max(lower, upper)), upper);
                }
                templateNode.Children.Add(instanceNode);
            }
            Aggregate(templateNode);
            templateNode.Diagnostics.AddRange(diagnostics.Where(item => item.Path == template.Path || item.Path.StartsWith(template.Path + ".", StringComparison.Ordinal) || item.Path.StartsWith(template.Path + ":", StringComparison.Ordinal)));
            return templateNode;
        }
    }

    public static void Refresh(StationDesignProject project) => project.Tree = Build(project);

    public static DesignTreeNode? Find(DesignTreeNode root, string id) => Flatten(root).FirstOrDefault(node => node.Id == id);

    /// <summary>The selected node first, followed by its parent requirements up to the station.</summary>
    public static List<DesignTreeNode> Ancestors(DesignTreeNode root, string id)
    {
        var index = Flatten(root).GroupBy(node => node.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var result = new List<DesignTreeNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (index.TryGetValue(id, out var node) && seen.Add(id))
        {
            result.Add(node);
            if (node.ParentId == null) break;
            id = node.ParentId;
        }
        return result;
    }

    public static IEnumerable<DesignTreeNode> Flatten(DesignTreeNode root)
    {
        var pending = new Stack<DesignTreeNode>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var next = pending.Pop(); yield return next;
            for (var i = next.Children.Count - 1; i >= 0; i--) pending.Push(next.Children[i]);
        }
    }

    private static RequirementNode Copy(RequirementNode source)
    {
        var result = ProgramJson.Clone(source);
        result.Children.Clear();
        return result;
    }

    private static RequirementNode DraftRequirements(StationProgramDocument definition)
    {
        var root = new RequirementNode { Path = "$station", Kind = "station", Name = definition.Name ?? "Station", Description = definition.Description ?? "", SourceRefs = [.. definition.SourceRefs ?? []], Gameflow = definition.Gameflow == null ? null : ProgramJson.Clone(definition.Gameflow), Systems = definition.Systems == null ? null : ProgramJson.Clone(definition.Systems) };
        foreach (var department in definition.Departments ?? [])
        {
            if (department == null) continue;
            var node = new RequirementNode
            {
                Path = department.Id ?? "", ParentPath = root.Path, Kind = "department", Name = string.IsNullOrEmpty(department.Name) ? department.Id ?? "Department" : department.Name, Description = department.Description ?? "",
                Tags = [.. department.Tags ?? []], SourceRefs = [.. department.SourceRefs ?? []],
                Area = department.Area == null ? null : ProgramJson.Clone(department.Area),
                Shape = department.Shape == null ? null : ProgramJson.Clone(department.Shape), Split = department.Split == null ? null : ProgramJson.Clone(department.Split)
            };
            AddRooms(node, department.Rooms); root.Children.Add(node);
        }
        if (definition.Maintenance != null)
        {
            var node = new RequirementNode { Path = RequirementCompiler.MaintenanceOwner, ParentPath = root.Path, Kind = "maintenance", Name = "True maintenance rooms", Area = definition.Maintenance.Area == null ? null : ProgramJson.Clone(definition.Maintenance.Area) };
            AddRooms(node, definition.Maintenance.Rooms); root.Children.Add(node);
        }
        return root;

        void AddRooms(RequirementNode owner, List<RoomDefinition>? rooms, int depth = 1)
        {
            if (depth > RoomHierarchy.MaxDepth) return;
            foreach (var room in rooms ?? [])
            {
                if (room == null) continue;
                var path = owner.Path + "/" + room.Id;
                var area = room.Area == null ? null : ProgramJson.Clone(room.Area);
                var count = room.Count == null ? null : ProgramJson.Clone(room.Count);
                var contents = room.Contents?.Where(content => content != null).ToList() ?? [];
                var reserved = contents.Aggregate(0L, (sum, content) =>
                {
                    var part = (long) Math.Max(0, content.Count?.Min ?? 0) * Math.Max(0, content.MinArea);
                    return sum > long.MaxValue - part ? long.MaxValue : sum + part;
                });
                var childNode = new RequirementNode
                {
                    Path = path, ParentPath = owner.Path, Kind = "room", Name = string.IsNullOrEmpty(room.Name) ? room.Id ?? "Room" : room.Name, Description = room.Description ?? "", Role = room.Role ?? "activity",
                    Tags = [.. room.Tags ?? []], SourceRefs = [.. room.SourceRefs ?? []], Area = area, Count = count,
                    Shape = room.Shape == null ? null : ProgramJson.Clone(room.Shape), Access = room.Access == null ? null : ProgramJson.Clone(room.Access),
                    Exterior = room.Exterior == null ? null : ProgramJson.Clone(room.Exterior), Environment = room.Environment == null ? null : ProgramJson.Clone(room.Environment),
                    Contents = ProgramJson.Clone(contents), AllowThroughTraffic = room.AllowThroughTraffic,
                    Placement = room.Placement == null ? null : ProgramJson.Clone(room.Placement), Nesting = room.Nesting == null ? null : ProgramJson.Clone(room.Nesting),
                    ComplexArea = room.ComplexArea == null ? null : ProgramJson.Clone(room.ComplexArea), ComplexShape = room.ComplexShape == null ? null : ProgramJson.Clone(room.ComplexShape),
                    EffectiveMinArea = (int) Math.Clamp(Math.Max(area?.Min ?? 0, reserved), 0, int.MaxValue), EffectiveMinCount = count?.Min ?? 0, ReservedContentArea = reserved,
                    FloorBudget = new((long) (count?.Min ?? 0) * (area?.Min ?? 0), (long) (count?.Target ?? 0) * (area?.Target ?? 0), (long) (count?.Max ?? 0) * (area?.Max ?? 0))
                };
                AddRooms(childNode, room.Rooms, depth + 1); owner.Children.Add(childNode);
            }
        }
    }

    private static void Aggregate(DesignTreeNode node)
    {
        node.FloorArea = node.Children.Sum(child => child.FloorArea);
        node.SelectedCount = node.Children.Sum(child => child.SelectedCount);
        node.SelectionFloorBudget = new(node.Children.Sum(child => child.SelectionFloorBudget.Min), node.Children.Sum(child => child.SelectionFloorBudget.Target), node.Children.Sum(child => child.SelectionFloorBudget.Max));
    }

    private static int InstanceOrder(string instance) => int.TryParse(instance, out var number) ? number : int.MaxValue;
}
