using System.Text.Json;
using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class PressureTests
{
    private static RoomMeasurement Measured(string path, string id, int area, int width, int height) =>
        new() { Path = path, InstanceId = id, Area = area, Width = width, Height = height, Components = 1, Environment = new() };

    private static PressureState MinimalState(int area = 40, int width = 8, int height = 5) => new()
    {
        Rooms = [Measured("Alpha/Work", "work-1", area, width, height)],
        Departments = [new() { Path = "Alpha", Width = width, Height = height, Components = 1 }]
    };

    [Test]
    public void IncompleteMinimumFloorIsAllowedDuringGrowthButFailsFinalValidation()
    {
        var source = ProgramTestData.Minimal();
        var state = MinimalState(4, 2, 2);
        var growth = PressureEvaluator.Evaluate(source, state);
        var final = PressureEvaluator.Evaluate(source, state, final: true);
        Assert.That(growth.Success, Is.True, ProgramTestData.Errors(growth.Diagnostics));
        Assert.That(growth.Cost, Is.GreaterThan(0));
        Assert.That(final.Success, Is.False);
    }

    [Test]
    public void MaximumAreaCannotBeViolatedEvenDuringGrowth()
    {
        var report = PressureEvaluator.Evaluate(ProgramTestData.Minimal(), MinimalState(121, 11, 11));
        Assert.That(report.Success, Is.False);
    }

    [Test]
    public void RequiredInstanceCountsAreDeferredUntilFinalValidation()
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Rooms[0].Count = new() { Min = 2, Target = 2, Max = 2 };
        var state = MinimalState();
        Assert.That(PressureEvaluator.Evaluate(source, state).Success, Is.True);
        Assert.That(PressureEvaluator.Evaluate(source, state, final: true).Success, Is.False);
    }

    [Test]
    public void ExcessiveAspectRatioAndSparseBoundingBoxesFailFinalChecks()
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Shape.MinFillRatio = .1;
        source.Departments[0].Shape.MaxAspectRatio = 100;
        source.Departments[0].Rooms[0].Shape.MaxAspectRatio = 2;
        var oblong = PressureEvaluator.Evaluate(source, MinimalState(40, 20, 2), final: true);
        Assert.That(oblong.Success, Is.False);
        source.Departments[0].Rooms[0].Shape.MaxAspectRatio = 100;
        var sparse = PressureEvaluator.Evaluate(source, MinimalState(40, 10, 10), final: true);
        Assert.That(sparse.Success, Is.True, ProgramTestData.Errors(sparse.Diagnostics));
        source.Departments[0].Rooms[0].Shape.MinFillRatio = .5;
        Assert.That(PressureEvaluator.Evaluate(source, MinimalState(40, 10, 10), final: true).Success, Is.False);
    }

    [Test]
    public void DepartmentSplitsHaveHighConfiguredCostAndHonorTheHardLimit()
    {
        var source = ProgramTestData.Minimal();
        var baseState = MinimalState();
        var splitState = ProgramJson.Clone(baseState);
        source.Departments[0].Split = new()
        {
            Allow = true, MaxComponents = 2, ComponentPenalty = 1234,
            HallCrossingPenalty = 2345, MaintenanceCrossingPenalty = 3456
        };
        splitState.Departments[0].Components = 2;
        splitState.Departments[0].HallCrossings = 1;
        splitState.Departments[0].MaintenanceCrossings = 1;
        var baseline = PressureEvaluator.Evaluate(source, baseState, final: true);
        var split = PressureEvaluator.Evaluate(source, splitState, final: true);
        Assert.That(split.Success, Is.True, ProgramTestData.Errors(split.Diagnostics));
        Assert.That(split.Cost - baseline.Cost, Is.EqualTo(1234 + 2345 + 3456).Within(1e-8));
        source.Departments[0].Split.Allow = false;
        Assert.That(PressureEvaluator.Evaluate(source, splitState, final: true).Success, Is.False);
    }

    [Test]
    public void SameDepartmentTransferLeavesAncestorAreaPressureUnchanged()
    {
        var source = ProgramTestData.Minimal();
        source.Settings.RoomPressureWeight = 0;
        source.Departments[0].Rooms[0].Area = ProgramTestData.Area(10, 20, 60);
        source.Departments[0].Rooms.Add(ProgramTestData.Room("Support", 10, 20, 60));
        var before = new PressureState
        {
            Rooms = [Measured("Alpha/Work", "work-1", 20, 5, 5), Measured("Alpha/Support", "support-1", 30, 6, 5)],
            Departments = [new() { Path = "Alpha", Width = 10, Height = 5, Components = 1 }]
        };
        var after = ProgramJson.Clone(before);
        after.Rooms[0].Area++;
        after.Rooms[1].Area--;
        var transfer = PressureEvaluator.EvaluateTransfer(source, before, after);
        Assert.That(transfer.Acceptable, Is.True, ProgramTestData.Errors(transfer.After.Diagnostics));
        Assert.That(transfer.Delta, Is.Zero.Within(1e-8));
    }

    [Test]
    public void CrossDepartmentTransferBalancesBothParentBudgets()
    {
        var source = ProgramTestData.Minimal();
        source.Settings.RoomPressureWeight = 0;
        source.Departments[0].Area = ProgramTestData.Area(10, 40, 120);
        source.Departments[0].Rooms[0].Area = ProgramTestData.Area(10, 40, 120);
        var second = ProgramJson.Clone(source.Departments[0]);
        second.Id = "Beta";
        source.Departments.Add(second);
        var before = new PressureState
        {
            Rooms = [Measured("Alpha/Work", "alpha-1", 60, 10, 6), Measured("Beta/Work", "beta-1", 20, 10, 6)],
            Departments =
            [
                new() { Path = "Alpha", Width = 10, Height = 6, Components = 1 },
                new() { Path = "Beta", Width = 10, Height = 6, Components = 1 }
            ]
        };
        source.Departments.ForEach(d => d.Shape.MinFillRatio = .1);
        var after = ProgramJson.Clone(before);
        after.Rooms[0].Area--;
        after.Rooms[1].Area++;
        var report = PressureEvaluator.EvaluateTransfer(source, before, after);
        Assert.That(report.Acceptable, Is.True, ProgramTestData.Errors(report.After.Diagnostics));
        Assert.That(report.Delta, Is.LessThan(0), "Moving floor from oversized Alpha to undersized Beta should reduce ancestor pressure.");
    }

    [Test]
    public void ConfiguredWeightsBalanceOpposingLocalAndAncestorPressures()
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Area = ProgramTestData.Area(10, 40, 120);
        source.Departments[0].Rooms[0].Area = ProgramTestData.Area(10, 80, 120);
        var beta = ProgramJson.Clone(source.Departments[0]);
        beta.Id = "Beta";
        beta.Rooms[0].Area.Target = 10;
        source.Departments.Add(beta);
        foreach (var department in source.Departments)
        {
            department.Shape.Weight = 0;
            department.Rooms[0].Shape.Weight = 0;
        }
        var before = new PressureState
        {
            Rooms = [Measured("Alpha/Work", "alpha-1", 60, 10, 6), Measured("Beta/Work", "beta-1", 20, 10, 6)],
            Departments =
            [
                new() { Path = "Alpha", Width = 10, Height = 6 },
                new() { Path = "Beta", Width = 10, Height = 6 }
            ]
        };
        var after = ProgramJson.Clone(before);
        after.Rooms[0].Area--;
        after.Rooms[1].Area++;
        source.Settings.RoomPressureWeight = 100;
        source.Settings.DepartmentPressureWeight = 1;
        var localDominates = PressureEvaluator.EvaluateTransfer(source, before, after);
        Assert.That(localDominates.Acceptable, Is.True, ProgramTestData.Errors(localDominates.After.Diagnostics));
        Assert.That(localDominates.Delta, Is.GreaterThan(0), "Local room targets oppose this otherwise useful parent-budget transfer.");
        source.Settings.RoomPressureWeight = 1;
        source.Settings.DepartmentPressureWeight = 100;
        var ancestorDominates = PressureEvaluator.EvaluateTransfer(source, before, after);
        Assert.That(ancestorDominates.Acceptable, Is.True, ProgramTestData.Errors(ancestorDominates.After.Diagnostics));
        Assert.That(ancestorDominates.Delta, Is.LessThan(0), "Larger parent pressure favors balancing the departments.");
    }

    [TestCase("ifPresent", true)]
    [TestCase("requireTarget", false)]
    public void OptionalTargetPresenceHasExplicitFinalSemantics(string presence, bool success)
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Rooms.Add(ProgramTestData.Optional("Lounge"));
        source.Departments[0].Rooms[0].Connections.Add(new()
        {
            Id = "lounge", To = ["Lounge"], Presence = presence, Priority = "required"
        });
        var report = PressureEvaluator.Evaluate(source, MinimalState(), final: true);
        Assert.That(report.Success, Is.EqualTo(success), ProgramTestData.Errors(report.Diagnostics));
    }

    [TestCase("any", true)]
    [TestCase("all", false)]
    public void OneRouteSatisfiesAnyChoiceButNotEveryTarget(string selection, bool success)
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Rooms.AddRange([ProgramTestData.Optional("A"), ProgramTestData.Optional("B")]);
        source.Departments[0].Rooms[0].Connections.Add(new()
        {
            Id = "support", To = ["A", "B"], Selection = selection,
            Presence = "requireTarget", Priority = "required", Modes = ["department"]
        });
        var state = MinimalState();
        state.Rooms.AddRange([Measured("Alpha/A", "a-1", 16, 4, 4), Measured("Alpha/B", "b-1", 16, 4, 4)]);
        state.Departments[0].Width = 9;
        state.Departments[0].Height = 8;
        state.Connections.Add(new()
        {
            Id = "Alpha/Work:support", SourceInstance = "Alpha/Work#work-1",
            TargetInstance = "Alpha/A#a-1", Mode = "department", Distance = 3
        });
        var report = PressureEvaluator.Evaluate(source, state, final: true);
        Assert.That(report.Success, Is.EqualTo(success), ProgramTestData.Errors(report.Diagnostics));
    }

    [Test]
    public void RequiredConnectionEvidenceIsPerSourceInstance()
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Rooms[0].Count = new() { Min = 2, Target = 2, Max = 2 };
        source.Departments[0].Rooms[0].Connections.Add(new()
        {
            Id = "support", To = ["Support"], Presence = "requireTarget", Modes = ["maintenance", "department"]
        });
        var support = ProgramTestData.Room("Support", 9, 16, 25);
        support.Access.AllowMaintenanceEntry = false;
        source.Departments[0].Rooms.Add(support);
        var state = new PressureState
        {
            Rooms =
            [
                Measured("Alpha/Work", "work-1", 40, 8, 5),
                Measured("Alpha/Work", "work-2", 40, 8, 5),
                Measured("Alpha/Support", "support-1", 16, 4, 4)
            ],
            Departments = [new() { Path = "Alpha", Width = 12, Height = 8 }],
            Connections =
            [
                new()
                {
                    Id = "Alpha/Work:support", SourceInstance = "Alpha/Work#work-1",
                    TargetInstance = "Alpha/Support#support-1", Mode = "department", Distance = 3
                }
            ]
        };
        Assert.That(PressureEvaluator.Evaluate(source, state, final: true).Success, Is.False,
            "The second source instance must independently reach support.");
        var second = ProgramJson.Clone(state.Connections[0]);
        second.SourceInstance = "Alpha/Work#work-2";
        state.Connections.Add(second);
        var full = PressureEvaluator.Evaluate(source, state, final: true);
        Assert.That(full.Success, Is.True, ProgramTestData.Errors(full.Diagnostics));
    }

    [TestCase("publicHall")]
    [TestCase("maintenance")]
    public void NetworkRoutesMayReachPrivateEndpointsIndirectlyButCannotAddForbiddenDirectDoors(string mode)
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Rooms[0].Access.AllowPublicEntry = false;
        source.Departments[0].Rooms[0].Access.AllowMaintenanceEntry = false;
        var beta = ProgramJson.Clone(source.Departments[0]);
        beta.Id = "Beta";
        source.Departments.Add(beta);
        var state = new PressureState();
        foreach (var department in source.Departments)
        {
            var circulation = ProgramTestData.Room("Circulation", 9, 16, 25);
            circulation.Role = "filler";
            circulation.AllowThroughTraffic = true;
            circulation.Access.AllowPublicEntry = true;
            circulation.Access.AllowMaintenanceEntry = true;
            department.Rooms.Add(circulation);
            state.Rooms.Add(Measured(department.Id + "/Work", "work-1", 40, 8, 5));
            var gateway = Measured(department.Id + "/Circulation", "circulation-1", 16, 4, 4);
            if (mode == "publicHall") gateway.PublicEntries = 1;
            else gateway.MaintenanceEntries = 1;
            state.Rooms.Add(gateway);
            state.Departments.Add(new() { Path = department.Id, Width = 8, Height = 7 });
        }
        source.Connections.Add(new()
        {
            Id = "private-route", From = "Alpha/Work", To = ["Beta/Work"],
            Modes = [mode], Priority = "required", Presence = "requireTarget"
        });
        var validation = ProgramValidator.Validate(source);
        Assert.That(validation.All(d => d.Severity != "error"), Is.True, ProgramTestData.Errors(validation));
        state.Connections.Add(new()
        {
            Id = "$station:private-route", SourceInstance = "Alpha/Work#work-1",
            TargetInstance = "Beta/Work#work-1", Mode = mode, Distance = 8
        });
        var legalRoute = PressureEvaluator.Evaluate(source, state, final: true);
        Assert.That(legalRoute.Success, Is.True, ProgramTestData.Errors(legalRoute.Diagnostics));
        if (mode == "publicHall") state.Rooms[0].PublicEntries = 1;
        else state.Rooms[0].MaintenanceEntries = 1;
        var directDoor = PressureEvaluator.Evaluate(source, state, final: true);
        Assert.That(directDoor.Success, Is.False);
        Assert.That(directDoor.Diagnostics.Any(d => d.Code == (mode == "publicHall" ? "public-entry" : "maintenance-entry")), Is.True);
    }

    [Test]
    public void FinalRequiredRouteHonorsMaximumDistanceAndAllowedMode()
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Rooms.Add(ProgramTestData.Room("Support", 9, 16, 25));
        source.Departments[0].Rooms[0].Connections.Add(new()
        {
            Id = "support", To = ["Support"], Presence = "requireTarget", Modes = ["direct"], MaxDistance = 5
        });
        var state = MinimalState();
        state.Rooms.Add(Measured("Alpha/Support", "support-1", 16, 4, 4));
        state.Departments[0].Width = 8;
        state.Departments[0].Height = 7;
        state.Connections.Add(new()
        {
            Id = "Alpha/Work:support", SourceInstance = "Alpha/Work#work-1",
            TargetInstance = "Alpha/Support#support-1", Mode = "direct", Distance = 6
        });
        Assert.That(PressureEvaluator.Evaluate(source, state, final: true).Success, Is.False);
        state.Connections[0].Distance = 5;
        var report = PressureEvaluator.Evaluate(source, state, final: true);
        Assert.That(report.Success, Is.True, ProgramTestData.Errors(report.Diagnostics));
        state.Connections[0].Mode = "publicHall";
        Assert.That(PressureEvaluator.Evaluate(source, state, final: true).Success, Is.False);
    }

    [Test]
    public void PressureEvaluationAndTransferDoNotMutateDefinitionsOrMeasurements()
    {
        var source = ProgramTestData.Minimal();
        var before = MinimalState();
        var after = MinimalState(39, 8, 5);
        var definitionJson = JsonSerializer.Serialize(source, ProgramJson.Options);
        var beforeJson = JsonSerializer.Serialize(before, ProgramJson.Options);
        var afterJson = JsonSerializer.Serialize(after, ProgramJson.Options);
        PressureEvaluator.EvaluateTransfer(source, before, after);
        Assert.That(JsonSerializer.Serialize(source, ProgramJson.Options), Is.EqualTo(definitionJson));
        Assert.That(JsonSerializer.Serialize(before, ProgramJson.Options), Is.EqualTo(beforeJson));
        Assert.That(JsonSerializer.Serialize(after, ProgramJson.Options), Is.EqualTo(afterJson));
    }
}
