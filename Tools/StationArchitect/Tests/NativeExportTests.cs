using System.Text.Json;
using Content.StationArchitect;
using NUnit.Framework;
using YamlDotNet.RepresentationModel;

namespace StationArchitect.Tests;

[TestFixture]
public sealed class NativeExportTests
{
    [Test]
    public void NativeComponentsAndControlsUseActualSerializedUidsAndReplaceGenericWires()
    {
        var definition = ProgramTestData.Minimal(); definition.Systems.Enabled = true;
        var project = new StationDesignProject { Definition = definition, Stage = "systemsDevices", Settings = new() { Width = 32, Height = 24 } };
        var room = new DesignRoom { Id = "Alpha/Work#1", DefinitionPath = "Alpha/Work", OwnerId = "Alpha", Cells = new GridBounds(4, 4, 10, 8).Cells().ToList() };
        project.Layout.Rooms.Add(room); project.Layout.Envelope = new GridBounds(2, 2, 14, 12).Cells().ToList();
        project.Layout.Walls = room.Cells.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Except(room.Cells).Distinct().ToList();
        foreach (var (id, prototype, position, placement) in new[] { ("Alarm", "AirAlarm", new Cell(3, 6), "boundary"), ("Sensor", "AirSensor", new Cell(7, 7), "floor") })
        {
            definition.Departments[0].Rooms[0].Contents.Add(new() { Id = id, Prototypes = [prototype], Placement = placement });
            definition.Systems.Profiles.Add(new() { Id = id, Prototype = prototype, RequiredComponents = ["ApcPowerReceiver", "ExtensionCableReceiver"],
                Ports = [new() { Id = "power", Kind = "power", Connection = "rangeReceiver", NodeGroup = "Apc", Range = 3 }],
                Components = id == "Alarm" ? new() { ["AirAlarm"] = JsonSerializer.SerializeToElement(new { autoMode = false }) } : [] });
            definition.Systems.Devices.Add(new() { Id = id, ProfileId = id, BindExisting = true, Scope = "room", ContentIds = [id], Content = new() { Id = id, Prototypes = [prototype], Placement = placement } });
            project.Layout.Equipment.Add(new() { Id = id, RoomId = room.Id, RequirementId = id, Prototype = prototype, Position = position, Placement = placement, Footprint = [position], WorkArea = placement == "floor" ? [position] : [] });
            project.Layout.SystemDevices.Add(new() { Id = "binding:" + id, RequirementId = id, ProfileId = id, EntityId = id, ScopeId = room.Id });
        }
        definition.Systems.Profiles.Add(new() { Id = "HV", Prototype = "CableHV", Kind = "cable", RequiredComponents = ["NodeContainer"], Ports = [new() { Id = "power", Kind = "power", Connection = "cable", NodeGroup = "HVPower" }] });
        definition.Systems.Networks.Add(new() { Id = "Power", Kind = "power", SegmentProfiles = ["HV"], Terminals = [new() { DeviceRuleId = "Sensor", PortId = "power", Role = "consumer" }] });
        definition.Systems.Controls.Add(new() { Id = "AlarmControl", ControllerRuleId = "Alarm", DeviceRuleIds = ["Sensor"], MaxDevices = 96 });
        project.Layout.SystemNetworks.Add(new() { Id = "Power", RequirementId = "Power", Segments = [new() { Id = "native-wire", ProfileId = "HV", Position = new(6, 6), Rotation = 90 }] });
        project.Layout.SystemControls.Add(new() { Id = "Control", RequirementId = "AlarmControl", ControllerDeviceId = "binding:Alarm", DeviceIds = ["binding:Sensor"] });
        project.Layout.Interfaces.Add(new() { Id = "old-wire-interface", Kind = "utility", Type = "cable", Cells = [new(8, 8)] });
        project.Layout.Utilities.Add(new() { Id = "old-generic", Type = "cable", Cells = [new(8, 8)] });
        var exported = DesignMapExporter.Export(project);
        var yaml = new YamlStream(); yaml.Load(new StringReader(exported.MapYaml)); var root = (YamlMappingNode) yaml.Documents[0].RootNode;
        var groups = ((YamlSequenceNode) root[new YamlScalarNode("entities")]).Children.Cast<YamlMappingNode>().ToList();
        YamlMappingNode Entity(string prototype) => ((YamlSequenceNode) groups.Single(group => group[new YamlScalarNode("proto")].ToString() == prototype)[new YamlScalarNode("entities")]).Children.Cast<YamlMappingNode>().Single();
        var alarm = Entity("AirAlarm"); var sensor = Entity("AirSensor");
        var alarmComponents = ((YamlSequenceNode) alarm[new YamlScalarNode("components")]).Children.Cast<YamlMappingNode>().ToList();
        var list = alarmComponents.Single(component => component[new YamlScalarNode("type")].ToString() == "DeviceList");
        Assert.That(((YamlSequenceNode) list[new YamlScalarNode("devices")]).Children.Single().ToString(), Is.EqualTo(sensor[new YamlScalarNode("uid")].ToString()));
        Assert.That(list[new YamlScalarNode("deviceLimit")].ToString(), Is.EqualTo("96"));
        Assert.That(alarmComponents.Single(component => component[new YamlScalarNode("type")].ToString() == "AirAlarm")[new YamlScalarNode("autoMode")].ToString(), Is.EqualTo("false"));
        Assert.That(groups.Any(group => group[new YamlScalarNode("proto")].ToString() == "CableMV"), Is.False);
        Assert.That(Entity("CableHV"), Is.Not.Null);
        using var manifest = JsonDocument.Parse(exported.ManifestJson);
        Assert.That(manifest.RootElement.GetProperty("nativeMaterialization").GetProperty("segmentUids").GetProperty("native-wire").GetInt32(), Is.EqualTo(int.Parse(Entity("CableHV")[new YamlScalarNode("uid")].ToString())));
        Assert.That(manifest.RootElement.TryGetProperty("gameflow", out _), Is.True);
        Assert.That(manifest.RootElement.TryGetProperty("systems", out _), Is.True);
    }
    [Test]
    public void DistinctNativePipeLayersKeepTheirOwnSerializedEntitiesAndComponentOverrides()
    {
        var project = new StationDesignProject { Definition = ProgramTestData.Minimal(), Stage = "atmos" };
        project.Definition.Systems.Enabled = true;
        foreach (var layer in new[] { 0, 1 })
            project.Definition.Systems.Profiles.Add(new() { Id = "pipe" + layer, Prototype = "GasPipeStraight", Kind = "pipe", RequiredComponents = ["NodeContainer", "AtmosPipeLayers"],
                Ports = [new() { Id = "pipe", Kind = "gas", Connection = "pipe", NodeGroup = "Pipe", PipeLayer = layer, Directions = ["South", "North"], VolumeLitres = 200 }],
                Components = new() { ["AtmosPipeLayers"] = JsonSerializer.SerializeToElement(new { pipeLayer = layer }) } });
        project.Definition.Systems.Networks.Add(new() { Id = "Gas", Kind = "gas", SegmentProfiles = ["pipe0", "pipe1"] });
        project.Layout.SystemNetworks.Add(new() { Id = "Gas", RequirementId = "Gas", Segments = [new() { Id = "supply", ProfileId = "pipe0", Position = new(6, 6) }, new() { Id = "waste", ProfileId = "pipe1", Position = new(6, 6) }] });
        var exported = DesignMapExporter.Export(project);
        var yaml = new YamlStream(); yaml.Load(new StringReader(exported.MapYaml)); var root = (YamlMappingNode) yaml.Documents[0].RootNode;
        var group = ((YamlSequenceNode) root[new YamlScalarNode("entities")]).Children.Cast<YamlMappingNode>().Single(group => group[new YamlScalarNode("proto")].ToString() == "GasPipeStraight");
        var pipes = ((YamlSequenceNode) group[new YamlScalarNode("entities")]).Children.Cast<YamlMappingNode>().ToList();
        Assert.That(pipes, Has.Count.EqualTo(2));
        Assert.That(pipes.Select(pipe => ((YamlSequenceNode) pipe[new YamlScalarNode("components")]).Children.Cast<YamlMappingNode>().Single(component => component[new YamlScalarNode("type")].ToString() == "AtmosPipeLayers")[new YamlScalarNode("pipeLayer")].ToString()), Is.EquivalentTo(new[] { "0", "1" }));
        using var manifest = JsonDocument.Parse(exported.ManifestJson);
        var uids = manifest.RootElement.GetProperty("nativeMaterialization").GetProperty("segmentUids");
        Assert.That(uids.GetProperty("supply").GetInt32(), Is.Not.EqualTo(uids.GetProperty("waste").GetInt32()));
        Assert.That(DesignSvgExporter.Export(project), Does.Contain("Gas: supply GasPipeStraight"));
    }

}
