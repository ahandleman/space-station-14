using System.Text.Json;
using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
public sealed class NativeTerminalPresenceTests
{
    private static StationDesignProject Project()
    {
        var project = new StationDesignProject { Definition = ProgramTestData.Minimal(), Settings = new() { Width = 32, Height = 24 }, Stage = "disposals" };
        project.Definition.Systems.Enabled = true;
        project.Layout.Rooms = [new() { Id = "Alpha/Work#1", DefinitionPath = "Alpha/Work", OwnerId = "Alpha", Instance = "1", Cells = new GridBounds(4, 4, 16, 7).Cells().ToList(), Seed = new(10, 6) }];
        project.Layout.Envelope = [.. project.Layout.Rooms[0].Cells];
        return project;
    }

    private static void Bind(StationDesignProject project, string id, string profileId, Cell position, bool present = true)
    {
        var profile = project.Definition.Systems.Profiles.Single(profile => profile.Id == profileId);
        var content = new RoomContentRequirement { Id = id, Prototypes = [profile.Prototype], Count = new() { Min = 0, Target = 0, Max = 1 } };
        project.Definition.Departments[0].Rooms[0].Contents.Add(content);
        project.Definition.Systems.Devices.Add(new() { Id = id, ProfileId = profileId, BindExisting = true, ContentIds = [id], Content = ProgramJson.Clone(content) });
        if (!present) return;
        project.Layout.Equipment.Add(new() { Id = id, RoomId = "Alpha/Work#1", RequirementId = id, Prototype = profile.Prototype, Position = position, Footprint = [position], WorkArea = [position] });
        project.Layout.SystemDevices.Add(new() { Id = "bound:" + id, RequirementId = id, EntityId = id, ProfileId = profileId, ScopeId = "Alpha/Work#1" });
    }

