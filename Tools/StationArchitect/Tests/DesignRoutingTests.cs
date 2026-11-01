using Content.StationArchitect;
using NUnit.Framework;
using System.Text.Json;

namespace StationArchitect.Tests;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class DesignRoutingTests
{
    private static List<Cell> Floor(int x, int y, int width, int height) => new GridBounds(x, y, width, height).Cells().ToList();

    private static StationDesignProject Pair()
    {
        var definition = ProgramTestData.Minimal();
        definition.Departments[0].Area = ProgramTestData.Area(16, 32, 200);
        definition.Departments[0].Rooms = [ProgramTestData.Room("Work", 9, 16, 100), ProgramTestData.Room("Hall", 9, 16, 100)];
        var hall = definition.Departments[0].Rooms[1]; hall.Role = "filler"; hall.AllowThroughTraffic = true;
        definition.Connections = [new() { Id = "WorkHall", From = "Alpha/Work", To = ["Alpha/Hall"], Modes = ["department"], Presence = "requireTarget" }];
        return new()
        {
            Definition = definition, Stage = "growth", Settings = new() { Width = 48, Height = 36 },
            Layout = new()
            {
                Envelope = Floor(1, 1, 46, 34),
                Rooms =
                [
                    new() { Id = "Alpha/Work#1", DefinitionPath = "Alpha/Work", OwnerId = "Alpha", Instance = "1", Cells = Floor(5, 5, 4, 4), Seed = new(6, 6), HasSeed = true },
                    new() { Id = "Alpha/Hall#1", DefinitionPath = "Alpha/Hall", OwnerId = "Alpha", Instance = "1", Cells = Floor(10, 5, 4, 4), Seed = new(11, 6), HasSeed = true }
                ]
            }
        };
    }

    [Test]
    public void RoutingUsesRealSharedWallAndDoesNotMutateCaller()
    {
        var project = Pair(); var before = JsonSerializer.Serialize(project, ProgramJson.Options);
        var routed = DesignRouting.Run(project);
        Assert.That(JsonSerializer.Serialize(project, ProgramJson.Options), Is.EqualTo(before));
        Assert.That(routed.Layout.Interfaces.Any(port => new[] { port.From, port.To }.ToHashSet().SetEquals(["Alpha/Work#1", "Alpha/Hall#1"]) && port.Cells.All(cell => cell.X == 9)), Is.True);
        var route = routed.Layout.Routes.Single(route => route.RequirementId == "$station:WorkHall");
        Assert.That(DesignMeasurements.RouteValid(routed, route), Is.True);
        Assert.That(route.Mode, Is.EqualTo("department"));
        Assert.That(DesignMeasurements.Build(routed).Connections.Any(connection => connection.Id == "$station:WorkHall"), Is.True);
    }

    [Test]
    public void ForgedRouteAcrossSpaceCannotSatisfyRequiredConnection()
    {
        var project = Pair(); project.Stage = "circulation";
        project.Layout.Routes.Add(new() { Id = "fake", RequirementId = "$station:WorkHall", From = "Alpha/Work#1", To = "Alpha/Hall#1", Mode = "department", Cells = [new(8, 5), new(9, 5), new(10, 5)] });
        Assert.That(DesignMeasurements.RouteValid(project, project.Layout.Routes[0]), Is.False);
        Assert.That(DesignMeasurements.Build(project).Connections, Is.Empty);
        Assert.That(DesignGeometryValidator.Validate(project).Diagnostics.Any(diagnostic => diagnostic.Code == "required-route"), Is.True);
    }

    [Test]
    public void ServingCounterDoesNotProvideCrewMovement()
    {
        var project = Pair();
        project.Layout.Interfaces.Add(new() { Id = "counter", From = "Alpha/Work#1", To = "Alpha/Hall#1", Kind = "interaction", Type = "counter", Cells = [new(9, 5)], Width = 1 });
        var route = new DesignRoute { Id = "walking-through-counter", From = "Alpha/Work#1", To = "Alpha/Hall#1", Kind = "walking", Mode = "direct", Cells = [new(8, 5), new(9, 5), new(10, 5)] };
        Assert.That(DesignMeasurements.InterfaceTouches(project, project.Layout.Interfaces[0]), Is.True);
        Assert.That(DesignMeasurements.RouteValid(project, route), Is.False);
        route.Kind = "interaction";
        Assert.That(DesignMeasurements.RouteValid(project, route), Is.True);
    }

