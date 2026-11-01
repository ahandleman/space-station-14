using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class ProjectTreeTests
{
    private static StationDesignProject New() => InstanceSelection.Resolve(new() { Definition = ProgramTestData.Minimal() });

    [Test]
    public void RuntimeTreeKeepsInstanceAndAncestorRequirementsAndAggregatesFloor()
    {
        var project = New();
        var room = project.Layout.Rooms.Single();
        room.Cells = Enumerable.Range(0, 40).Select(i => new Cell(i % 8, i / 8)).ToList();
        var tree = ProjectTree.Build(project);
        var ancestors = ProjectTree.Ancestors(tree, room.Id);
        Assert.That(ancestors.Select(node => node.Kind), Is.EqualTo(new[] { "instance", "template", "department", "station" }));
        Assert.That(ancestors.All(node => node.FloorArea == 40), Is.True);
        Assert.That(ancestors[0].Requirements!.Area!.Max, Is.EqualTo(120));
        Assert.That(ancestors[2].Requirements!.Area!.Max, Is.EqualTo(120));
        project.Definition.Departments[0].Rooms[0].Area.Max = 80;
        var fresh = ProjectTree.Build(project);
        Assert.That(ProjectTree.Find(fresh, room.Id)!.Requirements!.Area!.Max, Is.EqualTo(80));
        Assert.That(ancestors[0].Requirements!.Area!.Max, Is.EqualTo(120), "Previously returned requirement trees are independent snapshots.");
    }

    [Test]
    public void RoomChoicesResolveDeterministicallyWithoutMutatingDefinitions()
    {
        var project = New();
        project.Definition.Departments[0].Rooms.AddRange([ProgramTestData.Optional("OptionA"), ProgramTestData.Optional("OptionB")]);
        project.Definition.Choices = [new() { Id = "Support", Rooms = ["Alpha/OptionA", "Alpha/OptionB"] }];
        var before = ProgramJson.Clone(project.Definition);
        var first = InstanceSelection.Resolve(project);
        var second = InstanceSelection.Resolve(project);
        Assert.That(first.Report!.Success, Is.True, ProgramTestData.Errors(first.Report.Diagnostics));
        Assert.That(first.Layout.Rooms.Select(room => room.Id), Is.EqualTo(second.Layout.Rooms.Select(room => room.Id)));
        Assert.That(first.Layout.Rooms.Count(room => room.DefinitionPath is "Alpha/OptionA" or "Alpha/OptionB"), Is.EqualTo(1));
        Assert.That(System.Text.Json.JsonSerializer.Serialize(project.Definition, ProgramJson.Options), Is.EqualTo(System.Text.Json.JsonSerializer.Serialize(before, ProgramJson.Options)));
    }

    [Test]
    public void ExplicitCountConflictStaysVisibleInsteadOfWeakeningRequiredPresence()
    {
        var project = New();
        var option = ProgramTestData.Optional("Support");
        project.Definition.Departments[0].Rooms.Add(option);
        project.Definition.Departments[0].Rooms[0].Connections = [new() { Id = "NeedSupport", To = ["Support"], Presence = "requireTarget" }];
        project.Selection.RoomCounts["Alpha/Support"] = 0;
        var result = InstanceSelection.Resolve(project);
        Assert.That(result.Report!.Success, Is.False);
        Assert.That(result.Selection.RoomCounts["Alpha/Support"], Is.Zero);
        Assert.That(result.Report.Diagnostics.Any(item => item.Code is "selection-count" or "selection-presence"), Is.True);
        Assert.That(result.Definition.Departments[0].Rooms[0].Connections.Single().Presence, Is.EqualTo("requireTarget"));
    }

    [Test]
    public void ProtectedCountReductionRetainsStableGeometryAsAnExplicitOrphan()
    {
        var project = New();
        project.Definition.Departments[0].Rooms[0].Count = new() { Min = 1, Target = 2, Max = 3 };
        project = InstanceSelection.Resolve(project);
        var retained = project.Layout.Rooms.Single(room => room.Instance == "2");
        retained.Pinned = true; retained.Cells = [new(4, 5)];
        project.Selection.RoomCounts["Alpha/Work"] = 1;
        var result = InstanceSelection.Resolve(project);
        Assert.That(result.Layout.Rooms.Single(room => room.Id == "Alpha/Work#2").Cells, Is.EqualTo(new[] { new Cell(4, 5) }));
        Assert.That(result.Layout.Rooms.Single(room => room.Id == "Alpha/Work#2").Orphaned, Is.True);
        Assert.That(ProjectTree.Find(result.Tree!, "$orphaned"), Is.Not.Null);
        Assert.That(project.Layout.Rooms.Single(room => room.Id == "Alpha/Work#2").Orphaned, Is.False);
    }

    [Test]
    public void FloorTransferChangesRoomsButPreservesOwnerAreaAndInvalidatesDescendants()
    {
        var project = New();
        project.Definition.Departments[0].Rooms.Add(ProgramTestData.Room("Second", 1, 10, 40));
        project.Definition.Departments[0].Area.Max = 200;
        project = InstanceSelection.Resolve(project);
        project.Stage = "validation";
        foreach (var state in project.StageStates) { state.BuiltRevision = 1; state.Status = "complete"; }
        var first = project.Layout.Rooms.Single(room => room.DefinitionPath == "Alpha/Work");
        var second = project.Layout.Rooms.Single(room => room.DefinitionPath == "Alpha/Second");
        first.Cells = [new(0, 0), new(1, 0)]; second.Cells = [new(2, 0), new(3, 0)];
        var result = ProjectEdits.Apply(project, new() { Kind = "paint-room", NodeId = first.Id, Cells = [new(2, 0)] });
        Assert.That(result.Layout.Rooms.Single(room => room.Id == first.Id).Cells.Count, Is.EqualTo(3));
        Assert.That(result.Layout.Rooms.Single(room => room.Id == second.Id).Cells.Count, Is.EqualTo(1));
        Assert.That(ProjectTree.Find(result.Tree!, "Alpha")!.FloorArea, Is.EqualTo(4));
        Assert.That(result.StageStates.Where(state => DesignStages.Index(state.Stage) >= DesignStages.Index("growth")).All(state => state.Status == "dirty"), Is.True);
        Assert.That(project.Layout.Rooms.Single(room => room.Id == first.Id).Cells.Count, Is.EqualTo(2));
        Assert.That(result.Report!.Diagnostics.Any(item => item.Code == "area-bound"), Is.True, "A manual edit is retained together with its unsatisfied hard bounds.");
    }

    [Test]
    public void MovingAnOwnerCarriesChildGeometryEquipmentAndZones()
    {
        var project = New(); var room = project.Layout.Rooms.Single();
        project.Layout.Departments = [new() { OwnerId = "Alpha", Anchor = new(10, 10), Bounds = new(8, 8, 5, 5) }];
        room.Seed = new(9, 9); room.Cells = [new(9, 9)];
        project.Layout.Equipment = [new() { Id = "Machine", RoomId = room.Id, Position = new(9, 9), Footprint = [new(9, 9)], WorkArea = [new(9, 9), new(9, 10)] }];
        project.Layout.EnvironmentZones = [new() { RoomId = room.Id, Cells = [new(9, 9)] }];
        var result = ProjectEdits.Apply(project, new() { Kind = "move-owner", NodeId = "Alpha", Position = new(20, 21) });
        Assert.That(result.Layout.Rooms.Single().Cells, Is.EqualTo(new[] { new Cell(19, 20) }));
        Assert.That(result.Layout.Equipment.Single().Position, Is.EqualTo(new Cell(19, 20)));
        Assert.That(result.Layout.Equipment.Single().WorkArea, Is.EqualTo(new[] { new Cell(19, 20), new Cell(19, 21) }));
        Assert.That(result.Layout.EnvironmentZones.Single().Cells, Is.EqualTo(new[] { new Cell(19, 20) }));
        Assert.That(project.Layout.Rooms.Single().Cells, Is.EqualTo(new[] { new Cell(9, 9) }));
    }

    [Test]
    public void SelectionLimitRetainsTheExistingDraftRatherThanAllocatingUnboundedInstances()
    {
        var project = New();
        project.Settings.MaxRoomInstances = 1;
        project.Definition.Departments[0].Rooms[0].Count = new() { Min = 1, Target = 3, Max = 4 };
        var result = InstanceSelection.Resolve(project);
        Assert.That(result.Layout.Rooms.Count, Is.EqualTo(1));
        Assert.That(result.Report!.Diagnostics.Any(item => item.Code == "selection-limit"), Is.True);
        Assert.That(result.Definition.Departments[0].Rooms[0].Count.Target, Is.EqualTo(3));
    }

    [Test]
    public void ReviewChecksRawOverridesAndEmptyGrownFloorsInsteadOfSavedAcceptance()
    {
        var project = New(); project.Stage = "growth";
        project.Selection.RoomCounts["Alpha/Work"] = 100;
        project.Selection.RoomCounts["Missing/Room"] = 1;
        var diagnostics = ProjectEdits.Review(project);
        Assert.That(diagnostics.Any(item => item.Code == "selection-count" && item.Message.Contains("100")), Is.True);
        Assert.That(diagnostics.Any(item => item.Code == "selection-reference"), Is.True);
        Assert.That(diagnostics.Any(item => item.Code == "area-bound"), Is.True);
    }

    [Test]
    public void NewlyPaintedRoomHasAStableSeedAndLockedReservationForLaterStages()
    {
        var project = New(); var id = project.Layout.Rooms.Single().Id;
        var edited = ProjectEdits.Apply(project, new() { Kind = "paint-room", NodeId = id, Cells = [new(20, 20), new(21, 20)] });
        var room = edited.Layout.Rooms.Single();
        Assert.That(room.HasSeed, Is.True); Assert.That(room.Pinned, Is.True); Assert.That(room.FloorLocked, Is.True);
        Assert.That(room.Cells, Does.Contain(room.Seed)); Assert.That(room.GrowthReservation, Is.EquivalentTo(room.Cells));
    }

    [Test]
    public void InvalidIntermediateBoundsKeepTheEditableHierarchyAndFreshRawRequirements()
    {
        var project = New(); var room = project.Layout.Rooms.Single(); room.Cells = [new(20, 20)]; room.FloorEdited = true;
        project.Definition.Departments[0].Rooms[0].Area.Min = 150;
        var draft = ProjectTree.Build(project);
        Assert.That(ProjectTree.Find(draft, "Alpha"), Is.Not.Null);
        Assert.That(ProjectTree.Find(draft, "Alpha/Work"), Is.Not.Null);
        var instance = ProjectTree.Find(draft, room.Id)!;
        Assert.That(instance.Requirements!.Area!.Min, Is.EqualTo(150));
        Assert.That(instance.FloorArea, Is.EqualTo(1));
        Assert.That(ProjectTree.Find(draft, "$orphaned"), Is.Null, "An invalid range is not a removed definition.");
        Assert.That(draft.Diagnostics.Any(diagnostic => diagnostic.Severity == "error"), Is.True);
        project.Definition.Departments[0].Rooms[0].Area.Min = 31;
        project.Definition.Departments[0].Rooms[0].Area.Target = 40;
        var repaired = ProjectTree.Build(project);
        Assert.That(ProjectTree.Find(repaired, room.Id)!.Requirements!.Area!.Min, Is.EqualTo(31));
        Assert.That(ProjectTree.Find(repaired, room.Id)!.FloorArea, Is.EqualTo(1));
    }
}
