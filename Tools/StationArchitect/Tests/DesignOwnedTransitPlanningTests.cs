using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
public sealed class DesignOwnedTransitPlanningTests
{
    private static List<Cell> Floor(int x, int y, int width, int height) => new GridBounds(x, y, width, height).Cells().ToList();
    private static StationDesignProject Fixture()
    {
        var definition = ProgramTestData.Minimal();
        definition.Departments[0].Rooms = new[] { "West", "East" }.Select(id =>
        {
            var room = ProgramTestData.Room(id); room.Role = "filler"; room.AllowThroughTraffic = true;
            room.Access.AllowPublicEntry = true; room.Access.AllowMaintenanceEntry = true; return room;
        }).ToList();
        return new()
        {
            Definition = definition,
            Layout = new()
            {
                Rooms = [new() { Id = "Alpha/West#1", OwnerId = "Alpha", DefinitionPath = "Alpha/West", GrowthReservation = Floor(5, 5, 4, 4) },
                    new() { Id = "Alpha/East#1", OwnerId = "Alpha", DefinitionPath = "Alpha/East", GrowthReservation = Floor(10, 5, 4, 4) }],
                CirculationReservations = [new() { Id = "west-maint", RoomId = "Alpha/West#1", Network = "maintenance", Width = 2, Outward = new(-1, 0), BoundaryCells = Floor(4, 6, 1, 2), ApproachCells = Floor(2, 6, 2, 2) },
                    new() { Id = "east-public", RoomId = "Alpha/East#1", Network = "publicHall", Width = 3, Outward = new(1, 0), BoundaryCells = Floor(14, 5, 1, 3), ApproachCells = Floor(15, 5, 3, 3) }]
            }
        };
    }
    private static int Count(StationDesignProject project) => DesignOwnedTransitPlanning.CountOwnedTransitions(project,
        project.Layout.CirculationReservations.Where(guide => guide.Network == "maintenance"), project.Layout.CirculationReservations.Where(guide => guide.Network == "publicHall"));

    [Test]
    public void SeparateOwnedTransitRoomsCanProveARealPotentialWalkingConnection()
    {
        var project = Fixture(); Assert.That(Count(project), Is.EqualTo(1));
        Assert.That(project.Layout.Interfaces, Is.Empty, "Planning proof must not invent a built door.");
        Assert.That(project.Layout.Maintenance, Is.Empty); Assert.That(project.Layout.PublicCorridors, Is.Empty);
    }

    [Test]
    public void OwnerIdentityDoesNotBridgeAnUnallocatedGap()
    {
        var project = Fixture(); project.Layout.Rooms[0].GrowthReservation = Floor(4, 5, 4, 4);
        var guide = project.Layout.CirculationReservations[0]; guide.BoundaryCells = Floor(3, 6, 1, 2); guide.ApproachCells = Floor(1, 6, 2, 2);
        Assert.That(Count(project), Is.Zero);
    }

    [Test]
    public void RestrictedTrafficOrAccessCannotBeUsedAsAPublicServiceBridge()
    {
        var project = Fixture(); project.Definition.Departments[0].Rooms[0].AllowThroughTraffic = false;
        Assert.That(Count(project), Is.Zero);
        project = Fixture(); project.Definition.Departments[0].Rooms[1].Access.EntryOwners = ["Beta"];
        Assert.That(Count(project), Is.Zero);
    }

    [Test]
    public void SameRoomLabelDoesNotJoinDisconnectedExclusiveFloorComponents()
    {
        var project = Fixture();
        var room = project.Layout.Rooms[0]; room.GrowthReservation.AddRange(Floor(10, 5, 4, 4));
        project.Layout.Rooms.RemoveAt(1); project.Layout.CirculationReservations[1].RoomId = room.Id;
        Assert.That(Count(project), Is.Zero);
    }

    [Test]
    public void AnInternalOnlyNestedRoomCannotReceiveAnExternalGuide()
    {
        var project = Fixture(); var parent = project.Layout.Rooms[0]; var child = project.Layout.Rooms[1];
        var childDefinition = project.Definition.Departments[0].Rooms[1]; project.Definition.Departments[0].Rooms.RemoveAt(1);
        project.Definition.Departments[0].Rooms[0].Rooms = [childDefinition]; child.DefinitionPath = "Alpha/West/East"; child.ParentInstanceId = parent.Id;
        Assert.That(Count(project), Is.Zero);
    }
}
