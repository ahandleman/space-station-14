using System.Text.Json;
using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class NativeProgramYamlTests
{
    [Test]
    public void NativePayloadsAndGasFractionsRoundTripAsBoundedData()
    {
        var definition = ProgramTestData.Minimal();
        definition.Systems.Profiles.Add(new()
        {
            Id = "AirReserve", Prototype = "AirCanister", Kind = "device",
            Ports = [new() { Id = "port", Kind = "gas", Connection = "portable", NodeGroup = "Pipe" }],
            Components = new() { ["GasCanister"] = JsonSerializer.SerializeToElement(new { air = new { volume = 1500, temperature = 293.15, moles = new { Oxygen = 581.56, Nitrogen = 2187.79 } } }) },
            Sources = [new() { PortId = "port", Kind = "gas", VolumeLitres = 1500, PressureKpa = 4500, GasFractions = new() { ["Oxygen"] = .21, ["Nitrogen"] = .79 } }]
        });
        var imported = ProgramYaml.Import(ProgramYaml.Export(definition));
        Assert.That(imported.Success, Is.True, string.Join("; ", imported.Diagnostics.Select(item => item.Message)));
        var profile = imported.Document!.Systems.Profiles.Single();
        Assert.That(profile.Components["GasCanister"].GetProperty("air").GetProperty("volume").GetInt32(), Is.EqualTo(1500));
        Assert.That(profile.Components["GasCanister"].GetProperty("air").GetProperty("moles").GetProperty("Oxygen").GetDouble(), Is.EqualTo(581.56));
        Assert.That(profile.Sources.Single().GasFractions["Nitrogen"], Is.EqualTo(.79));
    }

    [Test]
    public void NativePayloadStillCannotConstructTaggedTypesOrUseAliases()
    {
        var yaml = ProgramYaml.Export(ProgramTestData.Minimal());
        var imported = ProgramYaml.Import(yaml.Replace("systems:", "systems: !type:Anything"));
        Assert.That(imported.Success, Is.False);
        Assert.That(imported.Diagnostics.Any(item => item.Message.Contains("tags")), Is.True);
    }

    [Test]
    public void EditingNativeSegmentsRetainsAnExplicitLockedDraft()
    {
        var project = DesignPipeline.New(ProgramTestData.Minimal());
        project.Definition.Systems.Networks.Add(new() { Id = "Wire", Kind = "power" });
        project.Layout.SystemNetworks.Add(new() { Id = "Wire#1", RequirementId = "Wire", Segments = [new() { Id = "Segment1", ProfileId = "Cable", Position = new(12, 12) }] });
        var moved = ProjectEdits.Apply(project, new() { Kind = "move-system-segment", NodeId = "Segment1", Position = new(13, 12) });
        Assert.That(moved.Layout.SystemNetworks.Single().Locked, Is.True);
        Assert.That(moved.Layout.SystemNetworks.Single().Segments.Single().Position, Is.EqualTo(new Cell(13, 12)));
        Assert.That(project.Layout.SystemNetworks.Single().Segments.Single().Position, Is.EqualTo(new Cell(12, 12)));
        var unlocked = ProjectEdits.Apply(moved, new() { Kind = "lock-system-network", NodeId = "Wire#1", Enabled = false });
        Assert.That(unlocked.Layout.SystemNetworks.Single().Edited, Is.False);
        Assert.That(unlocked.Layout.SystemNetworks.Single().Segments.Single().Locked, Is.False);
    }
}
