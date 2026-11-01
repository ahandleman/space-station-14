using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
public sealed class DesignNestedGeometryTests
{
    private static List<Cell> Floor(int x, int y, int width, int height) => new GridBounds(x, y, width, height).Cells().ToList();

    private static StationDesignProject Nested()
    {
        var definition = ProgramTestData.Minimal();
        var parent = ProgramTestData.Room("Suite", 25, 95, 200);
        parent.AllowThroughTraffic = false;
        parent.Shape.MinFillRatio = .2;
        parent.Rooms = [ProgramTestData.Room("Booth", 4, 9, 20)];
        parent.Rooms[0].Access.AllowPublicEntry = false;
        parent.Rooms[0].Access.AllowMaintenanceEntry = false;
        definition.Departments[0].Rooms = [parent];
        definition.Departments[0].Area = ProgramTestData.Area(25, 104, 400);
        var complex = Floor(5, 5, 12, 10); var hole = Floor(7, 7, 5, 5).ToHashSet();
        return new()
        {
            Definition = definition, Stage = "growth", Settings = new() { Width = 42, Height = 32, Packing = new() { MaintenanceCoverage = 0, MaintenanceConnectionsPerCluster = 1 } },
            Layout = new()
            {
                Envelope = Floor(1, 1, 40, 30),
                Rooms =
                [
                    new() { Id = "Alpha/Suite#1", DefinitionPath = "Alpha/Suite", OwnerId = "Alpha", Cells = complex.Except(hole).ToList(), ComplexReservation = complex, HasSeed = true, Seed = new(6, 6) },
                    new() { Id = "Alpha/Suite#1/Booth#1", DefinitionPath = "Alpha/Suite/Booth", ParentInstanceId = "Alpha/Suite#1", OwnerId = "Alpha", Cells = Floor(8, 8, 3, 3), HasSeed = true, Seed = new(9, 9) }
                ]
            }
        };
    }

    [Test]
    public void NestedDoorsReachParentWithoutPermittingForeignTransit()
    {
        var project = DesignRouting.Run(Nested());
        var child = project.Layout.Rooms[1];
        Assert.That(project.Layout.Interfaces.Any(port => port.Kind == "walking" && new[] { port.From, port.To }.ToHashSet().SetEquals([child.Id, child.ParentInstanceId])), Is.True);
        Assert.That(project.Layout.Interfaces.Any(port => (port.From == child.Id || port.To == child.Id) && (port.From.StartsWith('$') || port.To.StartsWith('$'))), Is.False);
        var measured = DesignMeasurements.Build(project);
        var parent = measured.Rooms.Single(room => room.Path == "Alpha/Suite");
        var booth = measured.Rooms.Single(room => room.Path == "Alpha/Suite/Booth");
        Assert.That(parent.Area, Is.EqualTo(95));
        Assert.That(parent.SubtreeFloorArea, Is.EqualTo(104));
        Assert.That(parent.ComplexArea, Is.GreaterThanOrEqualTo(104));
        Assert.That(RoomHierarchy.MeasurementKey(booth), Is.EqualTo(child.Id));
        Assert.That(booth.ContainedInParent, Is.True);
        Assert.That(booth.ParentConnectionVerified, Is.True);
        Assert.That(booth.ParentApproachVerified, Is.True);
    }

    [Test]
    public void PlanningReservationCannotProveAnOpenChildEnclosure()
    {
        var project = DesignRouting.Run(Nested());
        var removed = new Cell(11, 9);
        project.Layout.Walls.Remove(removed); project.Layout.Hull.Remove(removed); project.Layout.Windows.Remove(removed);
        Assert.That(DesignGeometryValidator.Validate(project).Diagnostics.Any(issue => issue.Code == "nested-enclosure"), Is.True);
        Assert.That(DesignMeasurements.Build(project).Rooms.Single(room => room.Path.EndsWith("/Booth")).ContainedInParent, Is.False);
    }

    [Test]
    public void PublicCutThroughParentCannotBypassItsFloor()
    {
        var project = DesignRouting.Run(Nested());
        var parent = project.Layout.Rooms[0]; var child = project.Layout.Rooms[1];
        var cut = Floor(12, 8, 9, 2).ToHashSet(); parent.Cells.RemoveAll(cut.Contains);
        project.Layout.PublicCorridors.AddRange(cut);
        var door = new Cell(11, 8); project.Layout.Walls.Remove(door); project.Layout.Hull.Remove(door); project.Layout.Windows.Remove(door);
        project.Layout.Interfaces.Add(new() { Id = "forbidden-side-door", From = child.Id, To = "$public", Cells = [door], Width = 1 });
        var diagnostics = DesignGeometryValidator.Validate(project).Diagnostics;
        Assert.That(diagnostics.Any(issue => issue.Code == "nested-approach-bypass"), Is.True);
        Assert.That(DesignMeasurements.Build(project).Rooms.Single(room => room.Path.EndsWith("/Booth")).ParentApproachVerified, Is.False);
    }

    [Test]
    public void MissingPhysicalParentDoorRemainsUnverified()
    {
        var project = DesignRouting.Run(Nested());
        project.Layout.Interfaces.RemoveAll(port => port.From == project.Layout.Rooms[1].Id || port.To == project.Layout.Rooms[1].Id);
        Assert.That(DesignMeasurements.Build(project).Rooms.Single(room => room.Path.EndsWith("/Booth")).ParentConnectionVerified, Is.False);
        Assert.That(DesignGeometryValidator.Validate(project).Diagnostics.Any(issue => issue.Code == "nested-parent-connection"), Is.True);
    }

