using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
public sealed class DirectedCrewAccessTests
{
    private static StationDesignProject DeepPair()
    {
        var definition = ProgramTestData.Minimal(); var beta = ProgramJson.Clone(definition.Departments[0]); beta.Id = "Beta"; definition.Departments.Add(beta);
        definition.Departments[0].Rooms[0].Access.RequireNetworkApproach = ["maintenance"];
        return new() { Definition = definition, Stage = "circulation", Layout = new()
        {
            Rooms = [new() { Id = "Alpha/Work#1", DefinitionPath = "Alpha/Work", OwnerId = "Alpha", Cells = new GridBounds(5, 5, 4, 4).Cells().ToList() },
                new() { Id = "Beta/Work#1", DefinitionPath = "Beta/Work", OwnerId = "Beta", Cells = new GridBounds(15, 5, 4, 4).Cells().ToList() }]
        } };
    }

    [Test]
    public void DirectedNativeDeliveryIntoDeepFloorIsABreachButAnExitCannotBeReversed()
    {
        var project = DeepPair(); var room = project.Layout.Rooms[0];
        var incoming = new Dictionary<Cell, List<Cell>> { [new(15, 6)] = [new(6, 6)] };
        var outgoing = new Dictionary<Cell, List<Cell>> { [new(6, 6)] = [new(15, 6)] };
        Assert.That(DesignMeasurements.VerifiedApproachNetworks(project, room, ["maintenance"], incoming), Is.Empty);
        Assert.That(DesignMeasurements.VerifiedApproachNetworks(project, room, ["maintenance"], outgoing), Does.Contain("maintenance"), "A directed outlet cannot be used as a fictional reverse disposal inlet. Missing walking connectivity is checked separately.");
    }

    [Test]
    public void NativeDeliveryIntoAChildMustStillTraverseTheParent()
    {
        var definition = ProgramTestData.Minimal(); var parent = ProgramTestData.Room("Suite"); parent.Rooms = [ProgramTestData.Room("Study", 1, 4, 16)]; parent.Rooms[0].Nesting = new() { Internal = true, RequireParentConnection = true, AllowExternalEntry = false }; definition.Departments[0].Rooms = [parent];
        var beta = ProgramJson.Clone(definition.Departments[0]); beta.Id = "Beta"; beta.Rooms = [ProgramTestData.Room("Work")]; definition.Departments.Add(beta);
        var childFloor = new GridBounds(8, 8, 2, 2).Cells().ToHashSet(); var childShell = DesignNetworkGeometry.Halo(childFloor);
        var complex = new GridBounds(5, 5, 9, 9).Cells().ToList();
        var project = new StationDesignProject { Definition = definition, Stage = "circulation" };
        project.Layout.Rooms = [new() { Id = "Alpha/Suite#1", DefinitionPath = "Alpha/Suite", OwnerId = "Alpha", Cells = complex.Except(childShell).ToList(), ComplexReservation = complex },
            new() { Id = "Alpha/Suite#1/Study#1", DefinitionPath = "Alpha/Suite/Study", ParentInstanceId = "Alpha/Suite#1", OwnerId = "Alpha", Cells = childFloor.ToList() },
            new() { Id = "Beta/Work#1", DefinitionPath = "Beta/Work", OwnerId = "Beta", Cells = new GridBounds(18, 5, 4, 4).Cells().ToList() }];
        var allFloor = project.Layout.Rooms.SelectMany(room => room.Cells).ToHashSet();
        project.Layout.Walls = allFloor.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Except(allFloor).Distinct().ToList();
        project.Layout.Interfaces.Add(new() { Id = "study-door", From = "Alpha/Suite#1", To = "Alpha/Suite#1/Study#1", Cells = [new(7, 8)], FromApproach = new(6, 8), ToApproach = new(8, 8) }); project.Layout.Walls.Remove(new(7, 8));
        var definitions = RequirementCompiler.Rooms(definition).ToDictionary(item => item.Path, item => item.Room);
        var incoming = new Dictionary<Cell, List<Cell>> { [new(19, 6)] = [new(8, 8)] };
        var outgoing = new Dictionary<Cell, List<Cell>> { [new(8, 8)] = [new(19, 6)] };
        Assert.That(DesignNestedGeometry.Validate(project, true, true, definitions, crewJumps: incoming).Any(issue => issue.Code == "nested-approach-bypass"), Is.True);
        Assert.That(DesignNestedGeometry.Validate(project, true, true, definitions, crewJumps: outgoing).Any(issue => issue.Code == "nested-approach-bypass"), Is.False);
    }
}
