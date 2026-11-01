using System;
using System.Linq;
using System.Text.Json;
using Content.StationGeneration;
using NUnit.Framework;

namespace Content.Tests.Shared.Procedural;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class StationGenerationTests
{
    [TestCase("hub", 0)]
    [TestCase("ring", 14014)]
    [TestCase("spine", 17)]
    [TestCase("ring", 90210)]
    public void RegressionSeedsPassActualGridValidation(string archetype, int seed)
    {
        var p = Generate(archetype, seed);
        Assert.That(StationValidator.Validate(p).Success, Is.True, Describe(p));
        Assert.That(p.Layout.Departments.Count, Is.EqualTo(9));
        Assert.That(p.Result.Metrics.TopologyBridges, Is.Zero);
        Assert.That(p.Result.Metrics.PublicArea, Is.GreaterThan(0));
        Assert.That(p.Result.Metrics.MaintenanceArea, Is.GreaterThan(0));
    }

    [Test]
    public void DeterministicAcrossRepeatedRunsAndDefinitionOrdering()
    {
        var config = new StationGenerationConfig { Seed = 51 };
        var a = StationGenerator.Generate(config);
        var b = StationGenerator.Generate(config);
        config.Departments.Reverse();
        var reversed = StationGenerator.Generate(config);
        Assert.That(a.Result.Success, Is.True, Describe(a));
        Assert.That(b.Result.Metrics.Hash, Is.EqualTo(a.Result.Metrics.Hash));
        Assert.That(reversed.Result.Metrics.Hash, Is.EqualTo(a.Result.Metrics.Hash));
        var roundTrip = ProjectJson.Clone(a);
        Assert.That(StationValidator.Validate(roundTrip).Metrics.Hash, Is.EqualTo(a.Result.Metrics.Hash));
    }

    [TestCase(2)]
    [TestCase(4)]
    [TestCase(5)]
    public void CorridorWidthsIncludeTurnsAndJunctions(int width)
    {
        var p = StationGenerator.Generate(new StationGenerationConfig { Seed = 72, Width = 176, Height = 144, CorridorWidth = width });
        Assert.That(p.Result.Success, Is.True, Describe(p));
        var tiles = p.Layout.PublicCorridors.ToHashSet();
        Assert.That(p.Layout.Routes.All(r => r.Centerline.SelectMany(p => StationGenerator.Brush(p, width)).All(tiles.Contains)), Is.True);
        var routeCell = p.Layout.Routes.First(r => r.Centerline.Count > 4).Centerline[3];
        var brush = StationGenerator.Brush(routeCell, width).First();
        p.Layout.PublicCorridors.Remove(brush);
        Assert.That(StationValidator.Validate(p).Diagnostics.Any(d => d.Code == "route-width"), Is.True);
    }

    [Test]
    public void InfeasibleConfigTerminatesWithStructuredFailure()
    {
        var p = StationGenerator.Generate(new StationGenerationConfig { Seed = 12, Width = 32, Height = 32, CandidateBudget = 2, OptimizationSteps = 5 });
        Assert.That(p.Result.Success, Is.False);
        Assert.That(p.Result.Attempts, Is.EqualTo(2));
        Assert.That(p.Result.Diagnostics.All(d => d.Stage == "placement" && d.Code.Length > 0), Is.True);
        Assert.That(p.Result.Seed, Is.EqualTo(12));
    }

    [Test]
    public void InvalidConfigFailsBeforeAttemptingCandidates()
    {
        var c = new StationGenerationConfig { CandidateBudget = 0 };
        c.Departments[0].RequiredConnections.Add("Missing");
        var p = StationGenerator.Generate(c);
        Assert.That(p.Result.Success, Is.False);
        Assert.That(p.Result.Attempts, Is.Zero);
        Assert.That(p.Result.Diagnostics.Any(d => d.Code == "budget"), Is.True);
        Assert.That(p.Result.Diagnostics.Any(d => d.Code == "relationship"), Is.True);
    }