    [Test]
    public void InterfaceWidthIsObservedRatherThanTrusted()
    {
        var project = Pair(); project.Stage = "circulation";
        project.Definition.Connections[0].MinWidth = 3;
        project.Layout.Interfaces.Add(new() { Id = "door", From = "Alpha/Work#1", To = "Alpha/Hall#1", Type = "doorway", Cells = [new(9, 5)], Width = 3 });
        project.Layout.Routes.Add(new() { Id = "route", RequirementId = "$station:WorkHall", From = "Alpha/Work#1", To = "Alpha/Hall#1", Mode = "department", Cells = [new(8, 5), new(9, 5), new(10, 5)] });
        Assert.That(DesignMeasurements.Build(project).Connections.Single().Width, Is.EqualTo(1));
        var diagnostics = DesignGeometryValidator.Validate(project).Diagnostics;
        Assert.That(diagnostics.Any(diagnostic => diagnostic.Code == "interface-width"), Is.True);
        Assert.That(diagnostics.Any(diagnostic => diagnostic.Code == "required-route"), Is.True);
    }

    [Test]
    public void BroadMaintenanceAndDisconnectedNetworksAreDiagnosed()
    {
        var project = Pair(); project.Stage = "circulation";
        project.Layout.Maintenance = Floor(25, 20, 4, 4).Concat(Floor(35, 20, 2, 2)).ToList();
        var diagnostics = DesignGeometryValidator.Validate(project).Diagnostics;
        Assert.That(diagnostics.Any(diagnostic => diagnostic.Code == "passage-max-width"), Is.True);
        Assert.That(diagnostics.Any(diagnostic => diagnostic.Code == "network-connectivity"), Is.True);
    }

    [Test]
    public void ABlockedRingCornerCannotProduceABacktrackingCenterline()
    {
        var project = Pair(); project.Settings.Width = 64; project.Settings.Height = 48;
        project.Layout.Envelope = Floor(1, 1, 62, 46);
        foreach (var room in project.Layout.Rooms)
        { room.Cells = room.Cells.Select(cell => cell + new Cell(25, 25)).ToList(); room.Seed += new Cell(25, 25); }
        project.Layout.PublicWaypoints = [new(13, 13), new(21, 13), new(21, 21), new(13, 21)];
        project.Layout.ReservedSpace = Floor(22, 22, 2, 2);
        var routed = DesignRouting.Run(project);
        var path = routed.Layout.PublicBackbone;
        Assert.That(path, Is.Not.Empty);
        Assert.That(path[0], Is.EqualTo(path[^1]));
        Assert.That(path.Take(path.Count - 1).Distinct().Count(), Is.EqualTo(path.Count - 1));
        Assert.That(DesignGeometryValidator.Validate(routed).Diagnostics.Any(issue => issue.Code == "ring-degenerate"), Is.False);
    }

    [TestCase("publicHall")]
    [TestCase("maintenance")]
    public void AutomaticPassageCapacityIncludesItsRequiredOuterSealingWall(string network)
    {
        var project = new StationDesignProject { Settings = new() { Width = 32, Height = 24 } };
        var width = network == "publicHall" ? project.Settings.PublicWidth : project.Settings.MaintenanceWidth;
        var centers = DesignNetworkGeometry.LegalCenters(project, network);
        var floor = centers.SelectMany(center => new GridBounds(center.X - width / 2, center.Y - width / 2, width, width).Cells()).ToHashSet();
        Assert.That(floor, Is.Not.Empty);
        Assert.That(floor.All(cell => cell.X > 0 && cell.Y > 0 && cell.X < 31 && cell.Y < 23), Is.True);
        var sealedFootprint = DesignNetworkGeometry.Halo(floor);
        Assert.That(sealedFootprint.All(new GridBounds(0, 0, 32, 24).Contains), Is.True);
    }

