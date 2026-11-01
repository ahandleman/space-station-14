using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class DesignPipelineTests
{
    [Test]
    public void InvalidRawSelectionCannotBeHiddenByLaterStagesOrSavedAcceptance()
    {
        var project = DesignPipeline.New(ProgramTestData.Minimal());
        project.Selection.RoomCounts["Alpha/Work"] = 0;
        project.Report = new() { Complete = true };
        var imported = DesignPipeline.Import(project);
        Assert.That(imported.Report!.Success, Is.False);
        Assert.That(imported.Report.Diagnostics.Any(item => item.Code == "selection-count"), Is.True);
        var checkedProgram = DesignPipeline.RunStage(project, "program");
        Assert.That(checkedProgram.Report!.Success, Is.False);
        Assert.That(checkedProgram.Definition.Departments[0].Rooms[0].Count.Min, Is.EqualTo(1));
    }

    [Test]
    public void DefinitionEditRetainsLockedFloorAndFreshRequirementsEvenWhenRangeIsInvalid()
    {
        var project = DesignPipeline.New(ProgramTestData.Minimal());
        var room = project.Layout.Rooms.Single();
        room.Cells = new GridBounds(5, 5, 8, 5).Cells().ToList();
        room.Bounds = new(5, 5, 8, 5); room.FloorLocked = true; room.HasSeed = true; room.Seed = new(8, 7);
        project.Stage = "growth";
        var definition = ProgramJson.Clone(project.Definition);
        definition.Departments[0].Rooms[0].Area.Min = 150;
        var edited = DesignPipeline.UpdateDefinition(project, definition);
        Assert.That(edited.Report!.Success, Is.False);
        Assert.That(edited.Layout.Rooms.Single().Cells, Is.EqualTo(room.Cells));
        Assert.That(ProjectTree.Find(edited.Tree!, "Alpha/Work#1")!.Requirements!.Area!.Min, Is.EqualTo(150));
        Assert.That(project.Definition.Departments[0].Rooms[0].Area.Min, Is.Not.EqualTo(150));
    }

    [Test]
    public void InvalidNewSettingsAreRejectedWithoutMutatingCurrentProject()
    {
        var project = DesignPipeline.New(ProgramTestData.Minimal());
        var settings = ProgramJson.Clone(project.Settings); settings.MaintenanceWidth = 8;
        Assert.Throws<ArgumentException>(() => DesignPipeline.UpdateSettings(project, settings));
        Assert.That(project.Settings.MaintenanceWidth, Is.EqualTo(2));
    }

    [Test]
    public void ExplicitEnvironmentResetUsesCurrentDefinitionWithoutMovingGeometry()
    {
        var project = DesignPipeline.New(ProgramTestData.Minimal());
        var room = project.Layout.Rooms.Single(); room.Cells = new GridBounds(5, 5, 8, 5).Cells().ToList();
        project.Layout.EnvironmentZones.Add(new() { RoomId = room.Id, Pressure = "standard", TemperatureKelvin = 400, Cells = [.. room.Cells], Edited = true });
        project.Definition.Departments[0].Rooms[0].Environment.Pressure = "custom";
        var reset = ProjectEdits.Apply(project, new() { Kind = "reset-zone", NodeId = room.Id });
        var zone = reset.Layout.EnvironmentZones.Single();
        Assert.That(zone.Pressure, Is.EqualTo("custom")); Assert.That(zone.Edited, Is.False);
        Assert.That(zone.Cells, Is.EqualTo(room.Cells)); Assert.That(reset.Layout.Rooms.Single().Cells, Is.EqualTo(room.Cells));
        Assert.That(project.Layout.EnvironmentZones.Single().TemperatureKelvin, Is.EqualTo(400));
    }

    [Test]
    public void SavedPlacementAttemptReplaysButOnlyPlacementAcceptsAnOverride()
    {
        var project = DesignPipeline.New(ProgramTestData.Minimal());
        project.Layout.PlacementAttempt = 2; project.Settings.CandidateBudget = 4;
        var first = DesignPipeline.RunStage(project, "placement");
        var second = DesignPipeline.RunStage(project, "placement", 2);
        Assert.That(first.Layout.Rooms.Select(room => room.Seed), Is.EqualTo(second.Layout.Rooms.Select(room => room.Seed)));
        Assert.Throws<ArgumentException>(() => DesignPipeline.RunStage(project, "growth", 1));
        Assert.Throws<ArgumentException>(() => DesignPipeline.RunStage(project, "placement", 9));
    }

    [Test]
    public void UnboundedPlanningDemandReturnsAnEditableCapacityErrorBeforeAllocatingMasks()
    {
        var definition = ProgramTestData.Minimal();
        definition.Departments[0].Area = new() { Min = 10, Target = 1000000, Max = 1000000 };
        definition.Departments[0].Rooms[0].Area = new() { Min = 10, Target = 1000000, Max = 1000000 };
        var project = DesignPipeline.New(definition);
        var result = DesignPipeline.RunStage(project, "placement");
        Assert.That(result.Report!.Diagnostics.Any(item => item.Code == "planning-capacity"), Is.True);
        Assert.That(result.Layout.Rooms.All(room => room.Cells.Count == 0 && room.GrowthReservation.Count == 0), Is.True);
        Assert.That(result.Definition.Departments[0].Rooms[0].Area.Target, Is.EqualTo(1000000));
    }
}
