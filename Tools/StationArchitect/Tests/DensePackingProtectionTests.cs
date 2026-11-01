using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class DensePackingProtectionTests
{
    private static StationDesignProject BlockedPreferredDock()
    {
        var definition = ProgramTestData.Minimal(); var room = definition.Departments[0].Rooms[0];
        room.Contents = [new() { Id = "DockAirlock", Prototypes = ["AirlockShuttle"], Placement = "spaceBoundary", InterfaceType = "dockingPort", Count = new() { Min = 0, Target = 0, Max = 1 } }];
        room.Exterior.Docks = [new() { Id = "Berth", ContentId = "DockAirlock", BerthWidth = 5, BerthDepth = 4, Clearance = 1 }];
        definition.Departments.Add(new() { Id = "Beta", Area = ProgramTestData.Area(100, 320, 600), Rooms = [ProgramTestData.Room("Blocker", 100, 320, 600)] });
        var source = new StationDesignProject
        {
            Definition = definition,
            Settings = new() { Topology = "spine", Width = 160, Height = 100, Packing = new() { InternalVariation = 0, SilhouetteVariation = 0, Interlock = 0, Compactness = 1 } }
        };
        var project = DesignPlacement.Run(InstanceSelection.Resolve(source));
        var own = project.Layout.Rooms.Single(room => room.OwnerId == "Alpha"); var other = project.Layout.Rooms.Single(room => room.OwnerId == "Beta");
        // Keep a fixed, broad obstacle immediately beyond the only permitted dock face.
        // Topology role assignment can otherwise put both departments beside one another,
        // making the preferred berth fit without exercising the relocation path.
        var ownBounds = LayoutGeometry.Bounds(own.GrowthReservation);
        var otherBounds = LayoutGeometry.Bounds(other.GrowthReservation);
        var shift = new Cell(ownBounds.Center.X - otherBounds.Center.X, ownBounds.Bottom + 8 - otherBounds.Y);
        project = ProjectEdits.Apply(project, new() { Kind = "move-room", NodeId = other.Id, Position = other.Seed + shift });
        project.Definition.Departments[0].Rooms[0].Exterior.Docks.Single().Directions = ["north"];
        project.Definition.Departments[0].Rooms[0].Contents.Single().Count.Target = 1;
        return project;
    }

    [Test]
    public void APlainProtectedDoorPreventsAutomaticDockRelocationWithoutPinningItsRoom()
    {
        var source = BlockedPreferredDock(); var own = source.Layout.Rooms.Single(room => room.OwnerId == "Alpha");
        var before = own.Seed; var bounds = LayoutGeometry.Bounds(own.GrowthReservation); var wall = new Cell(bounds.X - 1, bounds.Center.Y);
        var unprotected = DesignPlacement.Run(source);
        Assert.That(unprotected.Layout.Docks.Count, Is.EqualTo(1), "The fixture must have a valid berth after automatic relocation.");
        Assert.That(unprotected.Layout.Rooms.Single(room => room.Id == own.Id).Seed, Is.Not.EqualTo(before));
        source.Layout.Interfaces.Add(new() { Id = "manual-plain-door", From = own.Id, To = "$public", Cells = [wall], FromApproach = wall + new Cell(1, 0), ToApproach = wall + new Cell(-1, 0), Edited = true });
        var protectedProject = DesignPlacement.Run(source);
        Assert.That(protectedProject.Layout.Rooms.Single(room => room.Id == own.Id).Seed, Is.EqualTo(before));
        Assert.That(protectedProject.Layout.Rooms.Single(room => room.Id == own.Id).Pinned, Is.False, "Protection must not rewrite explicit user pin settings.");
        Assert.That(protectedProject.Layout.Interfaces.Single(port => port.Id == "manual-plain-door").Cells, Is.EqualTo(new[] { wall }));
    }

    [Test]
    public void ALockedRouteProtectsItsEndpointClusterDuringDockPlanning()
    {
        var source = BlockedPreferredDock(); var own = source.Layout.Rooms.Single(room => room.OwnerId == "Alpha"); var other = source.Layout.Rooms.Single(room => room.OwnerId == "Beta");
        source.Layout.Routes.Add(new() { Id = "locked-route", From = own.Id, To = other.Id, Kind = "walking", Mode = "publicHall", Cells = [own.Seed, other.Seed], Locked = true });
        var result = DesignPlacement.Run(source);
        Assert.That(result.Layout.Rooms.Single(room => room.Id == own.Id).Seed, Is.EqualTo(own.Seed));
        Assert.That(result.Layout.Routes.Single().Locked, Is.True); Assert.That(result.Layout.Routes.Single().Cells, Is.EqualTo(source.Layout.Routes.Single().Cells));
    }

    [Test]
    public void AutomaticDockRelocationCannotOccupyPaintedCorridorFloor()
    {
        var source = BlockedPreferredDock(); var own = source.Layout.Rooms.Single(room => room.OwnerId == "Alpha");
        var relocated = DesignPlacement.Run(source).Layout.Rooms.Single(room => room.Id == own.Id);
        var painted = relocated.GrowthReservation.Except(own.GrowthReservation).First();
        source.Layout.PublicCorridors = [painted]; source.Layout.PublicEdited = true;
        var result = DesignPlacement.Run(source);
        Assert.That(result.Layout.PublicCorridors, Is.EqualTo(new[] { painted }));
        Assert.That(result.Layout.Rooms.SelectMany(room => room.GrowthReservation).Contains(painted), Is.False, "Painted floor is a fixed obstacle before dock search and normalization, not just during final interlocking.");
    }

    [TestCase("interface")]
    [TestCase("route")]
    public void ChangedLayoutSettingsPreserveGeometryAnchoredByEditedInterfacesOrLockedRoutes(string kind)
    {
        var source = BlockedPreferredDock(); var initial = source.Layout.Rooms.Single(room => room.OwnerId == "Alpha");
        source = ProjectEdits.Apply(source, new() { Kind = "move-room", NodeId = initial.Id, Position = initial.Seed + new Cell(7, -5) });
        source = ProjectEdits.Apply(source, new() { Kind = "pin-room", NodeId = initial.Id, Enabled = false });
        var own = source.Layout.Rooms.Single(room => room.OwnerId == "Alpha");
        var other = source.Layout.Rooms.Single(room => room.OwnerId == "Beta");
        var before = own.GrowthReservation.ToList(); var seed = own.Seed; var bounds = LayoutGeometry.Bounds(before);
        source.Settings.Seed += 107;
        source.Settings.Packing.InternalVariation = source.Settings.Packing.SilhouetteVariation = 1;
        var unprotected = DesignPlacement.Run(source).Layout.Rooms.Single(room => room.Id == own.Id);
        Assert.That(unprotected.Seed != seed || !unprotected.GrowthReservation.SequenceEqual(before), Is.True,
            "A displaced unprotected room must be replanned, so preservation cannot pass because the layout is symmetric.");
        if (kind == "interface")
        {
            var wall = new Cell(bounds.X - 1, bounds.Center.Y);
            source.Layout.Interfaces.Add(new() { Id = "fixed-door", From = own.Id, To = "$public", Cells = [wall], FromApproach = wall + new Cell(1, 0), ToApproach = wall - new Cell(1, 0), Edited = true });
        }
        else source.Layout.Routes.Add(new() { Id = "fixed-route", From = own.Id, To = other.Id, Cells = [seed, other.Seed], Mode = "publicHall", Kind = "walking", Locked = true });
        var result = DesignPlacement.Run(source); var retained = result.Layout.Rooms.Single(room => room.Id == own.Id);
        Assert.That(retained.Seed, Is.EqualTo(seed));
        Assert.That(retained.GrowthReservation, Is.EqualTo(before), "The initial parcel/rotation phase must honor fixed dependencies before dense packing sees them.");
        Assert.That(retained.Pinned, Is.False);
    }
}
