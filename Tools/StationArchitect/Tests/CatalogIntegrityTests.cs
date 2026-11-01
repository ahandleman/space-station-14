using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class CatalogIntegrityTests
{
    [Test]
    public void EveryCatalogRoomHasInspectableEvidenceAndTargetsMatchItsParent()
    {
        var document = StationProgramDefaults.Create();
        Assert.That(document.SourceRefs, Is.Not.Empty);
        foreach (var department in document.Departments)
        {
            Assert.That(department.SourceRefs, Is.Not.Empty, department.Id);
            Assert.That(RequirementCompiler.Rooms(document).Where(pair => RequirementCompiler.Owner(pair.Path) == department.Id).All(pair => pair.Room.SourceRefs.Count > 0), Is.True, department.Id);
            Assert.That(department.Area.Target, Is.EqualTo(TargetFloor(department.Rooms)), department.Id);
        }
        Assert.That(document.Maintenance.Rooms.All(room => room.SourceRefs.Count > 0), Is.True);

        static long TargetFloor(IEnumerable<RoomDefinition> rooms) => rooms.Sum(room => room.Count.Target * (room.Area.Target + TargetFloor(room.Rooms)));
    }

    [Test]
    public void ChapelIsNestedAndPublicFacilitiesCanDisperseIndependentlyOfOwnership()
    {
        var document = StationProgramDefaults.Create();
        var rooms = RequirementCompiler.Rooms(document).ToDictionary(pair => pair.Path, pair => pair.Room);
        Assert.That(rooms.ContainsKey("Arrivals/Cryosleep"), Is.False);
        Assert.That(rooms["Commons/Cryosleep"].Placement.Policy, Is.EqualTo("stationwide"));
        Assert.That(rooms["Commons/Evacuation"].Count.Min, Is.EqualTo(1));
        Assert.That(rooms["Commons/Evacuation"].Placement.Anchor, Is.EqualTo("perimeter"));
        Assert.That(rooms["Commons/Evacuation"].Connections.Any(rule => rule.Priority == "required" && rule.Modes.Contains("publicHall")), Is.True);
        foreach (var path in new[] { "Service/Chapel/Confessional", "Service/Chapel/Crematorium" })
        {
            Assert.That(rooms[path].Nesting.Internal, Is.True);
            Assert.That(rooms[path].Nesting.RequireParentConnection, Is.True);
            Assert.That(rooms[path].Nesting.AllowExternalEntry, Is.False);
        }
        Assert.That(rooms["Service/Kitchen"].Placement.Group, Is.EqualTo(rooms["Service/Botany"].Placement.Group));
        Assert.That(rooms["Service/Chapel"].Placement.Policy, Is.EqualTo("distributed"));
    }

    [Test]
    public void ShuttleDestinationsDeclareBodyClearanceBoundToNativeDockingHardware()
    {
        var rooms = RequirementCompiler.Rooms(StationProgramDefaults.Create()).ToDictionary(pair => pair.Path, pair => pair.Room);
        foreach (var path in new[] { "Arrivals/Waiting", "Commons/Evacuation", "Cargo/LoadingBay" })
        {
            var room = rooms[path];
            Assert.That(room.Exterior.Docks, Has.Count.EqualTo(1), path);
            var dock = room.Exterior.Docks[0];
            var content = room.Contents.Single(item => item.Id == dock.ContentId);
            Assert.That(content.Count.Min, Is.GreaterThanOrEqualTo(1), path);
            Assert.That(content.Placement, Is.EqualTo("spaceBoundary"));
            Assert.That(content.InterfaceType, Is.EqualTo("dockingPort"));
            Assert.That(dock.BerthWidth, Is.GreaterThan(10));
            Assert.That(dock.BerthDepth, Is.GreaterThan(5));
            Assert.That(dock.ApproachLength, Is.GreaterThanOrEqualTo(16));
            Assert.That(dock.SourceRefs.Any(source => source.StartsWith("Resources/Maps/Shuttles/") && source.Contains("#uid=")), Is.True);
        }
    }

    [Test]
    public void CommonMappedFacilitiesAreFunctionalRoomPrograms()
    {
        var document = StationProgramDefaults.Create();
        var rooms = RequirementCompiler.Rooms(document).ToDictionary(pair => pair.Path, pair => pair.Room);
        var prototypes = rooms.Values.SelectMany(room => room.Contents).SelectMany(content => content.Prototypes).ToHashSet();
        Assert.That(prototypes, Is.SupersetOf(new[] { "CryoPod", "OperatingTable", "PlayerStationAi", "TegCenter", "AmeController", "SolarPanel", "ComputerBodyScanner", "HydroponicsTray" }));
        Assert.That(prototypes, Does.Not.Contain("CloningPod"), "Prototype existence alone is not map evidence for a standard cloning program.");
        Assert.That(document.Departments.Single(department => department.Id == "Silicon").Rooms, Is.Not.Empty);
        Assert.That(rooms.Values.Any(room => room.Contents.Any(content => content.Placement == "spaceBoundary")), Is.True);
        Assert.That(rooms.Values.Any(room => room.Environment.MaxTemperatureKelvin <= 273.15), Is.True);
    }

    [Test]
    public void CatalogSupportsCombinedPowerAndDistinguishesServiceFromCrewRoutes()
    {
        var document = StationProgramDefaults.Create();
        var power = document.Choices.Single(choice => choice.Rooms.Contains("Engineering/TEG"));
        Assert.That(power.Count.Min, Is.GreaterThanOrEqualTo(1));
        Assert.That(power.Count.Max, Is.GreaterThan(1), "Mapped power machinery often coexists; this is not an exclusive replacement rule.");
        var compiled = RequirementCompiler.Compile(document);
        Assert.That(compiled.Success, Is.True, ProgramTestData.Errors(compiled.Diagnostics));
        Assert.That(compiled.Connections.Any(connection => connection.Kind == "interaction" && connection.InterfaceTypes.Contains("counter")), Is.True);
        Assert.That(compiled.Connections.Any(connection => connection.Kind == "utility"), Is.True);
        Assert.That(document.Maintenance.Rooms.Any(room => room.Environment.Placement == "exterior" && room.Environment.Pressure == "vacuum"), Is.True);
    }

    [Test]
    public void PortableExampleAndEmbeddedYamlHaveIdenticalMeaning()
    {
        var example = Path.Combine(TestContext.CurrentContext.TestDirectory, "examples", "station-program.yaml");
        var imported = ProgramYaml.Parse(File.ReadAllText(example));
        var canonical = ProgramYaml.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Catalog", "standard-station.yaml")));
        Assert.That(System.Text.Json.JsonSerializer.Serialize(canonical, ProgramJson.Options),
            Is.EqualTo(System.Text.Json.JsonSerializer.Serialize(StationProgramDefaults.Create(), ProgramJson.Options)));
        Assert.That(System.Text.Json.JsonSerializer.Serialize(imported, ProgramJson.Options),
            Is.EqualTo(System.Text.Json.JsonSerializer.Serialize(StationProgramDefaults.Create(), ProgramJson.Options)));
    }
}