    [Test]
    public void PlanningPathsCannotSucceedFromAnIllegalStartAlreadyTouchingTheTarget()
    {
        var project = Pair(); var blocked = new Cell(1, 1);
        var path = DesignNetworkGeometry.PathToFloor(project, blocked, [blocked], 1, [new(2, 1), new(3, 1)]);
        Assert.That(path, Is.Null);
    }

    [Test]
    public void EnvironmentAssignmentCannotClaimASealWithoutBoundaries()
    {
        var project = Pair();
        project.Layout.EnvironmentZones.Add(new() { Id = "zone", RoomId = "Alpha/Work#1", Cells = [.. project.Layout.Rooms[0].Cells], Pressure = "standard", Placement = "interior", Sealed = true });
        var measured = DesignMeasurements.Build(project).Rooms.Single(room => room.Path == "Alpha/Work");
        Assert.That(measured.Environment, Is.Not.Null);
        Assert.That(measured.Environment!.Sealed, Is.False);
        Assert.That(measured.SpaceFrontage, Is.Zero);
    }

    [Test]
    public void GlobalOwnerLinksMeasureOwnerEndpointsAfterPhysicalRoomRouting()
    {
        var project = Pair();
        var beta = new DepartmentProgram { Id = "Beta", Rooms = [ProgramTestData.Room("Hall", 9, 16, 100)] };
        project.Definition.Departments.Add(beta);
        project.Definition.Connections = [new() { Id = "Owners", From = "Alpha", To = ["Beta"], Modes = ["direct"] }];
        project.Layout.Rooms[1].Id = "Beta/Hall#1"; project.Layout.Rooms[1].DefinitionPath = "Beta/Hall"; project.Layout.Rooms[1].OwnerId = "Beta";
        project.Layout.Interfaces.Add(new() { Id = "door", From = "Alpha/Work#1", To = "Beta/Hall#1", Cells = [new(9, 5)], Width = 1 });
        project.Layout.Routes.Add(new() { Id = "owners", RequirementId = "$station:Owners", From = "Alpha/Work#1", To = "Beta/Hall#1", Mode = "direct", Cells = [new(8, 5), new(9, 5), new(10, 5)] });
        var measurement = DesignMeasurements.Build(project).Connections.Single();
        Assert.That(measurement.SourceInstance, Is.EqualTo("Alpha"));
        Assert.That(measurement.TargetInstance, Is.EqualTo("Beta"));
    }

    [Test]
    public void EditedNetworkMasksRemainEditableWithoutBeingRegenerated()
    {
        var project = Pair(); project.Layout.MaintenanceEdited = true; project.Layout.Maintenance = Floor(30, 20, 2, 2);
        var routed = DesignRouting.Run(project);
        Assert.That(routed.Layout.Maintenance, Is.EqualTo(project.Layout.Maintenance));
    }

    [Test]
    public void PaintedNetworksCanRebuildTheirEntrancesWithoutChangingFloor()
    {
        var project = DesignRouting.Run(Pair());
        project.Layout.PublicEdited = true; project.Layout.MaintenanceEdited = true;
        var publicFloor = project.Layout.PublicCorridors.ToList(); var maintenanceFloor = project.Layout.Maintenance.ToList();
        project.Layout.Interfaces.RemoveAll(port => port.To is "$public" or "$maintenance");
        var rerun = DesignRouting.Run(project);
        Assert.That(rerun.Layout.PublicCorridors, Is.EqualTo(publicFloor));
        Assert.That(rerun.Layout.Maintenance, Is.EqualTo(maintenanceFloor));
        Assert.That(rerun.Layout.Interfaces.Any(port => port.To == "$public" && DesignMeasurements.InterfaceTouches(rerun, port)), Is.True);
        Assert.That(rerun.Layout.Interfaces.Any(port => port.To == "$maintenance" && DesignMeasurements.InterfaceTouches(rerun, port)), Is.True);
    }

    [Test]
    public void EveryCellOfWideInterfaceMustTouchBothFloors()
    {
        var project = Pair();
        var port = new DesignInterface { Id = "badwide", From = "Alpha/Work#1", To = "Alpha/Hall#1", Cells = [new(9, 4), new(9, 5)], Width = 2 };
        Assert.That(DesignMeasurements.InterfaceTouches(project, port), Is.False);
    }

