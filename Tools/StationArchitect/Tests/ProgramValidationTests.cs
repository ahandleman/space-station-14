using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class ProgramValidationTests
{
    [Test]
    public void RequiredOptionalAndFillerRoomsAreAllExplicitOwnedFloor()
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Rooms.Add(ProgramTestData.Optional("OptionalRoom"));
        var filler = ProgramTestData.Optional("Circulation", 6);
        filler.Role = "filler";
        filler.AllowThroughTraffic = true;
        filler.Count.Target = 2;
        source.Departments[0].Rooms.Add(filler);
        var diagnostics = ProgramValidator.Validate(source);
        Assert.That(diagnostics.Where(d => d.Severity == "error"), Is.Empty, ProgramTestData.Errors(diagnostics));
        Assert.That(source.Departments[0].Rooms[0].Count.Min, Is.EqualTo(1));
        Assert.That(source.Departments[0].Rooms[1].Count.Min, Is.Zero);
        Assert.That(source.Departments[0].Rooms[2].Role, Is.EqualTo("filler"));
    }

    [TestCase(-1, 1, 1)]
    [TestCase(2, 1, 3)]
    [TestCase(1, 3, 2)]
    [TestCase(0, 0, -1)]
    public void InvalidInstanceCountBoundsAreRejected(int min, int target, int max)
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Rooms[0].Count = new() { Min = min, Target = target, Max = max };
        Assert.That(ProgramValidator.Validate(source).Any(d => d.Severity == "error"), Is.True);
    }

    [TestCase(0, 20, 40)]
    [TestCase(30, 20, 40)]
    [TestCase(10, 50, 40)]
    public void InvalidRoomAreaBoundsAreRejected(int min, int target, int max)
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Rooms[0].Area = ProgramTestData.Area(min, target, max);
        Assert.That(ProgramValidator.Validate(source).Any(d => d.Severity == "error"), Is.True);
    }

    [Test]
    public void GuaranteedRoomFloorCannotExceedParentMaximum()
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Area = ProgramTestData.Area(16, 20, 30);
        source.Departments[0].Rooms[0].Area = ProgramTestData.Area(16, 20, 25);
        source.Departments[0].Rooms[0].Count = new() { Min = 2, Target = 2, Max = 2 };
        Assert.That(ProgramValidator.Validate(source).Any(d => d.Severity == "error"), Is.True);
    }

    [Test]
    public void RoomCapacityMustBeAbleToReachParentMinimumWithoutAnonymousResidualFloor()
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Area = ProgramTestData.Area(121, 130, 200);
        Assert.That(ProgramValidator.Validate(source).Any(d => d.Severity == "error"), Is.True);
    }

    [Test]
    public void DuplicateSiblingIdsAndMissingCanonicalReferencesAreRejected()
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Rooms.Add(ProgramTestData.Room("Work"));
        source.Connections.Add(new() { Id = "bad-target", From = "Alpha/Work", To = ["Missing/Room"] });
        var errors = ProgramValidator.Validate(source).Where(d => d.Severity == "error").ToList();
        Assert.That(errors.Any(d => d.Code == "duplicate"), Is.True, ProgramTestData.Errors(errors));
        source.Departments[0].Rooms.RemoveAt(1);
        errors = ProgramValidator.Validate(source).Where(d => d.Severity == "error").ToList();
        Assert.That(errors.Any(d => d.Code == "reference"), Is.True, ProgramTestData.Errors(errors));
    }

    [Test]
    public void ArbitraryDepartmentAndAccessNamesHaveNoSpecialBehavior()
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Id = "Observatory";
        source.Departments[0].Rooms[0].Access.Level = "telescope-crew";
        source.Departments[0].Rooms[0].Tags = ["custom-science-section"];
        var diagnostics = ProgramValidator.Validate(source);
        Assert.That(diagnostics.Where(d => d.Severity == "error"), Is.Empty, ProgramTestData.Errors(diagnostics));
    }

    [Test]
    public void LocalAndCanonicalReferencesCompileToTheSameEndpoints()
    {
        var local = ProgramTestData.Minimal();
        local.Departments[0].Rooms.Add(ProgramTestData.Optional("Lounge"));
        local.Departments[0].Rooms[0].Connections.Add(new() { Id = "lounge", To = ["Lounge"] });
        var canonical = ProgramJson.Clone(local);
        canonical.Departments[0].Rooms[0].Connections[0].To = ["Alpha/Lounge"];
        var left = RequirementCompiler.Compile(local);
        var right = RequirementCompiler.Compile(canonical);
        Assert.That(left.Success, Is.True, ProgramTestData.Errors(left.Diagnostics));
        Assert.That(right.Success, Is.True, ProgramTestData.Errors(right.Diagnostics));
        Assert.That(System.Text.Json.JsonSerializer.Serialize(left.Connections, ProgramJson.Options),
            Is.EqualTo(System.Text.Json.JsonSerializer.Serialize(right.Connections, ProgramJson.Options)));
    }

    [Test]
    public void OptionalMutualAccessDoesNotFormAnInvalidAuthoringCycle()
    {
        var source = ProgramTestData.Minimal();
        var a = ProgramTestData.Optional("A");
        var b = ProgramTestData.Optional("B");
        a.Connections.Add(new() { Id = "a-b", To = ["B"], Presence = "requireTarget" });
        b.Connections.Add(new() { Id = "b-a", To = ["A"], Presence = "requireTarget" });
        source.Departments[0].Rooms.AddRange([a, b]);
        var diagnostics = ProgramValidator.Validate(source);
        Assert.That(diagnostics.Where(d => d.Severity == "error"), Is.Empty, ProgramTestData.Errors(diagnostics));
    }

    [TestCase("all", false)]
    [TestCase("any", true)]
    public void RequiredOptionalChoiceNeedsOnlyTheSelectedTargetSet(string selection, bool success)
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Rooms.Add(ProgramTestData.Optional("Enabled"));
        source.Departments[0].Rooms.Add(ProgramTestData.Optional("Disabled", 0));
        source.Departments[0].Rooms[0].Connections.Add(new()
        {
            Id = "choice", To = ["Enabled", "Disabled"], Selection = selection,
            Presence = "requireTarget", Priority = "required"
        });
        var diagnostics = ProgramValidator.Validate(source);
        Assert.That(diagnostics.All(d => d.Severity != "error"), Is.EqualTo(success), ProgramTestData.Errors(diagnostics));
    }

    [Test]
    public void IfPresentDoesNotRequireAnOptionalTargetToExist()
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Rooms.Add(ProgramTestData.Optional("Absent", 0));
        source.Departments[0].Rooms[0].Connections.Add(new()
        {
            Id = "conditional", To = ["Absent"], Presence = "ifPresent", Priority = "required"
        });
        var diagnostics = ProgramValidator.Validate(source);
        Assert.That(diagnostics.Where(d => d.Severity == "error"), Is.Empty, ProgramTestData.Errors(diagnostics));
    }

    [TestCase(1.0, 1.01)]
    [TestCase(.9, .5)]
    [TestCase(double.NaN, .5)]
    public void InvalidShapeLimitsAreRejected(double aspect, double fill)
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Rooms[0].Shape = new() { MaxAspectRatio = aspect, MinFillRatio = fill };
        Assert.That(ProgramValidator.Validate(source).Any(d => d.Severity == "error"), Is.True);
    }

    [Test]
    public void MaintenancePassagesAndTrueRoomsHaveSeparateRequirements()
    {
        var source = ProgramTestData.Minimal();
        var maintenance = ProgramTestData.Optional("Store", 4);
        source.Maintenance.Rooms.Add(maintenance);
        source.Connections.Add(new()
        {
            Id = "service", From = "Alpha/Work", To = ["_maintenance/Store"],
            Priority = "desired", Modes = ["maintenance"], Presence = "ifPresent"
        });
        var diagnostics = ProgramValidator.Validate(source);
        Assert.That(diagnostics.Where(d => d.Severity == "error"), Is.Empty, ProgramTestData.Errors(diagnostics));
        source.Maintenance.PassageWidth.Max = 1;
        Assert.That(ProgramValidator.Validate(source).Any(d => d.Severity == "error"), Is.True);
    }

    [Test]
    public void ExteriorFrontageCannotBeRequiredWhenSpaceFrontageIsDisallowed()
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Rooms[0].Exterior.MinSpaceFrontage = 2;
        source.Departments[0].Rooms[0].Exterior.AllowSpaceExposure = false;
        Assert.That(ProgramValidator.Validate(source).Any(d => d.Severity == "error"), Is.True);
    }

    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    [TestCase(-1)]
    public void InvalidPressureWeightsAreRejected(double weight)
    {
        var source = ProgramTestData.Minimal();
        source.Settings.RoomPressureWeight = weight;
        Assert.That(ProgramValidator.Validate(source).Any(d => d.Severity == "error"), Is.True);
    }

    [Test]
    public void NullNestedCollectionsReturnDiagnosticsWithoutThrowing()
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Rooms.Add(null!);
        source.Connections.Add(null!);
        source.Maintenance.Rooms = null!;
        IReadOnlyList<ProgramDiagnostic>? diagnostics = null;
        Assert.DoesNotThrow(() => diagnostics = ProgramValidator.Validate(source));
        Assert.That(diagnostics!.Any(d => d.Severity == "error"), Is.True);
    }

    [Test]
    public void RequiredDepartmentOnlyRouteCannotCrossOwnerBoundaries()
    {
        var source = ProgramTestData.Minimal();
        var target = ProgramJson.Clone(source.Departments[0]);
        target.Id = "Beta";
        source.Departments.Add(target);
        source.Departments[0].Rooms[0].Access.AllowMaintenanceEntry = false;
        source.Departments[0].Rooms[0].Access.AllowPublicEntry = false;
        source.Connections.Add(new()
        {
            Id = "impossible-route", From = "Alpha/Work", To = ["Beta/Work"],
            Modes = ["department"], Priority = "required", Presence = "requireTarget"
        });
        var errors = ProgramValidator.Validate(source);
        Assert.That(errors.Any(d => d.Severity == "error"), Is.True,
            "A department-only route cannot cross department boundaries; " + ProgramTestData.Errors(errors));
    }

    [Test]
    public void ACompatibleAlternativeModeKeepsARequiredConnectionPossible()
    {
        var source = ProgramTestData.Minimal();
        var target = ProgramJson.Clone(source.Departments[0]);
        target.Id = "Beta";
        source.Departments.Add(target);
        source.Connections.Add(new()
        {
            Id = "possible-route", From = "Alpha/Work", To = ["Beta/Work"],
            Modes = ["department", "maintenance"], Priority = "required", Presence = "requireTarget"
        });
        var errors = ProgramValidator.Validate(source);
        Assert.That(errors.All(d => d.Severity != "error"), Is.True, ProgramTestData.Errors(errors));
    }

    [TestCase("any", true)]
    [TestCase("all", false)]
    public void AnyTargetMayChooseAnOwnerCompatibleAlternativeButAllCannot(string selection, bool success)
    {
        var source = ProgramTestData.Minimal();
        var beta = ProgramJson.Clone(source.Departments[0]);
        beta.Id = "Beta";
        source.Departments.Add(beta);
        source.Departments[0].Rooms.Add(ProgramTestData.Optional("Support"));
        source.Connections.Add(new()
        {
            Id = "route-choice", From = "Alpha/Work", To = ["Alpha/Support", "Beta/Work"],
            Selection = selection, Modes = ["department"], Priority = "required", Presence = "requireTarget"
        });
        var result = RequirementCompiler.Compile(source);
        Assert.That(result.Success, Is.EqualTo(success), ProgramTestData.Errors(result.Diagnostics));
        if (success)
        {
            var support = result.Tree!.Children.Single(n => n.Path == "Alpha").Children.Single(n => n.Path == "Alpha/Support");
            Assert.That(support.EffectiveFloorBudget.Min, Is.EqualTo(9),
                "The impossible cross-owner branch cannot prevent promotion of the only usable target.");
        }
    }

    [Test]
    public void IfPresentCanOmitAnOptionalOwnerIncompatibleTarget()
    {
        var source = ProgramTestData.Minimal();
        var beta = ProgramJson.Clone(source.Departments[0]);
        beta.Id = "Beta";
        source.Departments.Add(beta);
        beta.Rooms.Add(ProgramTestData.Optional("Absent", 0));
        source.Connections.Add(new()
        {
            Id = "optional-route", From = "Alpha/Work", To = ["Beta/Absent"],
            Modes = ["department"], Priority = "required", Presence = "ifPresent"
        });
        var result = RequirementCompiler.Compile(source);
        Assert.That(result.Success, Is.True, ProgramTestData.Errors(result.Diagnostics));
    }
}
