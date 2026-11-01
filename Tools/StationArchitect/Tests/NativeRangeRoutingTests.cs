using System.Text.Json;
using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
public sealed class NativeRangeRoutingTests
{
    [Test]
    public void ReverseSourceRoutingCannotUseAnAlreadyPoweredReceiverAsASupply()
    {
        var definition = ProgramTestData.Minimal(); definition.Departments[0].Area.Max = definition.Departments[0].Rooms[0].Area.Max = 1200;
        definition.Systems.Enabled = true;
        definition.Systems.Profiles = [new()
        {
            Id = "APC", Prototype = "APCBasic", RequiredComponents = ["NodeContainer", "Battery", "PowerNetworkBattery", "Apc", "PowerProvider"],
            Ports = [new() { Id = "output", Kind = "power", Connection = "cableDevice", NodeGroup = "Apc" }, new() { Id = "provider", Kind = "power", Connection = "rangeProvider", NodeGroup = "Apc", Range = 3 }],
            Transfers = [new() { FromPort = "output", ToPort = "provider" }], Sources = [new() { PortId = "output", Kind = "power", StoredEnergyJoules = 10000 }],
            Components = new() { ["Battery"] = JsonSerializer.SerializeToElement(new { startingCharge = 10000, maxCharge = 50000 }) }
        }, new()
        {
            Id = "Light", Prototype = "Poweredlight", RequiresPower = true, RequiredComponents = ["ApcPowerReceiver", "ExtensionCableReceiver"],
            Ports = [new() { Id = "power", Kind = "power", Connection = "rangeReceiver", NodeGroup = "Apc", Range = 3 }]
        }, new()
        {
            Id = "Wire", Prototype = "CableApcExtension", Kind = "cable", RequiredComponents = ["NodeContainer", "ExtensionCableProvider"],
            Ports = [new() { Id = "wire", Kind = "power", Connection = "cable", NodeGroup = "Apc" }, new() { Id = "provider", Kind = "power", Connection = "rangeProvider", NodeGroup = "Apc", Range = 3 }],
            Transfers = [new() { FromPort = "wire", ToPort = "provider" }]
        }];
        var project = new StationDesignProject { Definition = definition, Settings = new() { Width = 32, Height = 24 }, Stage = "systemsDevices" };
        project.Layout.Rooms = [new() { Id = "Alpha/Work#1", DefinitionPath = "Alpha/Work", OwnerId = "Alpha", Instance = "1", Seed = new(12, 10), Cells = new GridBounds(4, 4, 20, 14).Cells().ToList() }];
        project.Layout.Envelope = new GridBounds(2, 2, 26, 20).Cells().ToList();
        Add("Source", "APC", "APCBasic", new(6, 8)); Add("Anear", "Light", "Poweredlight", new(8, 8)); Add("Zfar", "Light", "Poweredlight", new(11, 8));
        definition.Systems.Networks = [new() { Id = "LV", Kind = "power", SegmentProfiles = ["Wire"],
            Terminals = [new() { DeviceRuleId = "Source", PortId = "output", Role = "source" }, new() { DeviceRuleId = "Anear", PortId = "power" }, new() { DeviceRuleId = "Zfar", PortId = "power" }] }];
        var result = DesignSystemsGeneration.Run(project, "power");
        var far = result.Systems!.Terminals.Single(terminal => terminal.DeviceId == "bound:Zfar");
        Assert.That(far.SourceReachable, Is.True, ProgramTestData.Errors(result.Report!.Diagnostics));
        Assert.That(result.Systems.Terminals.Where(terminal => terminal.Role == "consumer").All(terminal => terminal.SourceReachable), Is.True);
        Assert.That(result.Layout.SystemNetworks.Single().Segments.Any(segment => segment.Position.X >= 8), Is.True,
            "The remote receiver needs actual extension wiring; another receiver cannot relay power backward.");

        void Add(string id, string profile, string prototype, Cell cell)
        {
            definition.Departments[0].Rooms[0].Contents.Add(new() { Id = id, Prototypes = [prototype] });
            definition.Systems.Devices.Add(new() { Id = id, ProfileId = profile, BindExisting = true, Scope = "station", ContentIds = [id], Content = new() { Prototypes = [prototype] } });
            project.Layout.Equipment.Add(new() { Id = id, RoomId = "Alpha/Work#1", RequirementId = id, Prototype = prototype, Position = cell, Footprint = [cell], WorkArea = [cell] });
            project.Layout.SystemDevices.Add(new() { Id = "bound:" + id, RequirementId = id, EntityId = id, ProfileId = profile, ScopeId = "$station" });
        }
    }
}
