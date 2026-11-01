using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
public sealed class DesignTransportTests
{
    private static StationDesignProject Fixture(string mode = "disposalShortcut", string kind = "walking", bool bend = false)
    {
        var definition = new StationProgramDocument
        {
            Id = "TransportFixture",
            Departments =
            [
                new() { Id = "Alpha", Area = ProgramTestData.Area(16, 25, 120), Rooms = [ProgramTestData.Room("Work", 16, 25, 120)] },
                new() { Id = "Beta", Area = ProgramTestData.Area(16, 25, 120), Rooms = [ProgramTestData.Room("Lab", 16, 25, 120)] }
            ],
            Connections = [new() { Id = "Pneumatic", From = "Alpha/Work", To = ["Beta/Lab"], Kind = kind, Modes = [mode], Priority = "required", Presence = "requireTarget" }]
        };
        var project = new StationDesignProject { Definition = definition, Stage = "circulation", Settings = new() { Width = 40, Height = 30 } };
        project.Layout.Rooms =
        [
            new() { Id = "Alpha/Work#1", DefinitionPath = "Alpha/Work", OwnerId = "Alpha", Instance = "1", Seed = new(4, 4), Cells = new GridBounds(2, 2, 5, 5).Cells().ToList() },
            new() { Id = "Beta/Lab#1", DefinitionPath = "Beta/Lab", OwnerId = "Beta", Instance = "1", Seed = new(20, bend ? 14 : 4), Cells = new GridBounds(18, bend ? 12 : 2, 5, 5).Cells().ToList() }
        ];
        var publicFloor = new GridBounds(8, 3, 9, 3).Cells().ToHashSet();
        if (bend)
        {
            publicFloor.UnionWith(new GridBounds(15, 3, 6, 3).Cells());
            publicFloor.UnionWith(new GridBounds(19, 6, 3, 5).Cells());
        }
        project.Layout.PublicCorridors = LayoutGeometry.Sorted(publicFloor);
        var floor = project.Layout.Rooms.SelectMany(room => room.Cells).Concat(publicFloor).ToHashSet();
        project.Layout.Walls = LayoutGeometry.Sorted(floor.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Except(floor));
        project.Layout.Envelope = new GridBounds(0, 0, 40, 30).Cells().ToList();
        return project;
    }

    private static DesignRoute Build(StationDesignProject project)
    {
        var requirement = RequirementCompiler.Compile(project.Definition).Connections.Single();
        var route = DesignTransport.TryBuild(project, project.Layout.Rooms[0], project.Layout.Rooms[1], requirement, requirement.Modes[0]);
        Assert.That(route, Is.Not.Null);
        project.Layout.Routes.Add(route!);
        return route!;
    }

    [Test]
    public void CrewShortcutHasRealUnitsTrunksAndRotatedContiguousTubeSegments()
    {
        var project = Fixture(); var route = Build(project); var network = project.Layout.TransportNetworks.Single();
        Assert.That(DesignTransport.RouteValid(project, route), Is.True);
        Assert.That(network.Terminals.Select(unit => unit.Prototype), Is.EqualTo(new[] { "DisposalUnit", "DisposalUnit" }));
        Assert.That(network.Segments[0].Prototype, Is.EqualTo("DisposalTrunk"));
        Assert.That(network.Segments[^1].Prototype, Is.EqualTo("DisposalTrunk"));
        Assert.That(network.Segments.Skip(1).SkipLast(1).All(segment => segment.Prototype == "DisposalPipe"), Is.True);
        Assert.That(network.Segments.Select(segment => segment.Position), Is.EqualTo(route.Cells));
        Assert.That(DesignTransport.CrewEdges(project).Count(), Is.EqualTo(1));
        Assert.That(DesignTransport.EndpointFloorCells(project).Count, Is.EqualTo(2));
        Assert.That(DesignTransport.Validate(project), Is.Empty);
    }

    [Test]
    public void ADeclaredModeWithoutPhysicalNetworkNeverPasses()
    {
        var project = Fixture();
        var route = new DesignRoute { RequirementId = "$station:Pneumatic", From = "Alpha/Work#1", To = "Beta/Lab#1", Kind = "walking", Mode = "disposalShortcut", Cells = [new(6, 4), new(7, 4), new(8, 4)] };
        Assert.That(DesignTransport.RouteValid(project, route), Is.False);
        Assert.That(DesignTransport.Measure(project, route), Is.Null);
    }

    [Test]
    public void BrokenOrMisorientedTrunksAndBendsInvalidateTransportProof()
    {
        var project = Fixture(bend: true); var route = Build(project); var network = project.Layout.TransportNetworks.Single();
        var bend = network.Segments.First(segment => segment.Role == "bend");
        bend.Rotation = (bend.Rotation + 90) % 360;
        Assert.That(DesignTransport.RouteValid(project, route), Is.False);
        bend.Rotation = (bend.Rotation + 270) % 360;
        Assert.That(DesignTransport.RouteValid(project, route), Is.True);
        network.Segments.RemoveAt(0);
        Assert.That(DesignTransport.RouteValid(project, route), Is.False);
        Assert.That(DesignTransport.CrewEdges(project), Is.Empty);
    }