    [Test]
    public void InternalChildMayShareTheParentComplexOuterHull()
    {
        var project = Nested();
        project.Layout.Rooms[0].Cells = Floor(5, 5, 6, 8);
        project.Layout.Rooms[1].Cells = Floor(12, 6, 3, 3);
        project.Layout.Rooms[0].ComplexReservation = project.Layout.Rooms[0].Cells.Concat(project.Layout.Rooms[1].Cells).Concat(Floor(11, 6, 1, 3)).ToList();
        var routed = DesignRouting.Run(project);
        Assert.That(DesignGeometryValidator.Validate(routed).Diagnostics.Any(issue => issue.Code == "nested-internal-placement"), Is.False);
        var observation = DesignMeasurements.Build(routed).Rooms.Single(room => room.Path.EndsWith("/Booth"));
        Assert.That(observation.ParentConnectionVerified, Is.True);
        Assert.That(observation.ContainedInParent, Is.True);
    }

    [Test]
    public void PhysicalProofsRemainPendingBeforeCirculation()
    {
        var child = DesignMeasurements.Build(Nested()).Rooms.Single(room => room.Path.EndsWith("/Booth"));
        Assert.That(child.ContainedInParent, Is.Null);
        Assert.That(child.ParentConnectionVerified, Is.Null);
        Assert.That(child.ParentApproachVerified, Is.Null);
    }

    [Test]
    public void EmptyReservationCannotConnectADetachedInternalChild()
    {
        var project = Nested();
        project.Layout.Rooms[0].Cells = Floor(5, 5, 4, 4);
        project.Layout.Rooms[0].ComplexReservation = Floor(5, 5, 20, 12);
        project.Layout.Rooms[1].Cells = Floor(20, 12, 3, 3);
        var routed = DesignRouting.Run(project);
        Assert.That(DesignGeometryValidator.Validate(routed).Diagnostics.Any(issue => issue.Code == "nested-internal-placement"), Is.True);
        Assert.That(DesignMeasurements.Build(routed).Rooms.Single(room => room.Path.EndsWith("/Booth")).ContainedInParent, Is.False);
    }

    [Test]
    public void SameOwnerDisconnectedRoomsBecomeSeparatePhysicalClusters()
    {
        var project = Nested(); project.Layout.Rooms[1].ParentInstanceId = "";
        Assert.That(DesignClusterGeometry.Clusters(project), Has.Count.EqualTo(2));
    }

    [Test]
    public void MissingOptionalGatewayRemainsAMeasuredWarningAtLaterStages()
    {
        var project = DesignContents.Run(DesignRouting.Run(Nested()));
        foreach (var port in project.Layout.Interfaces.Where(port => port.From == "$public" || port.To == "$public").ToList())
        { project.Layout.Walls.AddRange(port.Cells); project.Layout.Interfaces.Remove(port); }
        project.Layout.PublicCorridors.Clear();
        foreach (var stage in new[] { "circulation", "contents", "validation" })
        {
            project.Stage = stage;
            var report = DesignGeometryValidator.Validate(project, stage == "validation");
            Assert.That(report.Diagnostics.Any(issue => issue.Code == "network-gateway" && issue.Message.Contains("public") && issue.Severity == "warning"), Is.True, stage);
            Assert.That(report.Success, Is.True, ProgramTestData.Errors(report.Diagnostics.Where(issue => issue.Severity == "error")));
        }
        project.Definition.Connections.Add(new() { Id = "AuthoredPublicRoute", From = "Alpha/Suite", To = ["Alpha/Suite/Booth"], Modes = ["publicHall"] });
        Assert.That(DesignGeometryValidator.Validate(project, true).Diagnostics.Any(issue => issue.Code == "required-route" && issue.Severity == "error"), Is.True, "An authored Required public route remains a hard physical failure.");
    }

    [Test]
    public void ActualWrapAddsConnectedNarrowFloorAndReportsObservedCoverage()
    {
        var plain = Nested(); plain.Settings.Packing.MaintenanceCoverage = 0;
        var wrapped = ProgramJson.Clone(plain); wrapped.Settings.Packing.MaintenanceCoverage = .65;
        var original = DesignRouting.Run(plain); var result = DesignRouting.Run(wrapped);
        Assert.That(result.Layout.Maintenance.Count, Is.GreaterThan(original.Layout.Maintenance.Count));
        Assert.That(DesignMeasurements.Components(result.Layout.Maintenance.ToHashSet()), Is.EqualTo(1));
        var maintenance = result.Layout.Maintenance.ToHashSet();
        Assert.That(maintenance.Any(origin => Enumerable.Range(0, 4).All(y => Enumerable.Range(0, 4).All(x => maintenance.Contains(origin + new Cell(x, y))))), Is.False);
        var observation = DesignClusterGeometry.MeasureMaintenance(result).Single();
        Assert.That(observation.CoveredEdges, Is.GreaterThan(0));
        Assert.That(observation.Coverage, Is.GreaterThan(DesignClusterGeometry.MeasureMaintenance(original).Single().Coverage));
        result.Layout.Maintenance.Clear();
        Assert.That(DesignClusterGeometry.MeasureMaintenance(result).Single().CoveredEdges, Is.Zero);
    }
}
