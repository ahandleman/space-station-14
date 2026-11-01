using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class GameflowIntegrationTests
{
    [Test]
    public void ChangingOnlyGameflowPreservesSelectionAndCompletedPlacement()
    {
        var project = DesignPipeline.RunStage(DesignPipeline.New(ProgramTestData.Minimal()), "placement");
        var definition = ProgramJson.Clone(project.Definition);
        definition.Gameflow.Rules.Add(new() { Id = "Loops", Weight = 7 });
        var updated = DesignPipeline.UpdateDefinition(project, definition);
        Assert.That(updated.Layout.Rooms.Select(room => room.Seed), Is.EqualTo(project.Layout.Rooms.Select(room => room.Seed)));
        Assert.That(updated.Selection.RoomCounts, Is.EqualTo(project.Selection.RoomCounts));
        Assert.That(updated.StageStates.Single(state => state.Stage == "placement").Status, Is.EqualTo("ready"));
        Assert.That(updated.StageStates.Single(state => state.Stage == "gameflow").Status, Is.EqualTo("pending"));
        Assert.That(updated.Definition.Gameflow.Rules.Single().Weight, Is.EqualTo(7));
    }

    [Test]
    public void ImportedGameflowCannotClaimARequiredLoopOnAnUnbuiltLayout()
    {
        var definition = ProgramTestData.Minimal();
        definition.Gameflow.Rules.Add(new() { Id = "Loops", Priority = "required", Bounds = new() { Min = 1 } });
        var project = DesignPipeline.New(definition);
        project.Stage = "validation";
        var room = project.Layout.Rooms.Single();
        room.Cells = new GridBounds(5, 5, 8, 5).Cells().ToList();
        room.Bounds = new(5, 5, 8, 5);
        project.Gameflow = new() { Complete = true, TotalCost = 0, Rules = [new() { Id = "Loops", Observed = true, Satisfied = true, TargetMet = true, Value = 42 }] };
        project.StageStates.RemoveAll(state => state.Stage == "gameflow");
        var imported = DesignPipeline.Import(project);
        Assert.That(imported.StageStates.Count(state => state.Stage == "gameflow"), Is.EqualTo(1));
        Assert.That(imported.Gameflow!.Rules.Single().Observed, Is.False);
        Assert.That(imported.Report!.Success, Is.False);
        Assert.That(imported.Report.Diagnostics.Any(issue => issue.Path == "gameflow.rules.Loops" && issue.Severity == "error"), Is.True);
        Assert.That(imported.Gameflow.Rules.Single().Value, Is.Not.EqualTo(42));
    }

    [Test]
    public void EmptyRulesKeepOldProgramsCompatibleAndGameflowPrecedesContents()
    {
        Assert.That(DesignStages.Index("gameflow"), Is.EqualTo(DesignStages.Index("circulation") + 1));
        Assert.That(DesignStages.Index("contents"), Is.EqualTo(DesignStages.Index("gameflow") + 1));
        var project = DesignPipeline.New(ProgramTestData.Minimal());
        var flow = GameflowEvaluator.Evaluate(project);
        Assert.That(flow.Complete, Is.True);
        Assert.That(flow.TotalCost, Is.Zero);
        Assert.That(flow.Diagnostics, Is.Empty);
    }
}
