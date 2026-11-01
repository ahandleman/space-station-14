using System.Text.Json;
using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class RequirementTreeTests
{
    [Test]
    public void TreeRetainsRoomConstraintsAndTheirParentDepartmentConstraints()
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Rooms[0].Count = new() { Min = 1, Target = 2, Max = 3 };
        source.Departments[0].Rooms[0].Area = ProgramTestData.Area(16, 25, 40);
        var compiled = RequirementCompiler.Compile(source);
        Assert.That(compiled.Success, Is.True, ProgramTestData.Errors(compiled.Diagnostics));
        Assert.That(compiled.Tree!.Kind, Is.EqualTo("station"));
        var department = compiled.Tree.Children.Single(n => n.Path == "Alpha");
        Assert.That(department.Kind, Is.EqualTo("department"));
        Assert.That(department.Area!.Target, Is.EqualTo(40));
        Assert.That(department.FloorBudget, Is.EqualTo(new FloorBudget(16, 50, 120)));
        Assert.That(department.EffectiveFloorBudget.Min, Is.EqualTo(16));
        var room = department.Children.Single();
        Assert.That(room.Path, Is.EqualTo("Alpha/Work"));
        Assert.That(room.Count!.Max, Is.EqualTo(3));
        Assert.That(room.Area!.Max, Is.EqualTo(40));
        Assert.That(room.FloorBudget.Max, Is.EqualTo(120));
        Assert.That(compiled.Tree.Children.Single(n => n.Kind == "maintenance").Path, Is.EqualTo("_maintenance"));
    }

    [Test]
    public void RequiredOptionalTargetsIncreaseEffectiveMinimumWithoutChangingAuthoredCounts()
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Rooms.Add(ProgramTestData.Optional("Lounge"));
        source.Departments[0].Rooms[0].Connections.Add(new()
        {
            Id = "must-have-lounge", To = ["Lounge"], Presence = "requireTarget", Priority = "required"
        });
        var compiled = RequirementCompiler.Compile(source);
        Assert.That(compiled.Success, Is.True, ProgramTestData.Errors(compiled.Diagnostics));
        var department = compiled.Tree!.Children.Single(n => n.Path == "Alpha");
        var lounge = department.Children.Single(n => n.Path == "Alpha/Lounge");
        Assert.That(lounge.Count!.Min, Is.Zero, "The program still authors an optional template; its implication is compiled separately.");
        Assert.That(lounge.FloorBudget.Min, Is.Zero);
        Assert.That(lounge.EffectiveFloorBudget.Min, Is.EqualTo(9));
        Assert.That(department.EffectiveFloorBudget.Min, Is.EqualTo(25));
        Assert.That(source.Departments[0].Rooms[1].Count.Min, Is.Zero);
    }

    [Test]
    public void RequiredPresenceCanMakeAnOtherwiseFeasibleParentBudgetImpossible()
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Area = ProgramTestData.Area(16, 20, 24);
        source.Departments[0].Rooms[0].Area = ProgramTestData.Area(16, 20, 24);
        source.Departments[0].Rooms.Add(ProgramTestData.Optional("Lounge"));
        source.Departments[0].Rooms[0].Connections.Add(new()
        {
            Id = "must-have-lounge", To = ["Lounge"], Presence = "requireTarget", Priority = "required"
        });
        var result = RequirementCompiler.Compile(source);
        Assert.That(result.Success, Is.False);
        Assert.That(result.Diagnostics.Any(d => d.Code == "impossible-area"), Is.True, ProgramTestData.Errors(result.Diagnostics));
    }

    [Test]
    public void AnyTargetChoiceIsRetainedInsteadOfMarkingEveryAlternativeMandatory()
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Rooms.AddRange([ProgramTestData.Optional("A"), ProgramTestData.Optional("B")]);
        source.Departments[0].Rooms[0].Connections.Add(new()
        {
            Id = "choose-support", To = ["A", "B"], Selection = "any", Presence = "requireTarget", Priority = "required"
        });
        var result = RequirementCompiler.Compile(source);
        Assert.That(result.Success, Is.True, ProgramTestData.Errors(result.Diagnostics));
        Assert.That(result.PresenceChoices, Has.Count.EqualTo(1));
        Assert.That(result.PresenceChoices[0].Alternatives, Is.EquivalentTo(new[] { "Alpha/A", "Alpha/B" }));
        var rooms = result.Tree!.Children.Single(n => n.Path == "Alpha").Children;
        Assert.That(rooms.Where(n => n.Path is "Alpha/A" or "Alpha/B").All(n => n.EffectiveFloorBudget.Min == 0), Is.True);
    }

    [Test]
    public void CompilationMakesAnIndependentRequirementSnapshotWithoutMutatingTheProgram()
    {
        var source = ProgramTestData.Minimal();
        var serialized = JsonSerializer.Serialize(source, ProgramJson.Options);
        var result = RequirementCompiler.Compile(source);
        Assert.That(JsonSerializer.Serialize(source, ProgramJson.Options), Is.EqualTo(serialized));
        var node = result.Tree!.Children.Single(n => n.Path == "Alpha");
        node.Area!.Max = 99;
        node.Children[0].Area!.Min = 22;
        node.Tags.Add("compiled-only");
        Assert.That(JsonSerializer.Serialize(source, ProgramJson.Options), Is.EqualTo(serialized));
    }
}