    [Test]
    public void ValidatorRejectsBrokenLayersAndTamperedPorts()
    {
        var baseline = Generate("hub", 14014);
        void Reject(Action<StationProject> edit, string code)
        {
            var p = ProjectJson.Clone(baseline); edit(p);
            var result = StationValidator.Validate(p);
            Assert.That(result.Success, Is.False);
            Assert.That(result.Diagnostics.Any(d => d.Code == code), Is.True, ProjectJson.Serialize(result.Diagnostics));
        }
        Reject(p => p.Layout.PublicCorridors.Add(p.Layout.Departments.First(d => d.Id == "Security").Bounds.Center), "room-crossing");
        Reject(p => p.Layout.Departments[0].Bounds = p.Layout.Departments[1].Bounds, "overlap-clearance");
        Reject(p => p.Layout.Doors[0].Center = new Cell(0, 0), "port-transform");
        Reject(p => p.Layout.Hull.Add(new Cell(0, 0)), "hull-boundary");
        Reject(p => p.Layout.Maintenance.Add(p.Layout.Departments[0].Bounds.Center), "maintenance-overlap");
        Reject(p => p.Layout.ReservedExterior.Clear(), "docking");
        Reject(p => p.Layout.Routes.Clear(), "required-route");
    }

    [TestCase("hub")]
    [TestCase("ring")]
    [TestCase("spine")]
    public void DefaultTopologyContainsIntentionalCycles(string archetype)
    {
        var c = new StationGenerationConfig { Archetype = archetype };
        var t = StationGenerator.GenerateTopology(c);
        Assert.That(StationValidator.CheckTopology(c, t), Is.Empty);
        Assert.That(t.Edges.Count(e => e.Hard), Is.GreaterThanOrEqualTo(t.Nodes.Count));
        Assert.That(t.Edges.Any(e => !e.Hard), Is.True);
        if (archetype == "hub") Assert.That(t.Edges.Count(e => e.Hard && (e.From == "Commons" || e.To == "Commons")), Is.EqualTo(8));
        t.Edges.RemoveAll(e => e.Hard);
        Assert.That(StationValidator.CheckTopology(c, t).Any(d => d.Severity == "error"), Is.False,
            "A dependency graph need not encode every baseline public walking connection.");
    }

    [Test]
    public void DependencyMetadataAndNodePositionsSurviveProjectRoundTrip()
    {
        var p = new StationProject();
        p.Config.DefaultAllowDisposalShortcuts = true;
        p.Config.DefaultAllowMailNetwork = true;
        p.Config.Departments[0].Description = "Primary public gathering and circulation space.";
        p.Topology = Topology(p.Config,
            new TopologyEdge
            {
                Id = "supply-delivery", From = "Cargo", To = "Service", Priority = "desired",
                Relation = "delivery", Purpose = "items", AllowedModes = ["staffPassage", "mail"],
                Direction = "forward", Weight = 7.5, MaxDistance = 30,
                Description = "Deliver ingredients without carrying them through public reception."
            },
            new TopologyEdge
            {
                Id = "secure-separation", From = "Security", To = "Arrivals", Priority = "avoided",
                Relation = "adjacency", Purpose = "crew", AllowedModes = ["publicHall"],
                StrictAvoidance = true, Weight = 3,
                Description = "Keep the secure perimeter away from the arrival lobby."
            });
        p.Topology.NodePositions["Cargo"] = new Cell(12, 18);
        var copy = ProjectJson.Clone(p);
        Assert.That(ProjectJson.Serialize(copy), Is.EqualTo(ProjectJson.Serialize(p)));
        Assert.That(copy.Topology.Edges[0].Description, Does.Contain("ingredients"));
        Assert.That(copy.Topology.Edges[1].Priority, Is.EqualTo("avoided"));
        Assert.That(copy.Topology.Edges[1].StrictAvoidance, Is.True);
        Assert.That(copy.Topology.NodePositions["Cargo"], Is.EqualTo(new Cell(12, 18)));
        Assert.That(StationValidator.CheckTopology(copy.Config, copy.Topology).Any(d => d.Severity == "error"), Is.False);
    }

