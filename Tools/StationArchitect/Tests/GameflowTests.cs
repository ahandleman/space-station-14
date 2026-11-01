using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
public sealed class GameflowTests
{
    private static StationDesignProject Network(IEnumerable<Cell> floor)
    {
        var definition = ProgramTestData.Minimal(); definition.Departments[0].Rooms[0].Count = new() { Min = 0, Target = 0, Max = 0 };
        definition.Departments[0].Area.Min = definition.Departments[0].Area.Target = 0;
        var project = new StationDesignProject { Definition = definition, Settings = new() { Width = 64, Height = 64, Topology = "spine" }, Stage = "circulation" };
        project.Layout.Envelope = new GridBounds(0, 0, 64, 64).Cells().ToList(); project.Layout.PublicCorridors = floor.Distinct().ToList();
        project.Definition.Gameflow.Rules = [new() { Id = "Chase", Priority = "required", Bounds = new() { Min = 1, Target = 1 } }];
        return project;
    }

    private static IEnumerable<Cell> Ring() => new GridBounds(8, 8, 40, 32).Cells().Except(new GridBounds(11, 11, 34, 26).Cells());
    private static List<Cell> Stamp(IEnumerable<Cell> centers, int width) => centers.SelectMany(center => new GridBounds(center.X - width / 2, center.Y - width / 2, width, width).Cells()).Distinct().ToList();

    [Test]
    public void AMeaningfulSeparatedPhysicalLoopHasObstacleAndPathWitnesses()
    {
        var report = GameflowEvaluator.Evaluate(Network(Ring())); var observed = report.Rules.Single();
        Assert.That(report.Success, Is.True, ProgramTestData.Errors(report.Diagnostics));
        Assert.That(observed.Observed && observed.CoverageComplete, Is.True);
        Assert.That(observed.Value, Is.EqualTo(1));
        var witness = observed.Witnesses.Single(witness => witness.Kind == "chase-loop");
        Assert.That(witness.Length + witness.AlternateLength, Is.GreaterThanOrEqualTo(32));
        Assert.That(witness.Separation, Is.GreaterThanOrEqualTo(4));
        Assert.That(witness.EnclosedBlockedArea, Is.GreaterThanOrEqualTo(12));
        Assert.That(witness.Assumptions, Is.Not.Empty);
    }

    [TestCase(2)]
    [TestCase(3)]
    public void CorridorGridCyclesDoNotCountAsChaseLoops(int width)
    {
        var project = Network(new GridBounds(8, 8, 40, width).Cells()); var report = GameflowEvaluator.Evaluate(project);
        Assert.That(report.Rules.Single().Observed, Is.True);
        Assert.That(report.Rules.Single().Value, Is.Zero);
        Assert.That(report.Diagnostics.Any(issue => issue.Code == "gameflow-bound" && issue.Severity == "error"), Is.True);
    }

    [Test]
    public void AnOpenRoomDoesNotInventALoopByWalkingAroundAnArbitraryPatchOfFloor()
    {
        var report = GameflowEvaluator.Evaluate(Network(new GridBounds(8, 8, 40, 32).Cells()));
        Assert.That(report.Rules.Single().Observed, Is.True);
        Assert.That(report.Rules.Single().Value, Is.Zero);
        Assert.That(report.Rules.Single().Witnesses.Any(witness => witness.Kind == "chase-loop"), Is.False);
    }

    private static StationDesignProject TwoRooms()
    {
        var definition = ProgramTestData.Minimal(); definition.Departments[0].Rooms.Add(ProgramTestData.Room("Secure", 9, 25, 100));
        definition.Departments[0].Rooms[1].Access.Level = "restricted";
        definition.Gameflow.Rules = [new() { Id = "Reach", Kind = "travelDistance", Priority = "required", From = new() { Paths = ["Alpha/Work"] }, To = new() { Paths = ["Alpha/Secure"] }, Bounds = new() { Max = 100 } }];
        var project = new StationDesignProject { Definition = definition, Settings = new() { Width = 40, Height = 32 }, Stage = "circulation" };
        project.Layout.Envelope = new GridBounds(0, 0, 40, 32).Cells().ToList();
        project.Layout.Rooms = [new() { Id = "Alpha/Work#1", DefinitionPath = "Alpha/Work", OwnerId = "Alpha", Instance = "1", Seed = new(8, 12), Cells = new GridBounds(5, 10, 6, 6).Cells().ToList() },
            new() { Id = "Alpha/Secure#1", DefinitionPath = "Alpha/Secure", OwnerId = "Alpha", Instance = "1", Seed = new(15, 12), Cells = new GridBounds(12, 10, 6, 6).Cells().ToList() }];
        project.Layout.Interfaces = [new() { Id = "door", From = "Alpha/Work#1", To = "Alpha/Secure#1", Kind = "walking", Type = "doorway", Width = 1, Cells = [new(11, 12)], FromApproach = new(10, 12), ToApproach = new(12, 12) }];
        return project;
    }

    [Test]
    public void AccessLabelsAreExplicitAndEditProtectionDoesNotGrantGameplayAccess()
    {
        var project = TwoRooms(); project.Layout.Rooms[1].Pinned = project.Layout.Rooms[1].FloorLocked = true; project.Layout.Interfaces[0].Edited = true;
        var denied = GameflowEvaluator.Evaluate(project);
        Assert.That(denied.Rules.Single().Observed, Is.False);
        Assert.That(denied.Diagnostics.Any(issue => issue.Code == "gameflow-selection"), Is.True);
        project.Definition.Gameflow.AccessProfiles[0].AllowedLevels.Add("restricted");
        var allowed = GameflowEvaluator.Evaluate(project);
        Assert.That(allowed.Rules.Single().Observed, Is.True, ProgramTestData.Errors(allowed.Diagnostics));
        Assert.That(allowed.Rules.Single().Value, Is.EqualTo(7));
    }

