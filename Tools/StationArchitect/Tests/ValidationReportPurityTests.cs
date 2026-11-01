using System.Text.Json;
using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
public sealed class ValidationReportPurityTests
{
    [Test]
    public void ReadOnlyValidationComputesFreshObservationsWithoutPublishingThem()
    {
        var project = DesignPipeline.New(ProgramTestData.Minimal());
        project.Stage = "gameflow";
        project.Gameflow = new() { TotalCost = 12345, Rules = [new() { Id = "forged", Value = 42 }] };
        project.Systems = new() { Complete = false };
        var original = JsonSerializer.Serialize(project, ProgramJson.Options);
        var report = DesignGeometryValidator.Validate(project);
        Assert.That(report.Metrics["gameflowCost"], Is.Zero, "Submitted report costs are never acceptance evidence.");
        Assert.That(JsonSerializer.Serialize(project, ProgramJson.Options), Is.EqualTo(original));
        DesignGeometryValidator.Validate(project, refreshReports: true);
        Assert.That(project.Gameflow!.Rules, Is.Empty);
        Assert.That(project.Gameflow.TotalCost, Is.Zero);
        Assert.That(project.Systems.Complete, Is.True);
    }

    [Test]
    public void ImportPublishesFreshReportsOnlyOnItsPrivateClone()
    {
        var project = DesignPipeline.New(ProgramTestData.Minimal());
        project.Stage = "gameflow";
        project.Layout.Rooms.Single().Cells = new GridBounds(5, 5, 6, 5).Cells().ToList();
        project.Gameflow = new() { TotalCost = 12345, Rules = [new() { Id = "forged", Value = 42 }] };
        var original = JsonSerializer.Serialize(project, ProgramJson.Options);
        var imported = DesignPipeline.Import(project);
        Assert.That(imported.Gameflow!.Rules, Is.Empty);
        Assert.That(imported.Gameflow.TotalCost, Is.Zero);
        Assert.That(imported.Systems.Complete, Is.True);
        Assert.That(JsonSerializer.Serialize(project, ProgramJson.Options), Is.EqualTo(original));
    }
}