    [TestCase(true, "required")]
    [TestCase(false, "desired")]
    public void LegacyHardFlagMigratesToDependencyPriority(bool hard, string expected)
    {
        var json = "{\"from\":\"Medical\",\"to\":\"Commons\",\"hard\":" +
            hard.ToString().ToLowerInvariant() + ",\"weight\":2}";
        var edge = JsonSerializer.Deserialize<TopologyEdge>(json, ProjectJson.Options);
        Assert.That(edge.Priority, Is.EqualTo(expected));
        Assert.That(edge.Weight, Is.EqualTo(2));
        Assert.That(edge.Origin, Is.EqualTo("custom"), "Legacy authored dependencies must be preserved when selecting presets.");
        Assert.That(edge.AllowedModes, Does.Contain("publicHall"));
        Assert.That(ProjectJson.Clone(edge).Priority, Is.EqualTo(expected));
    }

    [Test]
    public void TemplateDependencyIdentityIsStableAcrossPresentationAndEndpointOrder()
    {
        var edge = Dependency("Cargo", "Service", "desired");
        edge.Relation = "delivery";
        edge.Purpose = "items";
        edge.AllowedModes = ["staffPassage", "mail"];
        var first = StationGenerator.TemplateDependencyId(edge);
        var reordered = ProjectJson.Clone(edge);
        (reordered.From, reordered.To) = (reordered.To, reordered.From);
        reordered.AllowedModes.Reverse();
        reordered.Weight = 9;
        reordered.Description = "Mapper's description changes presentation, not identity.";
        Assert.That(StationGenerator.TemplateDependencyId(reordered), Is.EqualTo(first));
        reordered.Direction = "forward";
        Assert.That(StationGenerator.TemplateDependencyId(reordered), Is.Not.EqualTo(first));
        Assert.That(StationGenerator.GenerateTopology(new StationGenerationConfig()).Edges.All(e => e.Origin == "template"), Is.True);
    }

    [Test]
    public void SuppressedTemplateRemovalsRoundTripAndRejectMalformedLists()
    {
        var c = new StationGenerationConfig();
        var t = StationGenerator.GenerateTopology(c);
        var removed = t.Edges[0].Id;
        t.Edges.RemoveAt(0);
        t.SuppressedTemplateIds.Add(removed);
        var restored = ProjectJson.Clone(t);
        Assert.That(restored.SuppressedTemplateIds, Is.EqualTo(new[] { removed }));
        Assert.That(restored.Edges.Any(e => e.Id == removed), Is.False);
        Assert.That(StationValidator.CheckTopology(c, restored), Is.Empty);
        foreach (var malformed in new[]
        {
            new[] { removed, removed }, new[] { "custom-dependency" }, new[] { "" }, new string[] { null },
            new[] { "template-" + new string('a', 101) },
            Enumerable.Range(0, 513).Select(i => "template-" + i).ToArray()
        })
        {
            restored.SuppressedTemplateIds = malformed.ToList();
            Assert.That(StationValidator.CheckTopology(c, restored).Any(d => d.Code == "suppressed-template"), Is.True);
        }
        restored.SuppressedTemplateIds = null;
        StationGenerator.NormalizeTopology(restored);
        Assert.That(restored.SuppressedTemplateIds, Is.Empty);
    }

    [Test]
    public void AuthoredTopologyCanRemoveADepartmentTemplateDefault()
    {
        var c = new StationGenerationConfig();
        c.Departments.Single(d => d.Id == "Medical").RequiredConnections.Add("Engineering");
        var t = StationGenerator.GenerateTopology(c);
        t.Edges.RemoveAll(e => e.Hard &&
            ((e.From == "Medical" && e.To == "Engineering") || (e.From == "Engineering" && e.To == "Medical")));
        Assert.That(StationValidator.CheckTopology(c, t), Is.Empty,
            "Department defaults must not remain hidden requirements after the mapper edits the graph.");
    }

