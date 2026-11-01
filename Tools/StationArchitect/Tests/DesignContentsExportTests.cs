using System.Text.Json;
using Content.StationArchitect;
using NUnit.Framework;
using YamlDotNet.RepresentationModel;

namespace StationArchitect.Tests;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class DesignContentsExportTests
{
    private static StationDesignProject BuiltRoom()
    {
        var project = InstanceSelection.Resolve(new() { Definition = ProgramTestData.Minimal() });
        var room = project.Layout.Rooms.Single();
        room.Cells = new GridBounds(0, 0, 8, 5).Cells().ToList(); room.Seed = new(1, 2); room.HasSeed = true;
        project.Layout.Envelope = new GridBounds(-2, -2, 12, 9).Cells().ToList();
        project.Layout.Walls = new GridBounds(-1, -1, 10, 7).Cells().Except(room.Cells).ToList();
        project.Layout.PublicCorridors = [new(-2, 2)];
        project.Layout.Walls.Remove(new(-1, 2));
        project.Layout.Interfaces = [new() { Id = "entrance", From = room.Id, To = "$public", Cells = [new(-1, 2)], FromApproach = new(0, 2), ToApproach = new(-2, 2) }];
        return project;
    }

    [Test]
    public void ContentsRespectCountsWorkingFloorAndPhysicalSpaceAirlockInterfaces()
    {
        var project = BuiltRoom();
        project.Definition.Departments[0].Rooms[0].Contents =
        [
            new() { Id = "Equipment", Prototypes = ["Autolathe"], Count = new() { Min = 1, Target = 2, Max = 3 }, MinArea = 4 },
            new() { Id = "ExternalDoor", Prototypes = ["AirlockExternal"], Placement = "spaceBoundary", InterfaceType = "airlock", MinArea = 0 }
        ];
        var result = DesignContents.Run(project);
        Assert.That(result.Report!.Success, Is.True, ProgramTestData.Errors(result.Report.Diagnostics));
        var machines = result.Layout.Equipment.Where(entity => entity.RequirementId == "Equipment").ToList();
        Assert.That(machines.Count, Is.EqualTo(2));
        Assert.That(machines.All(entity => entity.WorkArea.Count >= 4), Is.True);
        Assert.That(machines.SelectMany(entity => entity.WorkArea).Distinct().Count(), Is.EqualTo(machines.Sum(entity => entity.WorkArea.Count)));
        var external = result.Layout.Equipment.Single(entity => entity.RequirementId == "ExternalDoor");
        var port = result.Layout.Interfaces.Single(port => port.Id == external.InterfaceId);
        Assert.That(port.To, Is.EqualTo("$space"));
        Assert.That(result.Layout.Walls.Intersect(external.Footprint), Is.Empty);
        Assert.That(DesignMeasurements.InterfaceTouches(result, port), Is.True);
        var measurement = DesignMeasurements.Build(result).Rooms.Single();
        Assert.That(measurement.Contents!.Single(content => content.Id == "ExternalDoor").InterfaceType, Is.EqualTo("airlock"));
        Assert.That(project.Layout.Equipment, Is.Empty, "Stage generation must not mutate the input project.");
    }

    [Test]
    public void RebuildingContentsRetainsPinnedEquipmentAndDoesNotInventItsValidity()
    {
        var project = BuiltRoom();
        project.Definition.Departments[0].Rooms[0].Contents = [new() { Id = "Machine", Prototypes = ["Autolathe"], MinArea = 4 }];
        var built = DesignContents.Run(project); var machine = built.Layout.Equipment.Single();
        machine.Pinned = true; var before = machine.Position;
        var rebuilt = DesignContents.Run(built);
        Assert.That(rebuilt.Layout.Equipment.Single().Position, Is.EqualTo(before));
        machine.Position = new(100, 100); machine.Footprint = [new(100, 100)];
        var invalid = DesignContents.Run(built);
        Assert.That(invalid.Layout.Equipment.Any(entity => entity.Position == new Cell(100, 100)), Is.True);
        Assert.That(invalid.Report!.Diagnostics.Any(item => item.Code == "pinned-equipment"), Is.True);
        Assert.That(DesignMeasurements.Build(invalid).Rooms.Single().Contents, Is.Empty);
    }

