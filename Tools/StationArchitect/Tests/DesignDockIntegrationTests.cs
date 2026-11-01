using System.Text.Json;
using Content.StationArchitect;
using NUnit.Framework;
using YamlDotNet.RepresentationModel;

namespace StationArchitect.Tests;

[TestFixture]
public sealed class DesignDockIntegrationTests
{
    private static StationDesignProject PlannedDock()
    {
        var definition = ProgramTestData.Minimal();
        var roomDefinition = definition.Departments[0].Rooms[0];
        roomDefinition.Contents = [new() { Id = "NativeDock", Prototypes = ["AirlockExternalGlassShuttleArrivals"], Placement = "spaceBoundary", InterfaceType = "dockingPort" }];
        roomDefinition.Exterior.Docks = [new() { Id = "Shuttle", ContentId = "NativeDock", BerthWidth = 7, BerthDepth = 5, ApproachLength = 8, Directions = ["east"] }];
        var project = InstanceSelection.Resolve(new() { Definition = definition, Settings = new() { Width = 64, Height = 48 } });
        var room = project.Layout.Rooms.Single();
        room.Cells = new GridBounds(8, 8, 8, 6).Cells().ToList(); room.Bounds = new(8, 8, 8, 6); room.Seed = new(9, 10); room.HasSeed = true;
        project.Layout.Envelope = new GridBounds(1, 1, 62, 46).Cells().ToList();
        var floor = room.Cells.ToHashSet();
        project.Layout.Walls = floor.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Except(floor).Distinct().ToList();
        project.Layout.Docks = [DesignDockGeometry.Create(room.Id, roomDefinition.Exterior.Docks[0], 1, new(16, 10), new(1, 0), new(0, 0, 64, 48))];
        project.Stage = "circulation";
        return project;
    }

    [Test]
    public void ContentsMaterializesOnlyTheExactAlignedNativeDock()
    {
        var project = DesignContents.Run(PlannedDock());
        var dock = project.Layout.Docks.Single(); var entity = project.Layout.Equipment.Single();
        Assert.That(entity.Id, Is.EqualTo(dock.EntityId));
        Assert.That(entity.Position, Is.EqualTo(dock.BoundaryCells.Single()));
        Assert.That(entity.Rotation, Is.EqualTo(90));
        Assert.That(entity.Prototype, Is.EqualTo("AirlockExternalGlassShuttleArrivals"));
        var port = project.Layout.Interfaces.Single(port => port.Id == dock.InterfaceId);
        Assert.That(port.Type, Is.EqualTo("dockingPort"));
        Assert.That(port.To, Is.EqualTo("$space"));
        Assert.That(DesignDockGeometry.ValidateDock(project, dock, true), Is.Empty);
        Assert.That(DesignMeasurements.Build(project).Rooms.Single().Docks!.Single().Count, Is.EqualTo(1));
    }

    [Test]
    public void AnArbitrarySpaceBoundaryDoorCannotSubstituteForTheReservedDock()
    {
        var project = DesignContents.Run(PlannedDock());
        var dock = project.Layout.Docks.Single(); var entity = project.Layout.Equipment.Single();
        var port = project.Layout.Interfaces.Single();
        entity.Position = new(11, 14); entity.Footprint = [entity.Position]; entity.Rotation = 180;
        port.Cells = [entity.Position]; port.FromApproach = new(11, 13); port.ToApproach = new(11, 15);
        Assert.That(DesignMeasurements.EntityPlacementValid(project, project.Layout.Rooms.Single(), entity), Is.False);
        Assert.That(DesignDockGeometry.Measure(project, project.Layout.Rooms.Single()).Single().Count, Is.Zero);
        Assert.That(DesignDockGeometry.ValidateDock(project, dock, true).Any(issue => issue.Code == "dock-hardware"), Is.True);
    }