    [Test]
    public void LegacyDependenciesReceiveStableSemanticIdsWithoutLosingAuthorship()
    {
        const string legacy = "{\"nodes\":[\"Cargo\",\"Service\",\"Medical\"],\"edges\":[" +
            "{\"from\":\"Cargo\",\"to\":\"Service\",\"hard\":true,\"weight\":1}," +
            "{\"from\":\"Medical\",\"to\":\"Service\",\"hard\":false,\"weight\":2}]}";
        var a = JsonSerializer.Deserialize<StationTopology>(legacy, ProjectJson.Options);
        var b = JsonSerializer.Deserialize<StationTopology>(legacy, ProjectJson.Options);
        b.Edges.Reverse();
        StationGenerator.NormalizeTopology(a);
        StationGenerator.NormalizeTopology(b);
        Assert.That(a.Edges.Select(e => e.Id).Order(StringComparer.Ordinal),
            Is.EqualTo(b.Edges.Select(e => e.Id).Order(StringComparer.Ordinal)));
        Assert.That(a.Edges.All(e => e.Id == StationGenerator.TemplateDependencyId(e)), Is.True);
        Assert.That(a.Edges.All(e => e.Id.StartsWith("template-", StringComparison.Ordinal) && e.Origin == "custom"), Is.True,
            "Old authored dependencies need stable IDs for removal tracking while retaining their custom origin.");
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(1001)]
    [TestCase(double.NaN)]
    public void DependencyWeightsMustBeFiniteAndPositive(double weight)
    {
        var c = new StationGenerationConfig();
        var t = Topology(c, Dependency("Medical", "Commons", "desired", weight));
        Assert.That(StationValidator.CheckTopology(c, t).Any(d => d.Severity == "error"), Is.True);
    }

    [Test]
    public void DependenciesRejectUnknownEndpointsAndSelfConnections()
    {
        var c = new StationGenerationConfig();
        foreach (var edge in new[] { Dependency("Medical", "Missing", "desired"), Dependency("Medical", "Medical", "required") })
            Assert.That(StationValidator.CheckTopology(c, Topology(c, edge)).Any(d => d.Severity == "error"), Is.True);
    }

    [Test]
    public void SameDepartmentsMayHaveDistinctWalkingAndDeliveryDependencies()
    {
        var c = new StationGenerationConfig();
        var walking = Dependency("Cargo", "Engineering", "required");
        var delivery = Dependency("Cargo", "Engineering", "desired");
        delivery.Id = "delivery";
        delivery.Relation = "delivery";
        delivery.Purpose = "items";
        delivery.AllowedModes = ["mail"];
        var t = Topology(c, walking, delivery);
        Assert.That(StationValidator.CheckTopology(c, t).Any(d => d.Severity == "error"), Is.False);
    }

    [Test]
    public void DuplicateDependenciesAreRejectedWithoutConflatingDirection()
    {
        var c = new StationGenerationConfig();
        var first = Dependency("Cargo", "Service", "desired");
        first.Direction = "forward";
        var duplicate = ProjectJson.Clone(first);
        duplicate.Id = "duplicate";
        Assert.That(StationValidator.CheckTopology(c, Topology(c, first, duplicate)).Any(d => d.Severity == "error"), Is.True);
        duplicate.From = "Service";
        duplicate.To = "Cargo";
        Assert.That(StationValidator.CheckTopology(c, Topology(c, first, duplicate)).Any(d => d.Severity == "error"), Is.False,
            "Two forward routes in opposite directions are distinct dependencies.");
    }

