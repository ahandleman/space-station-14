using Content.StationArchitect;
using NUnit.Framework;
using System.Text.Json;

namespace StationArchitect.Tests;

[TestFixture]
public sealed class DesignCirculationPlanningTests
{
    private static List<Cell> Floor(int x, int y, int width, int height) => new GridBounds(x, y, width, height).Cells().ToList();

    private static StationDesignProject Pair()
    {
        var project = new StationDesignProject { Settings = new() { Width = 64, Height = 40, Topology = "spine" } };
        project.Layout.PublicWaypoints = [new(5, 5), new(56, 5)];
        foreach (var (id, x) in new[] { ("Alpha/Work#1", 12), ("Beta/Work#1", 38) })
        {
            var floor = Floor(x, 14, 6, 6);
            project.Layout.Rooms.Add(new() { Id = id, Cells = [.. floor], GrowthReservation = [.. floor] });
            project.Layout.CirculationReservations.Add(new()
            {
                Id = id + ":public", RoomId = id, Network = "publicHall", Width = 3, Outward = new(0, -1),
                BoundaryCells = Floor(x + 1, 13, 3, 1), ApproachCells = Floor(x + 1, 10, 3, 3)
            });
            project.Layout.CirculationReservations.Add(new()
            {
                Id = id + ":service", RoomId = id, Network = "maintenance", Width = 2, Outward = new(0, 1),
                BoundaryCells = Floor(x + 1, 20, 2, 1), ApproachCells = Floor(x + 1, 21, 2, 2)
            });
        }
        return project;
    }

    [Test]
    public void BothPlanningNetworksPublishConnectedDisjointPathsWithoutInventingFloor()
    {
        var project = Pair();
        var result = DesignCirculationPlanning.Connect(project, project.Layout.CirculationReservations.Select(guide => guide.Id).ToList());
        Assert.That(result.Complete, Is.True, string.Join("\n", result.Diagnostics.Select(issue => issue.Message)));
        foreach (var network in new[] { "publicHall", "maintenance" })
        {
            var guides = project.Layout.CirculationReservations.Where(guide => guide.Network == network).ToList();
            var floor = guides.SelectMany(guide => guide.ApproachCells).ToHashSet();
            Assert.That(LayoutGeometry.Components(floor), Is.EqualTo(1), network);
            Assert.That(guides.Where(guide => guide.RoomId.Length > 0).All(guide => floor.Contains(DesignCirculationPlanning.Anchor(guide))), Is.True);
        }
        var publicFloor = project.Layout.CirculationReservations.Where(guide => guide.Network == "publicHall").SelectMany(guide => guide.ApproachCells).ToHashSet();
        var serviceFloor = project.Layout.CirculationReservations.Where(guide => guide.Network == "maintenance").SelectMany(guide => guide.ApproachCells).ToHashSet();
        Assert.That(DesignNetworkGeometry.Halo(publicFloor).Overlaps(serviceFloor), Is.False);
        Assert.That(project.Layout.PublicCorridors, Is.Empty);
        Assert.That(project.Layout.Maintenance, Is.Empty);
    }

    [Test]
    public void MissingExpectedGuideDoesNotPublishAPartialPlan()
    {
        var project = Pair(); var before = JsonSerializer.Serialize(project.Layout, ProgramJson.Options);
        var result = DesignCirculationPlanning.Connect(project, ["missing-entry"]);
        Assert.That(result.Complete, Is.False);
        Assert.That(result.Diagnostics.Any(issue => issue.Code == "planning-entry-missing"), Is.True);
        Assert.That(JsonSerializer.Serialize(project.Layout, ProgramJson.Options), Is.EqualTo(before));
    }

    [Test]
    public void ServiceApproachCannotPublishAnIllegalCellEvenWhenItsAnchorIsClear()
    {
        var project = Pair();
        project.Layout.CirculationReservations.First(guide => guide.Network == "maintenance").ApproachCells.Add(project.Layout.Rooms[0].Cells[0]);
        var before = JsonSerializer.Serialize(project.Layout, ProgramJson.Options);
        var result = DesignCirculationPlanning.Connect(project);
        Assert.That(result.Complete, Is.False);
        Assert.That(result.Diagnostics.Any(issue => issue.Code == "planning-entry-geometry"), Is.True);
        Assert.That(JsonSerializer.Serialize(project.Layout, ProgramJson.Options), Is.EqualTo(before));
    }

    [Test]
    public void AutomaticEntryCanUseAnotherLegalFaceWhenTheOriginalCannotReachTheBackbone()
    {
        var project = new StationDesignProject { Settings = new() { Width = 64, Height = 40, Topology = "spine" } };
        project.Layout.Rooms.Add(new() { Id = "Alpha/Barrier#1", GrowthReservation = Floor(28, 0, 4, 40) });
        project.Layout.PublicWaypoints = [new(5, 5), new(20, 5)];
        var original = new DesignCirculationReservation
        {
            Id = "auto:public", RoomId = "Alpha/Barrier#1", Network = "publicHall", Width = 3, Outward = new(1, 0),
            BoundaryCells = Floor(32, 14, 1, 3), ApproachCells = Floor(33, 14, 3, 3)
        };
        project.Layout.CirculationReservations.Add(original);
        var alternative = new DesignCirculationReservation
        {
            Id = original.Id, RoomId = original.RoomId, Network = original.Network, Width = 3, Outward = new(-1, 0),
            BoundaryCells = Floor(27, 14, 1, 3), ApproachCells = Floor(24, 14, 3, 3)
        };
        var result = DesignCirculationPlanning.Connect(project, [original.Id], _ => [alternative]);
        Assert.That(result.Complete, Is.True, string.Join("\n", result.Diagnostics.Select(issue => issue.Message)));
        Assert.That(project.Layout.CirculationReservations.Single(guide => guide.Id == original.Id).Outward, Is.EqualTo(new Cell(-1, 0)));
        Assert.That(project.Layout.PublicCorridors, Is.Empty);
    }
}
