using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class RoomAreaAllocationTests
{
    private static StationDesignProject TightComplex()
    {
        var definition = ProgramTestData.Minimal(); var parent = ProgramTestData.Room("Suite", 16, 40, 120);
        parent.Rooms = [ProgramTestData.Room("Booth", 4, 40, 60)]; parent.ComplexArea = ProgramTestData.Area(20, 30, 35);
        definition.Departments[0].Rooms = [parent]; definition.Departments[0].Area = ProgramTestData.Area(20, 30, 1000);
        return InstanceSelection.Resolve(new() { Definition = definition });
    }

    [Test]
    public void FeasibleTightComplexIsAllocatedBeforeAnyGeometryExistsEvenWithNoSearchBudget()
    {
        var project = TightComplex(); project.Settings.GrowthIterations = 0;
        var diagnostics = RoomAreaAllocation.Allocate(project);
        Assert.That(diagnostics, Is.Empty, ProgramTestData.Errors(diagnostics));
        Assert.That(project.Layout.Rooms.Sum(room => room.PlannedArea), Is.InRange(20, 35));
        Assert.That(project.Layout.Rooms.Single(room => room.ParentInstanceId.Length == 0).PlannedArea, Is.GreaterThanOrEqualTo(16));
        Assert.That(project.Layout.Rooms.Single(room => room.ParentInstanceId.Length > 0).PlannedArea, Is.GreaterThanOrEqualTo(4));
        Assert.That(project.Layout.Rooms.All(room => room.Cells.Count == 0), Is.True);
    }

    [Test]
    public void EveryRepeatedAncestorConstrainsItsOwnDescendantsWithoutCountingSiblingComplexes()
    {
        var definition = ProgramTestData.Minimal(); var parent = ProgramTestData.Room("Suite", 4, 20, 30);
        var child = ProgramTestData.Room("Booth", 4, 12, 20); child.Rooms = [ProgramTestData.Room("Closet", 4, 10, 20)];
        child.ComplexArea = ProgramTestData.Area(8, 15, 20); parent.Rooms = [child]; parent.ComplexArea = ProgramTestData.Area(12, 20, 25);
        parent.Count = new() { Min = 1, Target = 2, Max = 2 };
        definition.Departments[0].Rooms = [parent]; definition.Departments[0].Area = ProgramTestData.Area(24, 40, 80);
        var project = InstanceSelection.Resolve(new() { Definition = definition });
        var diagnostics = RoomAreaAllocation.Allocate(project);
        Assert.That(diagnostics, Is.Empty, ProgramTestData.Errors(diagnostics));
        foreach (var room in project.Layout.Rooms.Where(room => room.DefinitionPath == "Alpha/Suite"))
            Assert.That(RoomHierarchy.Descendants(project, room, true).Sum(childRoom => childRoom.PlannedArea), Is.InRange(12, 25));
        foreach (var room in project.Layout.Rooms.Where(room => room.DefinitionPath == "Alpha/Suite/Booth"))
            Assert.That(RoomHierarchy.Descendants(project, room, true).Sum(childRoom => childRoom.PlannedArea), Is.InRange(8, 20));
    }

    [Test]
    public void AncestorPressureWeightChangesThePreferredTotalWithinHardBounds()
    {
        var weak = TightComplex(); weak.Definition.Departments[0].Area.Target = 80;
        var complex = weak.Definition.Departments[0].Rooms[0].ComplexArea!; complex.Max = 100; complex.Weight = 0;
        var strong = ProgramJson.Clone(weak); strong.Definition.Departments[0].Rooms[0].ComplexArea!.Weight = 100;
        Assert.That(RoomAreaAllocation.Allocate(weak), Is.Empty); Assert.That(RoomAreaAllocation.Allocate(strong), Is.Empty);
        Assert.That(strong.Layout.Rooms.Sum(room => room.PlannedArea ?? 0), Is.LessThan(weak.Layout.Rooms.Sum(room => room.PlannedArea ?? 0) - 20));
    }

    [Test]
    public void ProtectedFloorCannotBeShrunkToPretendAnImpossibleComplexFits()
    {
        var project = TightComplex(); var parent = project.Layout.Rooms.Single(room => room.ParentInstanceId.Length == 0);
        parent.Cells = new GridBounds(10, 10, 8, 5).Cells().ToList(); parent.FloorLocked = true;
        var before = parent.Cells.ToList(); var diagnostics = RoomAreaAllocation.Allocate(project);
        Assert.That(diagnostics.Any(item => item.Code == "allocation-complex-capacity"), Is.True);
        Assert.That(parent.Cells, Is.EqualTo(before)); Assert.That(parent.PlannedArea, Is.EqualTo(40));
        Assert.That(project.Definition.Departments[0].Rooms[0].ComplexArea!.Max, Is.EqualTo(35));
    }

    [Test]
    public void GrowthAllocationRespectsMandatoryShellAndRealChildCapacityAndIgnoresSavedGoals()
    {
        var project = TightComplex(); var parent = project.Layout.Rooms.Single(room => room.ParentInstanceId.Length == 0);
        var child = project.Layout.Rooms.Single(room => room.ParentInstanceId.Length > 0);
        parent.GrowthSide = "complex"; parent.GrowthReservation = new GridBounds(10, 10, 6, 3).Cells().ToList(); parent.PlannedArea = 999;
        child.GrowthReservation = new GridBounds(20, 20, 2, 2).Cells().ToList(); child.PlannedArea = 999;
        var diagnostics = RoomAreaAllocation.Allocate(project, true);
        Assert.That(diagnostics, Is.Empty, ProgramTestData.Errors(diagnostics));
        Assert.That(parent.PlannedArea, Is.EqualTo(18)); Assert.That(child.PlannedArea, Is.EqualTo(4));
        Assert.That(parent.GrowthReservation.Count, Is.EqualTo(18)); Assert.That(child.GrowthReservation.Count, Is.EqualTo(4));
    }

    [TestCase(100, false)]
    [TestCase(10, true)]
    public void PlannedFrontageAndItsConnectedStandingFloorContributeToTheGeometricMinimum(int maximum, bool impossible)
    {
        var definition = ProgramTestData.Minimal(); definition.Departments[0].Area = ProgramTestData.Area(4, 4, 100);
        definition.Departments[0].Rooms[0].Area = ProgramTestData.Area(4, 4, maximum);
        var project = InstanceSelection.Resolve(new() { Definition = definition }); var room = project.Layout.Rooms.Single();
        room.GrowthReservation = new GridBounds(0, 0, 10, 4).Cells().ToList(); room.Seed = new(0, 0); room.HasSeed = true; room.FrontageFloor = [new(9, 3)];
        var diagnostics = RoomAreaAllocation.Allocate(project, true);
        if (impossible) Assert.That(diagnostics.Any(issue => issue.Code == "allocation-capacity"), Is.True);
        else { Assert.That(diagnostics, Is.Empty, ProgramTestData.Errors(diagnostics)); Assert.That(room.PlannedArea, Is.GreaterThanOrEqualTo(16)); }
        Assert.That(definition.Departments[0].Rooms[0].Area.Max, Is.EqualTo(maximum));
        Assert.That(room.Cells, Is.Empty, "Allocating anchor capacity must not invent grown floor.");
    }
}