    [Test]
    public void MailUsesItemOnlyUnitsAndReciprocalAddresses()
    {
        var project = Fixture("mail", "utility"); var route = Build(project); var network = project.Layout.TransportNetworks.Single();
        Assert.That(network.Terminals.All(unit => unit.Prototype == "MailingUnit"), Is.True);
        Assert.That(network.Terminals[0].TargetTag, Is.EqualTo(network.Terminals[1].Tag));
        Assert.That(network.Terminals[1].TargetTag, Is.EqualTo(network.Terminals[0].Tag));
        Assert.That(DesignTransport.CrewEdges(project), Is.Empty);
        var measurement = DesignTransport.Measure(project, route)!;
        Assert.That(measurement.Mode, Is.EqualTo("mail"));
        Assert.That(measurement.Kind, Is.EqualTo("utility"));
        Assert.That(measurement.Distance, Is.EqualTo(network.Segments.Count - 1));
        Assert.That(measurement.InterfaceType, Is.EqualTo("pipe"));
        network.Terminals[0].TargetTag = "Wrong Address";
        Assert.That(DesignTransport.RouteValid(project, route), Is.False);
    }

    [Test]
    public void MailDiscoveryFrequencyIsDeterministicAndSharedOnlyByThePair()
    {
        var first = Fixture("mail", "utility"); var route = Build(first); var network = first.Layout.TransportNetworks.Single();
        var replay = Fixture("mail", "utility"); Build(replay);
        Assert.That(network.DeviceFrequency, Is.InRange(40000, 65535));
        Assert.That(replay.Layout.TransportNetworks.Single().DeviceFrequency, Is.EqualTo(network.DeviceFrequency));
        Assert.That(network.Terminals.All(terminal => terminal.DeviceFrequency == network.DeviceFrequency), Is.True);
        network.Terminals[0].DeviceFrequency = network.DeviceFrequency - 1;
        Assert.That(DesignTransport.RouteValid(first, route), Is.False);
        network.Terminals[0].DeviceFrequency = network.DeviceFrequency;
        network.DeviceFrequency = null;
        Assert.That(DesignTransport.RouteValid(first, route), Is.False);
        Assert.That(DesignTransport.Validate(first).Any(issue => issue.Code == "transport-mail-frequency"), Is.True);
        first.Settings.TransportProfiles.Single(profile => profile.Mode == "mail").MailFrequencyMax = 65536;
        Assert.That(DesignTransport.ValidateConfiguration(first), Is.Not.Empty);
    }

    [Test]
    public void MailFrequencyAllocationRespectsRetainedRecordsAndRejectsLockedCollisions()
    {
        var reference = Fixture("mail", "utility"); Build(reference); var baseFrequency = reference.Layout.TransportNetworks.Single().DeviceFrequency;
        var project = Fixture("mail", "utility");
        var retained = new DesignTransportNetwork { Id = "retained-edited-mail", Mode = "mail", DeviceFrequency = baseFrequency, Edited = true, Locked = true };
        project.Layout.TransportNetworks.Add(retained);
        var route = Build(project); var generated = project.Layout.TransportNetworks.Single(network => network.Id != retained.Id);
        Assert.That(generated.DeviceFrequency, Is.Not.EqualTo(baseFrequency));
        Assert.That(retained.DeviceFrequency, Is.EqualTo(baseFrequency));
        Assert.That(DesignTransport.RouteValid(project, route), Is.True);
        retained.DeviceFrequency = generated.DeviceFrequency;
        generated.Locked = true;
        var requirement = RequirementCompiler.Compile(project.Definition).Connections.Single();
        Assert.That(DesignTransport.RouteValid(project, route), Is.False);
        Assert.That(DesignTransport.TryBuild(project, project.Layout.Rooms[0], project.Layout.Rooms[1], requirement, "mail"), Is.Null);
        Assert.That(project.Layout.TransportNetworks, Has.Count.EqualTo(2));
        Assert.That(retained.DeviceFrequency, Is.EqualTo(generated.DeviceFrequency));
        Assert.That(DesignTransport.Validate(project).Any(issue => issue.Code == "transport-mail-frequency"), Is.True);
    }

    [Test]
    public void MailingCannotBeRelabeledAsCrewConnectivity()
    {
        var source = Fixture("mail", "walking");
        Assert.That(RequirementCompiler.Compile(source.Definition).Success, Is.False);
        var project = Fixture("mail", "utility"); var route = Build(project); route.Kind = "walking";
        Assert.That(DesignTransport.RouteValid(project, route), Is.False);
        project.Settings.TransportProfiles.Single(profile => profile.Mode == "mail").CarriesCrew = true;
        Assert.That(DesignTransport.ValidateConfiguration(project), Is.Not.Empty);
    }

