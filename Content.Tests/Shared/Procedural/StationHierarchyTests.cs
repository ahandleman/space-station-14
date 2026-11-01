using System;
using System.Collections.Generic;
using System.Linq;
using Content.StationGeneration;
using NUnit.Framework;

namespace Content.Tests.Shared.Procedural;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class StationHierarchyTests
{
    [Test]
    public void RequiredProgramsHaveRealFootprintsPartitionsAndInternalAccess()
    {
        var project = ProgramFixture();
        var diagnostics = StationHierarchyPlanner.Build(project);
        Assert.That(diagnostics.Where(d => d.Severity == "error"), Is.Empty, ProjectJson.Serialize(diagnostics));
        Assert.That(StationHierarchyValidator.Validate(project).Where(d => d.Severity == "error"), Is.Empty);
        var hierarchy = project.SpatialPlan.Hierarchy;
        Assert.That(hierarchy.Subdepartments.Select(s => s.Id), Does.Contain("Engineering/Atmos").And.Contain("Cargo/Salvage"));
        Assert.That(hierarchy.RoomFeatures.Count, Is.EqualTo(10));
        Assert.That(hierarchy.RoomFeatures.Where(r => r.Kind == "room").All(r => r.Walls.Count > 0 && r.Doors.Count > 0), Is.True);
        Assert.That(hierarchy.Subdepartments.Where(s => s.Id is "Engineering/Atmos" or "Cargo/Salvage").All(s => !s.IsSatellite && s.HostDepartment == s.Department), Is.True);
        var teg = hierarchy.RoomFeatures.Single(r => r.Definition == "Engineering/Atmos/TEG");
        Assert.That(teg.Cells.Count, Is.GreaterThanOrEqualTo(25));
        Assert.That(teg.AccessRoutes.Select(r => r.From), Is.EquivalentTo(new[] { "Engineering", "Engineering/Atmos" }));
        foreach (var route in hierarchy.RoomFeatures.SelectMany(r => r.AccessRoutes))
            Assert.That(route.Cells.Zip(route.Cells.Skip(1)).All(p => p.First.Distance(p.Second) == 1), Is.True);
    }

    [TestCase("ring", 14014)]
    [TestCase("hub", 0)]
    [TestCase("spine", 17)]
    public void DefaultGrownFloorsFitRequiredHierarchy(string archetype, int seed)
    {
        var project = new StationProject { Config = new StationGenerationConfig { Archetype = archetype, Seed = seed } };
        project.Topology = StationGenerator.GenerateTopology(project.Config);
        StationSpatialPlanner.RunStage(project, "placement");
        Assert.That(project.Result?.Success, Is.Not.False, ProjectJson.Serialize(project.Result));
        StationSpatialPlanner.RunStage(project, "growth");
        Assert.That(project.Result?.Success, Is.Not.False, ProjectJson.Serialize(project.Result));
        var diagnostics = StationHierarchyPlanner.Build(project);
        Assert.That(diagnostics.Where(d => d.Severity == "error"), Is.Empty, ProjectJson.Serialize(diagnostics));
        var validation = StationHierarchyValidator.Validate(project);
        Assert.That(validation.Where(d => d.Severity == "error"), Is.Empty, ProjectJson.Serialize(validation));
    }

    [Test]
    public void SavedRoomCountsAndSharedAccessAreCheckedIndependently()
    {
        var project = ProgramFixture();
        StationHierarchyPlanner.Build(project);
        project.SpatialPlan.Hierarchy.RoomFeatures.RemoveAll(r => r.Definition.EndsWith("/Armory", StringComparison.Ordinal));
        var teg = project.SpatialPlan.Hierarchy.RoomFeatures.Single(r => r.Definition.EndsWith("/TEG", StringComparison.Ordinal));
        teg.AccessRoutes.Single(r => r.From == "Engineering").Cells.Clear();
        var diagnostics = StationHierarchyValidator.Validate(project);
        Assert.That(diagnostics.Any(d => d.Code == "room-count"), Is.True);
        Assert.That(diagnostics.Any(d => d.Code == "room-access"), Is.True);
        Assert.That(diagnostics.Any(d => d.Code == "circulation-mask"), Is.True,
            "Deleting a room declaration cannot leave an unverified circulation mask behind.");
    }

    [Test]
    public void FlexibleServiceCanUseAnExplicitSatelliteButInternalProgramsCannot()
    {
        var project = new StationProject
        {
            Config = new StationGenerationConfig
            {
                Departments =
                [
                    new() { Id = "Service", Subdepartments = [new() { Id = "Kitchen", MinArea = 40, TargetArea = 45 }] },
                    new() { Id = "Commons" }
                ]
            },
            SpatialPlan = new StationSpatialPlan()
        };
        AddFloor(project, "Service", new Rect(10, 10, 5, 5));
        AddFloor(project, "Commons", new Rect(25, 10, 14, 12));
        var diagnostics = StationHierarchyPlanner.Build(project);
        Assert.That(diagnostics.Any(d => d.Severity == "error"), Is.False, ProjectJson.Serialize(diagnostics));
        var zone = project.SpatialPlan.Hierarchy.Subdepartments.Single();
        Assert.That(zone.IsSatellite, Is.True);
        Assert.That(zone.HostDepartment, Is.EqualTo("Commons"));
        Assert.That(zone.Cells.All(project.SpatialPlan.Regions.Single(r => r.Id == "Commons").Cells.Contains), Is.True);
        Assert.That(StationHierarchyValidator.Validate(project).Any(d => d.Severity == "error"), Is.False);
        project.Config.Departments[0].Subdepartments[0].RequireInternalAccess = true;
        Assert.That(StationHierarchyValidator.Validate(project).Any(d => d.Code == "satellite-policy" || d.Code == "subdepartment-internal-access"), Is.True);
        diagnostics = StationHierarchyPlanner.Build(project);
        Assert.That(diagnostics.Any(d => d.Code == "program-fit"), Is.True);
        Assert.That(project.SpatialPlan.Hierarchy.Subdepartments, Is.Empty);
    }

