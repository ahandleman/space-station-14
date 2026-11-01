using System.Text.Json;
using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class InterfaceEditTests
{
    private static StationDesignProject SharedDoor()
    {
        var definition = ProgramTestData.Minimal();
        definition.Departments[0].Rooms.Add(ProgramTestData.Room("Second", 16, 25, 60));
        definition.Departments[0].Area.Max = 200;
        foreach (var room in definition.Departments[0].Rooms)
        { room.AllowThroughTraffic = true; room.Access.AllowPublicEntry = false; room.Access.AllowMaintenanceEntry = false; }
        var project = InstanceSelection.Resolve(new() { Definition = definition });
        project.Layout.Rooms.Single(room => room.DefinitionPath == "Alpha/Work").Cells = new GridBounds(10, 10, 4, 6).Cells().ToList();
        project.Layout.Rooms.Single(room => room.DefinitionPath == "Alpha/Second").Cells = new GridBounds(15, 10, 4, 6).Cells().ToList();
        project.Layout.Envelope = new GridBounds(9, 9, 11, 8).Cells().ToList();
        project.Layout.Interfaces = [new() { Id = "SharedDoor", From = "Alpha/Work#1", To = "Alpha/Second#1", Kind = "walking", Type = "airlock", Width = 2, Cells = [new(14, 11), new(14, 12)], FromApproach = new(13, 11), ToApproach = new(15, 11) }];
        DesignRouting.RebuildBoundaries(project);
        project.Stage = "validation";
        foreach (var state in project.StageStates) { state.BuiltRevision = 1; state.Status = "ready"; }
        return project;
    }

    [Test]
    public void MovingAndResizingAnInterfaceRebuildsWallCutsPreservesRoomsAndSurvivesRouting()
    {
        var source = SharedDoor();
        var floors = source.Layout.Rooms.ToDictionary(room => room.Id, room => room.Cells.ToList());
        var moved = ProjectEdits.Apply(source, new() { Kind = "move-interface", NodeId = "SharedDoor", Position = new(14, 13) });
        var port = moved.Layout.Interfaces.Single();
        Assert.That(port.Cells, Is.EqualTo(new[] { new Cell(14, 13), new Cell(14, 14) }));
        Assert.That(port.FromApproach, Is.EqualTo(new Cell(13, 13)));
        Assert.That(port.ToApproach, Is.EqualTo(new Cell(15, 13)));
        Assert.That(port.Edited, Is.True);
        var structural = moved.Layout.Walls.Concat(moved.Layout.Hull).Concat(moved.Layout.Windows).ToHashSet();
        Assert.That(structural, Does.Contain(new Cell(14, 11))); Assert.That(structural, Does.Contain(new Cell(14, 12)));
        Assert.That(port.Cells.Any(structural.Contains), Is.False);
        var resized = ProjectEdits.Apply(moved, new() { Kind = "set-interface", NodeId = port.Id, TextValue = "airlock", Layer = "walking", Value = 3 });
        Assert.That(resized.Layout.Interfaces.Single().Cells, Is.EqualTo(new[] { new Cell(14, 13), new Cell(14, 14), new Cell(14, 15) }));
        Assert.That(resized.Layout.Interfaces.Single().Width, Is.EqualTo(3));
        Assert.That(resized.Layout.Rooms.All(room => room.Cells.SequenceEqual(floors[room.Id])), Is.True);
        Assert.That(resized.StageStates.Single(state => state.Stage == "circulation").Status, Is.EqualTo("dirty"));
        var rebuilt = DesignRouting.Run(resized);
        Assert.That(rebuilt.Layout.Interfaces.Single(port => port.Id == "SharedDoor").Cells, Is.EqualTo(resized.Layout.Interfaces.Single().Cells));
        Assert.That(source.Layout.Interfaces.Single().Cells, Is.EqualTo(new[] { new Cell(14, 11), new Cell(14, 12) }));
    }

    [Test]
    public void CounterChangesStayEditableButDoNotSilentlySatisfyWalkingRequirements()
    {
        var source = SharedDoor();
        var changed = ProjectEdits.Apply(source, new() { Kind = "set-interface", NodeId = "SharedDoor", TextValue = "counter", Layer = "walking", Value = 2 });
        Assert.That(changed.Layout.Interfaces.Single().Type, Is.EqualTo("counter"));
        Assert.That(DesignGeometryValidator.Validate(changed).Diagnostics.Any(diagnostic => diagnostic.Code == "nonwalking-interface"), Is.True);
        var removed = ProjectEdits.Apply(changed, new() { Kind = "remove-interface", NodeId = "SharedDoor" });
        Assert.That(removed.Layout.Interfaces, Is.Empty);
        Assert.That(removed.Layout.Walls.Concat(removed.Layout.Hull).Concat(removed.Layout.Windows), Does.Contain(new Cell(14, 11)));
        Assert.That(removed.Layout.Rooms.Sum(room => room.Cells.Count), Is.EqualTo(source.Layout.Rooms.Sum(room => room.Cells.Count)));
    }

    [Test]
    public void PinningAndMalformedInterfaceRequestsAreExplicitAndDoNotPartiallyMutateTheSource()
    {
        var source = SharedDoor();
        var pinned = ProjectEdits.Apply(source, new() { Kind = "pin-interface", NodeId = "SharedDoor", Enabled = true });
        Assert.That(pinned.Layout.Interfaces.Single().Edited, Is.True);
        var released = ProjectEdits.Apply(pinned, new() { Kind = "pin-interface", NodeId = "SharedDoor", Enabled = false });
        Assert.That(released.Layout.Interfaces.Single().Edited, Is.False);
        var before = JsonSerializer.Serialize(source, ProgramJson.Options);
        Assert.Throws<ArgumentException>(() => ProjectEdits.Apply(source, new() { Kind = "set-interface", NodeId = "SharedDoor", TextValue = "window", Layer = "interaction", Value = 0 }));
        Assert.Throws<ArgumentException>(() => ProjectEdits.Apply(source, new() { Kind = "set-interface", NodeId = "SharedDoor", TextValue = "teleporter", Value = 2 }));
        Assert.That(JsonSerializer.Serialize(source, ProgramJson.Options), Is.EqualTo(before));
    }

    [Test]
    public void EquipmentMovementCarriesItsWorkingClearanceWithThePhysicalFootprint()
    {
        var source = SharedDoor();
        source.Layout.Equipment = [new() { Id = "Machine", RoomId = "Alpha/Work#1", Position = new(10, 10), Footprint = [new(10, 10)], WorkArea = [new(10, 10), new(10, 11)] }];
        var moved = ProjectEdits.Apply(source, new() { Kind = "move-equipment", NodeId = "Machine", Position = new(11, 12) });
        var entity = moved.Layout.Equipment.Single();
        Assert.That(entity.Footprint, Is.EqualTo(new[] { new Cell(11, 12) }));
        Assert.That(entity.WorkArea, Is.EqualTo(new[] { new Cell(11, 12), new Cell(11, 13) }));
        Assert.That(source.Layout.Equipment.Single().WorkArea, Is.EqualTo(new[] { new Cell(10, 10), new Cell(10, 11) }));
    }
}