    [Test]
    public void UnderfloorRoutesCannotBridgeUnmaterializedSpace()
    {
        var project = Fixture(); project.Layout.PublicCorridors.Clear();
        var roomFloor = project.Layout.Rooms.SelectMany(room => room.Cells).ToHashSet();
        project.Layout.Walls = LayoutGeometry.Sorted(roomFloor.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Except(roomFloor));
        project.Settings.TransportProfiles.Single(profile => profile.Mode == "disposalShortcut").AllowExteriorFloor = true;
        var requirement = RequirementCompiler.Compile(project.Definition).Connections.Single();
        Assert.That(DesignTransport.TryBuild(project, project.Layout.Rooms[0], project.Layout.Rooms[1], requirement, "disposalShortcut"), Is.Null);
        Assert.That(project.Layout.TransportNetworks, Is.Empty);
    }

    [Test]
    public void RemovingBuiltSupportOrBlockingAUnitInvalidatesTheLine()
    {
        var project = Fixture(); var route = Build(project);
        var point = route.Cells.First(project.Layout.PublicCorridors.Contains);
        project.Layout.PublicCorridors.Remove(point);
        Assert.That(DesignTransport.RouteValid(project, route), Is.False);
        project.Layout.PublicCorridors.Add(point);
        var unit = project.Layout.TransportNetworks.Single().Terminals[0];
        project.Layout.Equipment.Add(new() { Id = "blocking-machine", RoomId = unit.RoomId, Prototype = "Autolathe", Position = unit.Position, Footprint = [unit.Position] });
        Assert.That(DesignTransport.RouteValid(project, route), Is.False);
    }

    [Test]
    public void LockedBrokenNetworksAreRetainedForExplicitRepair()
    {
        var project = Fixture(); Build(project); var network = project.Layout.TransportNetworks.Single();
        network.Locked = true; network.Segments.RemoveAt(1);
        var requirement = RequirementCompiler.Compile(project.Definition).Connections.Single();
        Assert.That(DesignTransport.TryBuild(project, project.Layout.Rooms[0], project.Layout.Rooms[1], requirement, "disposalShortcut"), Is.Null);
        Assert.That(project.Layout.TransportNetworks.Single(), Is.SameAs(network));
        Assert.That(DesignTransport.Validate(project).Any(issue => issue.Code == "transport-geometry"), Is.True);
    }

    [Test]
    public void ConfigurablePrototypeProfilesDetermineActualPlacedHardware()
    {
        var project = Fixture(); var profile = project.Settings.TransportProfiles.Single(profile => profile.Mode == "disposalShortcut");
        profile.Id = "ServerCustomCrew"; profile.EntryUnitPrototype = "ServerEntry"; profile.ExitUnitPrototype = "ServerExit"; profile.TrunkPrototype = "ServerTrunk"; profile.StraightPrototype = "ServerStraight";
        var route = Build(project); var network = project.Layout.TransportNetworks.Single();
        Assert.That(network.ProfileId, Is.EqualTo(profile.Id));
        Assert.That(network.Terminals.Select(unit => unit.Prototype), Is.EqualTo(new[] { "ServerEntry", "ServerExit" }));
        Assert.That(network.Segments[0].Prototype, Is.EqualTo("ServerTrunk"));
        Assert.That(network.Segments.Skip(1).SkipLast(1).All(segment => segment.Prototype == "ServerStraight"), Is.True);
        Assert.That(DesignTransport.RouteValid(project, route), Is.True);
    }

    [Test]
    public void RestrictedMaintenanceApproachCannotGainADirectCrewTubeBypass()
    {
        var project = Fixture(); project.Definition.Departments[1].Rooms[0].Access.RequireNetworkApproach = ["maintenance"];
        var requirement = RequirementCompiler.Compile(project.Definition).Connections.Single();
        Assert.That(DesignTransport.TryBuild(project, project.Layout.Rooms[0], project.Layout.Rooms[1], requirement, "disposalShortcut"), Is.Null);
    }

    [Test]
    public void WrongLengthWrongRequirementAndWrongEndpointCannotForgeEvidence()
    {
        var project = Fixture(); var route = Build(project); var network = project.Layout.TransportNetworks.Single();
        network.From = "Beta/Lab#1"; Assert.That(DesignTransport.RouteValid(project, route), Is.False); network.From = route.From;
        project.Definition.Connections[0].MaxDistance = 1;
        Assert.That(DesignTransport.RouteValid(project, route), Is.False);
        project.Definition.Connections[0].MaxDistance = null; project.Definition.Connections[0].To = ["Alpha"];
        Assert.That(DesignTransport.RouteValid(project, route), Is.False);
    }
}
