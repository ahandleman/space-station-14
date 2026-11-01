using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class NestedRoomTests
{
    private static StationProgramDocument Definition()
    {
        var document = ProgramTestData.Minimal(); var owner = document.Departments[0];
        owner.Area = ProgramTestData.Area(0, 118, 1000);
        var chapel = ProgramTestData.Room("Chapel", 16, 40, 120); chapel.Count = new() { Min = 1, Target = 2, Max = 2 };
        var confessional = ProgramTestData.Room("Confessional", 2, 6, 12); confessional.Count.Max = 2;
        confessional.Rooms = [ProgramTestData.Room("Booth", 1, 1, 4)];
        var crematorium = ProgramTestData.Room("Crematorium", 4, 12, 30); crematorium.Count.Min = 0;
        chapel.Rooms = [confessional, crematorium]; owner.Rooms = [chapel];
        return document;
    }

    private static StationDesignProject Project() => InstanceSelection.Resolve(new() { Definition = Definition() });

    private static PressureState Observations(StationDesignProject project)
    {
        var definitions = RequirementCompiler.Rooms(project.Definition).ToDictionary(pair => pair.Path, pair => pair.Room);
        var state = new PressureState { Departments = [new() { Path = "Alpha", Width = 16, Height = 8 }] };
        foreach (var room in project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned))
        {
            var definition = definitions[room.DefinitionPath]; var parent = definition.Rooms.Count > 0;
            var subtree = RoomHierarchy.Descendants(project, room, true).Sum(child => definitions[child.DefinitionPath].Area.Target);
            state.Rooms.Add(new()
            {
                Path = room.DefinitionPath, InstanceId = room.Instance, ParentInstanceId = room.ParentInstanceId,
                Area = definition.Area.Target, SubtreeFloorArea = subtree, ComplexArea = parent ? room.DefinitionPath == "Alpha/Chapel" ? 64 : 8 : null,
                Width = parent ? room.DefinitionPath == "Alpha/Chapel" ? 8 : 4 : definition.Area.Target == 1 ? 1 : 4,
                Height = parent ? room.DefinitionPath == "Alpha/Chapel" ? 8 : 2 : definition.Area.Target == 1 ? 1 : 3,
                Environment = new(), EntryOwners = [], Contents = [], ContainedInParent = true, ParentConnectionVerified = true, ParentApproachVerified = true
            });
        }
        return state;
    }

    [Test]
    public void RecursiveYamlBudgetsAndRelativeReferencesPreserveExclusiveFloorSemantics()
    {
        var definition = Definition(); var child = definition.Departments[0].Rooms[0].Rooms[0];
        child.Connections = [new() { Id = "ParentAndSibling", To = ["..", "../Crematorium"] }];
        child.Placement.Policy = "distributed"; child.Placement.Group = "ChapelComplex";
        var roundtrip = ProgramYaml.Parse(ProgramYaml.Export(definition));
        var compiled = RequirementCompiler.Compile(roundtrip);
        Assert.That(compiled.Success, Is.True, ProgramTestData.Errors(compiled.Diagnostics));
        Assert.That(RequirementCompiler.Rooms(roundtrip).Select(pair => pair.Path), Is.EqualTo(new[] { "Alpha/Chapel", "Alpha/Chapel/Confessional", "Alpha/Chapel/Confessional/Booth", "Alpha/Chapel/Crematorium" }));
        var owner = compiled.Tree!.Children.Single(node => node.Path == "Alpha");
        Assert.That(owner.FloorBudget.Min, Is.EqualTo(19)); Assert.That(owner.FloorBudget.Target, Is.EqualTo(118));
        Assert.That(owner.Children.Single().Area!.Target, Is.EqualTo(40), "A parent's own floor budget excludes every child's floor.");
        Assert.That(compiled.Connections.Single().To, Is.EqualTo(new[] { "Alpha/Chapel", "Alpha/Chapel/Crematorium" }));
        Assert.That(compiled.Connections.Single().LocalScope, Is.True);
    }

    [Test]
    public void EachParentHasItsOwnCountsStableIdentitiesAndProtectedDescendantOrphans()
    {
        var project = Project();
        project.Selection.ParentRoomCounts["Alpha/Chapel#1/Confessional"] = 2;
        project = InstanceSelection.Resolve(project);
        Assert.That(project.Report!.Success, Is.True, ProgramTestData.Errors(project.Report.Diagnostics));
        Assert.That(project.Layout.Rooms.Count, Is.EqualTo(10));
        var childId = "Alpha/Chapel#1/Confessional#2";
        var child = project.Layout.Rooms.Single(room => room.Id == childId); child.Cells = [new(100, 101)]; child.Pinned = true;
        var grandchild = project.Layout.Rooms.Single(room => room.ParentInstanceId == childId); grandchild.Cells = [new(102, 101)];
        var replay = InstanceSelection.Resolve(project);
        Assert.That(replay.Layout.Rooms.Select(room => room.Id), Is.EqualTo(project.Layout.Rooms.Select(room => room.Id)));
        Assert.That(replay.Layout.Rooms.Single(room => room.Id == childId).Cells, Is.EqualTo(child.Cells));
        replay.Selection.ParentRoomCounts["Alpha/Chapel#1/Confessional"] = 1;
        var reduced = InstanceSelection.Resolve(replay);
        Assert.That(reduced.Layout.Rooms.Single(room => room.Id == childId).Orphaned, Is.True);
        Assert.That(reduced.Layout.Rooms.Single(room => room.Id == grandchild.Id).Orphaned, Is.True);
        Assert.That(reduced.Layout.Rooms.Count(room => room.ParentInstanceId == "Alpha/Chapel#2" && room.DefinitionPath == child.DefinitionPath && room.Enabled), Is.EqualTo(1));
        Assert.That(RoomHierarchy.TryParseInstanceId(grandchild.Id, out var path, out var ordinal, out var parent), Is.True);
        Assert.That(path, Is.EqualTo("Alpha/Chapel/Confessional/Booth")); Assert.That(ordinal, Is.EqualTo("1")); Assert.That(parent, Is.EqualTo(childId));
    }

    [Test]
    public void RuntimeAncestorsAndBudgetsIncludeEveryRealParentWithoutCountingFloorTwice()
    {
        var project = Project(); var definitions = RequirementCompiler.Rooms(project.Definition).ToDictionary(pair => pair.Path, pair => pair.Room);
        var offset = 0;
        foreach (var room in project.Layout.Rooms) { room.Cells = Enumerable.Range(offset, definitions[room.DefinitionPath].Area.Target).Select(index => new Cell(index, 0)).ToList(); offset += room.Cells.Count; }
        var tree = ProjectTree.Build(project); var chapel = ProjectTree.Find(tree, "Alpha/Chapel#1")!;
        Assert.That(chapel.OwnFloorArea, Is.EqualTo(40)); Assert.That(chapel.FloorArea, Is.EqualTo(59)); Assert.That(tree.FloorArea, Is.EqualTo(118));
        Assert.That(ProjectTree.Ancestors(tree, "Alpha/Chapel#1/Confessional#1/Booth#1").Select(node => node.Id), Does.Contain("Alpha/Chapel#1"));
        project.Definition.Departments[0].Rooms[0].Rooms[0].Area.Min = 100;
        var draft = ProjectTree.Build(project);
        Assert.That(ProjectTree.Find(draft, "Alpha/Chapel#1/Confessional#1")!.Requirements!.Area!.Min, Is.EqualTo(100));
        Assert.That(ProjectTree.Find(draft, "$orphaned"), Is.Null, "A transient invalid bound retains the editable nested hierarchy.");
    }

    [Test]
    public void ParentEditsCarryDescendantsAndScopedCountsWhileRetainingOtherParentInstances()
    {
        var project = Project(); var parent = project.Layout.Rooms.Single(room => room.Id == "Alpha/Chapel#1");
        foreach (var room in project.Layout.Rooms) { room.Seed = new(10, 10); room.Cells = [new(10, 10)]; room.ComplexReservation = [new(10, 10)]; }
        var child = project.Layout.Rooms.Single(room => room.Id == "Alpha/Chapel#1/Confessional#1");
        project.Layout.Equipment = [new() { Id = "machine", RoomId = child.Id, Position = new(10, 10), Footprint = [new(10, 10)], WorkArea = [new(10, 10)] }];
        project.Layout.Interfaces = [new() { Id = "nested-door", From = parent.Id, To = child.Id, Cells = [new(10, 11)], FromApproach = new(10, 10), ToApproach = new(10, 12) }];
        var moved = ProjectEdits.Apply(project, new() { Kind = "move-room", NodeId = parent.Id, Position = new(20, 20) });
        Assert.That(moved.Layout.Rooms.Single(room => room.Id == child.Id).Cells, Is.EqualTo(new[] { new Cell(20, 20) }));
        Assert.That(moved.Layout.Rooms.Single(room => room.Id == "Alpha/Chapel#2").Cells, Is.EqualTo(new[] { new Cell(10, 10) }));
        Assert.That(moved.Layout.Interfaces.Single().Cells, Is.EqualTo(new[] { new Cell(20, 21) }));
        Assert.That(moved.Layout.Equipment.Single().WorkArea, Is.EqualTo(new[] { new Cell(20, 20) }));
        var locked = ProjectEdits.Apply(moved, new() { Kind = "lock-room", NodeId = parent.Id, Enabled = true });
        Assert.That(RoomHierarchy.Descendants(locked, locked.Layout.Rooms.Single(room => room.Id == parent.Id), true).All(room => room.FloorLocked), Is.True);
        var counted = ProjectEdits.Apply(locked, new() { Kind = "set-count", NodeId = "Alpha/Chapel#2/Confessional", Value = 2 });
        Assert.That(counted.Selection.ParentRoomCounts["Alpha/Chapel#2/Confessional"], Is.EqualTo(2));
        var removed = ProjectEdits.Apply(counted, new() { Kind = "remove-instance", NodeId = parent.Id });
        Assert.That(removed.Layout.Rooms.Any(room => room.Id.StartsWith(parent.Id, StringComparison.Ordinal)), Is.False);
        Assert.That(removed.Layout.Equipment, Is.Empty); Assert.That(removed.Layout.Interfaces, Is.Empty);
    }

    [Test]
    public void PressureChecksCountsPerParentAndRequiresActualComplexAndApproachObservations()
    {
        var project = Project(); project.Definition.Departments[0].Rooms[0].ComplexArea = ProgramTestData.Area(50, 59, 70);
        var state = Observations(project);
        var report = PressureEvaluator.Evaluate(project.Definition, state, true);
        Assert.That(report.Success, Is.True, ProgramTestData.Errors(report.Diagnostics));
        var parent = state.Rooms.First(room => room.Path == "Alpha/Chapel"); parent.SubtreeFloorArea = 40;
        Assert.That(PressureEvaluator.Evaluate(project.Definition, state, true).Diagnostics.Any(item => item.Code == "complex-area-measurement"), Is.True);
        parent.SubtreeFloorArea = 59; parent.ComplexArea = null;
        Assert.That(PressureEvaluator.Evaluate(project.Definition, state, true).Diagnostics.Any(item => item.Code == "complex-shape-measurement"), Is.True);
        parent.ComplexArea = 64;
        state.Rooms.First(room => room.Path.EndsWith("/Confessional", StringComparison.Ordinal)).ParentConnectionVerified = null;
        Assert.That(PressureEvaluator.Evaluate(project.Definition, state, true).Diagnostics.Any(item => item.Code == "nested-parent-connection-measurement"), Is.True);
    }

    [Test]
    public void MultiplicativeCountsAreBoundedAndInvalidNestedPoliciesCannotBeFlattenedAway()
    {
        var project = Project(); project.Settings.MaxRoomInstances = 8;
        project.Selection.ParentRoomCounts["Alpha/Chapel#1/Confessional"] = 2;
        var result = InstanceSelection.Resolve(project);
        Assert.That(result.Report!.Diagnostics.Any(item => item.Code == "selection-limit"), Is.True);
        Assert.That(result.Layout.Rooms.Count, Is.EqualTo(8), "An oversized request preserves the existing draft.");
        project.Definition.Departments[0].Rooms[0].Rooms[0].Placement.Policy = "flatten";
        Assert.That(RequirementCompiler.Compile(project.Definition).Success, Is.False);
    }

    [Test]
    public void LocalNestedConnectionsCannotBorrowASiblingOrUncleFromAnotherParentInstance()
    {
        var project = Project();
        project.Definition.Departments[0].Rooms[0].Rooms[0].Connections = [new() { Id = "Sibling", To = ["../Crematorium"], Modes = ["direct"] }];
        var requirement = RequirementCompiler.Compile(project.Definition).Connections.Single();
        var state = Observations(project);
        state.Connections =
        [
            new() { Id = requirement.Id, SourceInstance = "Alpha/Chapel#1/Confessional#1", TargetInstance = "Alpha/Chapel#2/Crematorium#1", InterfaceType = "doorway", Width = 1 },
            new() { Id = requirement.Id, SourceInstance = "Alpha/Chapel#2/Confessional#1", TargetInstance = "Alpha/Chapel#1/Crematorium#1", InterfaceType = "doorway", Width = 1 }
        ];
        Assert.That(PressureEvaluator.Evaluate(project.Definition, state, true).Diagnostics.Count(item => item.Code == "measurement-parent-scope"), Is.EqualTo(2));
        var booth = project.Definition.Departments[0].Rooms[0].Rooms[0].Rooms[0];
        booth.Connections = [new() { Id = "Uncle", To = ["../../Crematorium"], Modes = ["direct"] }];
        var uncle = RequirementCompiler.Compile(project.Definition).Connections.Single(connection => connection.AuthoredId == "Uncle");
        Assert.That(uncle.To.Single(), Is.EqualTo("Alpha/Chapel/Crematorium"));
        Assert.That(RoomHierarchy.ConnectionScopeAllowed(uncle, "Alpha/Chapel/Confessional/Booth", "Alpha/Chapel#1/Confessional#1/Booth#1", "Alpha/Chapel#1/Confessional#1", "Alpha/Chapel/Crematorium", "Alpha/Chapel#2/Crematorium#1", "Alpha/Chapel#2"), Is.False);
        Assert.That(RoomHierarchy.ConnectionScopeAllowed(uncle, "Alpha/Chapel/Confessional/Booth", "Alpha/Chapel#1/Confessional#1/Booth#1", "Alpha/Chapel#1/Confessional#1", "Alpha/Chapel/Crematorium", "Alpha/Chapel#1/Crematorium#1", "Alpha/Chapel#1"), Is.True);
    }

    [Test]
    public void PerParentOverridesMustFitTheirSelectedComplexCapacity()
    {
        var project = Project(); project.Definition.Departments[0].Rooms[0].ComplexArea = ProgramTestData.Area(19, 19, 20);
        project.Selection.RoomCounts["Alpha/Chapel/Crematorium"] = 0;
        project = InstanceSelection.Resolve(project);
        Assert.That(project.Report!.Success, Is.True, ProgramTestData.Errors(project.Report.Diagnostics));
        var before = project.Layout.Rooms.Select(room => room.Id).ToList();
        project.Selection.ParentRoomCounts["Alpha/Chapel#1/Confessional"] = 2;
        var invalid = InstanceSelection.Resolve(project);
        Assert.That(invalid.Report!.Diagnostics.Any(item => item.Code == "selection-complex-area" && item.Path == "Alpha/Chapel#1"), Is.True);
        Assert.That(invalid.Layout.Rooms.Select(room => room.Id), Is.EqualTo(before));
        Assert.That(invalid.Selection.ParentRoomCounts["Alpha/Chapel#1/Confessional"], Is.EqualTo(2));
    }

    [Test]
    public void MovingAndRemovingAComplexRefreshesDerivedWallsWithoutChangingOtherRoomFloor()
    {
        var project = Project(); var parent = project.Layout.Rooms.Single(room => room.Id == "Alpha/Chapel#1");
        var child = project.Layout.Rooms.Single(room => room.Id == "Alpha/Chapel#1/Confessional#1");
        var other = project.Layout.Rooms.Single(room => room.Id == "Alpha/Chapel#2");
        parent.Seed = new(10, 10); parent.Cells = new GridBounds(10, 10, 3, 3).Cells().ToList();
        child.Seed = new(14, 11); child.Cells = new GridBounds(14, 11, 2, 2).Cells().ToList();
        other.Seed = new(60, 60); other.Cells = new GridBounds(60, 60, 3, 3).Cells().ToList();
        DesignRouting.RebuildBoundaries(project);
        Assert.That(project.Layout.Walls.Concat(project.Layout.Hull), Does.Contain(new Cell(9, 10)));
        var moved = ProjectEdits.Apply(project, new() { Kind = "move-room", NodeId = parent.Id, Position = new(30, 10) });
        Assert.That(moved.Layout.Walls.Concat(moved.Layout.Hull), Does.Not.Contain(new Cell(9, 10)));
        Assert.That(moved.Layout.Walls.Concat(moved.Layout.Hull), Does.Contain(new Cell(29, 10)));
        var removed = ProjectEdits.Apply(moved, new() { Kind = "remove-instance", NodeId = parent.Id });
        Assert.That(removed.Layout.Walls.Concat(removed.Layout.Hull), Does.Not.Contain(new Cell(29, 10)));
        Assert.That(removed.Layout.Walls.Concat(removed.Layout.Hull), Does.Contain(new Cell(59, 60)));
        Assert.That(removed.Layout.Rooms.Single(room => room.Id == other.Id).Cells, Is.EqualTo(other.Cells));
    }

    [Test]
    public void PerParentOverridesCannotHideAnActuallyEmptyChoice()
    {
        var project = Project(); var parent = project.Definition.Departments[0].Rooms[0]; parent.Rooms[0].Count.Min = 0;
        project.Definition.Choices = [new() { Id = "ChapelSupport", Rooms = ["Alpha/Chapel/Confessional", "Alpha/Chapel/Crematorium"], Count = new() { Min = 1, Target = 1, Max = 2 } }];
        foreach (var ordinal in new[] { "1", "2" })
        foreach (var child in new[] { "Confessional", "Crematorium" }) project.Selection.ParentRoomCounts[$"Alpha/Chapel#{ordinal}/{child}"] = 0;
        var resolved = InstanceSelection.Resolve(project);
        Assert.That(resolved.Report!.Diagnostics.Any(item => item.Code == "selection-choice" && item.Message.Contains("After per-parent")), Is.True);
        Assert.That(resolved.Selection.ParentRoomCounts.Values.All(count => count == 0), Is.True);
    }

    [Test]
    public void RequiredLocalPresenceCannotUseATargetSelectedOnlyInAnotherParent()
    {
        var project = Project(); var source = project.Definition.Departments[0].Rooms[0].Rooms[0]; source.Count.Min = 0;
        source.Connections = [new() { Id = "SiblingPresence", To = ["../Crematorium"], Presence = "requireTarget" }];
        project.Selection.ParentRoomCounts["Alpha/Chapel#1/Crematorium"] = 0;
        var resolved = InstanceSelection.Resolve(project);
        Assert.That(resolved.Report!.Diagnostics.Any(item => item.Code == "selection-presence" && item.Path == "Alpha/Chapel#1/Confessional#1"), Is.True);
        Assert.That(resolved.Selection.ParentRoomCounts["Alpha/Chapel#1/Crematorium"], Is.Zero);
        var imported = DesignPipeline.Import(resolved);
        Assert.That(imported.Report!.Success, Is.False, "Import must not hide an unapplied scoped count request behind preserved older geometry.");
        Assert.That(imported.Report.Diagnostics.Any(item => item.Code == "selection-count" && item.Path == "Alpha/Chapel#1/Crematorium"), Is.True);
    }
}