    [Test]
    public void ALongNarrowPassageHasOneCriticalRegionRatherThanOnePerTile()
    {
        var project = TwoRooms(); project.Definition.Gameflow.AccessProfiles[0].AccessPolicy = "allDoors";
        var rule = project.Definition.Gameflow.Rules[0]; rule.Kind = "chokepoints"; rule.Bounds = new() { Max = 1 }; rule.Routes.EndpointRadius = 1; rule.Routes.ChokepointWidth = 1;
        var report = GameflowEvaluator.Evaluate(project); var observed = report.Rules.Single();
        Assert.That(observed.Observed && observed.CoverageComplete, Is.True, ProgramTestData.Errors(report.Diagnostics));
        Assert.That(observed.Value, Is.EqualTo(1));
        Assert.That(observed.Witnesses.Single().CutCells, Is.Not.Empty);
    }

    [Test]
    public void BudgetExhaustionIsUnverifiedAndMoreExpensiveThanObservedPreferenceFailure()
    {
        var project = Network(Ring()); project.Definition.Gameflow.Settings.MaxSearchVisits = project.Definition.Gameflow.Settings.MaxTotalSearchVisits = 64;
        var incomplete = GameflowEvaluator.Evaluate(project);
        Assert.That(incomplete.Rules.Single().Observed, Is.False);
        Assert.That(incomplete.Diagnostics.Any(issue => issue.Code == "gameflow-unverified"), Is.True);
        var failed = GameflowEvaluator.Evaluate(Network(new GridBounds(8, 8, 40, 3).Cells()));
        Assert.That(incomplete.TotalCost, Is.GreaterThan(failed.TotalCost));
    }

    [Test]
    public void GameflowSchemaRoundTripsAndRequirementsStayAttachedToTheStationTree()
    {
        var project = Network(Ring()); project.Definition.Gameflow.Rules[0].Routes.MinSeparation = 6;
        var copy = ProgramYaml.Parse(ProgramYaml.Export(project.Definition));
        Assert.That(copy.Gameflow.Rules[0].Routes.MinSeparation, Is.EqualTo(6));
        var compiled = RequirementCompiler.Compile(copy);
        Assert.That(compiled.Success, Is.True, ProgramTestData.Errors(compiled.Diagnostics));
        Assert.That(compiled.Tree!.Gameflow!.Rules.Single().Id, Is.EqualTo("Chase"));
        copy.Gameflow.Rules[0].From.Paths = ["Missing/Room"];
        Assert.That(ProgramValidator.Validate(copy).Any(issue => issue.Code == "gameflow-reference"), Is.True);
    }

    [Test]
    public void PhysicalBypassRepairReducesWeightedPressureAndKeepsRequirementsAndSourceGeometry()
    {
        var centerline = Enumerable.Range(10, 31).Select(y => new Cell(10, y)).Concat(Enumerable.Range(11, 30).Select(x => new Cell(x, 40))).Concat(Enumerable.Range(10, 30).Reverse().Select(y => new Cell(40, y))).ToList();
        var project = Network(Stamp(centerline, 3)); project.Layout.PublicBackbone = centerline; project.Layout.PublicWaypoints = [new(10, 10), new(40, 10)];
        project.Definition.Gameflow.Settings.RepairBudget = 1;
        var before = GameflowEvaluator.Evaluate(project); var original = project.Layout.PublicCorridors.ToHashSet();
        var repaired = DesignGameflow.Run(project);
        Assert.That(repaired.Gameflow!.TotalCost, Is.LessThan(before.TotalCost));
        Assert.That(repaired.Gameflow.Repairs, Has.Count.EqualTo(1));
        Assert.That(repaired.Gameflow.Repairs[0].CellsAdded, Is.Not.Empty);
        Assert.That(original.IsSubsetOf(repaired.Layout.PublicCorridors), Is.True);
        Assert.That(project.Layout.PublicCorridors.ToHashSet().SetEquals(original), Is.True, "Run must clone the source.");
        Assert.That(repaired.Definition.Gameflow.Rules[0].Bounds.Min, Is.EqualTo(1));
        project.Layout.PublicEdited = true;
        var protectedResult = DesignGameflow.Run(project);
        Assert.That(protectedResult.Layout.PublicCorridors.ToHashSet().SetEquals(original), Is.True);
        Assert.That(protectedResult.Gameflow!.Repairs, Is.Empty);
    }

    [Test]
    public void SubmittedRepairProvenanceIsNeverAcceptedAsACurrentStageAction()
    {
        var project = Network(Ring()); var fresh = GameflowEvaluator.Evaluate(project);
        fresh.Repairs = [new() { Network = "forged", CellsAdded = [new(1, 1)] }]; project.Gameflow = fresh;
        var result = DesignGameflow.Run(project, allowRepairs: false);
        Assert.That(result.Gameflow!.Repairs, Is.Empty);
        Assert.That(result.Layout.PublicCorridors, Is.EqualTo(project.Layout.PublicCorridors));
    }
}
