using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
public sealed class SystemGenerationTests
{
    private static NativeSystemProfile Profile(string id, string kind, string connection, string group, params string[] directions) => new()
    { Id = id, Prototype = id, Kind = kind, RequiredComponents = ["NodeContainer"], Ports = [new() { Id = "node", Kind = kind == "device" || kind == "cable" ? "power" : kind == "pipe" ? "gas" : "disposal", Connection = connection, NodeGroup = group, Directions = [.. directions] }] };

    private static StationDesignProject Fixture()
    {
        var definition = ProgramTestData.Minimal(); definition.Departments[0].Rooms[0].Area.Max = definition.Departments[0].Area.Max = 1200;
        definition.Systems.Enabled = true;
        var project = new StationDesignProject { Definition = definition, Settings = new() { Width = 40, Height = 32 }, Stage = "contents" };
        var floor = new GridBounds(4, 4, 24, 16).Cells().ToList();
        project.Layout.Rooms = [new() { Id = "Alpha/Work#1", DefinitionPath = "Alpha/Work", OwnerId = "Alpha", Instance = "1", Cells = floor, Seed = new(16, 12) }];
        project.Layout.Envelope = new GridBounds(2, 2, 30, 24).Cells().ToList();
        project.Layout.Walls = floor.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Except(floor).Distinct().ToList();
        return project;
    }

    private static void Bind(StationDesignProject project, string id, NativeSystemProfile profile, Cell cell, string role)
    {
        project.Definition.Systems.Profiles.Add(profile);
        project.Definition.Departments[0].Rooms[0].Contents.Add(new() { Id = id, Prototypes = [profile.Prototype], Count = new() { Min = 1, Target = 1, Max = 64 } });
        project.Definition.Systems.Devices.Add(new() { Id = id, ProfileId = profile.Id, BindExisting = true, Scope = "station", ContentIds = [id], Content = new() { Id = id, Prototypes = [profile.Prototype], Count = new() { Min = 1, Target = 1, Max = 64 } } });
        project.Layout.Equipment.Add(new() { Id = id, RoomId = "Alpha/Work#1", RequirementId = id, Prototype = profile.Prototype, Position = cell, Footprint = [cell], WorkArea = [cell] });
        project.Layout.SystemDevices.Add(new() { Id = "bound:" + id, RequirementId = id, EntityId = id, ProfileId = profile.Id, ScopeId = "$station" });
    }

    private static void Network(StationDesignProject project, string kind, params NativeSystemProfile[] segments)
    {
        project.Definition.Systems.Profiles.AddRange(segments);
        project.Definition.Systems.Networks = [new() { Id = "Net", Kind = kind, SegmentProfiles = segments.Select(profile => profile.Id).ToList(),
            Terminals = [new() { DeviceRuleId = "Source", PortId = "node", Role = kind == "disposal" ? "outlet" : "source" }, new() { DeviceRuleId = "Sink", PortId = "node", Role = kind == "disposal" ? "inlet" : "consumer" }] }];
    }

    private static bool Connected(StationDesignProject project)
    {
        var profiles = project.Definition.Systems.Profiles.ToDictionary(profile => profile.Id);
        var ports = project.Layout.SystemDevices.SelectMany(device =>
        {
            var entity = project.Layout.Equipment.Single(entity => entity.Id == device.EntityId);
            return DesignSystems.Ports(profiles[device.ProfileId], entity.Position, entity.Rotation, device.Id);
        }).Concat(project.Layout.SystemNetworks.SelectMany(network => network.Segments).SelectMany(segment => DesignSystems.Ports(profiles[segment.ProfileId], segment.Position, segment.Rotation, segment.Id))).ToList();
        var source = ports.Single(port => port.InstanceId == "bound:Source"); var target = ports.Single(port => port.InstanceId == "bound:Sink");
        var reached = new HashSet<string> { source.Id }; var queue = new Queue<SystemPortGeometry>(); queue.Enqueue(source);
        while (queue.TryDequeue(out var current)) foreach (var next in ports) if (!reached.Contains(next.Id) && DesignSystems.Contacts(current, next) && reached.Add(next.Id)) queue.Enqueue(next);
        return reached.Contains(target.Id);
    }

