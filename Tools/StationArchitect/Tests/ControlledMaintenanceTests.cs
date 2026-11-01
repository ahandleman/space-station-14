using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
public sealed class ControlledMaintenanceTests
{
    private static List<Cell> Floor(int x, int y, int width, int height) => new GridBounds(x, y, width, height).Cells().ToList();

    private static StationDesignProject Fixture()
    {
        var definition = ProgramTestData.Minimal();
        var hall = ProgramTestData.Room("Hall", 9, 16, 100); hall.Role = "filler"; hall.AllowThroughTraffic = true;
        definition.Departments[0].Rooms = [hall];
        var beta = ProgramJson.Clone(definition.Departments[0]); beta.Id = "Beta"; definition.Departments.Add(beta);
        definition.Connections = [new() { Id = "Link", From = "Alpha/Hall", To = ["Beta/Hall"], Modes = ["controlledMaintenance"] }];
        definition.Maintenance.Connectivity = new() { Policy = "controlled", MaxComponents = 2, MinTransitionsPerComponent = 1, TransitionAccess = "maintenance" };
        var project = new StationDesignProject { Definition = definition, Stage = "circulation", Settings = new() { Width = 40, Height = 28 } };
        project.Layout.PublicCorridors = Floor(5, 15, 24, 3);
        project.Layout.Maintenance = Floor(5, 10, 2, 4).Concat(Floor(25, 10, 2, 4)).ToList();
        foreach (var (owner, x) in new[] { ("Alpha", 5), ("Beta", 25) })
        {
            project.Layout.Rooms.Add(new() { Id = owner + "/Hall#1", OwnerId = owner, DefinitionPath = owner + "/Hall", Cells = Floor(x, 5, 4, 4) });
            project.Layout.Interfaces.Add(new() { Id = owner + ":entry", From = owner + "/Hall#1", To = "$maintenance", Width = 2, Cells = Floor(x, 9, 2, 1), FromApproach = new(x, 8), ToApproach = new(x, 10), Access = owner });
            project.Layout.Interfaces.Add(new() { Id = owner + ":transition", From = "$maintenance", To = "$public", Width = 2, Cells = Floor(x, 14, 2, 1), FromApproach = new(x, 13), ToApproach = new(x, 15), Access = "maintenance" });
        }
        return project;
    }

    [Test]
    public void ExplicitMixedModeUsesRealMaintenanceLegsWithoutRedefiningStrictMode()
    {
        var project = Fixture(); var from = project.Layout.Rooms[0]; var to = project.Layout.Rooms[1];
        var path = DesignControlledMaintenance.FindRoute(project, from, to);
        Assert.That(path, Is.Not.Null);
        var route = new DesignRoute { Id = "mixed", RequirementId = "$station:Link", From = from.Id, To = to.Id, Mode = "controlledMaintenance", Cells = path! };
        Assert.That(DesignMeasurements.RouteValid(project, route), Is.True);
        Assert.That(path!.Any(project.Layout.PublicCorridors.Contains), Is.True);
        route.Mode = "maintenance";
        Assert.That(DesignMeasurements.RouteValid(project, route), Is.False);
    }

    [Test]
    public void MissingPhysicalTransitionCannotBeReplacedByDeclaredRouteCells()
    {
        var project = Fixture(); var path = DesignControlledMaintenance.FindRoute(project, project.Layout.Rooms[0], project.Layout.Rooms[1])!;
        project.Layout.Interfaces.RemoveAll(port => port.Id == "Beta:transition");
        var route = new DesignRoute { From = project.Layout.Rooms[0].Id, To = project.Layout.Rooms[1].Id, Mode = "controlledMaintenance", Cells = path };
        Assert.That(DesignControlledMaintenance.FindRoute(project, project.Layout.Rooms[0], project.Layout.Rooms[1]), Is.Null);
        Assert.That(DesignMeasurements.RouteValid(project, route), Is.False);
    }

    [Test]
    public void ComponentsStayDistinctAndNeedActualConfiguredStationAccess()
    {
        var project = Fixture();
        var observations = DesignMaintenanceConnectivity.Measure(project);
        Assert.That(observations, Has.Count.EqualTo(2));
        Assert.That(observations.All(component => component.Transitions.Count == 1), Is.True);
        Assert.That(DesignGeometryValidator.Validate(project).Diagnostics.Any(issue => issue.Code is "network-connectivity" or "maintenance-station-access"), Is.False);
        project.Definition.Maintenance.Connectivity.Policy = "continuous";
        Assert.That(DesignGeometryValidator.Validate(project).Diagnostics.Any(issue => issue.Code == "network-connectivity"), Is.True);
        project.Definition.Maintenance.Connectivity.Policy = "controlled";
        project.Layout.Interfaces.Single(port => port.Id == "Beta:transition").Access = "wrong-access";
        Assert.That(DesignGeometryValidator.Validate(project).Diagnostics.Any(issue => issue.Code == "maintenance-station-access"), Is.True);
    }

