using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class DefaultProgramTests
{
    [Test]
    public void DefaultStationCompilesAsDirectRoomOwnershipWithFeasibleBudgets()
    {
        var source = StationProgramDefaults.Create();
        var result = RequirementCompiler.Compile(source);
        Assert.That(result.Success, Is.True, ProgramTestData.Errors(result.Diagnostics));
        Assert.That(source.Departments.Select(d => d.Id), Is.EquivalentTo(new[]
        {
            "Arrivals", "Commons", "Service", "Medical", "Engineering", "Science", "Security", "Cargo", "Command", "Silicon"
        }));
        Assert.That(source.Departments.All(d => d.Rooms.Count > 0), Is.True);
        Assert.That(source.Departments.All(d => d.Rooms.Any(r => r.Role == "filler")), Is.True,
            "Each department needs explicit internal circulation/filler choices instead of anonymous leftover floor.");
        Assert.That(source.Departments.All(d => d.Rooms.Any(r => r.Count.Min > 0)), Is.True);
    }

    [Test]
    public void DefaultRoomProgramIncludesSharedPowerSecureRoomsAndCommandOffices()
    {
        var source = StationProgramDefaults.Create();
        var engineering = source.Departments.Single(d => d.Id == "Engineering");
        Assert.That(engineering.Rooms.Select(r => r.Id), Does.Contain("Atmos"));
        var teg = engineering.Rooms.Single(r => r.Id == "TEG");
        Assert.That(teg.Count.Min, Is.Zero, "A standard station may use a different primary power family.");
        Assert.That(source.Choices.Any(choice => choice.Count.Min >= 1 && choice.Rooms.Contains("Engineering/TEG")), Is.True);
        Assert.That(teg.Connections.Count, Is.GreaterThanOrEqualTo(2));
        var security = source.Departments.Single(d => d.Id == "Security");
        Assert.That(security.Rooms.Select(r => r.Id), Is.SupersetOf(new[]
        {
            "Armory", "Perma", "Sentencing", "WardensOffice", "Interrogation"
        }));
        var command = source.Departments.Single(d => d.Id == "Command");
        Assert.That(command.Rooms.Select(r => r.Id), Is.SupersetOf(new[]
        {
            "Bridge", "CaptainsRoom", "HoPOffice", "MeetingRoom"
        }));
    }

    [Test]
    public void DefaultMaintsSeparateNarrowPassagesFromEnclosedTrueRooms()
    {
        var maintenance = StationProgramDefaults.Create().Maintenance;
        Assert.That(maintenance.PassageWidth.Min, Is.EqualTo(2));
        Assert.That(maintenance.PassageWidth.Max, Is.EqualTo(3));
        Assert.That(maintenance.Rooms, Is.Not.Empty);
        Assert.That(maintenance.Rooms.All(r => r.Count.Max > 0), Is.True);
    }

    [Test]
    public void CommittedYamlExampleIsAValidRoomFirstProgram()
    {
        var examplePath = Path.Combine(TestContext.CurrentContext.TestDirectory, "examples", "station-program.yaml");
        Assert.That(File.Exists(examplePath), Is.True, examplePath);
        var result = ProgramYaml.Import(File.ReadAllText(examplePath));
        Assert.That(result.Success, Is.True, ProgramTestData.Errors(result.Diagnostics));
        var compiled = RequirementCompiler.Compile(result.Document!);
        Assert.That(compiled.Success, Is.True, ProgramTestData.Errors(compiled.Diagnostics));
        Assert.That(result.Document!.Departments.Count, Is.EqualTo(10));
    }

    [Test]
    public void CreatingDefaultsReturnsIndependentMutableDocuments()
    {
        var first = StationProgramDefaults.Create();
        var second = StationProgramDefaults.Create();
        first.Departments[0].Rooms[0].Area.Min = 12345;
        Assert.That(second.Departments[0].Rooms[0].Area.Min, Is.Not.EqualTo(12345));
    }
}