    [Test]
    public void EquipmentCannotSilentlyBlockAnAcceptedWalkingPath()
    {
        var project = Pair();
        project.Layout.Interfaces.Add(new() { Id = "door", From = "Alpha/Work#1", To = "Alpha/Hall#1", Cells = [new(9, 5)], Width = 1 });
        var route = new DesignRoute { From = "Alpha/Work#1", To = "Alpha/Hall#1", Mode = "department", Cells = [new(8, 5), new(9, 5), new(10, 5)] };
        Assert.That(DesignMeasurements.RouteValid(project, route), Is.True);
        project.Layout.Equipment.Add(new() { Id = "blocked", RoomId = "Alpha/Work#1", Placement = "floor", Position = new(8, 5), Footprint = [new(8, 5)] });
        Assert.That(DesignMeasurements.RouteValid(project, route), Is.False);
    }

    [Test]
    public void AUtilityRecordCannotClaimConstructionAcrossUnbuiltSpace()
    {
        var project = Pair();
        var path = new List<Cell> { new(8, 5), new(8, 4), new(9, 4), new(10, 4), new(10, 5) };
        var route = new DesignRoute { RequirementId = "cable", From = "Alpha/Work#1", To = "Alpha/Hall#1", Kind = "utility", Mode = "department", Cells = path };
        project.Layout.Utilities.Add(new() { RequirementId = "cable", From = route.From, To = route.To, Type = "cable", Cells = path });
        Assert.That(DesignMeasurements.RouteValid(project, route), Is.False);
    }

    [Test]
    public void SpaceFacingWindowInterfaceCountsAsActualFrontage()
    {
        var project = Pair();
        var room = project.Layout.Rooms[0];
        project.Layout.Interfaces.Add(new() { Id = "window", From = room.Id, To = "$space", Kind = "interaction", Type = "window", Cells = [new(4, 5)], Width = 1 });
        Assert.That(DesignMeasurements.SpaceFrontage(project, room), Is.EqualTo(1));
    }

    [Test]
    public void PinnedBoundaryEquipmentKeepsItsPhysicalInterfaceWhenRoutingAgain()
    {
        var project = Pair();
        project.Layout.Interfaces.Add(new() { Id = "pinned-door", From = "Alpha/Work#1", To = "$space", Type = "airlock", Cells = [new(4, 5)], Width = 1 });
        project.Layout.Equipment.Add(new() { Id = "door-entity", RoomId = "Alpha/Work#1", Placement = "spaceBoundary", Position = new(4, 5), Footprint = [new(4, 5)], Pinned = true, InterfaceId = "pinned-door" });
        var routed = DesignRouting.Run(project);
        Assert.That(routed.Layout.Interfaces.Any(port => port.Id == "pinned-door"), Is.True);
    }

    [Test]
    public void ReservedSpaceCannotBeHiddenUnderAClosedWall()
    {
        var project = Pair(); project.Layout.ReservedSpace = [new(20, 20)]; project.Layout.Hull = [new(20, 20)];
        Assert.That(DesignGeometryValidator.Validate(project).Diagnostics.Any(diagnostic => diagnostic.Code == "reserved-space-overlap"), Is.True);
    }

    [Test]
    public void EnclosedUnflooredCourtyardIsSpaceButMaterializedRoomFloorIsNot()
    {
        var project = Pair();
        var courtyard = Floor(25, 20, 3, 3).ToHashSet();
        project.Layout.Hull = courtyard.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Except(courtyard).Distinct().ToList();
        var space = DesignMeasurements.ExteriorSpace(project);
        Assert.That(courtyard.All(space.Contains), Is.True, "A closed Space-tile courtyard remains vacuum; it need not connect to the canvas edge.");
        Assert.That(project.Layout.Rooms.SelectMany(room => room.Cells).Any(space.Contains), Is.False);
    }

    [Test]
    public void InactiveRetainedFloorCannotDisappearFromFinalAcceptance()
    {
        var project = Pair(); project.Layout.Rooms[0].Enabled = false; project.Layout.Rooms[0].Orphaned = true;
        Assert.That(DesignGeometryValidator.Validate(project, true).Diagnostics.Any(diagnostic => diagnostic.Code == "orphaned-instance"), Is.True);
    }

