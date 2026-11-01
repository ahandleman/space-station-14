using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
public sealed class DepartmentGrowthShapeTests
{
    private static StationDesignProject Fixture()
    {
        var definition = ProgramTestData.Minimal();
        var owner = definition.Departments[0]; owner.Area = ProgramTestData.Area(30, 30, 100);
        owner.Shape = new() { MaxAspectRatio = 2, MinFillRatio = .1 };
        var room = ProgramTestData.Room("Work", 30, 30, 72); room.Shape = new() { MaxAspectRatio = 10, MinFillRatio = .1 };
        room.Access.AllowPublicEntry = false; room.Access.AllowMaintenanceEntry = false; owner.Rooms = [room];
        var allocation = new GridBounds(10, 10, 6, 12);
        return new()
        {
            Definition = definition, Settings = new() { OptimizationIterations = 0 },
            Layout = new()
            {
                Envelope = allocation.Expand(4).Cells().ToList(),
                Rooms = [new() { Id = "Alpha/Work#1", DefinitionPath = "Alpha/Work", OwnerId = "Alpha", HasSeed = true,
                    Seed = new(12, 16), Parcel = allocation, GrowthReservation = allocation.Cells().ToList(), GrowthSide = "right" }]
            }
        };
    }

    [Test]
    public void InitialRoomFillPreservesItsDepartmentsHardAspectWithoutIncreasingItsArea()
    {
        var source = Fixture(); var project = RoomGrowth.Run(source); var room = project.Layout.Rooms.Single();
        Assert.That(project.Report!.Success, Is.True, ProgramTestData.Errors(project.Report.Diagnostics));
        Assert.That(room.Cells.Count, Is.EqualTo(30));
        Assert.That(LayoutGeometry.ShapeFits(room.Cells, source.Definition.Departments[0].Shape), Is.True);
        Assert.That(source.Definition.Departments[0].Rooms[0].Area.Target, Is.EqualTo(30));
        Assert.That(RoomGrowth.Run(source).Layout.Rooms.Single().Cells, Is.EqualTo(room.Cells));
    }

    [Test]
    public void InvalidProtectedDepartmentShapeIsReportedWithoutChangingUserFloor()
    {
        var source = Fixture(); var room = source.Layout.Rooms.Single(); room.FloorLocked = true;
        room.Cells = new GridBounds(10, 10, 3, 12).Cells().ToList(); var original = room.Cells.ToList();
        var project = RoomGrowth.Run(source);
        Assert.That(project.Layout.Rooms.Single().Cells, Is.EqualTo(original));
        Assert.That(project.Report!.Diagnostics.Any(issue => issue.Path == "Alpha" && issue.Code == "department-shape"), Is.True);
    }

    [Test]
    public void AspectRepairDoesNotResetTheConfiguredFrontierGrowthBudget()
    {
        var source = Fixture(); source.Settings.GrowthIterations = 20;
        var project = RoomGrowth.Run(source);
        Assert.That(project.Layout.Rooms.Single().Cells.Count, Is.LessThanOrEqualTo(21));
        Assert.That(project.Report!.Diagnostics.Any(issue => issue.Code == "growth-budget"), Is.True);
    }
}
