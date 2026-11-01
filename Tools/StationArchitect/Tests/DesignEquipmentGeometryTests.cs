using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class DesignEquipmentGeometryTests
{
    [Test]
    public void CenteredAndOffsetProfilesRotateAroundTheEntityOrigin()
    {
        var settings = new DesignSettings();
        settings.PrototypeFootprints["Centered"] = new() { Width = 3, Height = 3, OffsetX = -1, OffsetY = -1 };
        foreach (var rotation in new[] { 0, 90, 180, 270 })
            Assert.That(DesignEquipmentGeometry.ExpectedFootprint(settings, "Centered", new(10, 10), rotation), Is.EquivalentTo(new GridBounds(9, 9, 3, 3).Cells()));
        settings.PrototypeFootprints["Offset"] = new() { Width = 2, Height = 1, OffsetX = -1 };
        Assert.That(DesignEquipmentGeometry.ExpectedFootprint(settings, "Offset", new(10, 10), 0), Is.EquivalentTo(new[] { new Cell(9, 10), new Cell(10, 10) }));
        Assert.That(DesignEquipmentGeometry.ExpectedFootprint(settings, "Offset", new(10, 10), 90), Is.EquivalentTo(new[] { new Cell(10, 9), new Cell(10, 10) }));
        Assert.That(DesignEquipmentGeometry.TryExpectedFootprint(settings, "Offset", new(10, 10), 45, out _), Is.False);
        settings.PrototypeFootprints["Offset"].OffsetX = 17;
        Assert.That(DesignEquipmentGeometry.TryExpectedFootprint(settings, "Offset", new(10, 10), 0, out _), Is.False);
    }

    [Test]
    public void ContentsAndIndependentMeasurementsUseTheSameCurrentFootprintProfile()
    {
        var project = InstanceSelection.Resolve(new() { Definition = ProgramTestData.Minimal() });
        var room = project.Layout.Rooms.Single(); room.Cells = new GridBounds(10, 10, 8, 5).Cells().ToList();
        project.Layout.Envelope = new GridBounds(9, 9, 10, 7).Cells().ToList();
        project.Layout.Walls = project.Layout.Envelope.Except(room.Cells).ToList();
        project.Definition.Departments[0].Rooms[0].Contents = [new() { Id = "Fabricator", Prototypes = ["Autolathe"], MinArea = 4 }];
        project.Settings.PrototypeFootprints["Autolathe"] = new() { Width = 3, Height = 3, OffsetX = -1, OffsetY = -1 };
        var built = DesignContents.Run(project); var entity = built.Layout.Equipment.Single();
        Assert.That(entity.Footprint.Count, Is.EqualTo(9)); Assert.That(entity.WorkArea.Count, Is.GreaterThanOrEqualTo(9));
        Assert.That(entity.Footprint, Is.EquivalentTo(DesignEquipmentGeometry.ExpectedFootprint(built.Settings, entity.Prototype, entity.Position, entity.Rotation)));
        Assert.That(DesignMeasurements.EntityPlacementValid(built, built.Layout.Rooms.Single(), entity), Is.True);
        built.Settings.PrototypeFootprints["Autolathe"].Width = 2;
        Assert.That(DesignMeasurements.EntityPlacementValid(built, built.Layout.Rooms.Single(), entity), Is.False, "A changed profile invalidates a saved old footprint instead of trusting old acceptance.");
    }

    [Test]
    public void BoundaryWorkingFloorIsReservedInsideTheRoomInsteadOfCountingWallTiles()
    {
        var project = InstanceSelection.Resolve(new() { Definition = ProgramTestData.Minimal() });
        var room = project.Layout.Rooms.Single(); room.Cells = new GridBounds(10, 10, 8, 5).Cells().ToList();
        project.Layout.Envelope = new GridBounds(9, 9, 10, 7).Cells().ToList();
        project.Layout.Walls = project.Layout.Envelope.Except(room.Cells).ToList();
        project.Definition.Departments[0].Rooms[0].Contents = [new() { Id = "Airlock", Prototypes = ["AirlockExternal"], Placement = "spaceBoundary", InterfaceType = "airlock", MinArea = 3 }];
        var built = DesignContents.Run(project); var entity = built.Layout.Equipment.Single();
        Assert.That(entity.WorkArea.Count, Is.EqualTo(3));
        Assert.That(entity.WorkArea.All(room.Cells.Contains), Is.True);
        Assert.That(entity.WorkArea.Intersect(entity.Footprint), Is.Empty);
    }
}