    [Test]
    public void FinalHardGeometryAcceptanceRetainsUnfulfilledSoftPreferenceCost()
    {
        var project = Pair(); project.Stage = "validation"; project.Definition.Connections[0].Priority = "desired";
        DesignRouting.RebuildBoundaries(project);
        foreach (var room in project.Layout.Rooms) project.Layout.EnvironmentZones.Add(new() { Id = room.Id + "/zone", RoomId = room.Id, Cells = [.. room.Cells], Pressure = "standard", TemperatureKelvin = 293.15, PressureKpa = 101.325 });
        var report = DesignGeometryValidator.Validate(project, true);
        Assert.That(report.Success, Is.True, ProgramTestData.Errors(report.Diagnostics));
        Assert.That(report.Complete, Is.True);
        Assert.That(report.Pressure!.Complete, Is.False);
        Assert.That(report.Diagnostics.Any(diagnostic => diagnostic.Code == "soft-preferences"), Is.True);
        Assert.That(report.Pressure.Terms.Any(term => term.Metric == "connection" && term.Cost > 0), Is.True);
        project.Definition.Connections[0].Priority = "required";
        Assert.That(DesignGeometryValidator.Validate(project, true).Success, Is.False);
    }

    [Test]
    public void OpenCounterSharesASealedAirCompartmentInsteadOfPretendingToBeAirtight()
    {
        var project = Pair();
        project.Layout.Interfaces.Add(new() { Id = "counter", From = "Alpha/Work#1", To = "Alpha/Hall#1", Kind = "interaction", Type = "counter", Cells = [new(9, 5)], Width = 1 });
        DesignRouting.RebuildBoundaries(project);
        foreach (var room in project.Layout.Rooms) project.Layout.EnvironmentZones.Add(new() { Id = room.Id + "/zone", RoomId = room.Id, Cells = [.. room.Cells], Pressure = "standard", TemperatureKelvin = 293.15, PressureKpa = 101.325 });
        var observations = DesignMeasurements.Build(project);
        Assert.That(observations.Rooms.All(room => room.Environment!.Sealed), Is.True);
        Assert.That(DesignMeasurements.AirCompartments(project).Count(compartment => compartment.Cells.Overlaps(project.Layout.Rooms[0].Cells) && compartment.Cells.Overlaps(project.Layout.Rooms[1].Cells)), Is.EqualTo(1));
        project.Layout.EnvironmentZones[1].Pressure = "vacuum"; project.Layout.EnvironmentZones[1].PressureKpa = 0;
        Assert.That(DesignGeometryValidator.Validate(project).Diagnostics.Any(diagnostic => diagnostic.Code == "open-compartment-conflict"), Is.True);
    }

    [Test]
    public void UtilityOverlayKeepsTheAirtightWallUnderIt()
    {
        var project = Pair();
        project.Layout.Interfaces.Add(new() { Id = "pipe", From = "Alpha/Work#1", To = "Alpha/Hall#1", Kind = "utility", Type = "pipe", Cells = [new(9, 5)], Width = 1 });
        DesignRouting.RebuildBoundaries(project);
        Assert.That(project.Layout.Walls.Concat(project.Layout.Hull).Contains(new Cell(9, 5)), Is.True);
        Assert.That(DesignMeasurements.AirCompartments(project).Any(compartment => compartment.Cells.Overlaps(project.Layout.Rooms[0].Cells) && compartment.Cells.Overlaps(project.Layout.Rooms[1].Cells)), Is.False);
        Assert.That(DesignGeometryValidator.Validate(project).Diagnostics.Any(diagnostic => diagnostic.Code == "interface-overlap"), Is.False);
    }

