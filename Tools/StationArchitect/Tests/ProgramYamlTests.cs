using System.Text.Json;
using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class ProgramYamlTests
{
    [Test]
    public void RoundTripPreservesRoomFirstDefinitionsAndDescriptions()
    {
        var source = ProgramTestData.Minimal();
        source.Description = "Authoring: rooms first\nRoom growth has local and parent pressures.";
        var department = source.Departments[0];
        department.Tags = ["arbitrary-group", "test"];
        department.Split.ComponentPenalty = 987.5;
        department.Rooms[0].Access.Level = "custom-access-class";
        department.Rooms[0].Connections =
        [
            new()
            {
                Id = "optional-links", To = ["Lounge"], Priority = "desired", Presence = "ifPresent",
                Selection = "any", Modes = ["department", "maintenance"], Weight = 7.25,
                Description = "A quiet route if the optional lounge is selected."
            }
        ];
        department.Rooms.Add(ProgramTestData.Optional("Lounge"));
        var yaml = ProgramYaml.Export(source);
        var imported = ProgramYaml.Import(yaml);
        Assert.That(imported.Success, Is.True, ProgramTestData.Errors(imported.Diagnostics));
        Assert.That(JsonSerializer.Serialize(imported.Document, ProgramJson.Options),
            Is.EqualTo(JsonSerializer.Serialize(source, ProgramJson.Options)));
        Assert.That(yaml, Does.Not.Contain("subdepartments:"));
        Assert.That(yaml, Does.Not.Contain("spatialPlan:"));
    }

    [TestCase("schemaVersion: 1\nid: Test\ndepartments: []", "schemaVersion")]
    [TestCase("schemaVersion: wrong\nid: Test\ndepartments: []", "Int32")]
    [TestCase("schemaVersion: 2\nid: Test\ndepartments: []\nunknownFutureRule: true", "unknown")]
    [TestCase("schemaVersion: 2\nid: Test\nid: Duplicate\ndepartments: []", "duplicate")]
    [TestCase("schemaVersion: 2\nid: Test\nId: Duplicate\ndepartments: []", "unknown")]
    [TestCase("schemaVersion: 2\nid: Test\ndepartments: !System.Object []", "tags")]
    [TestCase("schemaVersion: 2\nid: Test\ndepartments: &cycle [*cycle]", "anchors")]
    [TestCase("schemaVersion: 2\nid: Test\ndepartments: null", "null")]
    [TestCase("schemaVersion: 2\nid: Test\ndepartments: {}", "mapping")]
    [TestCase("---\nschemaVersion: 2\nid: Test\ndepartments: []\n---\nschemaVersion: 2\nid: Other\ndepartments: []", "one YAML")]
    [TestCase("id: Test\ndepartments: []", "schemaVersion")]
    public void MalformedUnsupportedOrAmbiguousDocumentsAreRejected(string yaml, string message)
    {
        var imported = ProgramYaml.Import(yaml);
        Assert.That(imported.Success, Is.False);
        Assert.That(imported.Document, Is.Null);
        Assert.That(imported.Diagnostics, Is.Not.Empty);
        Assert.That(ProgramTestData.Errors(imported.Diagnostics), Does.Contain(message).IgnoreCase);
    }

    [TestCase("roomPressureWeight: .inf")]
    [TestCase("roomPressureWeight: NaN")]
    [TestCase("roomPressureWeight: null")]
    [TestCase("roomPressureWeight: [1]")]
    [TestCase("roomPressureWeight: { value: 1 }")]
    public void NumericScalarTypesAreCheckedBeforeMaterialization(string invalidSetting)
    {
        var yaml = ProgramYaml.Export(ProgramTestData.Minimal()) + "\n";
        var document = ProgramYaml.Import(yaml.Replace("roomPressureWeight: 1", invalidSetting, StringComparison.Ordinal));
        Assert.That(document.Success, Is.False);
    }

    [Test]
    public void OversizedYamlIsRejectedBeforeParsing()
    {
        var imported = ProgramYaml.Import(new string('x', 2 * 1024 * 1024 + 1));
        Assert.That(imported.Success, Is.False);
        Assert.That(imported.Diagnostics, Is.Not.Empty);
    }

    [Test]
    public void NumericLookingRoomIdsRemainStrings()
    {
        var document = ProgramTestData.Minimal();
        document.Departments[0].Rooms[0].Id = "1234";
        var imported = ProgramYaml.Parse(ProgramYaml.Export(document));
        Assert.That(imported.Departments[0].Rooms[0].Id, Is.EqualTo("1234"));
    }
}
