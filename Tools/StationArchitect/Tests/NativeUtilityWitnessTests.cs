using System.Text.Json;
using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
public sealed class NativeUtilityWitnessTests
{
    [TestCase("walking", false)]
    [TestCase("interaction", false)]
    [TestCase("utility", true)]
    public void UtilityNetworkIsAnExplicitUtilityOnlyAuthoringMode(string kind, bool accepted)
    {
        var definition = ProgramTestData.Minimal();
        var beta = ProgramJson.Clone(definition.Departments[0]); beta.Id = "Beta"; definition.Departments.Add(beta);
        definition.Connections = [new() { Id = "Circuit", From = "Alpha/Work", To = ["Beta/Work"], Kind = kind, Modes = ["utilityNetwork"] }];
        var compiled = RequirementCompiler.Compile(definition);
        Assert.That(compiled.Diagnostics.Any(issue => issue.Code == "mode-kind"), Is.EqualTo(!accepted));
        if (accepted) Assert.That(compiled.Success, Is.True, ProgramTestData.Errors(compiled.Diagnostics));
    }

    [TestCase("department", 10, 21)]
    [TestCase("maintenance", 12, 25)]
    [TestCase("utilityNetwork", 10, 13)]
    public void ModeAwareSearchFindsAnActualAlternativeAndRejectsTheShortForeignCircuit(string mode, int longY, int expectedDistance)
    {
        var definition = ProgramTestData.Minimal(); definition.Systems.Enabled = true;
        definition.Departments[0].Area.Max = 1000;
        definition.Departments[0].Rooms.AddRange([ProgramTestData.Room("Target"), ProgramTestData.Room("Bridge", 1, 2, 10)]);
        var beta = ProgramJson.Clone(definition.Departments[0]); beta.Id = "Beta"; beta.Rooms = [ProgramTestData.Room("Transit", 1, 12, 100)]; definition.Departments.Add(beta);
        definition.Connections = [new() { Id = "Power", From = "Alpha/Work", To = ["Alpha/Target"], Kind = "utility", Modes = [mode] }];
        definition.Systems.Profiles = [new()
        {
            Id = "Source", Prototype = "SMESBasic", RequiredComponents = ["NodeContainer", "Battery", "BatteryDischarger", "PowerNetworkBattery"],
            Ports = [new() { Id = "output", Kind = "power", Connection = "cableDevice", NodeGroup = "MVPower" }],
            Sources = [new() { Kind = "power", PortId = "output", StoredEnergyJoules = 10000 }],
            Components = new() { ["Battery"] = JsonSerializer.SerializeToElement(new { startingCharge = 10000, maxCharge = 50000 }) }
        }, new()
        {
            Id = "Target", Prototype = "APCBasic", RequiredComponents = ["NodeContainer"], Ports = [new() { Id = "input", Kind = "power", Connection = "cableDevice", NodeGroup = "MVPower" }]
        }, new()
        {
            Id = "Wire", Prototype = "CableMV", Kind = "cable", RequiredComponents = ["NodeContainer"], Ports = [new() { Id = "power", Kind = "power", Connection = "cable", NodeGroup = "MVPower" }]
        }];
        var project = new StationDesignProject { Definition = definition, Stage = "power", Settings = new() { Width = 32, Height = 24 } };
        project.Layout.Rooms = [new() { Id = "Alpha/Work#1", DefinitionPath = "Alpha/Work", OwnerId = "Alpha", Cells = new GridBounds(4, 4, 8, 7).Cells().ToList() },
            new() { Id = "Alpha/Target#1", DefinitionPath = "Alpha/Target", OwnerId = "Alpha", Cells = new GridBounds(14, 4, 8, 7).Cells().ToList() },
            new() { Id = "Alpha/Bridge#1", DefinitionPath = "Alpha/Bridge", OwnerId = "Alpha", Cells = [new(12, 10), new(13, 10)] },
            new() { Id = "Beta/Transit#1", DefinitionPath = "Beta/Transit", OwnerId = "Beta", Cells = new GridBounds(12, 4, 2, 6).Cells().ToList() }];
        project.Layout.Envelope = new GridBounds(2, 2, 25, 15).Cells().ToList();
        project.Layout.Walls = project.Layout.Rooms.SelectMany(room => room.Cells).SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Except(project.Layout.Rooms.SelectMany(room => room.Cells)).Distinct().ToList();
        if (mode == "maintenance")
        {
            project.Layout.Maintenance = new GridBounds(5, 12, 16, 2).Cells().ToList();
            foreach (var (room, x) in new[] { ("Alpha/Work#1", 6), ("Alpha/Target#1", 19) })
                project.Layout.Interfaces.Add(new() { Id = room + ":maint", From = room, To = "$maintenance", Cells = [new(x, 11)], FromApproach = new(x, 10), ToApproach = new(x, 12) });
        }
        Bind("Source", "Alpha/Work#1", "Alpha/Work", "SMESBasic", new(6, 6));
        Bind("Target", "Alpha/Target#1", "Alpha/Target", "APCBasic", new(19, 6));
        definition.Systems.Networks = [new() { Id = "Power", Kind = "power", SegmentProfiles = ["Wire"], Terminals = [new() { DeviceRuleId = "Source", PortId = "output", Role = "source" }, new() { DeviceRuleId = "Target", PortId = "input" }] }];
        var wire = Enumerable.Range(6, 14).Select(x => new Cell(x, 6)).Concat(Enumerable.Range(6, longY - 5).Select(y => new Cell(6, y))).Concat(Enumerable.Range(6, 14).Select(x => new Cell(x, longY))).Concat(Enumerable.Range(6, longY - 5).Select(y => new Cell(19, y))).Distinct().ToList();
        project.Layout.SystemNetworks.Add(new() { Id = "Power", RequirementId = "Power", Terminals = [new() { DeviceId = "bound:Source", PortId = "output", Role = "source" }, new() { DeviceId = "bound:Target", PortId = "input" }], Segments = wire.Select((cell, index) => new DesignSystemSegment { Id = "wire" + index, ProfileId = "Wire", Position = cell }).ToList() });
        var route = new DesignRoute { Id = "power", RequirementId = "$station:Power", From = "Alpha/Work#1", To = "Alpha/Target#1", Kind = "utility", Mode = mode, Cells = [new(6, 6)] };
        project.Layout.Utilities.Add(new() { Id = route.Id, RequirementId = route.RequirementId, From = route.From, To = route.To, Type = "cable" });
        project.Layout.Routes.Add(route);
        var unconstrained = DesignSystems.CreateUtilityInspector(project)(route.From, route.To, "cable");
        Assert.That(unconstrained.Distance, Is.EqualTo(13));
        var observed = DesignSystems.CreateUtilityRouteInspector(project)(route);
        Assert.That(observed.Verified, Is.True);
        Assert.That(observed.Distance, Is.EqualTo(expectedDistance));
        Assert.That(observed.PortIds.Distinct().Count(), Is.EqualTo(observed.PortIds.Count), "An unrelated network spur cannot count through an out-and-back walk.");
        Assert.That(DesignMeasurements.RouteValid(project, route), Is.True);
        Assert.That(DesignMeasurements.Build(project).Connections.Single().Distance, Is.EqualTo(expectedDistance));
        project.Layout.SystemNetworks[0].Segments.RemoveAll(segment => segment.Position.Y != 6);
        if (mode == "utilityNetwork")
        {
            Assert.That(DesignSystems.CreateUtilityRouteInspector(project)(route).Verified, Is.True, "Explicit utilityNetwork permits the actual supported foreign-room circuit.");
            route.Kind = "walking";
            Assert.That(DesignMeasurements.RouteValid(project, route), Is.False);
            route.Kind = "utility";
            project.Layout.SystemNetworks[0].Segments.RemoveAll(segment => segment.Position == new Cell(12, 6));
        }
        Assert.That(DesignSystems.CreateUtilityRouteInspector(project)(route).Verified, Is.False);
        Assert.That(DesignMeasurements.RouteValid(project, route), Is.False, "The remaining shorter foreign circuit cannot satisfy the authored mode.");
        if (mode == "department")
        {
            var repaired = DesignSystemsGeneration.Run(project, "power");
            var generated = DesignSystems.CreateUtilityRouteInspector(repaired, final: true)(route);
            Assert.That(generated.Verified, Is.True, ProgramTestData.Errors(repaired.Report!.Diagnostics));
            Assert.That(generated.Distance, Is.EqualTo(expectedDistance));
            Assert.That(project.Layout.SystemNetworks[0].Segments.All(segment => segment.Position.Y == 6), Is.True, "Generation clones the caller and adds its constrained branch only on the new draft.");
        }


        void Bind(string id, string roomId, string path, string prototype, Cell cell)
        {
            definition.Departments[0].Rooms.First(room => "Alpha/" + room.Id == path).Contents.Add(new() { Id = id, Prototypes = [prototype] });
            definition.Systems.Devices.Add(new() { Id = id, ProfileId = id, BindExisting = true, Selector = new() { Paths = [path] }, Scope = "station", ContentIds = [id], Content = new() { Prototypes = [prototype] } });
            project.Layout.Equipment.Add(new() { Id = id, RoomId = roomId, RequirementId = id, Prototype = prototype, Position = cell, Footprint = [cell], WorkArea = [cell] });
            project.Layout.SystemDevices.Add(new() { Id = "bound:" + id, RequirementId = id, ProfileId = id, EntityId = id, ScopeId = "$station" });
        }
    }
}