    private static StationDesignProject DeepMaintenance(bool bypass)
    {
        var project = new StationDesignProject { Definition = ProgramTestData.Minimal(), Stage = "circulation", Settings = new() { Width = 48, Height = 36 } };
        var common = ProgramTestData.Room("Common", 9, 24, 100); common.AllowThroughTraffic = true;
        var deep = ProgramTestData.Room("Deep", 9, 16, 100); deep.Access.AllowPublicEntry = false; deep.Access.EntryOwners = ["_maintenance"]; deep.Access.RequireNetworkApproach = ["maintenance"];
        project.Definition.Maintenance = new() { Area = ProgramTestData.Area(16, 40, 300), Rooms = [common, deep] };
        project.Layout.Envelope = Floor(0, 0, 48, 36);
        project.Layout.Rooms =
        [
            new() { Id = "_maintenance/Common#1", DefinitionPath = "_maintenance/Common", OwnerId = "_maintenance", Instance = "1", Cells = Floor(5, 5, 6, 4) },
            new() { Id = "_maintenance/Deep#1", DefinitionPath = "_maintenance/Deep", OwnerId = "_maintenance", Instance = "1", Cells = Floor(12, 5, 4, 4) },
            new() { Id = "Alpha/Work#1", DefinitionPath = "Alpha/Work", OwnerId = "Alpha", Instance = "1", Cells = Floor(25, 5, 4, 4) }
        ];
        project.Layout.PublicCorridors = Floor(1, 5, 3, 3); project.Layout.Maintenance = Floor(5, 10, 11, 2);
        project.Layout.Interfaces =
        [
            new() { Id = "public", From = "_maintenance/Common#1", To = "$public", Cells = [new(4, 5)], Width = 1 },
            new() { Id = "common-maint", From = "_maintenance/Common#1", To = "$maintenance", Cells = [new(5, 9), new(6, 9)], Width = 2 },
            new() { Id = "deep-maint", From = "_maintenance/Deep#1", To = "$maintenance", Cells = [new(12, 9), new(13, 9)], Width = 2 }
        ];
        if (bypass) project.Layout.Interfaces.Add(new() { Id = "bypass", From = "_maintenance/Common#1", To = "_maintenance/Deep#1", Cells = [new(11, 5)], Width = 1 });
        return project;
    }

    [TestCase(true)]
    [TestCase(false)]
    public void DeepMaintenanceChecksIndirectCommonRoomBypasses(bool bypass)
    {
        var project = DeepMaintenance(bypass);
        var room = project.Layout.Rooms.Single(room => room.Id == "_maintenance/Deep#1");
        var observed = DesignMeasurements.VerifiedApproachNetworks(project, room, ["maintenance"]);
        Assert.That(observed.Contains("maintenance"), Is.EqualTo(!bypass));
        var state = DesignMeasurements.Build(project);
        Assert.That(PressureEvaluator.Evaluate(project.Definition, state, false).Diagnostics.Any(diagnostic => diagnostic.Code == "approach-network"), Is.EqualTo(bypass));
        Assert.That(ProgramYaml.Import(ProgramYaml.Export(project.Definition)).Success, Is.True);
    }

    [Test]
    public void RequiredNetworkApproachCannotPassWithoutAnObservation()
    {
        var definition = ProgramTestData.Minimal(); definition.Departments[0].Rooms[0].Access.RequireNetworkApproach = ["maintenance"];
        var state = new PressureState
        {
            Rooms = [new() { Path = "Alpha/Work", InstanceId = "1", Area = 40, Width = 8, Height = 5, EntryOwners = [], Contents = [], Environment = new() }],
            Departments = [new() { Path = "Alpha", Width = 8, Height = 5 }]
        };
        Assert.That(PressureEvaluator.Evaluate(definition, state, true).Diagnostics.Any(diagnostic => diagnostic.Code == "approach-network-measurement"), Is.True);
        state.Rooms[0].VerifiedApproachNetworks = ["maintenance"];
        Assert.That(PressureEvaluator.Evaluate(definition, state, true).Success, Is.True);
        definition.Departments[0].Rooms[0].Access.RequireNetworkApproach = ["mail"];
        Assert.That(RequirementCompiler.Compile(definition).Success, Is.False);
    }

