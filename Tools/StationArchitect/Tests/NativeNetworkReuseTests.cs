using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
public sealed class NativeNetworkReuseTests
{
    private static StationDesignProject Fixture(bool secondSource = false)
    {
        var project = new StationDesignProject { Definition = ProgramTestData.Minimal(), Settings = new() { Width = 32, Height = 24 }, Stage = "systemsDevices" };
        var floor = new GridBounds(4, 10, 20, 1).Cells().ToList();
        project.Layout.Rooms = [new() { Id = "Alpha/Work#1", DefinitionPath = "Alpha/Work", OwnerId = "Alpha", Cells = floor, Seed = new(12, 10) }];
        project.Layout.Envelope = floor;
        var system = project.Definition.Systems; system.Enabled = true;
        system.Profiles = [
            new() { Id = "wire", Prototype = "CableMV", Kind = "cable", Ports = [new() { Id = "power", Kind = "power", Connection = "cable", NodeGroup = "MVPower" }] },
            new() { Id = "device", Prototype = "SubstationBasic", Kind = "device", Ports = [new() { Id = "power", Kind = "power", Connection = "cableDevice", NodeGroup = "MVPower" }] }
        ];
        var network = new SystemNetworkRule { Id = "NewService", Kind = "power", SegmentProfiles = ["wire"] };
        system.Networks = [network];
        void Device(string id, Cell cell, string role)
        {
            system.Devices.Add(new() { Id = id, ProfileId = "device", BindExisting = true, Scope = "station", ContentIds = [id] });
            project.Definition.Departments[0].Rooms[0].Contents.Add(new() { Id = id, Prototypes = ["SubstationBasic"] });
            project.Layout.Equipment.Add(new() { Id = id, RequirementId = id, Prototype = "SubstationBasic", RoomId = "Alpha/Work#1", Position = cell, Footprint = [cell], WorkArea = [cell] });
            project.Layout.SystemDevices.Add(new() { Id = "bound:" + id, EntityId = id, RequirementId = id, ProfileId = "device", ScopeId = "$station" });
            network.Terminals.Add(new() { DeviceRuleId = id, PortId = "power", Role = role });
        }
        Device("Source", new(4, 10), "source");
        Device("Consumer", new(23, 10), "consumer");
        if (secondSource) Device("OtherSource", new(13, 10), "source");
        return project;
    }

    [Test]
    public void CompatibleProtectedWireIsReusedAcrossServiceScopesWithOnePhysicalIdentity()
    {
        var project = Fixture();
        project.Layout.SystemNetworks.Add(new() { Id = "existing", RequirementId = "EarlierService", ScopeId = "OtherOwner", Locked = true,
            Segments = [new() { Id = "protected-wire", ProfileId = "wire", Position = new(12, 10), Rotation = 90, Locked = true }] });
        var result = DesignSystemsGeneration.Run(project, "power");
        Assert.That(result.Report!.Diagnostics.Any(issue => issue.Code is "system-native-route" or "system-routing-budget"), Is.False);
        var segments = result.Layout.SystemNetworks.SelectMany(network => network.Segments).ToList();
        Assert.That(segments.Count(segment => segment.Position == new Cell(12, 10)), Is.EqualTo(1));
        var retained = segments.Single(segment => segment.Id == "protected-wire");
        Assert.That(retained.Rotation, Is.EqualTo(90)); Assert.That(retained.Locked, Is.True);
        Assert.That(segments.Select(segment => segment.Position), Does.Contain(new Cell(4, 10)).And.Contain(new Cell(23, 10)));
    }

    [Test]
    public void AllDeclaredSuppliesArePhysicallyJoinedInsteadOfAssumedConnected()
    {
        var result = DesignSystemsGeneration.Run(Fixture(secondSource: true), "power");
        var cells = result.Layout.SystemNetworks.SelectMany(network => network.Segments).Select(segment => segment.Position).ToHashSet();
        Assert.That(new GridBounds(4, 10, 20, 1).Cells().All(cells.Contains), Is.True,
            "Both sources and the consumer need actual same-tile cable and an uninterrupted physical path.");
        Assert.That(result.Report!.Diagnostics.Any(issue => issue.Code is "system-native-route" or "system-routing-budget"), Is.False);
    }
}