    [Test]
    public void ImpossibleProgramsFailWithoutFabricatingRoomsOrAccess()
    {
        var project = new StationProject
        {
            Config = new StationGenerationConfig
            {
                Departments = [new() { Id = "Security", AllowSplit = false, Subdepartments =
                    [new() { Id = "Brig", MinArea = 20, Rooms = [new() { Id = "Armory", MinArea = 100, MaxArea = 120 }] }] }]
            },
            SpatialPlan = new StationSpatialPlan()
        };
        AddFloor(project, "Security", new Rect(10, 10, 6, 6));
        Assert.That(StationHierarchyPlanner.Build(project).Any(d => d.Code == "program-fit"), Is.True);
        Assert.That(project.SpatialPlan.Hierarchy.RoomFeatures, Is.Empty);
        Assert.That(StationHierarchyValidator.Validate(project).Any(d => d.Code == "room-count"), Is.True);
    }

    [Test]
    public void RebuildingAndJsonRoundTripKeepDeterministicHierarchyGeometry()
    {
        var project = ProgramFixture();
        StationHierarchyPlanner.Build(project);
        var serialized = ProjectJson.Serialize(project.SpatialPlan.Hierarchy);
        StationHierarchyPlanner.Build(project);
        Assert.That(ProjectJson.Serialize(project.SpatialPlan.Hierarchy), Is.EqualTo(serialized));
        var loaded = ProjectJson.Clone(project);
        Assert.That(StationHierarchyValidator.Validate(loaded).Any(d => d.Severity == "error"), Is.False);
    }

    [Test]
    public void RequiredScopedRoutesUseActualWalkingFloorAndDistance()
    {
        var project = ProgramFixture();
        project.Config.HierarchyRelations = [new("Engineering", "Engineering/Atmos/TEG")
            { Id = "teg-access", AllowedModes = ["staffPassage"], MaxDistance = 100, Weight = 7, Description = "Engineering servicing route" }];
        StationHierarchyPlanner.Build(project);
        Assert.That(StationHierarchyValidator.Validate(project).Any(d => d.Code == "required-route"), Is.False);
        project.Config.HierarchyRelations[0].MaxDistance = 0;
        Assert.That(StationHierarchyValidator.Validate(project).Any(d => d.Code == "required-route"), Is.True);
        project.Config.HierarchyRelations[0].AllowedModes = ["disposalShortcut"];
        Assert.That(StationHierarchyValidator.Validate(project).Any(d => d.Code == "unsupported-fulfillment"), Is.True);
    }

    [Test]
    public void HierarchyWeightsMoveFlexibleSubdivisionsTowardTheStrongerRelation()
    {
        var project = new StationProject
        {
            Config = new StationGenerationConfig
            {
                Departments = [new() { Id = "Service", Subdepartments = [
                    new() { Id = "A", MinArea = 16, TargetArea = 16 },
                    new() { Id = "B", MinArea = 16, TargetArea = 16 },
                    new() { Id = "C", MinArea = 16, TargetArea = 16 }] }],
                HierarchyRelations = [
                    new("Service/A", "Service/B", false, 50) { Priority = "avoided" },
                    new("Service/C", "Service/A", false, 100),
                    new("Service/C", "Service/B", false, 1)]
            },
            SpatialPlan = new StationSpatialPlan()
        };
        AddFloor(project, "Service", new Rect(10, 10, 24, 14));
        StationHierarchyPlanner.Build(project);
        var first = Distances(project);
        Assert.That(first.ToA, Is.LessThan(first.ToB));
        project.Config.HierarchyRelations[1].Weight = 1;
        project.Config.HierarchyRelations[2].Weight = 100;
        StationHierarchyPlanner.Build(project);
        var second = Distances(project);
        Assert.That(second.ToB, Is.LessThan(second.ToA));

        static (double ToA, double ToB) Distances(StationProject source)
        {
            var zones = source.SpatialPlan.Hierarchy.Subdepartments.ToDictionary(z => z.Id);
            var c = zones["Service/C"].Cells;
            return (c.Average(p => zones["Service/A"].Cells.Min(p.Distance)), c.Average(p => zones["Service/B"].Cells.Min(p.Distance)));
        }
    }

    private static StationProject ProgramFixture()
    {
        var project = new StationProject { Config = new StationGenerationConfig { Width = 400, Height = 120 }, SpatialPlan = new StationSpatialPlan() };
        var index = 0;
        foreach (var department in project.Config.Departments)
        {
            var width = department.Id is "Security" or "Command" ? 22 : 17;
            AddFloor(project, department.Id, new Rect(4 + index++ * 35, 20, width, 18));
        }
        return project;
    }

    private static void AddFloor(StationProject project, string id, Rect bounds)
    {
        var floor = bounds.Cells().ToList();
        var ring = bounds.Expand(1).Cells().Except(floor).ToList();
        project.SpatialPlan.Regions.Add(new DepartmentRegion { Id = id, Cells = floor, Territory = bounds.Expand(1).Cells().ToList() });
        project.SpatialPlan.Walls.AddRange(ring);
        var door = new Cell(bounds.X - 1, bounds.Y + bounds.Height / 2);
        project.SpatialPlan.Ports.Add(new SpatialPort { Department = id, Cells = [door], Center = door, Width = 1 });
        project.SpatialPlan.Walls.Remove(door);
    }
}
