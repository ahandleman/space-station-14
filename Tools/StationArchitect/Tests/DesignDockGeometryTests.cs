using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class DesignDockGeometryTests
{
    private static StationDesignProject Fixture(bool hardware = true)
    {
        var definition = ProgramTestData.Minimal(); var room = definition.Departments[0].Rooms[0];
        definition.Departments[0].Area.Max = room.Area.Max = 300;
        definition.Departments[0].Shape.MaxAspectRatio = room.Shape.MaxAspectRatio = 8;
        room.Contents = [new() { Id = "DockAirlock", Prototypes = ["AirlockShuttle"], Placement = "spaceBoundary", InterfaceType = "dockingPort", Count = new() { Min = 1, Target = 1, Max = 2 } }];
        room.Exterior.Docks = [new() { Id = "Shuttle", ContentId = "DockAirlock", BerthWidth = 5, BerthDepth = 4, ApproachLength = 8, Clearance = 1 }];
        var project = InstanceSelection.Resolve(new() { Definition = definition, Settings = new() { Width = 80, Height = 60 } });
        var actual = project.Layout.Rooms.Single(); actual.Cells = new GridBounds(20, 20, 6, 5).Cells().ToList(); actual.Seed = new(22, 22); actual.HasSeed = true;
        project.Layout.Envelope = new GridBounds(18, 18, 12, 10).Cells().ToList();
        project.Layout.Walls = actual.Cells.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Except(actual.Cells).Distinct().ToList();
        var dock = DesignDockGeometry.Create(actual.Id, room.Exterior.Docks.Single(), 1, new(26, 22), new(1, 0), new(0, 0, 80, 60));
        project.Layout.Docks.Add(dock);
        if (hardware) AddHardware(project, dock);
        return project;
    }

    private static void AddHardware(StationDesignProject project, DesignDock dock)
    {
        var anchor = dock.BoundaryCells.Single();
        project.Layout.Walls.RemoveAll(dock.BoundaryCells.Contains); project.Layout.Hull.RemoveAll(dock.BoundaryCells.Contains); project.Layout.Windows.RemoveAll(dock.BoundaryCells.Contains);
        project.Layout.Equipment.Add(new() { Id = dock.EntityId, RoomId = dock.RoomId, RequirementId = dock.ContentId, Prototype = "AirlockShuttle", Position = anchor, Rotation = DesignDockGeometry.RotationForOutward(dock.Outward), Placement = "spaceBoundary", Footprint = [anchor], InterfaceId = dock.InterfaceId });
        project.Layout.Interfaces.Add(new() { Id = dock.InterfaceId, From = dock.RoomId, To = "$space", Kind = "walking", Type = "dockingPort", Cells = [anchor], Width = 1, FromApproach = anchor - dock.Outward, ToApproach = anchor + dock.Outward });
    }

    [TestCase("north")]
    [TestCase("east")]
    [TestCase("south")]
    [TestCase("west")]
    public void MasksUseTheCardinalFrameOffsetAndOnlyExteriorClearance(string direction)
    {
        var rule = new DockRequirement { BerthWidth = 6, BerthDepth = 3, BerthOffset = 1, Clearance = 1, ApproachLength = 8 };
        var anchor = new Cell(20, 15); var normal = DesignDockGeometry.Normal(direction); var tangent = DesignDockGeometry.Tangent(normal); var canvas = new GridBounds(0, 0, 40, 30);
        var dock = DesignDockGeometry.Create("Alpha/Work#1", rule, 1, anchor, normal, canvas);
        int Dot(Cell cell, Cell axis) { var delta = cell - anchor; return delta.X * axis.X + delta.Y * axis.Y; }
        Assert.That(dock.BerthCells.Count, Is.EqualTo(32));
        Assert.That(dock.BerthCells.Select(cell => Dot(cell, normal)), Is.All.InRange(1, 4));
        Assert.That(dock.BerthCells.Min(cell => Dot(cell, tangent)), Is.EqualTo(-3)); Assert.That(dock.BerthCells.Max(cell => Dot(cell, tangent)), Is.EqualTo(4));
        Assert.That(dock.ApproachCells.Any(cell => !canvas.Contains(cell)), Is.True, "The full straight lane must reach beyond the station's allowed canvas.");
        Assert.That(DesignDockGeometry.ReservedCells(dock).Any(cell => Dot(cell, normal) <= 0), Is.False, "Lateral clearance must not consume legitimate facade tiles behind the dock plane.");
        Assert.That(DesignDockGeometry.OutwardForRotation(DesignDockGeometry.RotationForOutward(normal)), Is.EqualTo(normal));
    }

    [Test]
    public void VerifiedDockRequiresItsOwnAlignedHardwareAndActuallyEmptyLane()
    {
        var project = Fixture(); var dock = project.Layout.Docks.Single();
        Assert.That(DesignDockGeometry.ValidateDock(project, dock, true), Is.Empty);
        Assert.That(DesignDockGeometry.Measure(project, project.Layout.Rooms.Single()).Single().Count, Is.EqualTo(1));
        project.Layout.Equipment.Add(new() { Id = "SolarObstruction", RoomId = "Other", Prototype = "SolarPanel", Position = new(29, 22), Footprint = [new(29, 22)] });
        Assert.That(DesignDockGeometry.ValidateDock(project, dock, true).Any(issue => issue.Code == "dock-obstructed"), Is.True);
        Assert.That(DesignDockGeometry.Measure(project, project.Layout.Rooms.Single()).Single().Count, Is.Zero);
    }

    [Test]
    public void NonblockingInteriorEquipmentPreservesCrewApproachButNeverOccupiesVacuumClearance()
    {
        var project = Fixture(); var dock = project.Layout.Docks.Single();
        project.Definition.Systems.Profiles.Add(new() { Id = "Sensor", Prototype = "AirSensor", OccupancyLayer = "sensor", BlocksCrew = false });
        var inside = dock.BoundaryCells.Single() - dock.Outward;
        var sensor = new DesignEntity { Id = "sensor", RoomId = dock.RoomId, Prototype = "AirSensor", Position = inside, Footprint = [inside] };
        project.Layout.Equipment.Add(sensor);
        Assert.That(DesignDockGeometry.ValidateDock(project, dock, true), Is.Empty);
        project.Definition.Systems.Profiles.Single().BlocksCrew = true;
        Assert.That(DesignDockGeometry.ValidateDock(project, dock, true).Any(issue => issue.Code == "dock-hardware"), Is.True);
        project.Definition.Systems.Profiles.Single().BlocksCrew = false;
        sensor.Position = dock.BerthCells[0]; sensor.Footprint = [sensor.Position];
        Assert.That(DesignDockGeometry.ValidateDock(project, dock, true).Any(issue => issue.Code == "dock-obstructed"), Is.True);
        project.Layout.Equipment.Remove(sensor);
        project.Layout.SystemNetworks.Add(new() { Id = "blocked", Segments = [new() { Id = "pipe", ProfileId = "pipe", Position = dock.ApproachCells[0] }] });
        Assert.That(DesignDockGeometry.ValidateDock(project, dock, true).Any(issue => issue.Code == "dock-obstructed"), Is.True, "Native underfloor hardware cannot occupy a vacuum approach.");
    }

    [Test]
    public void RemovingAllDockRecordsCannotHideMandatorySpaceProtectionBeforeCirculation()
    {
        var project = Fixture(false); project.Stage = "growth"; project.Layout.Docks.Clear();
        Assert.That(DesignDockGeometry.Validate(project).Any(issue => issue.Code == "dock-reservation-missing"), Is.True);
    }

    [Test]
    public void ConnectedVoidAndClearCenterlineDoNotProveAFullWidthShuttleApproach()
    {
        var project = Fixture(); var dock = project.Layout.Docks.Single();
        project.Layout.Walls.Add(new(45, 25)); // The centerline y22 stays clear; the shuttle's outer swept edge does not.
        Assert.That(DesignMeasurements.ExteriorSpace(project).Contains(dock.BoundaryCells.Single() + dock.Outward), Is.True);
        Assert.That(DesignDockGeometry.ValidateDock(project, dock).Any(issue => issue.Code is "dock-obstructed" or "dock-exterior"), Is.True);
    }

    [Test]
    public void TamperedMasksOrChangedPrototypeFootprintsCannotInventDockingEvidence()
    {
        var project = Fixture(); var dock = project.Layout.Docks.Single(); var expected = dock.BerthCells[0];
        dock.BerthCells.Clear();
        Assert.That(DesignDockGeometry.GeometryMatches(project, dock), Is.False);
        Assert.That(DesignDockGeometry.ReservedCells(project), Does.Contain(expected));
        Assert.That(DesignDockGeometry.Measure(project, project.Layout.Rooms.Single()).Single().Count, Is.Zero);
        project = Fixture(); project.Settings.PrototypeFootprints["AirlockShuttle"] = new() { Width = 2, Height = 1 };
        Assert.That(DesignDockGeometry.Measure(project, project.Layout.Rooms.Single()).Single().Count, Is.Zero);
        Assert.That(DesignDockGeometry.ValidateDock(project, project.Layout.Docks.Single(), true).Any(issue => issue.Code == "dock-hardware"), Is.True);
    }

    [Test]
    public void MissingPreferredHardwareStaysSoftAfterTheBoundMinimumIsSatisfied()
    {
        var project = Fixture(); var room = project.Layout.Rooms.Single();
        var definition = project.Definition.Departments[0].Rooms[0]; definition.Contents.Single().Count.Target = 2;
        room.Cells = new GridBounds(20, 20, 6, 30).Cells().ToList();
        project.Layout.Walls = room.Cells.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Except(room.Cells).Except(project.Layout.Docks.Single().BoundaryCells).Distinct().ToList();
        var second = DesignDockGeometry.Create(room.Id, definition.Exterior.Docks.Single(), 2, new(26, 42), new(1, 0), new(0, 0, 80, 60)); project.Layout.Docks.Add(second);
        var diagnostics = DesignDockGeometry.Validate(project, true);
        Assert.That(diagnostics.Where(issue => issue.Severity == "error"), Is.Empty, ProgramTestData.Errors(diagnostics));
        Assert.That(diagnostics.Any(issue => issue.Code == "dock-hardware" && issue.Severity == "warning"), Is.True);
        Assert.That(DesignDockGeometry.Measure(project, room).Single().Count, Is.EqualTo(1));
    }

    [Test]
    public void FinalPressureNeedsIndependentVerifiedDockCountsRatherThanADeclaredAirlockAlone()
    {
        var project = Fixture(); var room = project.Layout.Rooms.Single();
        var state = new PressureState
        {
            Rooms = [new() { Path = room.DefinitionPath, Area = 30, Width = 6, Height = 5, Environment = new(), EntryOwners = [], Contents = [new() { Id = "DockAirlock", Prototype = "AirlockShuttle", Count = 1, Placement = "spaceBoundary", InterfaceType = "dockingPort" }] }],
            Departments = [new() { Path = "Alpha", Width = 6, Height = 5 }]
        };
        Assert.That(PressureEvaluator.Evaluate(project.Definition, state, true).Diagnostics.Any(issue => issue.Code == "dock-measurement"), Is.True);
        state.Rooms.Single().Docks = DesignDockGeometry.Measure(project, room);
        var verified = PressureEvaluator.Evaluate(project.Definition, state, true);
        Assert.That(verified.Success, Is.True, ProgramTestData.Errors(verified.Diagnostics));
        state.Rooms.Single().Docks!.Single().Count = 0;
        Assert.That(PressureEvaluator.Evaluate(project.Definition, state, true).Diagnostics.Any(issue => issue.Path.Contains("/docks:Shuttle", StringComparison.Ordinal) && issue.Code == "count-bound"), Is.True);
    }

    [Test]
    public void DockSchemaRoundTripsAndRejectsDetachedOffsetsOrDuplicateContentBindings()
    {
        var project = Fixture(); var roundtrip = ProgramYaml.Parse(ProgramYaml.Export(project.Definition));
        Assert.That(roundtrip.Departments[0].Rooms[0].Exterior.Docks.Single().ContentId, Is.EqualTo("DockAirlock"));
        Assert.That(ProjectTree.Find(project.Tree!, "Alpha/Work#1")!.Requirements!.Exterior!.Docks.Single().ContentId, Is.EqualTo("DockAirlock"));
        var rule = project.Definition.Departments[0].Rooms[0].Exterior.Docks.Single(); rule.BerthOffset = 100;
        Assert.That(ProgramValidator.Validate(project.Definition).Any(issue => issue.Code == "dock-alignment"), Is.True);
        rule.BerthOffset = 0; project.Definition.Departments[0].Rooms[0].Exterior.Docks.Add(new() { Id = "OtherDock", ContentId = "DockAirlock" });
        Assert.That(ProgramValidator.Validate(project.Definition).Any(issue => issue.Code == "duplicate"), Is.True);
    }

    [Test]
    public void ParentEditsTranslateAndRemoveDockAndCirculationReservationsWithoutClearingProtection()
    {
        var project = Fixture(); var dock = project.Layout.Docks.Single(); dock.Edited = dock.Locked = true;
        project.Layout.CirculationReservations.Add(new() { Id = "hall-plan", RoomId = dock.RoomId, BoundaryCells = [new(19, 22)], ApproachCells = [new(18, 22)], Outward = new(-1, 0), Width = 1, Edited = true });
        var moved = ProjectEdits.Apply(project, new() { Kind = "move-room", NodeId = dock.RoomId, Position = new(27, 26) });
        Assert.That(moved.Layout.Docks.Single().BoundaryCells, Is.EqualTo(new[] { new Cell(31, 26) }));
        Assert.That(moved.Layout.Docks.Single().Edited && moved.Layout.Docks.Single().Locked, Is.True);
        Assert.That(moved.Layout.CirculationReservations.Single().BoundaryCells, Is.EqualTo(new[] { new Cell(24, 26) }));
        var removed = ProjectEdits.Apply(moved, new() { Kind = "remove-instance", NodeId = dock.RoomId });
        Assert.That(removed.Layout.Docks, Is.Empty); Assert.That(removed.Layout.CirculationReservations, Is.Empty);
    }

    [TestCase(5)]
    [TestCase(-5)]
    public void MovingADockAlongItsNormalRegeneratesTheDerivedExteriorLaneAndSurvivesImport(int distance)
    {
        var project = Fixture(); var original = project.Layout.Docks.Single(); original.Edited = original.Locked = true;
        project.Layout.Equipment.Single().Pinned = true; project.Layout.Interfaces.Single().Edited = true;
        var room = project.Layout.Rooms.Single();
        var moved = ProjectEdits.Apply(project, new() { Kind = "move-room", NodeId = room.Id, Position = new(room.Seed.X + distance, room.Seed.Y) });
        var dock = moved.Layout.Docks.Single();
        Assert.That(DesignDockGeometry.GeometryMatches(moved, dock), Is.True);
        Assert.That(dock.ApproachCells.Max(cell => cell.X), Is.EqualTo(80), "The derived exit stays at the fixed outer canvas edge, rather than translating the old endpoint.");
        Assert.That(dock.Id, Is.EqualTo(original.Id)); Assert.That(dock.EntityId, Is.EqualTo(original.EntityId)); Assert.That(dock.InterfaceId, Is.EqualTo(original.InterfaceId));
        Assert.That(dock.Edited && dock.Locked, Is.True);
        Assert.That(moved.Layout.Equipment.Single().Position, Is.EqualTo(original.BoundaryCells.Single() + new Cell(distance, 0)), "Protected hardware must move exactly once with its owning room.");
        var imported = DesignPipeline.Import(moved);
        Assert.That(DesignDockGeometry.GeometryMatches(imported, imported.Layout.Docks.Single()), Is.True);
        Assert.That(imported.Report!.Diagnostics.Any(issue => issue.Code == "dock-geometry"), Is.False);
    }

    [Test]
    public void PlanningDetectsCrossingFlightLanesBeyondTheProvisionalCanvasAfterTranslation()
    {
        var project = Fixture(false); var definition = project.Definition.Departments[0].Rooms[0]; var room = project.Layout.Rooms.Single();
        var arrivals = definition.Exterior.Docks.Single(); arrivals.BerthWidth = 18; arrivals.BerthDepth = 9; arrivals.BerthOffset = -2;
        var cargo = new DockRequirement { Id = "Cargo", ContentId = "CargoAirlock", BerthWidth = 15, BerthDepth = 7, BerthOffset = 1, Clearance = 1, ApproachLength = 16 };
        definition.Exterior.Docks.Add(cargo); project.Settings.Width = project.Settings.Height = 160;
        var east = DesignDockGeometry.Create(room.Id, arrivals, 1, new(33, 29), new(1, 0), new(0, 0, 160, 160));
        var south = DesignDockGeometry.Create(room.Id, cargo, 1, new(184, 183), new(0, -1), new(0, 0, 160, 160));
        Assert.That(DesignDockGeometry.ReservedCells(east).Overlaps(DesignDockGeometry.ReservedCells(south)), Is.False,
            "The regression requires a crossing beyond the shortened saved approach.");
        Assert.That(DesignDockGeometry.PlanningOverlap(project, east, south), Is.True);
        Assert.That(DesignDockGeometry.PlanningOverlap(project, south, east), Is.True);
        var delta = new Cell(-300, -250); DesignDockGeometry.Translate(east, delta); DesignDockGeometry.Translate(south, delta);
        east.BerthCells.Clear(); east.ApproachCells.Clear(); south.BerthCells.Clear(); south.ApproachCells.Clear();
        project.Settings.Width = project.Settings.Height = 512;
        Assert.That(DesignDockGeometry.PlanningOverlap(project, east, south), Is.True,
            "Global translation, canvas changes and saved-mask truncation cannot erase a future swept-lane crossing.");
    }

    [TestCase("north")]
    [TestCase("east")]
    [TestCase("south")]
    [TestCase("west")]
    public void PlanningIntersectsTheEntireOutwardLaneAndOnlyTheRequestedStructuralHalo(string direction)
    {
        var project = Fixture(false); var rule = project.Definition.Departments[0].Rooms[0].Exterior.Docks.Single();
        var normal = DesignDockGeometry.Normal(direction); var tangent = DesignDockGeometry.Tangent(normal); var anchor = new Cell(-35, -29);
        var dock = DesignDockGeometry.Create(project.Layout.Rooms.Single().Id, rule, 1, anchor, normal, new(0, 0, 80, 60));
        Cell At(int depth, int lateral) => new(anchor.X + depth * normal.X + lateral * tangent.X, anchor.Y + depth * normal.Y + lateral * tangent.Y);
        Assert.That(DesignDockGeometry.PlanningIntersects(project, dock, [At(10000, 0)]), Is.True, "Future geometry beyond any current canvas still blocks the straight flight lane.");
        Assert.That(DesignDockGeometry.PlanningIntersects(project, dock, [At(-1, 0)]), Is.False, "The station side of the docking plane remains usable.");
        Assert.That(DesignDockGeometry.PlanningIntersects(project, dock, [At(20, 4)]), Is.False);
        Assert.That(DesignDockGeometry.PlanningIntersects(project, dock, [At(20, 4)], true), Is.True, "A future wall beside a floor cell must stay out of the flight lane.");
        var other = DesignDockGeometry.Create(dock.RoomId, rule, 2, At(0, 20), normal, new(0, 0, 80, 60));
        Assert.That(DesignDockGeometry.PlanningOverlap(project, dock, other), Is.False, "Parallel separated lanes do not reserve all exterior space.");
    }
}
