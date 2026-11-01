using System;
using System.Collections.Generic;
using System.Linq;
using Content.StationGeneration;
using NUnit.Framework;

namespace Content.Tests.Shared.Procedural;

/// <summary>Passage width is checked on actual walkable floor, independently of service reservations.</summary>
[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class StationMaintenanceWidthTests
{
    private static readonly Lazy<StationProject> SpineTwo = new(() => StationSpatialPlanner.Generate(new StationProject
    {
        Config = new StationGenerationConfig { Archetype = "spine", Seed = 2 }
    }));

    [TestCase(2, "straight")]
    [TestCase(2, "bend")]
    [TestCase(2, "tee")]
    [TestCase(2, "cross")]
    [TestCase(3, "straight")]
    [TestCase(3, "bend")]
    [TestCase(3, "tee")]
    [TestCase(3, "cross")]
    public void NarrowPassageBendsAndJunctionsRemainLegal(int width, string shape)
    {
        var passage = Shape(width, shape);
        Assert.That(CoveredBySquares(passage, width).IsSupersetOf(passage), Is.True,
            "The fixture itself must provide its declared minimum clear width at every bend and junction.");
        Assert.That(ContainsSquare(passage, 4), Is.False);
        Assert.That(MaintenancePlanner.ValidatePassageWidth(passage, width, 3).Where(d => d.Severity == "error"), Is.Empty);
    }

    [Test]
    public void OpenFourByFourPocketIsRejectedWithoutChangingSavedFloor()
    {
        var passage = new Rect(10, 10, 4, 4).Cells().ToList();
        var saved = passage.ToList();
        var errors = MaintenancePlanner.ValidatePassageWidth(passage, 2, 3);
        Assert.That(errors.Any(d => d.Code == "maintenance-width-max"), Is.True);
        Assert.That(passage, Is.EqualTo(saved), "Read-only width validation must not silently convert a pocket into a room.");
    }

    [Test]
    public void MaximumClearWidthIsTakenFromTheDefinition()
    {
        var fourWide = new Rect(10, 10, 4, 4).Cells().ToHashSet();
        Assert.That(MaintenancePlanner.ValidatePassageWidth(fourWide, 2, 4).Where(d => d.Severity == "error"), Is.Empty);
        var fiveWide = new Rect(10, 10, 5, 5).Cells().ToHashSet();
        Assert.That(MaintenancePlanner.ValidatePassageWidth(fiveWide, 2, 4).Any(d => d.Code == "maintenance-width-max"), Is.True);
    }

    [Test]
    public void NarrowSliversAndInvertedWidthBoundsAreRejected()
    {
        Assert.That(MaintenancePlanner.ValidatePassageWidth(new Rect(10, 10, 1, 12).Cells(), 2, 3)
            .Any(d => d.Code == "maintenance-width"), Is.True);
        Assert.That(MaintenancePlanner.ValidatePassageWidth(Shape(2, "straight"), 3, 2)
            .Any(d => d.Severity == "error"), Is.True);
    }

    [TestCase("ring", 14014, 2, "departments")]
    [TestCase("hub", 0, 2, "departments")]
    [TestCase("spine", 17, 2, "departments")]
    [TestCase("spine", 2, 2, "departments")]
    [TestCase("ring", 14014, 3, "departments")]
    [TestCase("ring", 14014, 2, "rooms")]
    public void GeneratedPassagesHaveBoundedWidthWhileRoomsRemainSeparate(string archetype, int seed, int minimum, string preference)
    {
        var project = archetype == "spine" && seed == 2 && minimum == 2 && preference == "departments"
            ? ProjectJson.Clone(SpineTwo.Value)
            : StationSpatialPlanner.Generate(new StationProject
        {
            Config = new StationGenerationConfig { Archetype = archetype, Seed = seed, MaintenanceWidth = minimum,
                Spatial = new SpatialPlanningSettings { MaintenancePocketPreference = preference } }
        });
        Assert.That(project.Result.Success, Is.True, ProjectJson.Serialize(project.Result));
        var plan = project.SpatialPlan;
        var maintenance = plan.Maintenance.ToHashSet();
        Assert.That(project.Config.Spatial.MaintenancePassageMaxWidth, Is.EqualTo(3));
        Assert.That(ContainsSquare(maintenance, 4), Is.False,
            "Reserved service space must become narrow passages, enclosed rooms or department allocations rather than an open maintenance sea.");
        Assert.That(CoveredBySquares(maintenance, minimum).IsSupersetOf(maintenance), Is.True,
            "Narrowing must preserve usable clear width around bends, entrances and room doors.");
        Assert.That(plan.DeepMaintenanceRooms.Count,
            Is.InRange(project.Config.Spatial.DeepMaintenanceMin, project.Config.Spatial.DeepMaintenanceMax));
        Assert.That(plan.DeepMaintenanceRooms.All(room => !room.Cells.Concat(room.Walls).Concat(room.PortCells).Any(maintenance.Contains)), Is.True,
            "Enclosed deep room floor and partitions cannot remain classified as passage floor.");
        var assignedService = maintenance.Concat(plan.Walls).Concat(plan.Hull).Concat(plan.Ports.SelectMany(p => p.Cells))
            .Concat(plan.DeepMaintenanceRooms.SelectMany(room => room.Cells.Concat(room.PortCells)))
            .Concat(plan.CommonMaintenanceRooms.SelectMany(room => room.Cells.Concat(room.PublicDoorCells).Concat(room.MaintenanceDoorCells))).ToHashSet();
        Assert.That(plan.ServiceReservations.Except(assignedService), Is.Empty,
            "Surplus service reservations must be reclaimed or enclosed; unused open pockets cannot disappear from the passage mask while retaining anonymous service ownership.");
        var hull = plan.Hull.ToHashSet();
        var boundaryDepth = project.Config.Spatial.DepartmentBoundaryMaxDepth;
        Assert.That(boundaryDepth, Is.EqualTo(1));
        foreach (var region in plan.Regions)
        {
            // A territory claim must provide usable department floor. A thin,
            // arbitrarily long wall branch cannot count as department growth.
            // Chebyshev distance admits ordinary diagonal wall corners.
            var supported = region.Cells.SelectMany(cell => new Rect(cell.X - boundaryDepth, cell.Y - boundaryDepth,
                boundaryDepth * 2 + 1, boundaryDepth * 2 + 1).Cells()).ToHashSet();
            var wallOnlyClaims = region.Territory.Where(cell => !hull.Contains(cell) && !supported.Contains(cell)).ToList();
            Assert.That(wallOnlyClaims, Is.Empty,
                $"{region.Id}: department ownership includes tiles beyond the permitted boundary depth from actual usable floor. Wall-only tendrils must be replanned rather than used to hide maintenance pockets.");
        }
        Assert.That(StationSpatialPlanner.Validate(project).Success, Is.True,
            "The saved masks must pass independent validation without another generation pass.");
        if (preference == "rooms")
            Assert.That(plan.DeepMaintenanceRooms.Any(r => r.Cells.Count > project.Config.Spatial.DeepMaintenanceRoomMinArea), Is.True,
                "A room-first allocation must actually grow enclosed rooms when nearby surplus is available.");
        var upstream = ProjectJson.Serialize(plan.InitialServiceReservations);
        StationSpatialPlanner.RunStage(project, "growth");
        Assert.That(project.Result?.Diagnostics.Where(d => d.Severity == "error") ?? [], Is.Empty,
            "Explicit regrowth must restore the upstream whole-parcel reservations after fine-tile reclamation.");
        Assert.That(ProjectJson.Serialize(plan.InitialServiceReservations), Is.EqualTo(upstream));
        Assert.That(plan.DeepMaintenanceRooms, Is.Empty, "Regrowth explicitly replaces downstream maintenance features.");
        Assert.That(plan.CommonMaintenanceRooms, Is.Empty, "Regrowth explicitly replaces downstream hall-accessible service rooms.");
    }

    [Test]
    public void CommonRoomsProvideEnclosedFloorAndRealHallToMaintenanceAccess()
    {
        var project = ProjectJson.Clone(SpineTwo.Value);
        Assert.That(project.Result.Success, Is.True, ProjectJson.Serialize(project.Result));
        var plan = project.SpatialPlan;
        var maintenance = plan.Maintenance.ToHashSet();
        var halls = plan.PublicCorridors.ToHashSet();
        var partitions = plan.Walls.Concat(plan.Hull).ToHashSet();
        Assert.That(plan.CommonMaintenanceRooms, Is.Not.Empty,
            "This station has a hall-fronted service pocket that requires a real enclosed room.");
        Assert.That(plan.CommonMaintenanceRooms.Count,
            Is.InRange(project.Config.Spatial.CommonMaintenanceMin, project.Config.Spatial.CommonMaintenanceMax));
        foreach (var room in plan.CommonMaintenanceRooms)
        {
            var floor = room.Cells.ToHashSet();
            var publicDoors = room.PublicDoorCells.ToHashSet();
            var maintenanceDoors = room.MaintenanceDoorCells.ToHashSet();
            var openings = publicDoors.Concat(maintenanceDoors).ToHashSet();
            var footprint = floor.Concat(room.Walls).Concat(openings).ToHashSet();
            Assert.That(floor.Count, Is.InRange(project.Config.Spatial.CommonMaintenanceRoomMinArea, project.Config.Spatial.CommonMaintenanceRoomMaxArea));
            Assert.That(Connected(floor), Is.True);
            Assert.That(floor.All(cell => Neighbors(cell).All(next => floor.Contains(next) || partitions.Contains(next) || openings.Contains(next))), Is.True,
                $"{room.Id}: its clear floor needs a complete physical enclosure except the declared doors.");
            Assert.That(publicDoors.Count, Is.EqualTo(project.Config.CorridorWidth));
            Assert.That(maintenanceDoors.Count, Is.EqualTo(project.Config.MaintenanceWidth));
            Assert.That(publicDoors.Overlaps(maintenanceDoors), Is.False);
            Assert.That(publicDoors.All(cell => Neighbors(cell).Any(halls.Contains) && Neighbors(cell).Any(floor.Contains)), Is.True);
            Assert.That(maintenanceDoors.All(cell => Neighbors(cell).Any(maintenance.Contains) && Neighbors(cell).Any(floor.Contains)), Is.True);
            Assert.That(halls.Contains(room.PublicApproach), Is.True);
            Assert.That(maintenance.Contains(room.MaintenanceApproach), Is.True);
            Assert.That(footprint.Overlaps(maintenance) || footprint.Overlaps(halls), Is.False,
                "The room and doorways remain explicit physical layers, separate from both passage masks.");
            var walk = floor.Concat(openings).Append(room.PublicApproach).Append(room.MaintenanceApproach).ToHashSet();
            Assert.That(Reachable(room.PublicApproach, walk).Contains(room.MaintenanceApproach), Is.True,
                "A room declaration must provide an actual tile route between its hall and maintenance approaches.");
        }
        Assert.That(MaintenancePlanner.Validate(project).Where(d => d.Severity == "error"), Is.Empty);
    }

    [TestCase("approach")]
    [TestCase("door")]
    [TestCase("hall-floor")]
    public void CommonRoomAccessCannotBeSatisfiedByAnInvalidHallDeclaration(string corruption)
    {
        var project = ProjectJson.Clone(SpineTwo.Value);
        Assert.That(project.Result.Success, Is.True, ProjectJson.Serialize(project.Result));
        var room = project.SpatialPlan.CommonMaintenanceRooms.First();
        if (corruption == "approach") room.PublicApproach = room.MaintenanceApproach;
        if (corruption == "door") room.PublicDoorCells.Clear();
        if (corruption == "hall-floor") project.SpatialPlan.PublicCorridors.Remove(room.PublicApproach);
        Assert.That(MaintenancePlanner.Validate(project).Any(d => d.Code == "common-maintenance-access"), Is.True,
            "An ordinary maintenance room avoids deep-room classification only when its declared hall doorway actually reaches hall floor.");
    }

    private static IEnumerable<Cell> Neighbors(Cell cell) => Cell.Directions.Select(direction => cell + direction);

    private static bool Connected(HashSet<Cell> floor) => floor.Count > 0 && Reachable(floor.First(), floor).Count == floor.Count;

    private static HashSet<Cell> Reachable(Cell start, HashSet<Cell> floor)
    {
        var reached = new HashSet<Cell> { start };
        var queue = new Queue<Cell>(); queue.Enqueue(start);
        while (queue.TryDequeue(out var cell))
            foreach (var next in Neighbors(cell)) if (floor.Contains(next) && reached.Add(next)) queue.Enqueue(next);
        return reached;
    }

    private static HashSet<Cell> Shape(int width, string kind)
    {
        var result = new HashSet<Cell>();
        if (kind == "straight") result.UnionWith(new Rect(10, 10, 18, width).Cells());
        if (kind == "bend")
        {
            result.UnionWith(new Rect(10, 10, 12, width).Cells());
            result.UnionWith(new Rect(22 - width, 10, width, 12).Cells());
        }
        if (kind == "tee")
        {
            result.UnionWith(new Rect(10, 10, 18, width).Cells());
            result.UnionWith(new Rect(18, 10, width, 12).Cells());
        }
        if (kind == "cross")
        {
            result.UnionWith(new Rect(10, 17, 18, width).Cells());
            result.UnionWith(new Rect(18, 10, width, 18).Cells());
        }
        return result;
    }

    private static bool ContainsSquare(HashSet<Cell> cells, int side) =>
        cells.Any(p => new Rect(p.X, p.Y, side, side).Cells().All(cells.Contains));

    private static HashSet<Cell> CoveredBySquares(HashSet<Cell> cells, int side)
    {
        var covered = new HashSet<Cell>();
        foreach (var cell in cells)
        {
            var square = new Rect(cell.X, cell.Y, side, side).Cells().ToArray();
            if (square.All(cells.Contains)) covered.UnionWith(square);
        }
        return covered;
    }
}
