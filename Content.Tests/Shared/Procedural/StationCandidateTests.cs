using System;
using System.Linq;
using Content.StationGeneration;
using NUnit.Framework;

namespace Content.Tests.Shared.Procedural;

/// <summary>Full candidate retries apply only to fresh authoring, never to accepted spatial work.</summary>
[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class StationCandidateTests
{
    private static readonly Lazy<CandidateFixture> RingEightyEight = new(() => GenerateFixture("ring", 88));

    [TestCase("hub", 9)]
    [TestCase("ring", 67)]
    [TestCase("ring", 88)]
    public void FreshCandidatesPreserveAuthoringAndOriginalSeed(string archetype, int seed)
    {
        var fixture = archetype == "ring" && seed == 88 ? RingEightyEight.Value : GenerateFixture(archetype, seed);
        var source = fixture.Source;
        var generated = fixture.Generated;
        Assert.That(generated.Result.Success, Is.True, ProjectJson.Serialize(generated.Result));
        Assert.That(StationSpatialPlanner.Validate(generated).Success, Is.True,
            "An accepted full candidate must independently satisfy the final strict geometry and program checks.");
        Assert.That(source.SpatialPlan, Is.Null, "Candidate exploration must not populate the author's source project.");
        Assert.That(source.Config.Seed, Is.EqualTo(seed));
        Assert.That(generated.Config.Seed, Is.EqualTo(seed));
        Assert.That(generated.Result.Seed, Is.EqualTo(seed));
        Assert.That(ProjectJson.Serialize(source.Topology), Is.EqualTo(fixture.AuthoredTopology));
        Assert.That(ProjectJson.Serialize(generated.Topology), Is.EqualTo(fixture.AuthoredTopology),
            "Physical candidates must preserve authored dependency IDs, priorities, weights, modes and descriptions.");
        Assert.That(generated.Result.Attempts, Is.EqualTo(generated.SpatialPlan.PlacementAttempts));
        Assert.That(generated.Result.Attempts, Is.InRange(1, source.Config.CandidateBudget),
            "Search is bounded by the configured candidate budget; a better future planner may succeed on its first candidate.");
    }

    [Test]
    public void SavedGeometryFailsAnImpossibleNewDistanceWithoutResampling()
    {
        var accepted = RingEightyEight.Value.Generated;
        Assert.That(accepted.Result.Success, Is.True, ProjectJson.Serialize(accepted.Result));
        var saved = ProjectJson.Clone(accepted);
        var dependency = saved.Topology.Edges.First(edge => edge.Hard && edge.Relation == "walking" && edge.AllowedModes.Contains("publicHall"));
        dependency.MaxDistance = 1;
        var authoredTopology = ProjectJson.Serialize(saved.Topology);
        var seeds = ProjectJson.Serialize(saved.SpatialPlan.Seeds);
        var territories = TerritorySnapshot(saved);
        var checkedProject = StationSpatialPlanner.Generate(saved);
        Assert.That(checkedProject.Result.Success, Is.False);
        Assert.That(checkedProject.Result.Diagnostics.Any(diagnostic => diagnostic.Code == "required-distance"), Is.True,
            ProjectJson.Serialize(checkedProject.Result));
        Assert.That(ProjectJson.Serialize(checkedProject.SpatialPlan.Seeds), Is.EqualTo(seeds),
            "A saved project must retain its accepted physical seed positions and pins when a dependency becomes impossible.");
        Assert.That(TerritorySnapshot(checkedProject), Is.EqualTo(territories),
            "Validation failure must not trigger a new department placement or territory allocation.");
        Assert.That(ProjectJson.Serialize(saved.SpatialPlan.Seeds), Is.EqualTo(seeds));
        Assert.That(TerritorySnapshot(saved), Is.EqualTo(territories));
        Assert.That(ProjectJson.Serialize(checkedProject.Topology), Is.EqualTo(authoredTopology));
        Assert.That(checkedProject.Config.Seed, Is.EqualTo(accepted.Config.Seed));
    }

    [Test]
    public void RequiredMailIsRejectedBeforePhysicalCandidateExploration()
    {
        var source = Start("ring", 14014);
        source.Config.DefaultAllowMailNetwork = true;
        source.Topology.Edges =
        [
            new("Cargo", "Service", true, 7)
            {
                Id = "required-mail-program", Origin = "custom", Relation = "delivery", Purpose = "items",
                AllowedModes = ["mail"], Description = "This delivery requires a physical mail network."
            }
        ];
        var authoredTopology = ProjectJson.Serialize(source.Topology);
        Assert.That(StationSpatialPlanner.Check(source), Is.Empty, "Mail delivery is a valid authored topology requirement.");
        var generated = StationSpatialPlanner.Generate(source);
        Assert.That(generated.Result.Success, Is.False);
        Assert.That(generated.Result.Diagnostics.Any(diagnostic => diagnostic.Code == "unsupported-fulfillment"), Is.True);
        Assert.That(generated.SpatialPlan, Is.Null,
            "Unsupported physical systems should fail before placement, growth or maintenance candidate exploration.");
        Assert.That(generated.Result.Attempts, Is.EqualTo(1));
        Assert.That(source.SpatialPlan, Is.Null);
        Assert.That(ProjectJson.Serialize(source.Topology), Is.EqualTo(authoredTopology));
        Assert.That(ProjectJson.Serialize(generated.Topology), Is.EqualTo(authoredTopology),
            "A required mail network must retain its authored mode rather than acquire a walking fallback.");
    }

    private static CandidateFixture GenerateFixture(string archetype, int seed)
    {
        var source = Start(archetype, seed);
        var dependency = source.Topology.Edges.First(edge => edge.Hard && edge.Relation == "walking");
        dependency.Origin = "custom";
        dependency.Description = "Preserve the mapper's authored dependency through every physical candidate.";
        var authoredTopology = ProjectJson.Serialize(source.Topology);
        return new CandidateFixture(source, authoredTopology, StationSpatialPlanner.Generate(source));
    }

    private static StationProject Start(string archetype, int seed)
    {
        var source = new StationProject { Config = new StationGenerationConfig { Archetype = archetype, Seed = seed } };
        source.Topology = StationGenerator.GenerateTopology(source.Config);
        return source;
    }

    private static string TerritorySnapshot(StationProject project) => ProjectJson.Serialize(project.SpatialPlan.Regions
        .Select(region => new { region.Id, region.Territory }).ToList());

    private sealed record CandidateFixture(StationProject Source, string AuthoredTopology, StationProject Generated);
}