    [Test]
    public void CrewTubeIntoAnAdjacentCommonRoomCannotBypassDeepMaintenanceApproach()
    {
        var project = DeepMaintenance(false);
        var inner = ProgramTestData.Room("Inner", 9, 16, 100);
        project.Definition.Maintenance.Rooms.Add(inner);
        var innerInstance = new DesignRoom { Id = "_maintenance/Inner#1", DefinitionPath = "_maintenance/Inner", OwnerId = "_maintenance", Instance = "1", Cells = Floor(17, 5, 4, 4), Seed = new(19, 6) };
        project.Layout.Rooms.Add(innerInstance);
        project.Layout.Interfaces.Add(new() { Id = "inner-deep", From = "_maintenance/Deep#1", To = innerInstance.Id, Cells = [new(16, 5)], Width = 1 });
        project.Layout.Walls.AddRange(Floor(21, 6, 4, 1));
        var deep = project.Layout.Rooms.Single(room => room.Id == "_maintenance/Deep#1");
        Assert.That(DesignMeasurements.VerifiedApproachNetworks(project, deep, ["maintenance"]), Does.Contain("maintenance"));
        var requirement = new ConnectionRequirement { Id = "Tube", From = "Alpha/Work", To = ["_maintenance/Inner"], Kind = "walking", Modes = ["disposalShortcut"] };
        project.Definition.Connections.Add(requirement);
        var compiled = RequirementCompiler.Compile(project.Definition).Connections.Single(connection => connection.AuthoredId == "Tube");
        var foreign = project.Layout.Rooms.Single(room => room.Id == "Alpha/Work#1");
        var route = DesignTransport.TryBuild(project, foreign, innerInstance, compiled, "disposalShortcut");
        Assert.That(route, Is.Not.Null, "The verified underfloor line can enter an ordinary same-owner room, which makes the indirect bypass important.");
        project.Layout.Routes.Add(route!);
        Assert.That(DesignMeasurements.VerifiedApproachNetworks(project, deep, ["maintenance"]), Does.Not.Contain("maintenance"));
    }

    [Test]
    public void OffsetFootprintMayExtendAwayFromItsEntityOriginButMustMatchItsProfile()
    {
        var project = Pair();
        project.Settings.PrototypeFootprints["OffsetMachine"] = new() { Width = 2, Height = 1, OffsetX = 1 };
        var entity = new DesignEntity { Prototype = "OffsetMachine", RoomId = "Alpha/Work#1", Position = new(5, 5), Placement = "floor", Footprint = [new(6, 5), new(7, 5)] };
        Assert.That(DesignMeasurements.EntityPlacementValid(project, project.Layout.Rooms[0], entity), Is.True);
        entity.Footprint.RemoveAt(1);
        Assert.That(DesignMeasurements.EntityPlacementValid(project, project.Layout.Rooms[0], entity), Is.False);
    }

    [Test]
    public void DesignHashIgnoresInstanceListOrderingWithoutMutatingTheProject()
    {
        var project = Pair();
        var original = JsonSerializer.Serialize(project, ProgramJson.Options);
        var first = DesignGeometryValidator.Validate(project).Hash;
        Assert.That(JsonSerializer.Serialize(project, ProgramJson.Options), Is.EqualTo(original));
        project.Layout.Rooms.Reverse();
        var reordered = JsonSerializer.Serialize(project, ProgramJson.Options);
        Assert.That(DesignGeometryValidator.Validate(project).Hash, Is.EqualTo(first));
        Assert.That(JsonSerializer.Serialize(project, ProgramJson.Options), Is.EqualTo(reordered), "Validation must not reorder the caller's room list.");
        var room = project.Layout.Rooms.Single(room => room.Id == "Alpha/Work#1");
        room.Cells = room.Cells.Select(cell => cell + new Cell(-1, 0)).ToList();
        Assert.That(DesignGeometryValidator.Validate(project).Hash, Is.Not.EqualTo(first), "Actual floor movement must still change the design hash.");
    }

