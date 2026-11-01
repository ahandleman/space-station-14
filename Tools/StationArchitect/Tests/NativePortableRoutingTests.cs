using System.Text.Json;
using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
public sealed class NativePortableRoutingTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void PortableSuppliesRetainConnectorsAndAnExtendableTrunk(bool adjacentInitialSupplies)
    {
        var project = new StationDesignProject { Definition = ProgramTestData.Minimal(), Settings = new() { Width = 32, Height = 24 }, Stage = "systemsDevices" };
        var room = new DesignRoom { Id = "Alpha/Work#1", DefinitionPath = "Alpha/Work", OwnerId = "Alpha", Instance = "1", Seed = new(14, 10), Cells = new GridBounds(4, 8, 22, 5).Cells().ToList() };
        project.Layout.Rooms = [room]; project.Layout.Envelope = [.. room.Cells];
        var systems = project.Definition.Systems; systems.Enabled = true;
        const double pressure = 1000, volume = 1000, temperature = 293.15;
        var moles = pressure * volume / (8.31446261815324 * temperature);
        systems.Profiles = [new()
        {
            Id = "Portable", Prototype = "AirCanister", RequiredComponents = ["NodeContainer", "GasCanister"],
            Ports = [new() { Id = "port", Kind = "gas", Connection = "portable", NodeGroup = "Pipe" }],
            Sources = [new() { PortId = "port", Kind = "gas", PressureKpa = pressure, VolumeLitres = volume, TemperatureKelvin = temperature, GasFractions = new() { ["Oxygen"] = .21, ["Nitrogen"] = .79 } }],
            Components = new() { ["GasCanister"] = JsonSerializer.SerializeToElement(new { air = new { volume, temperature, moles = new Dictionary<string, double> { ["Oxygen"] = moles * .21, ["Nitrogen"] = moles * .79 } } }) }
        }, Pipe("Straight", "pipe", "South", "North"), Pipe("Bend", "pipe", "South", "West"), Pipe("Tee", "pipe", "South", "East", "West"),
            Pipe("Cross", "pipe", "South", "East", "North", "West"), Pipe("Connector", "pipePort", "South")];
        // A -> B's shortest path crosses C. C must still receive its real
        // same-tile unary connector after that earlier branch is constructed.
        // Adjacent A/B must not become a closed pair of unary connectors
        // which can never accept the third terminal.
        foreach (var (id, position) in new[] { ("A", new Cell(6, 10)), ("B", new Cell(adjacentInitialSupplies ? 7 : 23, 10)), ("C", new Cell(14, 10)) })
        {
            project.Definition.Departments[0].Rooms[0].Contents.Add(new() { Id = id, Prototypes = ["AirCanister"] });
            systems.Devices.Add(new() { Id = id, ProfileId = "Portable", BindExisting = true, ContentIds = [id], Content = new() { Prototypes = ["AirCanister"] } });
            project.Layout.Equipment.Add(new() { Id = id, RoomId = room.Id, RequirementId = id, Prototype = "AirCanister", Position = position, Footprint = [position], WorkArea = [position] });
            project.Layout.SystemDevices.Add(new() { Id = "bound:" + id, RequirementId = id, EntityId = id, ProfileId = "Portable", ScopeId = room.Id });
        }
        systems.Networks = [new() { Id = "Air", Kind = "gas", SegmentProfiles = ["Straight", "Bend", "Tee", "Cross", "Connector"],
            Terminals = [new() { DeviceRuleId = "A", PortId = "port", Role = "source" }, new() { DeviceRuleId = "B", PortId = "port", Role = "source" }, new() { DeviceRuleId = "C", PortId = "port", Role = "source" }] }];
        var result = DesignSystemsGeneration.Run(project, "gas");
        Assert.That(result.Systems!.Terminals, Has.Count.EqualTo(3));
        Assert.That(result.Systems.Terminals.All(terminal => terminal.Connected && terminal.SourceReachable), Is.True,
            string.Join(Environment.NewLine, result.Systems.Diagnostics.Select(issue => issue.Code + ": " + issue.Message)));
        var atMiddle = result.Layout.SystemNetworks.SelectMany(network => network.Segments).Where(segment => segment.Position == new Cell(14, 10)).ToList();
        Assert.That(atMiddle, Has.Count.EqualTo(1));
        Assert.That(atMiddle.Single().ProfileId, Is.EqualTo("Connector"), "An ordinary junction under a canister cannot replace its required same-tile native connector.");
        Assert.That(project.Layout.SystemNetworks, Is.Empty);
    }

    private static NativeSystemProfile Pipe(string id, string connection, params string[] directions) => new()
    {
        Id = id, Prototype = id, Kind = "pipe", RequiredComponents = ["NodeContainer"],
        Ports = [new() { Id = "pipe", Kind = "gas", Connection = connection, NodeGroup = "Pipe", Directions = [.. directions], VolumeLitres = 100 }]
    };
}