    [Test]
    public void MailIsNotAnEligibleCrewOrPatientConnection()
    {
        var c = new StationGenerationConfig();
        foreach (var purpose in new[] { "crew", "patients" })
        {
            var edge = Dependency("Medical", "Commons", "required");
            edge.Purpose = purpose;
            edge.Relation = "transport";
            edge.AllowedModes = ["mail"];
            Assert.That(StationValidator.CheckTopology(c, Topology(c, edge)).Any(d => d.Severity == "error"), Is.True,
                $"Mail must not satisfy {purpose} access.");
        }
    }

    [Test]
    public void StrictAvoidanceConflictsWithTheSameRequiredRelationship()
    {
        var c = new StationGenerationConfig();
        var required = Dependency("Medical", "Commons", "required");
        var avoided = Dependency("Commons", "Medical", "avoided");
        avoided.Id = "avoided";
        avoided.StrictAvoidance = true;
        Assert.That(StationValidator.CheckTopology(c, Topology(c, required, avoided)).Any(d => d.Severity == "error"), Is.True);
    }

    [Test]
    public void RequiredAlternativeMethodsRemainPossibleWhenOnlyOneIsAvoided()
    {
        var c = new StationGenerationConfig();
        var required = Dependency("Cargo", "Service", "required");
        required.Relation = "delivery";
        required.Purpose = "items";
        required.AllowedModes = ["publicHall", "mail"];
        var avoided = ProjectJson.Clone(required);
        avoided.Id = "avoid-public-delivery";
        avoided.Priority = "avoided";
        avoided.StrictAvoidance = true;
        avoided.AllowedModes = ["publicHall"];
        Assert.That(StationValidator.CheckTopology(c, Topology(c, required, avoided)).Any(d => d.Severity == "error"), Is.False,
            "Mail remains an allowed alternative for the required delivery.");
        avoided.AllowedModes.Add("mail");
        Assert.That(StationValidator.CheckTopology(c, Topology(c, required, avoided)).Any(d => d.Severity == "error"), Is.True,
            "Reject the plan when every fulfillment method is prohibited.");
    }

    [TestCase("desired")]
    [TestCase("avoided")]
    public void DependencyWeightChangesThePlacementObjective(string priority)
    {
        var c = new StationGenerationConfig();
        c.Weights.Compactness = 0;
        var edge = Dependency("Medical", "Commons", priority);
        edge.MaxDistance = priority == "desired" ? 5 : 30;
        var t = Topology(c, edge);
        var layout = new StationLayout
        {
            Departments =
            [
                new() { Id = "Medical", Bounds = new Rect(10, 10, 10, 10) },
                new() { Id = "Commons", Bounds = new Rect(22, 10, 10, 10) }
            ]
        };
        var low = StationGenerator.PlacementScore(c, t, layout);
        edge.Weight = 10;
        var high = StationGenerator.PlacementScore(c, t, layout);
        Assert.That(high, Is.GreaterThan(low), "A larger weight must change the preference penalty.");
    }

    [Test]
    public void RequiredMailCanBePlannedButCannotSilentlyBecomeAPublicCorridor()
    {
        var c = new StationGenerationConfig();
        var edge = Dependency("Cargo", "Service", "required");
        edge.Relation = "delivery";
        edge.Purpose = "items";
        edge.AllowedModes = ["mail"];
        edge.Description = "Mail is the required delivery mechanism.";
        var p = new StationProject { Config = c, Topology = Topology(c, edge) };
        Assert.That(StationValidator.CheckTopology(c, p.Topology).Any(d => d.Severity == "error"), Is.False);
        var generated = StationGenerator.Generate(p);
        Assert.That(generated.Result.Success, Is.False);
        Assert.That(generated.Result.Diagnostics.Any(d => d.Severity == "error"), Is.True);
        Assert.That(generated.Topology.Edges.Single().AllowedModes, Is.EqualTo(new[] { "mail" }));
        Assert.That(generated.Topology.Edges.Single().Description, Is.EqualTo(edge.Description));
    }