    [Test]
    public void NativeDevicesUseRealFreeFootprintAndWorkFloorAndRetainPinnedHardware()
    {
        var project = Fixture(); var profile = Profile("Machine", "device", "cableDevice", "MVPower"); project.Definition.Systems.Profiles.Add(profile);
        project.Definition.Systems.Devices = [new() { Id = "Machines", ProfileId = profile.Id, Content = new() { Id = "Machines", Prototypes = [profile.Prototype], MinArea = 6, Count = new() { Min = 2, Target = 2, Max = 3 } } }];
        var first = DesignSystemsGeneration.RunDevices(project);
        Assert.That(first.Layout.SystemDevices, Has.Count.EqualTo(2)); Assert.That(first.Layout.Equipment, Has.Count.EqualTo(2));
        Assert.That(first.Layout.Equipment.All(entity => entity.WorkArea.Count >= 6), Is.True);
        Assert.That(first.Layout.Equipment[0].WorkArea.Intersect(first.Layout.Equipment[1].WorkArea), Is.Empty);
        first.Layout.Equipment[0].Pinned = true; var before = first.Layout.Equipment[0].Position;
        var again = DesignSystemsGeneration.RunDevices(first);
        Assert.That(again.Layout.Equipment.Single(entity => entity.Id == first.Layout.Equipment[0].Id).Position, Is.EqualTo(before));
        Assert.That(project.Layout.Equipment, Is.Empty, "Generation clones the input project.");
    }

    [Test]
    public void ExistingHardwareBindingDoesNotSilentlyStopAtThePreferredCount()
    {
        var project = Fixture(); var profile = Profile("Machine", "device", "cableDevice", "MVPower");
        Bind(project, "Source", profile, new(6, 6), "source"); project.Layout.SystemDevices.Clear();
        project.Layout.Equipment.Add(new() { Id = "second", RequirementId = "Source", RoomId = "Alpha/Work#1", Prototype = profile.Prototype, Position = new(8, 6), Footprint = [new(8, 6)] });
        var result = DesignSystemsGeneration.RunDevices(project);
        Assert.That(result.Layout.SystemDevices, Has.Count.EqualTo(2));
        Assert.That(result.Layout.Equipment.All(entity => entity.SystemRuleId.Length == 0), Is.True, "Ordinary content identities must remain intact.");
    }

    [Test]
    public void ExistingContentRulesDoNotClaimOrDemandSystemGeneratedMachinesOfTheSamePrototype()
    {
        var project = Fixture(); var profile = Profile("Machine", "device", "cableDevice", "MVPower"); Bind(project, "Existing", profile, new(6, 6), "consumer");
        project.Definition.Systems.Devices[0].ContentIds.Clear();
        project.Definition.Systems.Devices.Add(new() { Id = "Generated", ProfileId = profile.Id, Content = new() { Prototypes = [profile.Prototype] } });
        project.Layout.Equipment.Add(new() { Id = "generated", SystemRuleId = "Generated", RequirementId = "systems:Generated", RoomId = "Alpha/Work#1", Prototype = profile.Prototype, Position = new(10, 6), Footprint = [new(10, 6)], WorkArea = [new(10, 6)], Pinned = true });
        project.Layout.SystemDevices.Add(new() { Id = "generated-binding", RequirementId = "Generated", EntityId = "generated", ProfileId = profile.Id, ScopeId = "Alpha/Work#1" });
        var result = DesignSystemsGeneration.RunDevices(project);
        Assert.That(result.Layout.SystemDevices.Where(device => device.RequirementId == "Existing").Select(device => device.EntityId), Is.EqualTo(new[] { "Existing" }));
        Assert.That(result.Report!.Diagnostics.Any(issue => issue.Code is "native-content-unbound" or "native-device-content"), Is.False,
            "Generated machinery belongs to its own native rule and must not create a second ordinary-content obligation.");
        Assert.That(result.Layout.Equipment.Single(entity => entity.Id == "generated").Pinned, Is.True);
    }

    [Test]
    public void UnfitWorkingAreaIsReportedRatherThanOverlappingEquipment()
    {
        var project = Fixture(); var profile = Profile("LargeWork", "device", "cableDevice", "MVPower"); project.Definition.Systems.Profiles.Add(profile);
        project.Definition.Systems.Devices = [new() { Id = "Unfit", ProfileId = profile.Id, Content = new() { Prototypes = [profile.Prototype], MinArea = 600 } }];
        var result = DesignSystemsGeneration.RunDevices(project);
        Assert.That(result.Layout.Equipment, Is.Empty);
        Assert.That(result.Report!.Diagnostics.Any(issue => issue.Code == "system-device-fit"), Is.True);
    }

    [Test]
    public void CableDeviceEndpointsRequireActualSameTileCable()
    {
        var project = Fixture(); Bind(project, "Source", Profile("SourceDevice", "device", "cableDevice", "MVPower"), new(6, 8), "source");
        Bind(project, "Sink", Profile("SinkDevice", "device", "cableDevice", "MVPower"), new(23, 8), "consumer");
        Network(project, "power", Profile("Cable", "cable", "cable", "MVPower"));
        var result = DesignSystemsGeneration.Run(project, "power");
        Assert.That(Connected(result), Is.True);
        Assert.That(result.Layout.SystemNetworks.Single().Segments.Select(segment => segment.Position), Does.Contain(new Cell(6, 8)).And.Contain(new Cell(23, 8)));
        Assert.That(project.Layout.SystemNetworks, Is.Empty);
    }