    private static StationDesignProject OptionalPower(bool present)
    {
        var project = Project();
        project.Definition.Systems.Profiles = [new()
        {
            Id = "Battery", Prototype = "SMESBasic", RequiredComponents = ["NodeContainer", "Battery", "BatteryDischarger", "PowerNetworkBattery"],
            Ports = [new() { Id = "output", Kind = "power", Connection = "cableDevice", NodeGroup = "HVPower" }],
            Sources = [new() { PortId = "output", StoredEnergyJoules = 1000 }],
            Components = new() { ["Battery"] = JsonSerializer.SerializeToElement(new { startingCharge = 1000, maxCharge = 1000 }) }
        }];
        Bind(project, "OptionalSupply", "Battery", new(6, 6), present);
        project.Definition.Systems.Networks = [new() { Id = "Power", Kind = "power", Terminals = [new() { DeviceRuleId = "OptionalSupply", PortId = "output", Role = "source", Required = false }] }];
        project.Layout.SystemNetworks = [new() { Id = "Power", RequirementId = "Power", ScopeId = "$station", Terminals = present ? [new() { DeviceId = "bound:OptionalSupply", PortId = "output", Role = "source" }] : [] }];
        return project;
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OptionalSelectorAllowsAbsentHardwareButChecksEveryPresentTerminal(bool present)
    {
        var project = OptionalPower(present);
        var report = DesignSystems.Validate(project);
        Assert.That(report.Diagnostics.Any(issue => issue.Code == "native-terminal-missing"), Is.False);
        Assert.That(report.Diagnostics.Any(issue => issue.Code == "native-terminal-unserved"), Is.EqualTo(present));
        if (present)
        {
            Assert.That(report.Terminals.Single().SourceReachable, Is.True, "A battery's own source must not hide its missing physical connection.");
            Assert.That(report.Terminals.Single().Connected, Is.False);
            Assert.That(report.Metrics["verifiedTerminals"], Is.Zero, "An isolated source is not a verified physical network terminal.");
        }
        else Assert.That(report.Success, Is.True, ProgramTestData.Errors(report.Diagnostics));
    }

    [Test]
    public void PresentHardwareCannotLoseItsTerminalBindingBehindAnOptionalSelector()
    {
        var project = OptionalPower(true); project.Layout.SystemNetworks[0].Terminals.Clear();
        Assert.That(DesignSystems.Validate(project).Diagnostics.Any(issue => issue.Code == "native-terminal-binding"), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DisposalProofUsesNativePreferredExitRatherThanUndirectedTubeConnectivity(bool wrongPreferredExit)
    {
        var project = Disposal(wrongPreferredExit);
        var report = DesignSystems.Validate(project); var inlet = report.Terminals.Single(terminal => terminal.Role == "inlet");
        Assert.That(inlet.SourceReachable, Is.EqualTo(!wrongPreferredExit), ProgramTestData.Errors(report.Diagnostics));
        Assert.That(report.Diagnostics.Any(issue => issue.Code == "native-terminal-unserved"), Is.EqualTo(wrongPreferredExit), "Present optional inlets must not silently eject waste through a preferred exit into empty space.");
        if (!wrongPreferredExit) Assert.That(report.Success, Is.True, ProgramTestData.Errors(report.Diagnostics));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NativeDirectionRepairPreservesTopologyAndRetainsLockedBadFlow(bool locked)
    {
        var project = Disposal(true); project.Layout.SystemNetworks[0].Segments.Single(segment => segment.Id == "pipe:10").Locked = locked;
        var original = JsonSerializer.Serialize(project, ProgramJson.Options);
        var result = DesignSystemsGeneration.NormalizeDisposal(project);
        Assert.That(JsonSerializer.Serialize(project, ProgramJson.Options), Is.EqualTo(original));
        Assert.That(result.Layout.Rooms.Single().Cells, Is.EqualTo(project.Layout.Rooms.Single().Cells));
        Assert.That(result.Layout.SystemNetworks[0].Segments.Select(segment => segment.Position), Is.EqualTo(project.Layout.SystemNetworks[0].Segments.Select(segment => segment.Position)));
        Assert.That(result.Systems!.Terminals.Single(terminal => terminal.Role == "inlet").SourceReachable, Is.EqualTo(!locked));
        var repaired = result.Layout.SystemNetworks[0].Segments.Single(segment => segment.Id == "pipe:10");
        if (locked)
        {
            Assert.That(repaired.ProfileId, Is.EqualTo("Junction")); Assert.That(repaired.Rotation, Is.EqualTo(180)); Assert.That(repaired.Locked, Is.True);
            Assert.That(result.Report!.Diagnostics.Any(issue => issue.Code == "native-terminal-unserved"), Is.True);
        }
        else Assert.That(result.Systems.Success, Is.True, ProgramTestData.Errors(result.Systems.Diagnostics));
    }

    [Test]
    public void MergingSideBranchesUsesTheAuthoredNativeYJunctionToPreferTheOutletStem()
    {
        var project = Disposal(false); var systems = project.Definition.Systems;
        var input = project.Layout.Equipment.Single(entity => entity.Id == "Input"); input.Position = new(8, 10); input.Footprint = [input.Position]; input.WorkArea = [input.Position];
        var output = project.Layout.Equipment.Single(entity => entity.Id == "Receiving"); output.Position = new(10, 6); output.Footprint = [output.Position]; output.WorkArea = [output.Position]; output.Rotation = 180;
        Bind(project, "Input2", "Unit", new(12, 10));
        systems.Profiles.Add(new() { Id = "Side", Prototype = "DisposalJunction", Kind = "disposal", RequiredComponents = ["DisposalTube"], Ports = [new() { Id = "tube", Kind = "disposal", Connection = "disposal", NodeGroup = "Disposal", Directions = ["South", "West", "North"] }] });
        systems.Networks[0].SegmentProfiles.Add("Side"); systems.Networks[0].Terminals.Add(new() { DeviceRuleId = "Input2", PortId = "entry", Role = "inlet", Required = false });
        var network = project.Layout.SystemNetworks[0]; network.Terminals.Add(new() { DeviceId = "bound:Input2", PortId = "entry", Role = "inlet" });
        network.Segments = [new() { Id = "left", ProfileId = "Trunk", Position = new(8, 10), Rotation = 90 }, new() { Id = "right", ProfileId = "Trunk", Position = new(12, 10), Rotation = 270 },
            new() { Id = "merge", ProfileId = "Side", Position = new(10, 10), Rotation = 90 }, new() { Id = "left-pipe", ProfileId = "Straight", Position = new(9, 10), Rotation = 90 }, new() { Id = "right-pipe", ProfileId = "Straight", Position = new(11, 10), Rotation = 90 }];
        for (var y = 7; y < 10; y++) network.Segments.Add(new() { Id = "stem:" + y, ProfileId = "Straight", Position = new(10, y) });
        var result = DesignSystemsGeneration.NormalizeDisposal(project);
        Assert.That(result.Systems!.Success, Is.True, ProgramTestData.Errors(result.Systems.Diagnostics));
        Assert.That(result.Systems.Terminals.Where(terminal => terminal.Role == "inlet").All(terminal => terminal.SourceReachable), Is.True);
        var merge = result.Layout.SystemNetworks[0].Segments.Single(segment => segment.Id == "merge");
        Assert.That(merge.ProfileId, Is.EqualTo("Junction")); Assert.That(merge.Rotation, Is.Zero);
    }

    private static StationDesignProject Disposal(bool wrongPreferredExit)
    {
        var project = Project();
        project.Definition.Systems.Profiles = [new() { Id = "Unit", Prototype = "DisposalUnit", RequiredComponents = ["DisposalUnit"], Ports = [Port("entry", "disposalUnit")] },
            new() { Id = "Outlet", Prototype = "DisposalTrunk", RequiredComponents = ["DisposalTube", "DisposalEntry"], Ports = [Port("tube", "disposalEntry", "South")] },
            new() { Id = "Trunk", Prototype = "DisposalTrunk", Kind = "disposal", RequiredComponents = ["DisposalTube", "DisposalEntry"], Ports = [Port("tube", "disposalEntry", "South")] },
            new() { Id = "Straight", Prototype = "DisposalPipe", Kind = "disposal", RequiredComponents = ["DisposalTube"], Ports = [Port("tube", "disposal", "South", "North")] },
            new() { Id = "Junction", Prototype = "DisposalYJunction", Kind = "disposal", RequiredComponents = ["DisposalTube"], Ports = [Port("tube", "disposal", "South", "West", "East")] }];
        Bind(project, "Input", "Unit", new(6, 6)); Bind(project, "Receiving", "Outlet", new(14, 6));
        project.Layout.Equipment.Single(entity => entity.Id == "Receiving").Rotation = 270;
        project.Definition.Systems.Networks = [new() { Id = "Waste", Kind = "disposal", SegmentProfiles = ["Trunk", "Straight", "Junction"],
            Terminals = [new() { DeviceRuleId = "Input", PortId = "entry", Role = "inlet", Required = false }, new() { DeviceRuleId = "Receiving", PortId = "tube", Role = "outlet" }] }];
        var network = new DesignSystemNetwork { Id = "Waste", RequirementId = "Waste", ScopeId = "$station", Terminals = [new() { DeviceId = "bound:Input", PortId = "entry", Role = "inlet" }, new() { DeviceId = "bound:Receiving", PortId = "tube", Role = "outlet" }] };
        network.Segments.Add(new() { Id = "entry", ProfileId = "Trunk", Position = new(6, 6), Rotation = 90 });
        for (var x = 7; x < 14; x++) network.Segments.Add(new() { Id = "pipe:" + x, ProfileId = wrongPreferredExit && x == 10 ? "Junction" : "Straight", Position = new(x, 6), Rotation = wrongPreferredExit && x == 10 ? 180 : 90 });
        project.Layout.SystemNetworks = [network];
        return project;
        static NativeSystemPort Port(string id, string connection, params string[] directions) => new() { Id = id, Kind = "disposal", Connection = connection, NodeGroup = "Disposal", Directions = [.. directions] };
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void NativeCrewTravelIsDirectedAndRequiresActualFlowAndCrewCapableEntry(bool wrongPreferredExit, bool itemOnly)
    {
        var project = Disposal(wrongPreferredExit); var profile = project.Definition.Systems.Profiles.Single(profile => profile.Id == "Unit");
        if (itemOnly) profile.Components["DisposalUnit"] = JsonSerializer.SerializeToElement(new { whitelist = new { components = new[] { "Item" } } });
        var original = JsonSerializer.Serialize(project, ProgramJson.Options);
        var edges = DesignSystems.NativeCrewEdges(project);
        Assert.That(JsonSerializer.Serialize(project, ProgramJson.Options), Is.EqualTo(original));
        if (wrongPreferredExit || itemOnly) Assert.That(edges, Is.Empty);
        else
        {
            Assert.That(edges, Is.Not.Empty, "Legacy component/entry facts must conservatively recognize crew capability even before the new metadata is authored.");
            Assert.That(edges.All(edge => edge.From.Distance(new(6, 6)) == 1 && edge.To.Distance(new(14, 6)) == 1), Is.True,
                "Crew interacts from actual free floor next to the solid inlet and exits only in the forward native direction.");
            Assert.That(edges.Any(edge => edge.From.Distance(new(14, 6)) <= 1), Is.False);
        }
    }
}
