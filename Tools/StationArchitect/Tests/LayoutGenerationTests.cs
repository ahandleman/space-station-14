using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
public sealed class LayoutGenerationTests
{
    private static StationDesignProject Minimal() => new()
    {
        Definition = ProgramTestData.Minimal(),
        Settings = new() { Width = 80, Height = 70, AutoSize = true, OptimizationIterations = 100 }
    };

    [Test]
    public void RoomGrowthCreatesConnectedOwnedFloorWithoutAnonymousDepartmentFloor()
    {
        var project = RoomGrowth.Run(DesignPlacement.Run(InstanceSelection.Resolve(Minimal())));
        var room = project.Layout.Rooms.Single();
        Assert.That(room.Cells.Count, Is.InRange(16, 120));
        Assert.That(LayoutGeometry.Connected(room.Cells), Is.True);
        Assert.That(project.Layout.Departments.Single(owner => owner.OwnerId == "Alpha").Cells, Is.EquivalentTo(room.Cells));
        Assert.That(project.Report!.Diagnostics, Is.Empty, ProgramTestData.Errors(project.Report.Diagnostics));
    }

    [Test]
    public void PlanningAndGrowthReplayDeterministicallyWithoutMutatingInput()
    {
        var input = InstanceSelection.Resolve(Minimal());
        var first = RoomGrowth.Run(DesignPlacement.Run(input));
        var second = RoomGrowth.Run(DesignPlacement.Run(input));
        Assert.That(input.Layout.Rooms.All(room => room.Cells.Count == 0 && !room.HasSeed), Is.True);
        Assert.That(first.Layout.Rooms.SelectMany(room => room.Cells), Is.EqualTo(second.Layout.Rooms.SelectMany(room => room.Cells)));
        Assert.That(first.Settings.Width, Is.EqualTo(second.Settings.Width));
    }

    [Test]
    public void LockedAndPaintedFloorSurvivesBothStageReruns()
    {
        var project = RoomGrowth.Run(DesignPlacement.Run(InstanceSelection.Resolve(Minimal())));
        var room = project.Layout.Rooms.Single();
        room.FloorLocked = true;
        room.Cells = new GridBounds(20, 20, 5, 4).Cells().ToList();
        var expected = room.Cells.ToList();
        var rerun = RoomGrowth.Run(DesignPlacement.Run(project));
        Assert.That(rerun.Layout.Rooms.Single().Cells, Is.EqualTo(expected));
    }

    [Test]
    public void ASeedPinnedBeforeFirstPlacementReceivesARealAllocation()
    {
        var source = InstanceSelection.Resolve(Minimal());
        var room = source.Layout.Rooms.Single(); room.Seed = new(40, 35); room.HasSeed = true; room.Pinned = true;
        var project = RoomGrowth.Run(DesignPlacement.Run(source));
        var grown = project.Layout.Rooms.Single();
        Assert.That(grown.Seed, Is.EqualTo(new Cell(40, 35)));
        Assert.That(grown.Cells, Does.Contain(grown.Seed));
        Assert.That(grown.Cells.Count, Is.GreaterThanOrEqualTo(16));
    }

    [Test]
    public void CuttingOneUnusedEnvelopeTileDoesNotForbidEveryCorridorGap()
    {
        var source = new StationDesignProject
        {
            Settings = new() { Width = 80, Height = 80, Topology = "spine", OptimizationIterations = 20 },
            Definition = new()
            {
                Id = "EditableEnvelope",
                Departments =
                [
                    new() { Id = "Alpha", Area = ProgramTestData.Area(16, 25, 80), Rooms = [ProgramTestData.Room("Work", 16, 25, 80)] },
                    new() { Id = "Beta", Area = ProgramTestData.Area(16, 25, 80), Rooms = [ProgramTestData.Room("Lab", 16, 25, 80)] }
                ],
                Connections = [new() { Id = "CrewAccess", From = "Alpha/Work", To = ["Beta/Lab"], Modes = ["publicHall"], Priority = "required", Presence = "requireTarget" }]
            }
        };
        var placed = DesignPlacement.Run(InstanceSelection.Resolve(source));
        var allocated = placed.Layout.Rooms.SelectMany(room => room.GrowthReservation).ToHashSet();
        var cut = placed.Layout.Envelope.First(cell => !allocated.Contains(cell) && Cell.Directions.All(direction => !allocated.Contains(cell + direction)));
        placed = ProjectEdits.Apply(placed, new() { Kind = "erase-envelope", Cells = [cut] });
        var project = DesignRouting.Run(RoomGrowth.Run(placed));
        Assert.That(project.Layout.EnvelopeEdited, Is.True);
        Assert.That(project.Layout.Envelope, Does.Not.Contain(cut));
        Assert.That(project.Layout.PublicCorridors, Is.Not.Empty);
        Assert.That(project.Layout.Routes.Any(route => route.RequirementId == "$station:CrewAccess" && DesignMeasurements.RouteValid(project, route)), Is.True);
    }