    [Test]
    public void StandardCatalogCirculationProducesPhysicalRequiredRoutes()
    {
        var project = new StationDesignProject { Definition = StationProgramDefaults.Create() };
        project = RoomGrowth.Run(DesignPlacement.Run(InstanceSelection.Resolve(project)));
        project = DesignRouting.Run(project);
        Assert.That(project.Report!.Diagnostics.Where(diagnostic => diagnostic.Severity == "error"), Is.Empty, "The full-budget automatic circulation stage must complete its actual construction goals.");
        var report = DesignGeometryValidator.Validate(project);
        TestContext.Progress.WriteLine("Default circulation: " + string.Join(", ", report.Metrics.Select(pair => pair.Key + "=" + pair.Value)));
        var errors = report.Diagnostics.Where(diagnostic => diagnostic.Severity == "error").ToList();
        TestContext.Progress.WriteLine(ProgramTestData.Errors(errors.Take(50)));
        Assert.That(errors, Is.Empty, ProgramTestData.Errors(errors.Take(50)));
        var definitions = RequirementCompiler.Rooms(project.Definition).ToDictionary(item => item.Path, item => item.Room, StringComparer.Ordinal);
        var interfaces = DesignMeasurements.ValidInterfaces(project);
        foreach (var cluster in DesignClusterGeometry.Clusters(project).Where(group => group[0].OwnerId != RequirementCompiler.MaintenanceOwner && group.Any(room => definitions[room.DefinitionPath].Access.AllowPublicEntry && (room.ParentInstanceId.Length == 0 || definitions[room.DefinitionPath].Nesting.AllowExternalEntry))))
        {
            var ids = cluster.Select(room => room.Id).ToHashSet(StringComparer.Ordinal);
            Assert.That(interfaces.Any(port => port.Kind == "walking" && (port.From == "$public" && ids.Contains(port.To) || port.To == "$public" && ids.Contains(port.From))), Is.True, cluster[0].Id + " needs an actual public gateway, not only a clean later report.");
        }
        var medical = project.Layout.Rooms.Where(room => room.OwnerId == "Medical").Select(room => room.Id).ToHashSet(StringComparer.Ordinal);
        Assert.That(DesignMeasurements.ValidInterfaces(project).Any(port => port.Kind == "walking" && (medical.Contains(port.From) && port.To == "$maintenance" || medical.Contains(port.To) && port.From == "$maintenance")), Is.True, "Public branches must leave Medical a real entrance to the connected maintenance network.");
        foreach (var required in new[] { "$station:MaintenanceServiceMedical", "$station:MaintenanceMedicalScience" })
        {
            var allowedModes = RequirementCompiler.Compile(project.Definition).Connections.Single(connection => connection.Id == required).Modes;
            Assert.That(project.Layout.Routes.Any(route => route.RequirementId == required && allowedModes.Contains(route.Mode) && DesignMeasurements.RouteValid(project, route)), Is.True, required + " needs a physically valid authored route alternative.");
            Assert.That(DesignMeasurements.Build(project).Connections.Any(connection => connection.Id == required && allowedModes.Contains(connection.Mode)), Is.True, required + " needs independently observed route evidence.");
        }
    }

    [Test]
    public void StandardCatalogContentsPassesMandatoryPhysicalAndAirChecks()
    {
        var project = new StationDesignProject { Definition = StationProgramDefaults.Create() };
        project = DesignContents.Run(DesignRouting.Run(RoomGrowth.Run(DesignPlacement.Run(InstanceSelection.Resolve(project)))));
        var generationErrors = project.Report!.Diagnostics.Where(diagnostic => diagnostic.Severity == "error").ToList();
        var report = DesignGeometryValidator.Validate(project);
        var errors = report.Diagnostics.Where(diagnostic => diagnostic.Severity == "error").ToList();
        var pending = RequirementCompiler.Compile(project.Definition).Connections.Where(connection => DesignMeasurements.NativeUtilityNetworkPending(project, connection)).Select(connection => connection.Id).ToHashSet(StringComparer.Ordinal);
        var mandatory = PressureEvaluator.Evaluate(project.Definition, DesignMeasurements.Build(project), final: true);
        var dueErrors = mandatory.Diagnostics.Where(diagnostic => diagnostic.Severity == "error" && !pending.Contains(diagnostic.Path)).ToList();
        TestContext.Progress.WriteLine("Default contents: " + string.Join(", ", report.Metrics.Select(pair => pair.Key + "=" + pair.Value)));
        Assert.That(generationErrors.Concat(errors).Concat(dueErrors), Is.Empty, ProgramTestData.Errors(generationErrors.Concat(errors).Concat(dueErrors).Take(50)));
        Assert.That(report.Diagnostics.Any(issue => issue.Code == "native-utility-pending"), Is.True);
        Assert.That(DesignMeasurements.Build(project).Connections.Any(connection => pending.Contains(connection.Id)), Is.False, "A planning line cannot fulfill the pending native circuit.");
    }
}