    [Test]
    public void FullGenerationUsesTheAuthoredDependenciesInsteadOfRegeneratingThem()
    {
        var c = new StationGenerationConfig { Seed = 14014 };
        var edge = Dependency("Medical", "Commons", "desired", 9);
        edge.Description = "Mapper-authored short medical access.";
        var input = new StationProject { Config = c, Topology = Topology(c, edge) };
        var p = StationGenerator.Generate(input);
        Assert.That(p.Result.Success, Is.True, Describe(p));
        Assert.That(p.Topology.Edges, Has.Count.EqualTo(1));
        Assert.That(p.Topology.Edges[0].Description, Is.EqualTo(edge.Description));
        Assert.That(p.Topology.Edges[0].Weight, Is.EqualTo(9));
    }

    private static TopologyEdge Dependency(string from, string to, string priority, double weight = 1) => new()
    {
        Id = "dependency", From = from, To = to, Priority = priority, Weight = weight,
        Relation = "walking", Purpose = "crew", AllowedModes = ["publicHall"], Direction = "both"
    };

    private static StationTopology Topology(StationGenerationConfig c, params TopologyEdge[] edges) => new()
    {
        Nodes = c.Departments.Select(d => d.Id).ToList(), Edges = edges.ToList()
    };

    [Test]
    public void PortsFollowHalfOpenWallsAndQuarterTurnDirection()
    {
        var room = new PlacedDepartment { Id = "Test", Bounds = new Rect(20, 30, 15, 11) };
        var east = StationGenerator.MakeDoor(room, 0, 3, "secure");
        Assert.That(east.Center, Is.EqualTo(new Cell(35, 35)));
        Assert.That(east.Cells.All(p => p.X == room.Bounds.Right && !room.Bounds.Contains(p)), Is.True);
        room.Bounds = new Rect(20, 30, 11, 15); room.Rotation = 90;
        var south = StationGenerator.MakeDoor(room, 1, 3, "secure");
        Assert.That(south.Center, Is.EqualTo(new Cell(25, 45)));
        Assert.That(south.Cells.Count, Is.EqualTo(east.Cells.Count));
        Assert.That(south.Cells.All(p => p.Y == room.Bounds.Bottom && !room.Bounds.Contains(p)), Is.True);
        Assert.That(south.Approach, Is.EqualTo(south.Center + Cell.Directions[1] * 4));
        Assert.That(south.Access, Is.EqualTo("secure"));
    }

    [Test]
    public void StageEditsAreUsedAndDownstreamGeometryIsRegenerated()
    {
        var p = new StationProject();
        StationGenerator.RunStage(p, "topology"); StationGenerator.RunStage(p, "placement");
        Assert.That(p.Layout, Is.Not.Null, Describe(p));
        var room = p.Layout.Departments.First(d => d.Id == "Medical");
        room.Bounds = room.Bounds with { X = room.Bounds.X + 1 };
        var edited = room.Bounds;
        StationGenerator.RunStage(p, "circulation");
        Assert.That(p.Result, Is.Null, Describe(p));
        Assert.That(p.Layout.Departments.First(d => d.Id == "Medical").Bounds, Is.EqualTo(edited));
        StationGenerator.RunStage(p, "shell"); StationGenerator.RunStage(p, "validation");
        Assert.That(p.Result.Success, Is.True, Describe(p));
    }

    [Test]
    public void SeedAndArchetypeChangeGeometry()
    {
        var hashes = Enumerable.Range(0, 6).Select(seed => Generate(seed % 2 == 0 ? "ring" : "hub", seed).Result.Metrics.Hash).Distinct().Count();
        Assert.That(hashes, Is.GreaterThan(1));
    }

    private static StationProject Generate(string archetype, int seed)
    {
        var p = StationGenerator.Generate(new StationGenerationConfig { Archetype = archetype, Seed = seed });
        Assert.That(p.Result.Success, Is.True, Describe(p));
        return p;
    }

    private static string Describe(StationProject p) => ProjectJson.Serialize(p.Result);
}