    [Test]
    public void ExplicitColdEnvironmentIsMaterializedAsAZoneAndMissingCustomMixtureIsNotInvented()
    {
        var project = BuiltRoom(); project.Definition.Departments[0].Rooms[0].Environment.MaxTemperatureKelvin = 270;
        var built = DesignContents.Run(project);
        Assert.That(built.Layout.EnvironmentZones.Single(zone => zone.RoomId == "Alpha/Work#1").TemperatureKelvin, Is.EqualTo(270));
        Assert.That(built.Layout.EnvironmentZones.Single(zone => zone.RoomId == "Alpha/Work#1").GasFractions["Oxygen"], Is.EqualTo(.21));
        Assert.That(built.Layout.EnvironmentZones.Single(zone => zone.RoomId == "$public").PressureKpa, Is.EqualTo(101.325));
        project.Definition.Departments[0].Rooms[0].Environment.Pressure = "custom";
        var custom = DesignContents.Run(project);
        Assert.That(custom.Layout.EnvironmentZones.Single(zone => zone.RoomId == "Alpha/Work#1").PressureKpa, Is.Null);
        Assert.That(DesignMapExporter.Export(custom).Diagnostics.Any(item => item.Code == "export-atmosphere"), Is.True);
    }

    [Test]
    public void MapExportUsesActualFormatSevenTilesUniqueEntitiesAndCompanionPaths()
    {
        var project = BuiltRoom();
        project.Definition.Departments[0].Rooms[0].Contents = [new() { Id = "Machine", Prototypes = ["Autolathe"] }];
        project = DesignContents.Run(project);
        var exported = DesignMapExporter.Export(project);
        var stream = new YamlStream(); stream.Load(new StringReader(exported.MapYaml));
        var root = (YamlMappingNode) stream.Documents.Single().RootNode;
        Assert.That(((YamlMappingNode) root[new YamlScalarNode("meta")])[new YamlScalarNode("format")].ToString(), Is.EqualTo("7"));
        Assert.That(((YamlSequenceNode) root[new YamlScalarNode("maps")]).Children.Single().ToString(), Is.EqualTo("1"));
        Assert.That(((YamlSequenceNode) root[new YamlScalarNode("grids")]).Children.Single().ToString(), Is.EqualTo("2"));
        var groups = (YamlSequenceNode) root[new YamlScalarNode("entities")];
        var rows = groups.Children.Cast<YamlMappingNode>().SelectMany(group => ((YamlSequenceNode) group[new YamlScalarNode("entities")]).Children.Cast<YamlMappingNode>()).ToList();
        var ids = rows.Select(row => int.Parse(row[new YamlScalarNode("uid")].ToString())).ToList();
        Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Count));
        Assert.That(groups.Children.Cast<YamlMappingNode>().Any(group => group[new YamlScalarNode("proto")].ToString() == "Autolathe"), Is.True);
        var grid = rows.Single(row => row[new YamlScalarNode("uid")].ToString() == "2");
        var components = (YamlSequenceNode) grid[new YamlScalarNode("components")];
        var mapGrid = components.Children.Cast<YamlMappingNode>().Single(component => component[new YamlScalarNode("type")].ToString() == "MapGrid");
        var chunks = (YamlMappingNode) mapGrid[new YamlScalarNode("chunks")];
        foreach (var chunk in chunks.Children.Values.Cast<YamlMappingNode>())
        {
            var bytes = Convert.FromBase64String(chunk[new YamlScalarNode("tiles")].ToString());
            Assert.That(bytes.Length, Is.EqualTo(16 * 16 * 7));
            Assert.That(chunk[new YamlScalarNode("version")].ToString(), Is.EqualTo("7"));
        }
        Assert.That(exported.DescriptorYaml, Does.Contain("/Maps/Generated/" + exported.MapFileName));
        using var manifest = JsonDocument.Parse(exported.ManifestJson);
        Assert.That(manifest.RootElement.GetProperty("operational").GetBoolean(), Is.False);
        Assert.That(manifest.RootElement.GetProperty("simulationVerified").GetBoolean(), Is.False);
        Assert.That(exported.MapYaml, Is.EqualTo(DesignMapExporter.Export(project).MapYaml), "Repeated exports are deterministic.");
    }

    [Test]
    public void BoundaryInterfaceRequirementSurvivesYamlAndCannotBeClaimedWithoutARealInterface()
    {
        var definition = ProgramTestData.Minimal();
        definition.Departments[0].Rooms[0].Contents = [new() { Id = "Airlock", Prototypes = ["AirlockExternal"], Placement = "spaceBoundary", InterfaceType = "airlock" }];
        var parsed = ProgramYaml.Parse(ProgramYaml.Export(definition));
        Assert.That(parsed.Departments[0].Rooms[0].Contents.Single().InterfaceType, Is.EqualTo("airlock"));
        var state = new PressureState
        {
            Rooms = [new() { Path = "Alpha/Work", Area = 40, Width = 8, Height = 5, Environment = new(), Contents = [new() { Id = "Airlock", Prototype = "AirlockExternal", Placement = "spaceBoundary", Count = 1 }] }],
            Departments = [new() { Path = "Alpha", Width = 8, Height = 5 }]
        };
        Assert.That(PressureEvaluator.Evaluate(parsed, state, true).Diagnostics.Any(item => item.Code == "content-interface-measurement"), Is.True);
    }

    [Test]
    public void ExteriorMaintenanceCrossingGetsAFullWidthPhysicalAirlock()
    {
        var project = new StationDesignProject();
        project.Layout.Envelope = new GridBounds(0, 0, 5, 10).Cells().ToList();
        project.Layout.Maintenance = new GridBounds(0, 4, 11, 2).Cells().ToList();
        var diagnostics = new List<ProgramDiagnostic>();
        MaintenanceAirlockGates.Apply(project, diagnostics);
        Assert.That(diagnostics, Is.Empty);
        var gate = project.Layout.Interfaces.Single();
        Assert.That(gate.Type, Is.EqualTo("airlock")); Assert.That(gate.Width, Is.EqualTo(2));
        Assert.That(gate.From, Is.EqualTo("$maintenance")); Assert.That(gate.To, Is.EqualTo("$exterior-maintenance"));
        Assert.That(project.Layout.Maintenance.Intersect(gate.Cells), Is.Empty);
        Assert.That(gate.Cells, Is.EquivalentTo(new[] { new Cell(4, 4), new Cell(4, 5) }));
        Assert.That(DesignMeasurements.InterfaceTouches(project, gate), Is.True);
        MaintenanceAirlockGates.Apply(project, diagnostics);
        Assert.That(project.Layout.Interfaces.Count, Is.EqualTo(1), "Rebuilding contents must not duplicate the physical crossing gate.");
    }

    [Test]
    public void AnUnsealableNarrowCrossingStaysAVisibleErrorRatherThanBecomingFakeAirlockData()
    {
        var project = new StationDesignProject();
        project.Layout.Envelope = new GridBounds(0, 0, 5, 10).Cells().ToList();
        project.Layout.Maintenance = new GridBounds(0, 4, 11, 1).Cells().ToList();
        var diagnostics = new List<ProgramDiagnostic>(); MaintenanceAirlockGates.Apply(project, diagnostics);
        Assert.That(project.Layout.Interfaces, Is.Empty);
        Assert.That(diagnostics.Any(item => item.Code == "exterior-maintenance-gate"), Is.True);
    }

    [Test]
    public void UtilityEquipmentOnABoundaryDoesNotRemoveItsPressureWall()
    {
        var project = BuiltRoom();
        project.Definition.Departments[0].Rooms[0].Contents = [new() { Id = "Pipe", Prototypes = ["GasPipeStraight"], Placement = "spaceBoundary", InterfaceType = "pipe" }];
        var result = DesignContents.Run(project);
        var entity = result.Layout.Equipment.Single();
        Assert.That(result.Layout.Interfaces.Single(port => port.Id == entity.InterfaceId).Kind, Is.EqualTo("utility"));
        Assert.That(entity.Footprint.All(result.Layout.Walls.Contains), Is.True);
    }

    [Test]
    public void SerializedMapRetainsWallsBehindUtilityAndOrdinaryWallMountedEquipment()
    {
        var project = BuiltRoom();
        project.Definition.Departments[0].Rooms[0].Contents =
        [
            new() { Id = "Alarm", Prototypes = ["AirAlarm"], Placement = "boundary" },
            new() { Id = "Pipe", Prototypes = ["GasPipeStraight"], Placement = "boundary", InterfaceType = "pipe" }
        ];
        project.Layout.Equipment =
        [
            new() { Id = "alarm", RoomId = "Alpha/Work#1", RequirementId = "Alarm", Prototype = "AirAlarm", Position = new(-1, 1), Placement = "boundary", Footprint = [new(-1, 1)] },
            new() { Id = "pipe", RoomId = "Alpha/Work#1", RequirementId = "Pipe", Prototype = "GasPipeStraight", Position = new(8, 3), Placement = "boundary", InterfaceId = "pipe-wall", Footprint = [new(8, 3)] }
        ];
        project.Layout.Interfaces.Add(new() { Id = "pipe-wall", From = "Alpha/Work#1", To = "$space", Kind = "utility", Type = "pipe", Cells = [new(8, 3)], FromApproach = new(7, 3), ToApproach = new(9, 3) });
        var exported = DesignMapExporter.Export(project);
        var stream = new YamlStream(); stream.Load(new StringReader(exported.MapYaml));
        var root = (YamlMappingNode) stream.Documents.Single().RootNode;
        var rows = ((YamlSequenceNode) root[new YamlScalarNode("entities")]).Children.Cast<YamlMappingNode>()
            .SelectMany(group => ((YamlSequenceNode) group[new YamlScalarNode("entities")]).Children.Cast<YamlMappingNode>()
                .Select(row => (Prototype: group[new YamlScalarNode("proto")].ToString(), Row: row))).ToList();
        HashSet<string> At(Cell cell)
        {
            var position = FormattableString.Invariant($"{cell.X + .5},{cell.Y + .5}");
            return rows.Where(item => ((YamlSequenceNode) item.Row[new YamlScalarNode("components")]).Children.Cast<YamlMappingNode>()
                .Any(component => component[new YamlScalarNode("type")].ToString() == "Transform" && component.Children.TryGetValue(new YamlScalarNode("pos"), out var pos) && pos.ToString() == position))
                .Select(item => item.Prototype).ToHashSet();
        }
        Assert.That(At(new(-1, 1)), Is.SupersetOf(new[] { "WallSolid", "AirAlarm" }));
        Assert.That(At(new(8, 3)), Is.SupersetOf(new[] { "WallSolid", "GasPipeStraight" }));
        Assert.That(At(new(-1, 2)), Does.Contain("Airlock"));
        Assert.That(At(new(-1, 2)), Does.Not.Contain("WallSolid"), "Only the real walking doorway cuts the pressure boundary.");
    }

    [Test]
    public void CustomMixtureEditsSetInitialGasWithoutChangingTheDefinitionOrGeometryAndRejectInvalidPayloads()
    {
        var source = BuiltRoom(); source.Definition.Departments[0].Rooms[0].Environment.Pressure = "custom";
        source = DesignContents.Run(source);
        var before = System.Text.Json.JsonSerializer.Serialize(source, ProgramJson.Options);
        var edited = ProjectEdits.Apply(source, new() { Kind = "set-zone-mixture", NodeId = "Alpha/Work#1", TextValue = "{\"pressureKpa\":120,\"gasFractions\":{\"Nitrogen\":1}}" });
        var zone = edited.Layout.EnvironmentZones.Single(zone => zone.RoomId == "Alpha/Work#1");
        Assert.That(zone.PressureKpa, Is.EqualTo(120)); Assert.That(zone.GasFractions["Nitrogen"], Is.EqualTo(1)); Assert.That(zone.Edited, Is.True);
        Assert.That(zone.Cells, Is.EqualTo(source.Layout.EnvironmentZones.Single(zone => zone.RoomId == "Alpha/Work#1").Cells));
        Assert.That(edited.Definition.Departments[0].Rooms[0].Environment.Pressure, Is.EqualTo("custom"));
        foreach (var bad in new[] { "{\"pressureKpa\":120,\"gasFractions\":{\"Oxygen\":0.8,\"Nitrogen\":0.8}}", "{\"pressureKpa\":120,\"gasFractions\":{\"Hydrogen\":1}}", "{\"pressureKpa\":120,\"gasFractions\":{}}", "{\"pressureKpa\":-1,\"gasFractions\":{\"Oxygen\":1}}" })
            Assert.Throws<ArgumentException>(() => ProjectEdits.Apply(source, new() { Kind = "set-zone-mixture", NodeId = "Alpha/Work#1", TextValue = bad }));
        Assert.That(System.Text.Json.JsonSerializer.Serialize(source, ProgramJson.Options), Is.EqualTo(before));
        var cleared = ProjectEdits.Apply(edited, new() { Kind = "set-zone-mixture", NodeId = "Alpha/Work#1", TextValue = "{\"pressureKpa\":null,\"gasFractions\":{}}" });
        Assert.That(cleared.Layout.EnvironmentZones.Single(zone => zone.RoomId == "Alpha/Work#1").PressureKpa, Is.Null);
    }
}