    [Test]
    public void ARequestedCableLineCannotSubstituteForANativeTerminalAdapter()
    {
        var project = Fixture(); Bind(project, "Source", Profile("SourceDevice", "device", "cableDevice", "MVPower"), new(6, 8), "source");
        Bind(project, "Sink", Profile("SinkDevice", "device", "terminalPort", "MVPower"), new(23, 8), "consumer");
        Network(project, "power", Profile("Cable", "cable", "cable", "MVPower"));
        var missing = DesignSystemsGeneration.Run(project, "power");
        Assert.That(missing.Report!.Diagnostics.Any(issue => issue.Code == "system-native-route"), Is.True);
        Assert.That(missing.Layout.SystemNetworks.Single().Segments, Is.Empty);
        var terminal = Profile("Terminal", "cable", "terminal", "MVPower", "South"); project.Definition.Systems.Profiles.Add(terminal); project.Definition.Systems.Networks[0].SegmentProfiles.Add(terminal.Id);
        var complete = DesignSystemsGeneration.Run(project, "power");
        Assert.That(Connected(complete), Is.True);
        Assert.That(complete.Layout.SystemNetworks.Single().Segments.Any(segment => segment.ProfileId == terminal.Id), Is.True);
    }

    [Test]
    public void DirectionalPipeSegmentsAreRotatedToMakeActualContacts()
    {
        var project = Fixture(); var source = Profile("GasSource", "pipe", "pipe", "Pipe", "East"); source.Kind = "device";
        var sink = Profile("GasSink", "pipe", "pipe", "Pipe", "South"); sink.Kind = "device";
        Bind(project, "Source", source, new(6, 7), "source"); Bind(project, "Sink", sink, new(23, 14), "consumer");
        Network(project, "gas", Profile("Straight", "pipe", "pipe", "Pipe", "South", "North"), Profile("Bend", "pipe", "pipe", "Pipe", "South", "West"));
        var result = DesignSystemsGeneration.Run(project, "gas");
        Assert.That(Connected(result), Is.True);
        Assert.That(result.Layout.SystemNetworks.Single().Segments.Any(segment => segment.Rotation != 0), Is.True);
    }

    [Test]
    public void DisposalUnitUsesASameTileEntryAndAnActualOutletTrunk()
    {
        var project = Fixture(); var outlet = Profile("Outlet", "disposal", "disposalEntry", "Disposal", "South"); outlet.Kind = "device";
        var unit = Profile("Unit", "disposal", "disposalUnit", "Disposal"); unit.Kind = "device";
        Bind(project, "Source", outlet, new(23, 8), "outlet"); Bind(project, "Sink", unit, new(6, 8), "inlet");
        Network(project, "disposal", Profile("Trunk", "disposal", "disposalEntry", "Disposal", "South"), Profile("Tube", "disposal", "disposal", "Disposal", "South", "North"), Profile("Bend", "disposal", "disposal", "Disposal", "South", "West"));
        var result = DesignSystemsGeneration.Run(project, "disposal");
        Assert.That(Connected(result), Is.True);
        Assert.That(result.Layout.SystemNetworks.Single().Segments.Any(segment => segment.ProfileId == "Trunk" && segment.Position == new Cell(6, 8)), Is.True);
        Assert.That(result.Layout.SystemNetworks.Single().Segments.Any(segment => segment.Position == new Cell(23, 8)), Is.False, "The actual outlet trunk already exists as bound hardware.");
    }