    [TestCase("ring")]
    [TestCase("hub")]
    [TestCase("spine")]
    public void DefaultCatalogGrowsActualRoomsWithinAreaAndConnectivityBounds(string topology)
    {
        var project = new StationDesignProject { Definition = StationProgramDefaults.Create(), Settings = new() { Topology = topology, OptimizationIterations = 250 } };
        project = RoomGrowth.Run(DesignPlacement.Run(InstanceSelection.Resolve(project)));
        var definitions = LayoutGeometry.Definitions(project);
        var issues = new List<string>();
        foreach (var room in project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned))
        {
            var definition = definitions[room.DefinitionPath];
            if (!LayoutGeometry.Connected(room.Cells)) issues.Add(room.Id + ": disconnected");
            if (room.Cells.Count < RequirementCompiler.EffectiveMinimumArea(definition) || room.Cells.Count > definition.Area.Max) issues.Add(room.Id + ": area=" + room.Cells.Count);
            var parent = project.Layout.Rooms.Any(child => child.ParentInstanceId == room.Id);
            if (!LayoutGeometry.ShapeFits(parent ? LayoutGeometry.ComplexFloor(project, room) : room.Cells, parent ? definition.ComplexShape ?? definition.Shape : definition.Shape)) issues.Add(room.Id + ": shape");
        }
        Assert.That(issues, Is.Empty, string.Join(Environment.NewLine, issues));
        Assert.That(project.Layout.Rooms.Any(room => room.Cells.Count < room.Bounds.Area), Is.True, "Room growth supports nonrectangular actual masks.");
        Assert.That(project.Layout.Rooms.SelectMany(room => room.Cells).Distinct().Count(), Is.EqualTo(project.Layout.Rooms.Sum(room => room.Cells.Count)), "Floor has one room owner.");
    }

    [TestCase("ring")]
    [TestCase("hub")]
    [TestCase("spine")]
    public void RequiredCounterHasRealSharedWallBetweenItsDifferentOwners(string topology)
    {
        var project = new StationDesignProject { Definition = StationProgramDefaults.Create(), Settings = new() { Topology = topology, OptimizationIterations = 0 } };
        project = RoomGrowth.Run(DesignPlacement.Run(InstanceSelection.Resolve(project)));
        var kitchen = project.Layout.Rooms.Single(room => room.DefinitionPath == "Service/Kitchen");
        var concourse = project.Layout.Rooms.Where(room => room.DefinitionPath == "Commons/Concourse").SelectMany(room => room.Cells).ToHashSet();
        Assert.That(kitchen.Cells.Count(cell => Cell.Directions.Any(direction => !kitchen.Cells.Contains(cell + direction) && concourse.Contains(cell + direction + direction))), Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public void TopologyChangesActualOwnerAnchorsAndRoutingGuides()
    {
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var topology in new[] { "ring", "hub", "spine" })
        {
            var source = new StationDesignProject { Definition = StationProgramDefaults.Create(), Settings = new() { Topology = topology } };
            var project = DesignPlacement.Run(InstanceSelection.Resolve(source));
            signatures.Add(string.Join(';', project.Layout.Departments.Where(owner => owner.OwnerId != RequirementCompiler.MaintenanceOwner).OrderBy(owner => owner.OwnerId, StringComparer.Ordinal).Select(owner => owner.Anchor.ToString())));
            Assert.That(project.Layout.PublicWaypoints.Count, Is.EqualTo(topology == "ring" ? 4 : topology == "hub" ? 1 : 2));
            Assert.That(project.Layout.PublicWaypoints.All(point => new GridBounds(0, 0, project.Settings.Width, project.Settings.Height).Contains(point)), Is.True);
        }
        Assert.That(signatures.Count, Is.EqualTo(3), "Topology choices must alter spatial structure, not only their label.");
    }

    [Test]
    public void ExplicitFillerCountsAreNeverOverriddenToMakeAPlanFit()
    {
        var source = Minimal();
        source.Definition.Departments[0].Area = ProgramTestData.Area(100, 140, 500);
        source.Definition.Departments[0].Rooms[0].Count = new() { Min = 6, Target = 6, Max = 6 };
        source.Definition.Departments[0].Rooms.Add(new() { Id = "Circulation", Role = "filler", Count = new() { Min = 1, Target = 1, Max = 4 }, Area = ProgramTestData.Area(9, 10, 12), Shape = new() { MaxAspectRatio = 3 } });
        source.Selection.RoomCounts["Alpha/Circulation"] = 1;
        var project = DesignPlacement.Run(InstanceSelection.Resolve(source));
        Assert.That(project.Layout.Rooms.Count(room => room.DefinitionPath == "Alpha/Circulation" && room.Enabled), Is.EqualTo(1));
        Assert.That(project.Selection.RoomCounts["Alpha/Circulation"], Is.EqualTo(1));
        Assert.That(project.Report!.Diagnostics.Any(issue => issue.Code == "circulation-capacity"), Is.True);
    }

    [Test]
    public void ActualBoundaryNegotiationBalancesRoomPressuresWithoutChangingParentFloor()
    {
        var source = Minimal();
        source.Definition.Departments[0].Area = ProgramTestData.Area(25, 80, 160);
        source.Definition.Departments[0].Rooms =
        [
            ProgramTestData.Room("Work", 16, 70, 100),
            new() { Id = "Circulation", Role = "filler", AllowThroughTraffic = true, Area = ProgramTestData.Area(9, 10, 40) }
        ];
        source.Layout.Rooms =
        [
            new() { Id = "Alpha/Work#1", DefinitionPath = "Alpha/Work", OwnerId = "Alpha", HasSeed = true, Seed = new(14, 14), Parcel = new(10, 10, 5, 8), GrowthReservation = new GridBounds(10, 10, 5, 8).Cells().ToList(), GrowthSide = "left" },
            new() { Id = "Alpha/Circulation#1", DefinitionPath = "Alpha/Circulation", OwnerId = "Alpha", HasSeed = true, Seed = new(18, 14), Parcel = new(16, 10, 4, 8), GrowthReservation = new GridBounds(16, 10, 4, 8).Cells().ToList(), GrowthSide = "spine" }
        ];
        source.Settings.OptimizationIterations = 0;
        var before = RoomGrowth.Run(source);
        source.Settings.OptimizationIterations = 100;
        var after = RoomGrowth.Run(source);
        Assert.That(after.Layout.Rooms.Single(room => room.DefinitionPath == "Alpha/Work").Cells.Count,
            Is.GreaterThan(before.Layout.Rooms.Single(room => room.DefinitionPath == "Alpha/Work").Cells.Count));
        Assert.That(after.Layout.Rooms.Sum(room => room.Cells.Count), Is.EqualTo(before.Layout.Rooms.Sum(room => room.Cells.Count)));
        Assert.That(after.Layout.Rooms.All(room => LayoutGeometry.Connected(room.Cells)), Is.True);
        Assert.That(after.Layout.Rooms.SelectMany(room => room.Cells).Distinct().Count(), Is.EqualTo(after.Layout.Rooms.Sum(room => room.Cells.Count)));
    }

    [Test]
    public void AThirdRoomsCornerSeamCannotBeConsumedByBoundaryPressure()
    {
        var source = Minimal();
        source.Definition.Departments[0].Area = ProgramTestData.Area(26, 81, 170);
        source.Definition.Departments[0].Rooms =
        [
            ProgramTestData.Room("Work", 16, 70, 100),
            new() { Id = "Circulation", Role = "filler", AllowThroughTraffic = true, Area = ProgramTestData.Area(9, 10, 40) },
            new() { Id = "Isolated", Area = ProgramTestData.Area(1, 1, 9), Environment = new() { Pressure = "low" } }
        ];
        source.Layout.Rooms =
        [
            new() { Id = "Alpha/Work#1", DefinitionPath = "Alpha/Work", OwnerId = "Alpha", HasSeed = true, Seed = new(14, 14), Parcel = new(10, 10, 5, 8), GrowthReservation = new GridBounds(10, 10, 5, 8).Cells().ToList(), GrowthSide = "left" },
            new() { Id = "Alpha/Circulation#1", DefinitionPath = "Alpha/Circulation", OwnerId = "Alpha", HasSeed = true, Seed = new(18, 14), Parcel = new(16, 10, 4, 8), GrowthReservation = new GridBounds(16, 10, 4, 8).Cells().ToList(), GrowthSide = "spine" },
            new() { Id = "Alpha/Isolated#1", DefinitionPath = "Alpha/Isolated", OwnerId = "Alpha", HasSeed = true, Seed = new(15, 9), Parcel = new(15, 9, 1, 1), GrowthReservation = [new(15, 9)] }
        ];
        source.Settings.OptimizationIterations = 5000;
        var project = RoomGrowth.Run(source);
        var floor = project.Layout.Rooms.SelectMany(room => room.Cells.Select(cell => (cell, room.Id))).ToDictionary(item => item.cell, item => item.Id);
        Assert.That(floor.All(item => Cell.Directions.All(direction => !floor.TryGetValue(item.Key + direction, out var other) || other == item.Value)), Is.True,
            "A two-room area improvement cannot remove the third room's structural corner seal.");
    }

    [TestCase(.9)]
    [TestCase(1.0)]
    public void FullDefaultOptimizationPreservesRoomSeamsForControlledInterfaces(double compactness)
    {
        var source = new StationDesignProject { Definition = StationProgramDefaults.Create(), Settings = new() { OptimizationIterations = 5000 } };
        source.Settings.Packing.Compactness = compactness;
        var qaLabel = (Environment.GetEnvironmentVariable("STATION_ARCHITECT_QA_LABEL") ?? "qa") + "-compact-" + compactness.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var placed = DesignPlacement.Run(InstanceSelection.Resolve(source));
        var placementMilliseconds = stopwatch.ElapsedMilliseconds; stopwatch.Restart();
        var project = RoomGrowth.Run(placed);
        TestContext.WriteLine($"Default runtime: placement={placementMilliseconds}ms growth={stopwatch.ElapsedMilliseconds}ms");
        if (Environment.GetEnvironmentVariable("STATION_ARCHITECT_QA_OUTPUT") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            var label = qaLabel;
            File.WriteAllText(Path.Combine(directory, "architect-dense-" + label + "-placement.station.json"), System.Text.Json.JsonSerializer.Serialize(placed, ProgramJson.Options));
            File.WriteAllText(Path.Combine(directory, "architect-dense-" + label + "-growth.station.json"), System.Text.Json.JsonSerializer.Serialize(project, ProgramJson.Options));
        }
        Assert.That(placed.Report!.Diagnostics.Any(issue => issue.Code == "planning-incomplete"), Is.False, ProgramTestData.Errors(placed.Report.Diagnostics));
        Assert.That(placed.Layout.CirculationReservations.Any(guide => guide.Id == "planning:public-backbone"), Is.True,
            "The default must reserve complete connected public and maintenance arteries before room growth.");
        Assert.That(project.Report!.Success, Is.True, ProgramTestData.Errors(project.Report.Diagnostics));
        var kitchen = project.Layout.Rooms.Single(room => room.DefinitionPath == "Service/Kitchen");
        var concourse = project.Layout.Rooms.Where(room => room.DefinitionPath == "Commons/Concourse").SelectMany(room => room.Cells).ToHashSet();
        Assert.That(kitchen.Cells.Count(cell => Cell.Directions.Any(direction => concourse.Contains(cell + direction + direction))), Is.GreaterThanOrEqualTo(2));
        var floor = project.Layout.Rooms.SelectMany(room => room.Cells.Select(cell => (cell, room.Id))).ToDictionary(item => item.cell, item => item.Id);
        Assert.That(floor.All(item => Cell.Directions.All(direction => !floor.TryGetValue(item.Key + direction, out var other) || other == item.Value)), Is.True,
            "Room floor must retain structural separation until explicit interfaces are constructed.");
        var final = DesignContents.Run(DesignRouting.Run(project));
        var generationErrors = final.Report!.Diagnostics.Where(issue => issue.Severity == "error").ToList();
        if (Environment.GetEnvironmentVariable("STATION_ARCHITECT_QA_OUTPUT") is { Length: > 0 } contentsOutput)
        {
            var label = qaLabel;
            File.WriteAllText(Path.Combine(contentsOutput, "architect-dense-" + label + "-contents.station.json"), System.Text.Json.JsonSerializer.Serialize(final, ProgramJson.Options));
        }
        var observations = DesignMeasurements.Build(final);
        var pendingNative = RequirementCompiler.Compile(final.Definition).Connections.Where(connection => DesignMeasurements.NativeUtilityNetworkPending(final, connection)).Select(connection => connection.Id).ToHashSet(StringComparer.Ordinal);
        var mandatory = PressureEvaluator.Evaluate(final.Definition, observations, final: true);
        var dueErrors = mandatory.Diagnostics.Where(issue => issue.Severity == "error" && !pendingNative.Contains(issue.Path)).ToList();
        var validation = DesignGeometryValidator.Validate(final);
        validation.Diagnostics.AddRange(generationErrors.Concat(dueErrors));
        if (Environment.GetEnvironmentVariable("STATION_ARCHITECT_QA_OUTPUT") is { Length: > 0 } output)
        {
            var label = qaLabel;
            final.Report = validation;
            File.WriteAllText(Path.Combine(output, "architect-dense-" + label + "-validation.station.json"), System.Text.Json.JsonSerializer.Serialize(final, ProgramJson.Options));
        }
        Assert.That(generationErrors.Concat(validation.Diagnostics.Where(issue => issue.Severity == "error")).Concat(dueErrors), Is.Empty,
            ProgramTestData.Errors(generationErrors.Concat(validation.Diagnostics).Concat(dueErrors)));
        if (pendingNative.Count > 0)
        {
            Assert.That(validation.Diagnostics.Any(issue => issue.Code == "native-utility-pending"), Is.True);
            Assert.That(observations.Connections.Any(connection => pendingNative.Contains(connection.Id)), Is.False,
                "Unbuilt native circuits stay unfulfilled until their power/atmos stage; they are not geometry proof.");
        }
    }

    [Test]
    public void FrontageCutsRemainActualVoidBeyondRoomWalls()
    {
        var source = new StationDesignProject { Definition = StationProgramDefaults.Create(), Settings = new() { OptimizationIterations = 50 } };
        var project = RoomGrowth.Run(DesignPlacement.Run(InstanceSelection.Resolve(source)));
        var floor = project.Layout.Rooms.SelectMany(room => room.Cells).ToHashSet();
        var walls = floor.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).ToHashSet();
        var reserved = project.Layout.ReservedSpace.ToHashSet();
        Assert.That(reserved, Is.Not.Empty);
        Assert.That(reserved.Overlaps(floor), Is.False);
        Assert.That(reserved.Overlaps(walls), Is.False, "Space reservations must not remove a sealing wall to manufacture frontage.");
        var definitions = LayoutGeometry.Definitions(project);
        foreach (var room in project.Layout.Rooms)
        {
            var definition = definitions[room.DefinitionPath];
            if (definition.Environment.Placement == "exterior") continue;
            var minimum = definition.Exterior.MinSpaceFrontage + definition.Contents.Where(content => content.Placement == "spaceBoundary").Sum(content => content.Count.Target);
            var frontage = room.Cells.SelectMany(cell => Cell.Directions.Select(direction => (Wall: cell + direction, Beyond: cell + direction + direction)))
                .Where(pair => !floor.Contains(pair.Wall) && reserved.Contains(pair.Beyond)).Select(pair => pair.Wall).Distinct().Count();
            Assert.That(frontage, Is.GreaterThanOrEqualTo(minimum), room.Id);
        }
    }

    [Test]
    public void NestedRoomsHaveExclusiveFloorInsideAnActualParentCirculationShell()
    {
        var source = Minimal();
        source.Definition.Departments[0].Area = ProgramTestData.Area(45, 80, 300);
        var parentDefinition = source.Definition.Departments[0].Rooms[0];
        parentDefinition.Area = ProgramTestData.Area(36, 60, 180);
        parentDefinition.Rooms = [ProgramTestData.Room("QuietRoom", 9, 12, 30), ProgramTestData.Room("Office", 9, 12, 30)];
        var project = RoomGrowth.Run(DesignPlacement.Run(InstanceSelection.Resolve(source)));
        var parent = project.Layout.Rooms.Single(room => string.IsNullOrEmpty(room.ParentInstanceId));
        var parentFloor = parent.Cells.ToHashSet();
        Assert.That(project.Report!.Diagnostics, Is.Empty, ProgramTestData.Errors(project.Report.Diagnostics));
        foreach (var child in project.Layout.Rooms.Where(room => room.ParentInstanceId == parent.Id))
        {
            Assert.That(child.Cells.All(parent.ComplexReservation.Contains), Is.True);
            Assert.That(child.Cells.Any(parentFloor.Contains), Is.False);
            Assert.That(child.Cells.Any(cell => Cell.Directions.Any(direction => parentFloor.Contains(cell + direction + direction))), Is.True,
                "A child needs a real wall interface with its enclosing parent's circulation floor.");
            Assert.That(LayoutGeometry.Connected(child.Cells), Is.True);
        }
        Assert.That(LayoutGeometry.Connected(parentFloor), Is.True);
        Assert.That(parent.Cells.Count, Is.InRange(36, 180));
    }

    [Test]
    public void CompactPackingHasMeasurablySmallerBoundsAndHigherOwnedFloorDensity()
    {
        var source = new StationDesignProject { Definition = StationProgramDefaults.Create(), Settings = new() { Topology = "ring", OptimizationIterations = 0 } };
        source.Settings.Packing.Compactness = 1;
        var compact = RoomGrowth.Run(DesignPlacement.Run(InstanceSelection.Resolve(source)));
        source.Settings.Packing.Compactness = .9;
        var loose = RoomGrowth.Run(DesignPlacement.Run(InstanceSelection.Resolve(source)));
        var compactFloor = compact.Layout.Rooms.SelectMany(room => room.Cells).ToHashSet();
        var looseFloor = loose.Layout.Rooms.SelectMany(room => room.Cells).ToHashSet();
        var compactArea = LayoutGeometry.Bounds(compactFloor).Area; var looseArea = LayoutGeometry.Bounds(looseFloor).Area;
        var compactDensity = (double)compactFloor.Count / compactArea; var looseDensity = (double)looseFloor.Count / looseArea;
        if (Environment.GetEnvironmentVariable("STATION_ARCHITECT_QA_OUTPUT") is { Length: > 0 } output)
        {
            var label = Environment.GetEnvironmentVariable("STATION_ARCHITECT_QA_LABEL") ?? "qa";
            File.WriteAllText(Path.Combine(output, "architect-dense-" + label + "-density-compact.station.json"), System.Text.Json.JsonSerializer.Serialize(compact, ProgramJson.Options));
            File.WriteAllText(Path.Combine(output, "architect-dense-" + label + "-density-loose.station.json"), System.Text.Json.JsonSerializer.Serialize(loose, ProgramJson.Options));
        }
        TestContext.WriteLine($"Same-seed compact vs loose: bounds={compactArea}/{looseArea}, ownedDensity={compactDensity:F4}/{looseDensity:F4}, floor={compactFloor.Count}/{looseFloor.Count}");
        Assert.That(compact.Report!.Success, Is.True, ProgramTestData.Errors(compact.Report.Diagnostics));
        Assert.That(loose.Report!.Success, Is.True, ProgramTestData.Errors(loose.Report.Diagnostics));
        Assert.That(compact.Layout.CirculationReservations.Any(guide => guide.Id == "planning:public-backbone"), Is.True);
        Assert.That(loose.Layout.CirculationReservations.Any(guide => guide.Id == "planning:public-backbone"), Is.True);
        Assert.That(compactArea, Is.LessThan(looseArea * .9));
        Assert.That(compactDensity, Is.GreaterThan(looseDensity * 1.1));
        Assert.That(compactFloor.Count, Is.EqualTo(looseFloor.Count), "Density comparisons must retain the same complete floor program.");
    }

    [Test]
    public void DistributedNamedGroupsRemainTogetherAndStationwideRoomsUseSeparateSites()
    {
        var source = Minimal(); var owner = source.Definition.Departments[0];
        owner.Area = ProgramTestData.Area(64, 140, 500);
        owner.Rooms = Enumerable.Range(0, 6).Select(index => ProgramTestData.Room("Room" + index, 9, 20, 50)).ToList();
        foreach (var room in owner.Rooms.Take(2)) room.Placement = new() { Policy = "distributed", Group = "WorkshopSuite" };
        foreach (var room in owner.Rooms.Skip(2)) room.Placement.Policy = "stationwide";
        var project = DesignPlacement.Run(InstanceSelection.Resolve(source));
        var rooms = project.Layout.Rooms;
        var groupedDistance = rooms[0].Seed.Distance(rooms[1].Seed);
        Assert.That(groupedDistance, Is.LessThan(rooms.Skip(2).Min(room => room.Seed.Distance(rooms[0].Seed))));
        Assert.That(rooms.Skip(2).Select(room => room.Parcel).Distinct().Count(), Is.EqualTo(4));
        Assert.That(rooms.SelectMany(room => room.GrowthReservation).Distinct().Count(), Is.EqualTo(rooms.Sum(room => room.GrowthReservation.Count)));
    }

    [Test]
    public void CrossOwnerPressureMovesAnOrdinaryBoundaryWithoutRequiringADirectRelation()
    {
        var source = Minimal(); source.Settings.OptimizationIterations = 100;
        source.Definition.Departments[0].Area = ProgramTestData.Area(16, 80, 120);
        source.Definition.Departments[0].Rooms[0].Area = ProgramTestData.Area(16, 80, 100);
        source.Definition.Departments.Add(new() { Id = "Beta", Area = ProgramTestData.Area(9, 10, 60), Rooms = [new() { Id = "Store", Role = "filler", Area = ProgramTestData.Area(9, 10, 40) }] });
        source.Layout.Rooms =
        [
            new() { Id = "Alpha/Work#1", DefinitionPath = "Alpha/Work", OwnerId = "Alpha", HasSeed = true, Seed = new(14,14), Parcel = new(10,10,5,8), GrowthReservation = new GridBounds(10,10,5,8).Cells().ToList(), GrowthSide = "left" },
            new() { Id = "Beta/Store#1", DefinitionPath = "Beta/Store", OwnerId = "Beta", HasSeed = true, Seed = new(18,14), Parcel = new(16,10,4,8), GrowthReservation = new GridBounds(16,10,4,8).Cells().ToList(), GrowthSide = "spine" }
        ];
        source.Settings.Packing.BoundaryGrowthBudget = 0; var before = RoomGrowth.Run(source);
        source.Settings.Packing.BoundaryGrowthBudget = 100; var after = RoomGrowth.Run(source);
        Assert.That(after.Layout.Rooms[0].Cells.Count, Is.GreaterThan(before.Layout.Rooms[0].Cells.Count));
        Assert.That(after.Layout.Rooms.Sum(room => room.Cells.Count), Is.EqualTo(before.Layout.Rooms.Sum(room => room.Cells.Count)));
        Assert.That(after.Layout.Rooms[1].Cells.Count, Is.GreaterThanOrEqualTo(9));
        source.Layout.Departments.Add(new() { OwnerId = "Beta", Locked = true });
        Assert.That(RoomGrowth.Run(source).Layout.Rooms[0].Cells.Count, Is.EqualTo(before.Layout.Rooms[0].Cells.Count));
    }

    [Test]
    public void InitialNestedGeometryFitsAFeasibleComplexBudgetInsteadOfSummingPreferredSizes()
    {
        var source = Minimal(); var parent = source.Definition.Departments[0].Rooms[0];
        source.Definition.Departments[0].Area = ProgramTestData.Area(20, 30, 500);
        parent.Area = ProgramTestData.Area(16, 40, 120); parent.ComplexArea = ProgramTestData.Area(20, 30, 35);
        parent.Rooms = [ProgramTestData.Room("Child", 4, 40, 60)];
        var project = RoomGrowth.Run(DesignPlacement.Run(InstanceSelection.Resolve(source)));
        Assert.That(project.Layout.Rooms.Sum(room => room.Cells.Count), Is.InRange(20, 35));
        Assert.That(project.Definition.Departments[0].Rooms[0].Area.Target, Is.EqualTo(40));
        Assert.That(project.Definition.Departments[0].Rooms[0].Rooms[0].Area.Target, Is.EqualTo(40));
        Assert.That(project.Layout.Rooms.All(room => room.Cells.Count >= RequirementCompiler.EffectiveMinimumArea(LayoutGeometry.Definitions(project)[room.DefinitionPath])), Is.True);
        Assert.That(project.Report!.Diagnostics.Where(issue => issue.Severity == "error"), Is.Empty, ProgramTestData.Errors(project.Report.Diagnostics));
    }

    [Test]
    public void ALeafComplexShapeOverrideControlsActualDimensionsAndGrowth()
    {
        var source = Minimal(); var room = source.Definition.Departments[0].Rooms[0];
        room.Area = ProgramTestData.Area(16, 25, 100);
        room.Shape = new() { MaxAspectRatio = 6, MinFillRatio = .2 };
        room.ComplexShape = new() { MaxAspectRatio = 1.2, MinFillRatio = .9 };
        var project = RoomGrowth.Run(DesignPlacement.Run(InstanceSelection.Resolve(source)));
        Assert.That(LayoutGeometry.ShapeFits(project.Layout.Rooms.Single().Cells, room.ComplexShape), Is.True);
    }

    [Test]
    public void AGrandparentComplexBudgetConstrainsAndBalancesCrossOwnerLeafGrowth()
    {
        var work = ProgramTestData.Room("Work", 16, 80, 100);
        var suite = ProgramTestData.Room("Suite", 40, 40, 40); suite.Rooms = [work];
        var outer = ProgramTestData.Room("Outer", 24, 24, 24); outer.Rooms = [suite]; outer.ComplexArea = ProgramTestData.Area(80, 104, 104);
        var source = Minimal(); source.Settings.OptimizationIterations = 100;
        source.Definition.Departments[0].Rooms = [outer]; source.Definition.Departments[0].Area = ProgramTestData.Area(80, 140, 260);
        source.Definition.Departments.Add(new() { Id = "Beta", Area = ProgramTestData.Area(9, 10, 60), Rooms = [new() { Id = "Store", Role = "filler", Area = ProgramTestData.Area(9, 10, 40) }] });
        DesignRoom Floor(string id, string path, string owner, string parent, GridBounds bounds, string side = "left") => new()
        { Id = id, DefinitionPath = path, OwnerId = owner, ParentInstanceId = parent, HasSeed = true, Seed = bounds.Center, Parcel = bounds, GrowthReservation = bounds.Cells().ToList(), ComplexReservation = bounds.Cells().ToList(), GrowthSide = side };
        var root = Floor("Alpha/Outer#1", "Alpha/Outer", "Alpha", "", new(0, 10, 3, 8)); root.ComplexReservation = new GridBounds(0, 10, 15, 8).Cells().ToList();
        var middle = Floor(root.Id + "/Suite#1", "Alpha/Outer/Suite", "Alpha", root.Id, new(4, 10, 5, 8)); middle.ComplexReservation = new GridBounds(4, 10, 11, 8).Cells().ToList();
        var leaf = Floor(middle.Id + "/Work#1", "Alpha/Outer/Suite/Work", "Alpha", middle.Id, new(10, 10, 5, 8));
        source.Layout.Rooms = [root, middle, leaf, Floor("Beta/Store#1", "Beta/Store", "Beta", "", new(16, 10, 4, 8), "spine")];
        var bounded = RoomGrowth.Run(source);
        Assert.That(bounded.Layout.Rooms.Single(room => room.Id == leaf.Id).Cells.Count, Is.EqualTo(40));
        outer.ComplexArea = ProgramTestData.Area(80, 112, 160); outer.ComplexArea.Weight = 1000;
        var balanced = RoomGrowth.Run(source); var grown = balanced.Layout.Rooms.Single(room => room.Id == leaf.Id);
        Assert.That(grown.Cells.Count, Is.EqualTo(48), "The grandparent's weighted preferred total stops growth before the leaf's own preferred area80.");
        var observedRoot = balanced.Layout.Rooms.Single(room => room.Id == root.Id);
        var observedMiddle = balanced.Layout.Rooms.Single(room => room.Id == middle.Id);
        Assert.That(grown.Cells.All(observedRoot.ComplexReservation.Contains), Is.True);
        Assert.That(grown.Cells.All(observedMiddle.ComplexReservation.Contains), Is.True);
        Assert.That(balanced.Layout.Rooms.Where(room => room.OwnerId == "Alpha").Sum(room => room.Cells.Count), Is.EqualTo(112));
    }

    [Test]
    public void OversizedAutomaticPackingRemainsABoundedEditableDraftWithEveryInstance()
    {
        var source = Minimal(); source.Settings.Topology = "spine"; source.Settings.DepartmentGap = 32; source.Settings.Packing.Compactness = 0;
        var owner = source.Definition.Departments[0]; owner.Area = ProgramTestData.Area(1024, 1024, 1024);
        owner.Rooms[0].Area = ProgramTestData.Area(16, 16, 16); owner.Rooms[0].Count = new() { Min = 64, Target = 64, Max = 64 }; owner.Rooms[0].Placement.Policy = "stationwide";
        var project = DesignPlacement.Run(InstanceSelection.Resolve(source));
        Assert.That(project.Layout.Rooms.Count(room => room.Enabled && !room.Orphaned), Is.EqualTo(64));
        Assert.That(project.Settings.Width, Is.LessThanOrEqualTo(512)); Assert.That(project.Settings.Height, Is.LessThanOrEqualTo(512));
        Assert.That(project.Report!.Diagnostics.Any(issue => issue.Code == "placement-capacity"), Is.True);
        Assert.DoesNotThrow(() => DesignPipeline.CheckStructure(project));
    }

    [Test]
    public void DepartmentBundlingChangesActualDistributedClusterPositions()
    {
        var source = new StationDesignProject { Definition = StationProgramDefaults.Create(), Settings = new() { Topology = "ring" } };
        source.Settings.Packing.DepartmentBundling = 0; var loose = DesignPlacement.Run(InstanceSelection.Resolve(source));
        source.Settings.Packing.DepartmentBundling = 1; var bundled = DesignPlacement.Run(InstanceSelection.Resolve(source));
        Assert.That(bundled.Layout.Rooms.Select(room => room.Seed), Is.Not.EqualTo(loose.Layout.Rooms.Select(room => room.Seed)));
    }
}
