using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
public sealed class EquipmentOccupancyTests
{
    private static StationDesignProject Pair()
    {
        var definition = ProgramTestData.Minimal();
        definition.Departments[0].Rooms.Add(ProgramTestData.Room("Other"));
        definition.Departments[0].Rooms[0].Contents.Add(new() { Id = "Device", Prototypes = ["Sensor"] });
        definition.Systems.Profiles.Add(new() { Id = "Sensor", Prototype = "Sensor", OccupancyLayer = "sensor", BlocksCrew = false });
        return new()
        {
            Definition = definition, Stage = "circulation", Settings = new() { Width = 32, Height = 24 },
            Layout = new()
            {
                Envelope = new GridBounds(0, 0, 32, 24).Cells().ToList(),
                Rooms = [new() { Id = "Alpha/Work#1", DefinitionPath = "Alpha/Work", OwnerId = "Alpha", Cells = new GridBounds(5, 5, 4, 4).Cells().ToList() },
                    new() { Id = "Alpha/Other#1", DefinitionPath = "Alpha/Other", OwnerId = "Alpha", Cells = new GridBounds(10, 5, 4, 4).Cells().ToList() }],
                Interfaces = [new() { Id = "door", From = "Alpha/Work#1", To = "Alpha/Other#1", Cells = [new(9, 6)], FromApproach = new(8, 6), ToApproach = new(10, 6) }],
                Equipment = [new() { Id = "sensor", RequirementId = "Device", RoomId = "Alpha/Work#1", Prototype = "Sensor", Position = new(8, 6), Footprint = [new(8, 6)] }]
            }
        };
    }

    [Test]
    public void ActualNonblockingProfileDoesNotObstructAnExistingWalkingRoute()
    {
        var project = Pair();
        var route = new DesignRoute { From = "Alpha/Work#1", To = "Alpha/Other#1", Mode = "department", Cells = [new(8, 6), new(9, 6), new(10, 6)] };
        Assert.That(DesignMeasurements.RouteValid(project, route), Is.True);
        project.Definition.Systems.Profiles[0].BlocksCrew = true;
        Assert.That(DesignMeasurements.RouteValid(project, route), Is.False);
    }

    [Test]
    public void DifferentOccupancyLayersMayShareBodiesButExplicitWorkFloorStillReservesSpace()
    {
        var project = Pair();
        project.Definition.Departments[0].Rooms[0].Contents.Add(new() { Id = "Machine", Prototypes = ["Machine"] });
        project.Layout.Equipment.Add(new() { Id = "machine", RequirementId = "Machine", RoomId = "Alpha/Work#1", Prototype = "Machine", Position = new(8, 6), Footprint = [new(8, 6)], WorkArea = [new(8, 6)] });
        var report = DesignGeometryValidator.Validate(project);
        Assert.That(report.Diagnostics.Where(issue => issue.Code is "equipment-overlap" or "equipment-working-area" or "equipment-working-obstruction"), Is.Empty);
        project.Definition.Systems.Profiles[0].OccupancyLayer = "floor";
        Assert.That(DesignGeometryValidator.Validate(project).Diagnostics.Any(issue => issue.Code == "equipment-overlap"), Is.True);
        project.Definition.Systems.Profiles[0].OccupancyLayer = "sensor";
        project.Definition.Departments[0].Rooms[0].Contents[0].MinArea = 1;
        Assert.That(DesignGeometryValidator.Validate(project).Diagnostics.Any(issue => issue.Path == "sensor" && issue.Code == "equipment-working-area"), Is.True);
    }
}