    [Test]
    public void AutomaticMaintenanceFaceCanJoinAnExistingComponentWithoutChangingTheCap()
    {
        var definition = ProgramTestData.Minimal();
        var barrierDefinition = ProgramTestData.Room("Barrier"); barrierDefinition.AllowThroughTraffic = true;
        definition.Departments[0].Rooms.Add(barrierDefinition);
        definition.Maintenance.Connectivity = new() { Policy = "controlled", MaxComponents = 1, MinTransitionsPerComponent = 1 };
        var project = new StationDesignProject { Definition = definition, Settings = new() { Width = 64, Height = 40, Topology = "spine" } };
        project.Layout.Rooms = [new() { Id = "Alpha/Barrier#1", DefinitionPath = "Alpha/Barrier", GrowthReservation = Floor(28, 0, 4, 40) },
            new() { Id = "Alpha/Work#1", DefinitionPath = "Alpha/Work", GrowthReservation = Floor(12, 20, 6, 6) }];
        project.Layout.PublicWaypoints = [new(5, 5), new(20, 5)];
        project.Layout.CirculationReservations = [
            new() { Id = "barrier-public", RoomId = "Alpha/Barrier#1", Network = "publicHall", Width = 3, Outward = new(-1, 0), BoundaryCells = Floor(27, 5, 1, 3), ApproachCells = Floor(24, 5, 3, 3) },
            new() { Id = "barrier-maint", RoomId = "Alpha/Barrier#1", Network = "maintenance", Width = 2, Outward = new(1, 0), BoundaryCells = Floor(32, 14, 1, 2), ApproachCells = Floor(33, 14, 2, 2) },
            new() { Id = "work-maint", RoomId = "Alpha/Work#1", Network = "maintenance", Width = 2, Outward = new(0, 1), BoundaryCells = Floor(13, 26, 2, 1), ApproachCells = Floor(13, 27, 2, 2) }
        ];
        var original = project.Layout.CirculationReservations.Single(guide => guide.Id == "barrier-maint");
        var failed = DesignCirculationPlanning.Connect(project);
        Assert.That(failed.Complete, Is.False);
        Assert.That(failed.Diagnostics.Any(issue => issue.Code == "planning-service-components"), Is.True);
        Assert.That(project.Layout.CirculationReservations.Single(guide => guide.Id == original.Id).Outward, Is.EqualTo(new Cell(1, 0)));
        var alternative = new DesignCirculationReservation { Id = original.Id, RoomId = original.RoomId, Network = original.Network, Width = 2, Outward = new(-1, 0), BoundaryCells = Floor(27, 14, 1, 2), ApproachCells = Floor(25, 14, 2, 2) };
        var result = DesignCirculationPlanning.Connect(project, alternatives: guide => guide.Id == original.Id ? [alternative] : []);
        Assert.That(result.Complete, Is.True, ProgramTestData.Errors(result.Diagnostics));
        Assert.That(project.Definition.Maintenance.Connectivity.MaxComponents, Is.EqualTo(1));
        Assert.That(project.Layout.CirculationReservations.Single(guide => guide.Id == original.Id).Outward, Is.EqualTo(new Cell(-1, 0)));
        Assert.That(project.Layout.Maintenance, Is.Empty, "Planning choices do not fabricate physical passages.");
    }

    [Test]
    public void OptionalSecondaryIslandNeedsActualOrPhysicallyBuildableStationAccess()
    {
        var project = Fixture();
        project.Layout.Maintenance = Floor(5, 10, 2, 4);
        var entrance = project.Layout.Interfaces.Single(port => port.Id == "Beta:entry");
        project.Layout.Interfaces.RemoveAll(port => port.Id.StartsWith("Beta:", StringComparison.Ordinal));
        var before = System.Text.Json.JsonSerializer.Serialize(project, ProgramJson.Options);
        var unreachable = project.Layout.Maintenance.Concat(Floor(25, 10, 2, 2));
        Assert.That(DesignMaintenanceConnectivity.AdditionalComponentCanReachStation(project, unreachable, entrance), Is.False,
            "A private filler door does not give a disconnected island station access.");
        var adjacentToPublic = project.Layout.Maintenance.Concat(Floor(25, 10, 2, 4));
        Assert.That(DesignMaintenanceConnectivity.AdditionalComponentCanReachStation(project, adjacentToPublic, entrance), Is.True,
            "A full-width actual shared public wall can receive its real controlled transition.");
        Assert.That(System.Text.Json.JsonSerializer.Serialize(project, ProgramJson.Options), Is.EqualTo(before), "Preview preserves every caller layer and interface.");
    }

    [Test]
    public void RuntimeCanBuildOnlyRealSharedWallTransitions()
    {
        var project = Fixture(); project.Layout.Interfaces.RemoveAll(port => port.To == "$public");
        var diagnostics = new List<ProgramDiagnostic>(); DesignMaintenanceConnectivity.AddTransitions(project, diagnostics);
        Assert.That(diagnostics, Is.Empty);
        var transitions = project.Layout.Interfaces.Where(port => port.To is "$maintenance" or "$exterior-maintenance" && port.From == "$public").ToList();
        Assert.That(transitions, Has.Count.EqualTo(2));
        Assert.That(transitions.All(port => DesignMeasurements.InterfaceTouches(project, port)), Is.True);
        Assert.That(DesignMaintenanceConnectivity.Measure(project).All(component => component.Transitions.Count >= 1), Is.True);
    }
}
