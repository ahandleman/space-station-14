using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
public sealed class SeededLayoutVariationTests
{
    private static StationDesignProject Fixture(int seed, double variation)
    {
        var project = new StationDesignProject { Settings = new() { Seed = seed, OptimizationIterations = 0 } };
        project.Settings.Packing.InternalVariation = variation;
        project.Settings.Packing.SilhouetteVariation = variation;
        project.Settings.Packing.Interlock = 0;
        project.Definition.Departments = Enumerable.Range(0, 3).Select(owner =>
        {
            var activities = Enumerable.Range(0, 7).Select(index => ProgramTestData.Room("Activity" + index, 9, 18 + index * 2, 80)).ToList();
            var circulation = ProgramTestData.Room("Circulation", 16, 45, 200);
            circulation.Role = "filler"; circulation.Count = new() { Min = 2, Target = 2, Max = 2 };
            circulation.Shape.MaxAspectRatio = 16;
            circulation.Access.AllowPublicEntry = true; circulation.Access.AllowMaintenanceEntry = true;
            activities.Add(circulation);
            return new DepartmentProgram { Id = "Owner" + owner, Area = ProgramTestData.Area(100, 258, 600), Rooms = activities };
        }).ToList();
        return project;
    }

    [Test]
    public void SeedsChangeInternalRoomAdjacencyAndSilhouetteBeyondRigidTransforms()
    {
        var layouts = new[] { 14014, 14015, 14016 }.Select(seed => DesignPlacement.Run(InstanceSelection.Resolve(Fixture(seed, 1)))).ToList();
        var owners = layouts[0].Definition.Departments.Select(owner => owner.Id).ToList();
        Assert.That(layouts.Select(layout => layout.Layout.Rooms.Count), Is.All.EqualTo(27), "Variation retains the exact same selected room program.");
        Assert.That(owners.Count(owner => layouts.Select(layout => Canonical(layout, owner, labels: false)).Distinct().Count() > 1), Is.GreaterThanOrEqualTo(2));
        Assert.That(owners.Count(owner => layouts.Select(layout => Canonical(layout, owner, labels: true)).Distinct().Count() > 1), Is.GreaterThanOrEqualTo(2));
        Assert.That(owners.Count(owner => layouts.Select(layout => Adjacency(layout, owner)).Distinct().Count() > 1), Is.GreaterThanOrEqualTo(2),
            "Room neighbors must change; translating, reflecting or rotating an unchanged arrangement is insufficient.");
        var replay = DesignPlacement.Run(InstanceSelection.Resolve(Fixture(14014, 1)));
        Assert.That(replay.Layout.Rooms.Select(room => (room.Id, string.Join(';', room.GrowthReservation))),
            Is.EqualTo(layouts[0].Layout.Rooms.Select(room => (room.Id, string.Join(';', room.GrowthReservation)))));
    }

    [Test]
    public void ZeroVariationControlsKeepInternalOrganizationStableAcrossSeeds()
    {
        var first = DesignPlacement.Run(InstanceSelection.Resolve(Fixture(14014, 0)));
        var second = DesignPlacement.Run(InstanceSelection.Resolve(Fixture(14015, 0)));
        foreach (var owner in first.Definition.Departments)
        {
            Assert.That(Canonical(second, owner.Id, labels: true), Is.EqualTo(Canonical(first, owner.Id, labels: true)), owner.Id);
            Assert.That(Adjacency(second, owner.Id), Is.EqualTo(Adjacency(first, owner.Id)), owner.Id);
        }
    }

    private static string Canonical(StationDesignProject project, string ownerId, bool labels)
    {
        var points = project.Layout.Rooms.Where(room => room.OwnerId == ownerId).SelectMany(room => room.GrowthReservation.Select(cell =>
            (Cell: cell, Label: labels ? room.Id : ""))).ToList();
        var variants = new List<string>();
        foreach (var reflection in new[] { -1, 1 })
        for (var turns = 0; turns < 4; turns++)
        {
            var transformed = points.Select(point =>
            {
                var cell = new Cell(point.Cell.X * reflection, point.Cell.Y);
                for (var turn = 0; turn < turns; turn++) cell = new(-cell.Y, cell.X);
                return (Cell: cell, point.Label);
            }).ToList();
            var x = transformed.Min(point => point.Cell.X); var y = transformed.Min(point => point.Cell.Y);
            variants.Add(string.Join(';', transformed.OrderBy(point => point.Cell.X).ThenBy(point => point.Cell.Y).ThenBy(point => point.Label, StringComparer.Ordinal)
                .Select(point => $"{point.Cell.X - x},{point.Cell.Y - y}:{point.Label}")));
        }
        return variants.Min(StringComparer.Ordinal)!;
    }

    private static string Adjacency(StationDesignProject project, string ownerId)
    {
        var floor = project.Layout.Rooms.Where(room => room.OwnerId == ownerId).SelectMany(room => room.GrowthReservation.Select(cell => (cell, room.Id))).ToDictionary(item => item.cell, item => item.Id);
        var edges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (cell, room) in floor)
        foreach (var normal in Cell.Directions)
            if (!floor.ContainsKey(cell + normal) && floor.TryGetValue(cell + normal + normal, out var other) && other != room)
                edges.Add(string.CompareOrdinal(room, other) < 0 ? room + ":" + other : other + ":" + room);
        return string.Join(';', edges.Order(StringComparer.Ordinal));
    }
}
