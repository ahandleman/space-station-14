using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class OwnerShapePackingTests
{
    [TestCase("hub", "Service")]
    [TestCase("spine", "Commons")]
    public void TopologyRolesRespectDistributedOwnerShapeWithoutChangingAuthoredLimits(string topology, string ownerId)
    {
        var source = new StationDesignProject { Definition = StationProgramDefaults.Create(), Settings = new() { Topology = topology, Seed = 14014 } };
        var selected = InstanceSelection.Resolve(source);
        var placed = DesignPlacement.Run(selected);
        Assert.That(placed.Report!.Diagnostics.Any(issue => issue.Code == "owner-shape-packing"), Is.False, ProgramTestData.Errors(placed.Report.Diagnostics));
        var grown = RoomGrowth.Run(placed);
        var owner = grown.Definition.Departments.Single(department => department.Id == ownerId);
        var floor = grown.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && room.OwnerId == ownerId).SelectMany(room => room.Cells).ToHashSet();
        Assert.That(LayoutGeometry.ShapeFits(floor, owner.Shape), Is.True, $"{ownerId} actual bounds {LayoutGeometry.Bounds(floor)} violate its declared shape.");
        Assert.That(owner.Shape.MaxAspectRatio, Is.EqualTo(8));
        Assert.That(source.Definition.Departments.Single(department => department.Id == ownerId).Shape.MaxAspectRatio, Is.EqualTo(8));
        Assert.That(grown.Settings.Width, Is.LessThanOrEqualTo(512)); Assert.That(grown.Settings.Height, Is.LessThanOrEqualTo(512));
        var replay = DesignPlacement.Run(selected);
        Assert.That(replay.Layout.Rooms.Select(room => room.Seed), Is.EqualTo(placed.Layout.Rooms.Select(room => room.Seed)));
        Assert.That(replay.Layout.PublicWaypoints, Is.EqualTo(placed.Layout.PublicWaypoints));
    }
}
