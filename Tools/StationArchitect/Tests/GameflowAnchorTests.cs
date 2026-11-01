using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
public sealed class GameflowAnchorTests
{
    [Test]
    public void NearbyDoorEntriesDoNotHideASubstantialPhysicalRing()
    {
        var definition = ProgramTestData.Minimal(); definition.Departments[0].Rooms[0].Access.Level = "public";
        definition.Gameflow.Rules = [new() { Id = "Loop", Kind = "chaseLoops", Priority = "required", Bounds = new() { Min = 1 }, From = new() { Networks = ["publicHall"] } }];
        var project = new StationDesignProject { Definition = definition, Stage = "circulation", Settings = new() { Width = 64, Height = 64 } };
        project.Layout.Envelope = new GridBounds(0, 0, 64, 64).Cells().ToList();
        project.Layout.PublicCorridors = new GridBounds(8, 8, 40, 32).Cells().Except(new GridBounds(11, 11, 34, 26).Cells()).ToList();
        project.Layout.Rooms.Add(new() { Id = "Alpha/Work#1", DefinitionPath = "Alpha/Work", OwnerId = "Alpha", Cells = new GridBounds(12, 4, 7, 3).Cells().ToList() });
        foreach (var x in new[] { 12, 15, 18 }) project.Layout.Interfaces.Add(new() { Id = "entry:" + x, From = "Alpha/Work#1", To = "$public", Access = "public", Cells = [new(x, 7)], FromApproach = new(x, 6), ToApproach = new(x, 8) });
        var report = GameflowEvaluator.Evaluate(project);
        Assert.That(report.Rules.Single().Value, Is.GreaterThanOrEqualTo(1), string.Join("\n", report.Diagnostics.Select(issue => issue.Message)));
        Assert.That(report.Rules.Single().Witnesses.Any(witness => witness.Kind == "chase-loop" && witness.EnclosedBlockedArea >= 12), Is.True);
    }
}