    [Test]
    public void ADeclaredDockWithoutAPlanCannotGetAnUnrelatedExteriorDoor()
    {
        var project = PlannedDock(); project.Layout.Docks.Clear();
        var result = DesignContents.Run(project);
        Assert.That(result.Layout.Equipment, Is.Empty);
        Assert.That(result.Report!.Diagnostics.Any(issue => issue.Code == "dock-plan-missing"), Is.True);
    }

    [TestCase("public")]
    [TestCase("maintenance")]
    [TestCase("wall")]
    [TestCase("window")]
    [TestCase("gear")]
    [TestCase("solarFloor")]
    public void ActualDockSpaceRejectsMaterializedObstructions(string layer)
    {
        var project = PlannedDock(); var dock = project.Layout.Docks.Single(); var cell = dock.BerthCells.Last();
        switch (layer)
        {
            case "public": project.Layout.PublicCorridors.Add(cell); break;
            case "maintenance": project.Layout.Maintenance.Add(cell); break;
            case "wall": project.Layout.Hull.Add(cell); break;
            case "window": project.Layout.Windows.Add(cell); break;
            case "gear": project.Layout.Equipment.Add(new() { Id = "obstruction", RoomId = project.Layout.Rooms[0].Id, Position = cell, Footprint = [cell] }); break;
            case "solarFloor": project.Layout.Rooms.Add(new() { Id = "custom-exterior", Cells = [cell] }); break;
        }
        Assert.That(DesignDockGeometry.ValidateDock(project, dock).Any(issue => issue.Code == "dock-obstructed"), Is.True);
    }

    [Test]
    public void ExportHasNativeDockingComponentsAndHonestClearanceManifest()
    {
        var project = DesignContents.Run(PlannedDock()); project.Stage = "validation";
        var exported = DesignMapExporter.Export(project);
        var stream = new YamlStream(); stream.Load(new StringReader(exported.MapYaml));
        var root = (YamlMappingNode) stream.Documents[0].RootNode;
        var groups = (YamlSequenceNode) root[new YamlScalarNode("entities")];
        var hardware = groups.Children.Cast<YamlMappingNode>().Single(group => group[new YamlScalarNode("proto")].ToString() == "AirlockExternalGlassShuttleArrivals");
        var row = (YamlMappingNode) ((YamlSequenceNode) hardware[new YamlScalarNode("entities")]).Children.Single();
        var components = ((YamlSequenceNode) row[new YamlScalarNode("components")]).Children.Cast<YamlMappingNode>().ToDictionary(component => component[new YamlScalarNode("type")].ToString());
        Assert.That(components.Keys, Does.Contain("Docking"));
        Assert.That(components.Keys, Does.Contain("DockingSignalControl"));
        Assert.That(components["Transform"][new YamlScalarNode("rot")].ToString(), Is.EqualTo("90 deg"));
        using var manifest = JsonDocument.Parse(exported.ManifestJson);
        var record = manifest.RootElement.GetProperty("dockingReservations").EnumerateArray().Single();
        Assert.That(record.GetProperty("nativeDockingComponentsMaterialized").GetBoolean(), Is.True);
        Assert.That(record.GetProperty("geometryAndHardwareAccepted").GetBoolean(), Is.True);
        Assert.That(manifest.RootElement.GetProperty("simulationVerified").GetBoolean(), Is.False);
        var svg = DesignSvgExporter.Export(project);
        Assert.That(svg, Does.Contain("reserved empty shuttle berth"));
        Assert.That(svg, Does.Contain("reserved empty exterior approach"));
    }

    [Test]
    public void RoutingPreservesPaintAndReportsItsBerthConflict()
    {
        var project = PlannedDock(); var dock = project.Layout.Docks.Single();
        project.Layout.MaintenanceEdited = true;
        project.Layout.Maintenance = dock.BerthCells.Take(4).ToList();
        var original = project.Layout.Maintenance.ToList();
        var routed = DesignRouting.Run(project);
        Assert.That(routed.Layout.Maintenance, Is.EqualTo(original));
        Assert.That(DesignGeometryValidator.Validate(routed).Diagnostics.Any(issue => issue.Code == "dock-obstructed"), Is.True);
    }
}
