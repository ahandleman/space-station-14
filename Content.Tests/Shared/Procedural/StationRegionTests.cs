using System;
using System.Collections.Generic;
using System.Linq;
using Content.StationGeneration;
using NUnit.Framework;

namespace Content.Tests.Shared.Procedural;

/// <summary>
/// Invariants are checked directly on exported masks as well as by the planner's
/// validator. These tests deliberately do not depend on a particular incidental map.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class StationRegionTests
{
    [TestCase("hub", 0)]
    [TestCase("ring", 14014)]
    [TestCase("spine", 17)]
    [TestCase("ring", 42)]
    public void GrownStationHasContiguousTerritoriesAndUsablePublicAccess(string archetype, int seed)
    {
        var project = Generate(archetype, seed, validate: false);
        var plan = project.SpatialPlan;
        var envelope = plan.Envelope.ToHashSet();
        var definitions = project.Config.Departments.ToDictionary(d => d.Id);
        var ownership = new HashSet<Cell>();
        var floors = new HashSet<Cell>();
        Assert.That(plan.Regions.Select(r => r.Id).ToHashSet(), Is.EquivalentTo(definitions.Keys));

        foreach (var region in plan.Regions)
        {
            var territory = region.Territory.ToHashSet();
            var roomFloors = region.Cells.ToHashSet();
            Assert.That(region.Territory.Count, Is.EqualTo(territory.Count), $"{region.Id}: duplicate territory cells");
            Assert.That(region.Cells.Count, Is.EqualTo(roomFloors.Count), $"{region.Id}: duplicate room floor cells");
            Assert.That(territory.Count, Is.GreaterThan(0), $"{region.Id}: empty territory");
            Assert.That(ComponentCount(territory), Is.EqualTo(1), $"{region.Id}: disconnected territory");
            Assert.That(ComponentCount(roomFloors), Is.EqualTo(1), $"{region.Id}: disconnected usable interior");
            Assert.That(envelope.IsSupersetOf(territory), Is.True, $"{region.Id}: outside station envelope");
            Assert.That(territory.IsSupersetOf(roomFloors), Is.True, $"{region.Id}: floors outside territory");
            Assert.That(ownership.Overlaps(territory), Is.False, $"{region.Id}: department ownership overlap");
            Assert.That(roomFloors.Count, Is.InRange(definitions[region.Id].MinArea, definitions[region.Id].MaxArea),
                $"{region.Id}: usable footprint is outside its configured budget");
            Assert.That(roomFloors.All(p => InTwoByTwoSquare(p, roomFloors)), Is.True,
                $"{region.Id}: one-tile interior tendril or isolated sliver");
            Assert.That(ContainsSquare(roomFloors, 5), Is.True,
                $"{region.Id}: a major department needs a usable five-by-five room core, not only a narrow floor ribbon");
            ownership.UnionWith(territory);
            floors.UnionWith(roomFloors);
        }

        var publicTiles = plan.PublicCorridors.ToHashSet();
        var publicPorts = plan.Ports.Where(p => p.Kind == "public").ToList();
        publicTiles.UnionWith(publicPorts.SelectMany(p => p.Cells));
        Assert.That(publicTiles.Count, Is.GreaterThan(0));
        Assert.That(ComponentCount(publicTiles), Is.EqualTo(1), "The actual public walking layer must be connected.");
        Assert.That(floors.Overlaps(publicTiles), Is.False, "Public circulation must not traverse room interiors.");
        foreach (var region in plan.Regions)
        {
            var roomFloors = region.Cells.ToHashSet();
            var ports = publicPorts.Where(p => p.Department == region.Id).ToList();
            Assert.That(ports.Count, Is.GreaterThanOrEqualTo(definitions[region.Id].Entrances),
                $"{region.Id}: insufficient declared public frontage entrances");
            foreach (var port in ports)
            {
                Assert.That(publicTiles.IsSupersetOf(port.Cells), Is.True, $"{region.Id}: missing public entrance");
                Assert.That(port.Cells.Any(p => Cell.Directions.Any(d => roomFloors.Contains(p + d))), Is.True,
                    $"{region.Id}: entrance does not lead into the department");
                Assert.That(port.Access, Is.EqualTo(definitions[region.Id].Access),
                    $"{region.Id}: entrance access must match its declared policy");
            }
        }
    }

    [Test]
    public void MaintenanceIsAnAccessibleServiceNetworkWithoutRoomBypasses()
    {
        var project = Generate("ring", 14014, validate: false);
        var plan = project.SpatialPlan;
        var maintenance = plan.Maintenance.ToHashSet();
        var envelope = plan.Envelope.ToHashSet();
        var roomFloors = plan.Regions.SelectMany(r => r.Cells).ToHashSet();
        var publicTiles = plan.PublicCorridors.Concat(plan.Ports.Where(p => p.Kind == "public").SelectMany(p => p.Cells)).ToHashSet();
        Assert.That(maintenance.Count, Is.GreaterThan(0));
        Assert.That(envelope.IsSupersetOf(maintenance), Is.True);
        Assert.That(maintenance.Overlaps(roomFloors), Is.False);
        Assert.That(maintenance.Overlaps(plan.Walls), Is.False);
        Assert.That(maintenance.Overlaps(publicTiles), Is.False);
        Assert.That(maintenance.Overlaps(plan.ReservedExterior), Is.False);
        foreach (var segment in Components(maintenance))
            Assert.That(segment.Any(p => Cell.Directions.Any(d => publicTiles.Contains(p + d))), Is.True,
                "Every service segment must have an actual access route to public circulation.");
        Assert.That(maintenance.Any(p => Cell.Directions.All(d => envelope.Contains(p + d))), Is.True,
            "Maintenance must include internal service space rather than only exterior boundary cells.");
        Assert.That(plan.Ports.All(p => p.Kind is "public" or "adjacency" or "maintenance"), Is.True);
        foreach (var port in plan.Ports.Where(p => p.Kind == "maintenance"))
        {
            var definition = project.Config.Departments.Single(d => d.Id == port.Department);
            Assert.That(definition.Access, Is.Not.EqualTo("secure"),
                "Secure departments must not receive implicit maintenance bypasses.");
            Assert.That(port.Access, Is.EqualTo(definition.Access), "Service entrances retain the department's access policy.");
        }
    }

    [TestCase("walking", "crew")]
    [TestCase("delivery", "items")]
    public void RequiredDependencyCanUseAnActualMaintenanceRoute(string relation, string purpose)
    {
        var (project, dependency) = MaintenanceDependencyFixture();
        dependency.Relation = relation;
        dependency.Purpose = purpose;
        var result = StationSpatialPlanner.Validate(project);
        Assert.That(result.Success, Is.True, ProjectJson.Serialize(result));
        Assert.That(dependency.AllowedModes, Is.EqualTo(new[] { "maintenance" }));
    }

    [Test]
    public void MaintenanceOnlyRequirementDoesNotFallBackToPublicHallAccess()
    {
        var (project, dependency) = MaintenanceDependencyFixture();
        Assert.That(StationSpatialPlanner.Validate(project).Success, Is.True);
        Assert.That(project.SpatialPlan.Ports.Any(p => p.Department == dependency.To && p.Kind == "public"), Is.True,
            "The destination deliberately retains public access, which must not fulfill a maintenance-only requirement.");
        project.SpatialPlan.Ports.RemoveAll(p => p.Department == dependency.To && p.Kind == "maintenance");
        var result = StationSpatialPlanner.Validate(project);
        Assert.That(result.Success, Is.False);
        Assert.That(result.Diagnostics.Any(d => d.Code == "required-route"), Is.True,
            ProjectJson.Serialize(result.Diagnostics));
    }

    [Test]
    public void MaintenanceOnlyRequirementChecksActualNetworkTravelDistance()
    {
        var (project, dependency) = MaintenanceDependencyFixture();
        Assert.That(StationSpatialPlanner.Validate(project).Success, Is.True);
        dependency.MaxDistance = 1;
        var result = StationSpatialPlanner.Validate(project);
        Assert.That(result.Success, Is.False);
        Assert.That(result.Diagnostics.Any(d => d.Code == "required-distance"), Is.True,
            ProjectJson.Serialize(result.Diagnostics));
    }

    [Test]
    public void MaintenanceOnlyRequirementNeedsWalkableServiceFloorBetweenPorts()
    {
        var (project, dependency) = MaintenanceDependencyFixture();
        Assert.That(StationSpatialPlanner.Validate(project).Success, Is.True);
        Assert.That(project.SpatialPlan.Ports.Count(p => p.Kind == "maintenance" &&
            (p.Department == dependency.From || p.Department == dependency.To)), Is.GreaterThanOrEqualTo(2));
        project.SpatialPlan.Maintenance.Clear();
        var result = StationSpatialPlanner.Validate(project);
        Assert.That(result.Success, Is.False);
        Assert.That(result.Diagnostics.Any(d => d.Code == "required-route"), Is.True,
            "A pair of endpoint declarations cannot replace actual connected maintenance floor tiles.");
    }

    [Test]
    public void EveryEnvelopeCellHasExplicitOwnershipOrReservation()
    {
        var project = Generate("hub", 51, validate: false);
        var plan = project.SpatialPlan;
        var classified = plan.Regions.SelectMany(r => r.Territory).Concat(plan.PublicCorridors)
            .Concat(plan.Maintenance).Concat(plan.Walls).Concat(plan.Hull).Concat(plan.Ports.SelectMany(p => p.Cells)).Concat(plan.StructuralReserve)
            .Concat(plan.DeepMaintenanceRooms.SelectMany(room => room.Cells.Concat(room.PortCells)))
            .Concat(plan.ReservedExterior).ToHashSet();
        Assert.That(classified.IsSupersetOf(plan.Envelope), Is.True,
            "Unassigned envelope space must be an explicit structural reservation, not implicit maintenance.");
        Assert.That(plan.StructuralReserve.Intersect(plan.Regions.SelectMany(r => r.Cells)), Is.Empty);
        Assert.That(plan.StructuralReserve.Intersect(plan.PublicCorridors), Is.Empty);
        Assert.That(plan.StructuralReserve.Intersect(plan.Maintenance), Is.Empty);
    }

    [Test]
    public void GrowthProducesNonrectangularTerritoriesAndSeedVariation()
    {
        var hashes = new HashSet<string>();
        var nonrectangular = 0;
        for (var seed = 0; seed < 6; seed++)
        {
            var project = Generate(seed % 2 == 0 ? "hub" : "ring", seed);
            hashes.Add(project.Result.Metrics.Hash);
            nonrectangular += project.SpatialPlan.Regions.Count(r =>
                (r.Territory.Max(p => p.X) - r.Territory.Min(p => p.X) + 1) *
                (r.Territory.Max(p => p.Y) - r.Territory.Min(p => p.Y) + 1) > r.Territory.Count);
        }
        Assert.That(nonrectangular, Is.GreaterThan(0), "Growth must change actual masks, not only rectangle metadata.");
        Assert.That(hashes.Count, Is.GreaterThan(1));
    }

    [Test]
    public void SameSeedAndSavedProjectProduceIdenticalMasksAndMetrics()
    {
        var first = Generate("ring", 72);
        var second = Generate("ring", 72);
        Assert.That(ProjectJson.Serialize(first.SpatialPlan), Is.EqualTo(ProjectJson.Serialize(second.SpatialPlan)));
        Assert.That(first.Result.Metrics.Hash, Is.EqualTo(second.Result.Metrics.Hash));
        var loaded = ProjectJson.Clone(first);
        var validation = StationSpatialPlanner.Validate(loaded);
        Assert.That(validation.Success, Is.True, ProjectJson.Serialize(validation));
        Assert.That(validation.Metrics.Hash, Is.EqualTo(first.Result.Metrics.Hash));
    }

    [Test]
    public void DownstreamStagesPreserveSeedsAndAuthoredTopology()
    {
        var project = Start("ring", 14014);
        StationSpatialPlanner.RunStage(project, "placement");
        AssertNoStageErrors(project);
        var seed = project.SpatialPlan.Seeds[0];
        seed.Pinned = true;
        var position = seed.Position;
        var target = seed.TargetArea;
        project.Topology.Edges[0].Description = "Mapper-authored dependency survives growth.";
        var topology = ProjectJson.Serialize(project.Topology);
        foreach (var stage in new[] { "growth", "maintenance", "boundaries", "validation" })
        {
            StationSpatialPlanner.RunStage(project, stage);
            AssertNoStageErrors(project);
        }
        Assert.That(project.SpatialPlan.Seeds[0].Position, Is.EqualTo(position));
        Assert.That(project.SpatialPlan.Seeds[0].TargetArea, Is.EqualTo(target));
        Assert.That(project.SpatialPlan.Seeds[0].Pinned, Is.True);
        Assert.That(ProjectJson.Serialize(project.Topology), Is.EqualTo(topology));
    }

    [Test]
    public void EditedEnvelopeAndPinnedSeedSurvivePlacementAndGrowth()
    {
        var project = Start("ring", 14014);
        StationSpatialPlanner.RunStage(project, "placement");
        AssertNoStageErrors(project);
        var plan = project.SpatialPlan;
        var envelope = plan.Envelope.ToHashSet();
        var protectedCells = plan.PublicReservations.Concat(plan.ServiceReservations)
            .Concat(plan.Seeds.SelectMany(s => s.CoreFootprint)).ToHashSet();
        var groups = plan.Envelope.GroupBy(p => new Cell(p.X / plan.CellSize, p.Y / plan.CellSize));
        var removable = groups.Where(group => group.Count() == plan.CellSize * plan.CellSize && !group.Any(protectedCells.Contains))
            .Select(group => group.ToHashSet()).FirstOrDefault(patch => ComponentCount(envelope.Except(patch).ToHashSet()) == 1);
        Assert.That(removable, Is.Not.Null, "The reference envelope should have an unreserved parcel for a mapper edit.");
        envelope.ExceptWith(removable);
        plan.Envelope = envelope.OrderBy(p => p.Y).ThenBy(p => p.X).ToList();
        plan.EnvelopeEdited = true;
        var pinned = plan.Seeds.Single(s => s.Id == "Medical");
        pinned.Pinned = true;
        var position = pinned.Position;
        foreach (var stage in new[] { "placement", "growth", "maintenance", "validation" })
        {
            StationSpatialPlanner.RunStage(project, stage);
            AssertNoStageErrors(project);
        }
        Assert.That(project.Result.Success, Is.True);
        Assert.That(project.SpatialPlan.Envelope.ToHashSet().SetEquals(envelope), Is.True,
            "Automatic placement/growth must not replace an edited envelope with a fresh silhouette.");
        Assert.That(project.SpatialPlan.Seeds.Single(s => s.Id == "Medical").Position, Is.EqualTo(position));
        Assert.That(project.SpatialPlan.Seeds.Single(s => s.Id == "Medical").Pinned, Is.True);
    }

    [Test]
    public void ImpossibleSeedAndInvalidSearchSettingsReturnStructuredFailures()
    {
        var invalid = Start("ring", 9);
        invalid.Config.Spatial.CellSize = 0;
        StationSpatialPlanner.RunStage(invalid, "placement");
        Assert.That(invalid.Result, Is.Not.Null);
        Assert.That(invalid.Result.Success, Is.False);
        Assert.That(invalid.Result.Diagnostics.Any(d => d.Severity == "error"), Is.True);

        var misplaced = Start("hub", 9);
        StationSpatialPlanner.RunStage(misplaced, "placement");
        AssertNoStageErrors(misplaced);
        misplaced.SpatialPlan.Seeds[0].Position = new Cell(-10, -10);
        StationSpatialPlanner.RunStage(misplaced, "growth");
        Assert.That(misplaced.Result, Is.Not.Null);
        Assert.That(misplaced.Result.Success, Is.False);
        Assert.That(misplaced.Result.Diagnostics.Any(d => d.Severity == "error"), Is.True);
    }

    [Test]
    public void UsableAreaBudgetCanExceedTheLegacyRectangleEstimate()
    {
        var project = Start("hub", 51);
        var medical = project.Config.Departments.Single(d => d.Id == "Medical");
        Assert.That(medical.Width * medical.Height, Is.LessThan(300));
        medical.MinArea = 300;
        medical.MaxArea = 800;
        StationSpatialPlanner.RunStage(project, "placement");
        AssertNoStageErrors(project);
        Assert.That(project.SpatialPlan.Seeds.Single(s => s.Id == "Medical").TargetArea, Is.InRange(300, 800),
            "Rectangle dimensions are an estimate; spatial area budgets must be independently editable.");
    }

    [Test]
    public void DependencyPreferencesInfluencePhysicalSeedPlacement()
    {
        int PlaceAndMeasure(string priority, double weight)
        {
            var project = Start("hub", 17);
            project.Topology.Edges =
            [
                new TopologyEdge("Medical", "Science", false, weight)
                {
                    Id = "medical-science-preference", Priority = priority,
                    Description = "A mapper preference must influence physical department placement."
                }
            ];
            StationSpatialPlanner.RunStage(project, "placement");
            AssertNoStageErrors(project);
            var seeds = project.SpatialPlan.Seeds.ToDictionary(s => s.Id);
            return seeds["Medical"].Position.Distance(seeds["Science"].Position);
        }
        var weakDesired = PlaceAndMeasure("desired", .1);
        var strongDesired = PlaceAndMeasure("desired", 1000);
        var strongAvoided = PlaceAndMeasure("avoided", 1000);
        Assert.That(strongDesired, Is.LessThanOrEqualTo(weakDesired));
        Assert.That(strongAvoided, Is.GreaterThan(strongDesired),
            "Required/Desired/Avoided must shape physical placement rather than remain diagram-only metadata.");
    }

    [Test]
    public void ValidatorRejectsEditedMasksThatViolateActualGridInvariants()
    {
        var baseline = Generate("ring", 14014);
        void Reject(Action<StationProject> edit, string reason)
        {
            var project = ProjectJson.Clone(baseline);
            edit(project);
            var result = StationSpatialPlanner.Validate(project);
            Assert.That(result.Success, Is.False, reason);
            Assert.That(result.Diagnostics.Any(d => d.Severity == "error"), Is.True, reason);
        }
        Reject(p => p.SpatialPlan.Regions[0].Territory.Add(new Cell(-1, -1)), "Out-of-envelope ownership");
        Reject(p => p.SpatialPlan.Regions[1].Territory.Add(p.SpatialPlan.Regions[0].Territory[0]), "Overlapping territories");
        Reject(p => p.SpatialPlan.PublicCorridors.Add(p.SpatialPlan.Regions[0].Cells[0]), "Hall through department interior");
        Reject(p => p.SpatialPlan.PublicCorridors.Clear(), "Missing public circulation");
        Reject(p => p.SpatialPlan.Maintenance.Add(p.SpatialPlan.Regions.First(r => r.Id == "Security").Cells[0]), "Maintenance bypass into Security");
        Reject(p => p.SpatialPlan.Maintenance.Clear(), "Missing deliberate maintenance network");
    }

    [Test]
    public void ValidatorRejectsFloorRibbonWithAreaButNoUsableRoomCore()
    {
        var project = Generate("hub", 0);
        var region = project.SpatialPlan.Regions.First(r => r.Id == "Medical");
        // It is contiguous, has no one-tile tendril, and exceeds the area's lower
        // bound. The separate room-fit diagnostic must still reject its shape.
        region.Cells = new Rect(region.Seed.X, region.Seed.Y, 3, 20).Cells().ToList();
        Assert.That(region.Cells.Count, Is.GreaterThanOrEqualTo(project.Config.Departments.Single(d => d.Id == region.Id).MinArea));
        var result = StationSpatialPlanner.Validate(project);
        Assert.That(result.Success, Is.False);
        Assert.That(result.Diagnostics.Any(d => d.Code == "room-fit"), Is.True, ProjectJson.Serialize(result.Diagnostics));
    }

    [Test]
    [Explicit("Developer-run spatial regression batch; select this exact test to run all 100 seeds.")]
    public void SpatialBatchOfOneHundredSeeds()
    {
        var failures = new List<string>();
        var hashes = new HashSet<string>();
        var styles = new[] { "hub", "ring", "spine" };
        for (var seed = 0; seed < 100; seed++)
        {
            var project = Start(styles[seed % styles.Length], seed);
            var stageFailed = false;
            foreach (var stage in new[] { "placement", "growth", "maintenance", "validation" })
            {
                StationSpatialPlanner.RunStage(project, stage);
                if (project.Result?.Diagnostics.Any(d => d.Severity == "error") != true) continue;
                failures.Add($"seed={seed}, stage={stage}: {ProjectJson.Serialize(project.Result.Diagnostics)}");
                stageFailed = true;
                break;
            }
            if (project.Result?.Success != true)
            {
                if (!stageFailed) failures.Add($"seed={seed}: validation did not return an accepted result");
                continue;
            }
            hashes.Add(project.Result.Metrics.Hash);
            var owned = new HashSet<Cell>();
            foreach (var region in project.SpatialPlan.Regions)
            {
                var cells = region.Territory.ToHashSet();
                if (ComponentCount(cells) != 1 || owned.Overlaps(cells))
                    failures.Add($"seed={seed}: invalid territory ownership/connectivity for {region.Id}");
                owned.UnionWith(cells);
            }
            if ((seed + 1) % 20 == 0) TestContext.Progress.WriteLine($"Spatial seeds attempted {seed + 1}/100; failures={failures.Count}");
        }
        TestContext.Progress.WriteLine($"Spatial batch attempted=100, failures={failures.Count}, unique accepted hashes={hashes.Count}");
        Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
        Assert.That(hashes.Count, Is.GreaterThan(1));
    }

    private static StationProject Start(string archetype, int seed)
    {
        var project = new StationProject { Config = new StationGenerationConfig { Archetype = archetype, Seed = seed } };
        project.Topology = StationGenerator.GenerateTopology(project.Config);
        return project;
    }

    private static (StationProject Project, TopologyEdge Dependency) MaintenanceDependencyFixture()
    {
        // Choose endpoints from a real generated maintenance component. This is a
        // fixture search, not generation failure masking: every attempted seed must
        // independently generate and validate before its service frontages are used.
        for (var seed = 0; seed <= 10; seed++)
        {
            var project = Generate("ring", seed);
            var plan = project.SpatialPlan;
            var ports = plan.Ports.Where(p => p.Kind == "maintenance").ToList();
            var walking = plan.Maintenance.Concat(ports.SelectMany(p => p.Cells)).ToHashSet();
            foreach (var component in Components(walking))
            {
                var endpoints = ports.Where(p => component.Contains(p.Center))
                    .OrderBy(p => p.Department, StringComparer.Ordinal).ToList();
                if (endpoints.Select(p => p.Department).Distinct(StringComparer.Ordinal).Count() < 2) continue;
                var from = endpoints[0];
                var to = endpoints.First(p => p.Department != from.Department);
                var dependency = new TopologyEdge(from.Department, to.Department, true, 5)
                {
                    Id = "required-maintenance-services", Origin = "custom", AllowedModes = ["maintenance"],
                    Description = "These departments require an actual connection through their declared maintenance entrances."
                };
                project.Topology.Edges.Add(dependency);
                return (project, dependency);
            }
        }
        Assert.Fail("The reference station profiles did not provide two declared department entrances on one maintenance component.");
        throw new InvalidOperationException("Unreachable maintenance fixture failure.");
    }

    private static StationProject Generate(string archetype, int seed, bool validate = true)
    {
        var project = Start(archetype, seed);
        foreach (var stage in validate ? new[] { "placement", "growth", "maintenance", "validation" } : new[] { "placement", "growth", "maintenance" })
        {
            StationSpatialPlanner.RunStage(project, stage);
            AssertNoStageErrors(project);
        }
        if (validate) Assert.That(project.Result?.Success, Is.True, ProjectJson.Serialize(project.Result));
        return project;
    }

    private static void AssertNoStageErrors(StationProject project) =>
        Assert.That(project.Result?.Diagnostics.Any(d => d.Severity == "error") ?? false, Is.False, ProjectJson.Serialize(project.Result));

    private static bool InTwoByTwoSquare(Cell cell, HashSet<Cell> region)
    {
        for (var dy = -1; dy <= 0; dy++)
        for (var dx = -1; dx <= 0; dx++)
            if (region.Contains(cell + new Cell(dx, dy)) && region.Contains(cell + new Cell(dx + 1, dy)) &&
                region.Contains(cell + new Cell(dx, dy + 1)) && region.Contains(cell + new Cell(dx + 1, dy + 1))) return true;
        return false;
    }

    private static bool ContainsSquare(HashSet<Cell> cells, int width) => cells.Any(origin =>
        Enumerable.Range(0, width).All(y => Enumerable.Range(0, width).All(x => cells.Contains(origin + new Cell(x, y)))));

    private static int ComponentCount(HashSet<Cell> cells) => Components(cells).Count;

    private static List<HashSet<Cell>> Components(HashSet<Cell> cells)
    {
        var remaining = new HashSet<Cell>(cells);
        var components = new List<HashSet<Cell>>();
        while (remaining.Count > 0)
        {
            var seed = remaining.First();
            var component = new HashSet<Cell> { seed };
            var queue = new Queue<Cell>();
            remaining.Remove(seed); queue.Enqueue(seed);
            while (queue.TryDequeue(out var current))
            foreach (var direction in Cell.Directions)
            {
                var next = current + direction;
                if (!remaining.Remove(next)) continue;
                component.Add(next); queue.Enqueue(next);
            }
            components.Add(component);
        }
        return components;
    }
}
