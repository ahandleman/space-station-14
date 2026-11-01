using System;
using System.Collections.Generic;
using System.Linq;
using Content.StationGeneration;
using NUnit.Framework;

namespace Content.Tests.Shared.Procedural;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class StationMaintenanceTests
{
    private static readonly Lazy<StationProject> Default = new(() => StationSpatialPlanner.Generate(new StationProject()));
    private static StationProject Project() => ProjectJson.Clone(Default.Value);
    private static IEnumerable<Cell> Neighbors(Cell p) => Cell.Directions.Select(d => p + d);

    [Test]
    public void DefaultStationHasNarrowMaintenanceAndSeparatesDepartments()
    {
        var project = Project();var plan = project.SpatialPlan;
        Assert.That(project.Result.Success, Is.True, string.Join("; ", project.Result.Diagnostics));
        Assert.That(plan.Maintenance.Count, Is.GreaterThan(0));
        Assert.That(MaintenancePlanner.ValidatePassageWidth(plan.Maintenance,project.Config.MaintenanceWidth,project.Config.Spatial.MaintenancePassageMaxWidth), Is.Empty);
        var ownership = plan.Regions.SelectMany(r => r.Territory.Select(cell => (cell, r.Id))).ToDictionary(p => p.cell, p => p.Id);
        Assert.That(ownership.Any(pair => Neighbors(pair.Key).Any(q => ownership.TryGetValue(q, out var other) && other != pair.Value)), Is.False);
    }

    [Test]
    public void DeepRoomsHaveEnclosedFloorAndMaintenanceOnlyDoors()
    {
        var project = Project();var plan = project.SpatialPlan;var maintenance = plan.Maintenance.ToHashSet();var halls = plan.PublicCorridors.ToHashSet();
        Assert.That(plan.DeepMaintenanceRooms.Count, Is.InRange(project.Config.Spatial.DeepMaintenanceMin, project.Config.Spatial.DeepMaintenanceMax));
        foreach (var room in plan.DeepMaintenanceRooms)
        {
            var floor = room.Cells.ToHashSet();var boundary = plan.Walls.Concat(plan.Hull).Concat(room.PortCells).ToHashSet();
            Assert.That(floor.All(p => Neighbors(p).All(q => floor.Contains(q) || boundary.Contains(q))), Is.True);
            Assert.That(room.PortCells.All(p => Neighbors(p).Any(maintenance.Contains) && !Neighbors(p).Any(halls.Contains)), Is.True);
            Assert.That(maintenance.Contains(room.MaintenanceApproach), Is.True);
            Assert.That(floor.Overlaps(maintenance), Is.False);
        }
    }

    [Test]
    public void SolarPanelsAndCatwalksRemainExternalAndClearOfDocking()
    {
        var project = Project();var plan = project.SpatialPlan;var envelope = plan.Envelope.ToHashSet();var docks = plan.ReservedExterior.ToHashSet();
        Assert.That(plan.SolarInstallations.Count, Is.InRange(project.Config.Spatial.SolarMin, project.Config.Spatial.SolarMax));
        foreach (var solar in plan.SolarInstallations)
        {
            Assert.That(solar.Cells.Concat(solar.Catwalk).Any(p => envelope.Contains(p) || docks.Contains(p)), Is.False);
            Assert.That(plan.Maintenance, Does.Contain(solar.MaintenanceApproach));
            Assert.That(solar.ConnectionCells.All(plan.Hull.Contains), Is.True);
            Assert.That(solar.ConnectionCells.All(p => Neighbors(p).Any(solar.Catwalk.Contains)), Is.True);
        }
    }

    [Test]
    public void SpaceAccessRetainsWallOrWindowBarrierAtHull()
    {
        var project = Project();var plan = project.SpatialPlan;
        Assert.That(plan.MaintenanceSpaceAccess.Count, Is.InRange(project.Config.Spatial.SpaceAccessMin, project.Config.Spatial.SpaceAccessMax));
        foreach (var access in plan.MaintenanceSpaceAccess)
        {
            Assert.That(access.Barrier, Is.AnyOf("wall", "window"));
            Assert.That(access.BarrierCells.All(plan.Hull.Contains), Is.True);
            Assert.That(plan.Maintenance, Does.Contain(access.MaintenanceApproach));
            Assert.That(access.ExteriorCells.Any(plan.Envelope.Contains), Is.False);
        }
    }

    [Test]
    public void DeepRoomCannotReplaceMaintenanceApproachWithPublicHall()
    {
        var project = Project();project.SpatialPlan.DeepMaintenanceRooms[0].MaintenanceApproach = project.SpatialPlan.PublicCorridors[0];
        Assert.That(MaintenancePlanner.Validate(project).Any(d => d.Code == "deep-maintenance-access"), Is.True);
    }

    [Test]
    public void DeepRoomCannotHideDetachedSolidWalls()
    {
        var project = Project();var plan = project.SpatialPlan;var room = plan.DeepMaintenanceRooms[0];
        var depth = project.Config.Spatial.DepartmentBoundaryMaxDepth;
        var detached = plan.Maintenance.First(p => room.Cells.All(floor => Math.Max(Math.Abs(p.X-floor.X), Math.Abs(p.Y-floor.Y)) > depth));
        plan.Maintenance.Remove(detached);plan.Walls.Add(detached);room.Walls.Add(detached);
        Assert.That(MaintenancePlanner.Validate(project).Any(d => d.Code == "deep-maintenance-boundary"), Is.True);
    }

    [TestCase(43)]
    public void LaterDeepRoomsRetainEarlierDoorApproaches(int seed)
    {
        var project = StationSpatialPlanner.Generate(new StationProject { Config = new StationGenerationConfig { Seed = seed, Archetype = "ring" } });
        Assert.That(project.Result.Success, Is.True, string.Join("; ", project.Result.Diagnostics));
        var maintenance = project.SpatialPlan.Maintenance.ToHashSet();
        Assert.That(project.SpatialPlan.DeepMaintenanceRooms.All(room => maintenance.Contains(room.MaintenanceApproach) && room.PortCells.All(p => Neighbors(p).Any(maintenance.Contains))), Is.True);
    }

    [Test]
    public void SolarCannotOccupyDockingApproach()
    {
        var project = Project();project.SpatialPlan.SolarInstallations[0].Cells.Add(project.SpatialPlan.ReservedExterior[0]);
        Assert.That(MaintenancePlanner.Validate(project).Any(d => d.Code == "solar-footprint"), Is.True);
    }

    [Test]
    public void ExteriorAccessCannotLoseItsClosedBarrier()
    {
        var project = Project();project.SpatialPlan.Hull.Remove(project.SpatialPlan.MaintenanceSpaceAccess[0].BarrierCells[0]);
        Assert.That(MaintenancePlanner.Validate(project).Any(d => d.Code == "maintenance-space-access"), Is.True);
    }

    [Test]
    public void CountBoundsAreValidatedAndConfiguredCountsAreEnforced()
    {
        var project = Project();project.Config.Spatial.DeepMaintenanceMin = 5;project.Config.Spatial.DeepMaintenanceMax = 2;
        Assert.That(StationSpatialPlanner.Check(project).Any(d => d.Code == "feature-counts"), Is.True);
        project = Project();project.Config.Spatial.SolarMin = project.SpatialPlan.SolarInstallations.Count + 1;
        project.Config.Spatial.SolarMax = project.Config.Spatial.SolarMin;
        Assert.That(MaintenancePlanner.Validate(project).Any(d => d.Code == "solar-count"), Is.True);
    }
}