    [Test]
    public void EveryMandatoryDeviceGetsAChanceBeforePreferredLightingConsumesBoundarySlots()
    {
        var project = Fixture(); var room = project.Layout.Rooms.Single(); room.Cells = [new(5, 5)]; room.Seed = new(5, 5);
        project.Definition.Departments[0].Area = ProgramTestData.Area(1, 1, 8); project.Definition.Departments[0].Rooms[0].Area = ProgramTestData.Area(1, 1, 8);
        project.Layout.Walls = [new(4, 5), new(6, 5), new(5, 6)];
        project.Layout.PublicCorridors = [new(5, 3)];
        project.Layout.Interfaces = [new() { Id = "door", From = room.Id, To = "$public", Kind = "walking", Type = "doorway", Width = 1, Cells = [new(5, 4)], FromApproach = new(5, 5), ToApproach = new(5, 3) }];
        project.Definition.Systems.Profiles = [new() { Id = "Light", Prototype = "Light" }, new() { Id = "Alarm", Prototype = "Alarm" }];
        project.Definition.Systems.Devices = [new() { Id = "OptionalLights", ProfileId = "Light", Content = new() { Prototypes = ["Light"], Placement = "boundary", Count = new() { Min = 0, Target = 3, Max = 3 } } },
            new() { Id = "MandatoryAlarm", ProfileId = "Alarm", Content = new() { Prototypes = ["Alarm"], Placement = "boundary" } }];
        var result = DesignSystemsGeneration.RunDevices(project);
        Assert.That(result.Layout.SystemDevices.Count(device => device.RequirementId == "MandatoryAlarm"), Is.EqualTo(1));
        Assert.That(result.Layout.SystemDevices.Count(device => device.RequirementId == "OptionalLights"), Is.EqualTo(2));
        Assert.That(result.Report!.Diagnostics.Any(issue => issue.Code == "system-device-fit" && issue.Path.StartsWith("MandatoryAlarm", StringComparison.Ordinal)), Is.False);
    }

    [Test]
    public void ConfiguredNonblockingSubfloorHardwareCanShareARealMachineTileWithoutClaimingItsWorkingFloor()
    {
        var project = Fixture(); var room = project.Layout.Rooms.Single(); room.Cells = [new(5, 5)]; room.Seed = new(5, 5);
        project.Layout.Equipment = [new() { Id = "table", RoomId = room.Id, Prototype = "Table", Position = new(5, 5), Footprint = [new(5, 5)], WorkArea = [new(5, 5)], Pinned = true }];
        project.Definition.Systems.Profiles = [new() { Id = "Vent", Prototype = "Vent", OccupancyLayer = "atmosphere0", BlocksCrew = false }];
        project.Definition.Systems.Devices = [new() { Id = "Vents", ProfileId = "Vent", Content = new() { Prototypes = ["Vent"], Placement = "floor", MinArea = 0 } }];
        var result = DesignSystemsGeneration.RunDevices(project); var vent = result.Layout.Equipment.Single(entity => entity.SystemRuleId == "Vents");
        Assert.That(vent.Position, Is.EqualTo(new Cell(5, 5))); Assert.That(vent.WorkArea, Is.Empty);
        Assert.That(DesignSystems.BlocksCrew(result, vent), Is.False);
        Assert.That(DesignSystems.CanShareCell(result, result.Layout.Equipment.Single(entity => entity.Id == "table"), vent), Is.True);
        Assert.That(result.Layout.Equipment.Single(entity => entity.Id == "table").Pinned, Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DirectionalDevicePlacementFacesAUsableNativeSegmentInsteadOfAReservedVoid(bool allExitsBlocked)
    {
        var project = Fixture(); var room = project.Layout.Rooms.Single(); room.Cells = [new(5, 5)]; room.Seed = new(5, 5);
        project.Layout.Walls = Cell.Directions.Select(direction => room.Seed + direction).ToList();
        project.Layout.ReservedSpace = [new(5, 4), new(6, 5), new(4, 5)];
        if (allExitsBlocked) project.Layout.ReservedSpace.Add(new(5, 6));
        var vent = Profile("Vent", "pipe", "pipe", "Pipe", "South"); vent.Kind = "device"; vent.BlocksCrew = false; vent.OccupancyLayer = "atmosphere0";
        var pipe = Profile("Pipe", "pipe", "pipe", "Pipe", "South", "North");
        project.Definition.Systems.Profiles = [vent, pipe];
        project.Definition.Systems.Devices = [new() { Id = "Vents", ProfileId = vent.Id, Content = new() { Prototypes = [vent.Prototype] } }];
        project.Definition.Systems.Networks = [new() { Id = "Air", Kind = "gas", SegmentProfiles = [pipe.Id], Terminals = [new() { DeviceRuleId = "Vents", PortId = "node" }] }];
        var result = DesignSystemsGeneration.RunDevices(project);
        if (allExitsBlocked)
        {
            Assert.That(result.Layout.Equipment, Is.Empty, "A requested native pipe line cannot justify a device whose every exit points into a protected void.");
            Assert.That(result.Report!.Diagnostics.Any(issue => issue.Code == "system-device-fit"), Is.True);
        }
        else
        {
            var device = result.Layout.Equipment.Single(); var port = DesignSystems.Ports(vent, device.Position, device.Rotation, device.Id).Single();
            Assert.That(device.Position, Is.EqualTo(room.Seed));
            Assert.That(port.Directions, Is.EqualTo(new[] { new Cell(0, 1) }));
            Assert.That(project.Layout.Equipment, Is.Empty, "Choosing a native port orientation must not mutate the input draft.");
        }
    }
}
