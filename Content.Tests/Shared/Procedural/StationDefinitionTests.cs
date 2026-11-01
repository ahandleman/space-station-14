using System;
using System.Linq;
using System.Text.Json;
using Content.StationGeneration;
using NUnit.Framework;

namespace Content.Tests.Shared.Procedural;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class StationDefinitionTests
{
    [Test]
    public void TopologyUsesAuthoredHubAndOrderingForArbitraryDepartmentNames()
    {
        var config = new StationGenerationConfig { Archetype = "hub", Departments =
        [
            new() { Id = "Commons", PlacementOrder = 2 },
            new() { Id = "Arrivals", PlacementOrder = 4 },
            new() { Id = "Workshop", PlacementOrder = 3 },
            new() { Id = "NewHub", PlacementOrder = 1, TopologyHub = true },
            new() { Id = "Observatory", PlacementOrder = 0 }
        ] };
        var first = StationGenerator.GenerateTopology(config);
        Assert.That(first.Edges.Count(e => e.From == "NewHub" || e.To == "NewHub"), Is.EqualTo(4));
        Assert.That(first.Edges.Count(e => e.From == "Commons" || e.To == "Commons"), Is.EqualTo(3),
            "A department name must not override its authored topology role.");
        config.Departments.Reverse();
        Assert.That(ProjectJson.Serialize(StationGenerator.GenerateTopology(config)), Is.EqualTo(ProjectJson.Serialize(first)));
        config.Departments[0].TopologyHub = true;
        Assert.That(StationHierarchy.Validate(config).Any(d => d.Code == "hierarchy-topology-hub"), Is.True);
    }

    [Test]
    public void AuthoringYamlRoundTripsHierarchyTopologyAndMaintenanceSettingsWithoutGeometry()
    {
        var project = new StationProject();
        project.Topology = StationGenerator.GenerateTopology(project.Config);
        project.Topology.Edges[0].Weight = 3.75;
        project.Topology.Edges[0].Description = "Priority: arrivals → station\nKeep public frontage.";
        project.Topology.SuppressedTemplateIds = ["template-removed-by-mapper"];
        project.Topology.NodePositions["Engineering"] = new(245, 178);
        project.Config.Spatial.DeepMaintenanceMin = 3;
        project.Config.Spatial.DeepMaintenanceMax = 5;
        project.Config.Spatial.MaintenancePassageMaxWidth = 3;
        project.Config.Spatial.MaintenancePocketPreference = "rooms";
        project.Config.Spatial.DeepMaintenanceRoomMinArea = 16;
        project.Config.Spatial.DeepMaintenanceRoomMaxArea = 200;
        project.Config.Spatial.SolarMin = 1;
        project.Config.Spatial.SolarMax = 4;
        project.Config.HierarchyRelations.Add(new TopologyEdge
        {
            Id = "crops-to-kitchen", From = "Service/Botany", To = "Service/Kitchen",
            Description = "Deliver food crops", Priority = "desired", Weight = 7.5,
            Relation = "delivery", Purpose = "items", AllowedModes = ["staffPassage", "mail"]
        });
        project.SpatialPlan = new StationSpatialPlan { Envelope = [new(7, 8)] };
        var yaml = StationDefinitionYaml.Export(project);
        Assert.That(yaml, Does.Not.Contain("envelope:"));
        Assert.That(yaml, Does.Not.Contain("spatialPlan:"));
        var imported = StationDefinitionYaml.Import(yaml);
        Assert.That(imported.Success, Is.True, string.Join("\n", imported.Diagnostics.Select(d => d.Message)));
        Assert.That(ProjectJson.Serialize(imported.Document), Is.EqualTo(ProjectJson.Serialize(StationDefinitionDocument.FromProject(project))));
        var restored = imported.Document.ToProject();
        Assert.That(restored.SpatialPlan, Is.Null);
        Assert.That(restored.Stage, Is.EqualTo("topology"));
        restored.Config.Departments[0].Description = "Changed after import";
        Assert.That(imported.Document.Config.Departments[0].Description, Is.Not.EqualTo("Changed after import"), "ToProject must clone authoring data.");
    }

    [Test]
    public void MinimalYamlUsesExplicitSchemaAndPreservesStringIdsThatLookNumeric()
    {
        var project = new StationProject();
        project.Config.Departments.Single(d => d.Id == "Science").Id = "1234";
        var yaml = StationDefinitionYaml.Export(project);
        var imported = StationDefinitionYaml.Import(yaml);
        Assert.That(imported.Success, Is.True, string.Join("\n", imported.Diagnostics.Select(d => d.Message)));
        Assert.That(imported.Document.Config.Departments.Any(d => d.Id == "1234"), Is.True);
        Assert.That(StationDefinitionYaml.Import("schemaVersion: 1\nconfig: {}\n").Success, Is.True);
    }

    [Test]
    public void DefaultProgramsHaveInternalAtmosSalvageAndSharedTegAccess()
    {
        var config = new StationGenerationConfig();
        Assert.That(StationHierarchy.Validate(config), Is.Empty);
        var engineering = config.Departments.Single(d => d.Id == "Engineering");
        var atmos = engineering.Subdepartments.Single(s => s.Id == "Atmos");
        Assert.That(atmos.RequireInternalAccess, Is.True);
        Assert.That(atmos.AllowSplit, Is.False);
        Assert.That(atmos.Rooms.Single(r => r.Id == "TEG").AccessibleFrom, Is.EquivalentTo(new[] { "Engineering", "Engineering/Atmos" }));
        Assert.That(config.Departments.Single(d => d.Id == "Cargo").Subdepartments.Single().RequireInternalAccess, Is.True);
        Assert.That(config.Departments.Single(d => d.Id == "Security").Subdepartments.Single().Rooms.Count, Is.EqualTo(5));
        Assert.That(config.Departments.Single(d => d.Id == "Command").Subdepartments.Single().Rooms.Count, Is.EqualTo(4));
        Assert.That(StationHierarchy.RequiredFloorArea(config.Departments.Single(d => d.Id == "Security")), Is.GreaterThan(250));
    }

    [Test]
    public void OlderJsonDoesNotInventSubdepartmentsOnImportedDepartments()
    {
        var department = JsonSerializer.Deserialize<DepartmentDefinition>("{\"id\":\"Custom\",\"minArea\":40}", ProjectJson.Options);
        Assert.That(department.Subdepartments, Is.Empty);
    }

    [TestCase("config: {}", "schemaVersion")]
    [TestCase("schemaVersion: 1", "config")]
    [TestCase("schemaVersion: 9\nconfig: {}", "schemaVersion")]
    [TestCase("schemaVersion: wrong\nconfig: {}", "Int32")]
    [TestCase("schemaVersion: 1\nconfig: null", "null")]
    [TestCase("schemaVersion: 1\nconfig: []", "list")]
    [TestCase("schemaVersion: 1\nconfig:\n  seed: 999999999999999", "Int32")]
    [TestCase("schemaVersion: 1\nconfig:\n  defaultAllowMailNetwork: yes", "Boolean")]
    [TestCase("schemaVersion: 1\nconfig:\n  weights:\n    distance: .inf", "Double")]
    [TestCase("schemaVersion: 1\nconfig:\n  weights:\n    distance: NaN", "Double")]
    [TestCase("schemaVersion: 1\nconfig:\n  seed: [1, 2]", "list")]
    [TestCase("schemaVersion: 1\nconfig:\n  seed: { x: 1 }", "mapping")]
    [TestCase("schemaVersion: 1\nconfig:\n  departments: null", "null")]
    [TestCase("schemaVersion: 1\nconfig:\n  departments: []", "departments")]
    [TestCase("schemaVersion: 1\nconfig:\n  spatialPlan: {}", "unknown")]
    [TestCase("schemaVersion: 1\nconfig:\n  seed: 1\n  seed: 2", "Duplicate")]
    [TestCase("schemaVersion: 1\nconfig:\n  seed: 1\n  Seed: 2", "duplicate")]
    [TestCase("schemaVersion: 1\nconfig: !System.Object {}", "tags")]
    [TestCase("schemaVersion: 1\nconfig: &loop { spatial: *loop }", "aliases")]
    [TestCase("---\nschemaVersion: 1\nconfig: {}\n---\nschemaVersion: 1\nconfig: {}", "one YAML")]
    public void MalformedYamlIsRejectedWithDiagnostic(string yaml, string message)
    {
        var imported = StationDefinitionYaml.Import(yaml);
        Assert.That(imported.Success, Is.False);
        Assert.That(imported.Document, Is.Null);
        Assert.That(string.Join("\n", imported.Diagnostics.Select(d => d.Message)), Does.Contain(message).IgnoreCase);
    }

    [Test]
    public void ExcessiveAliasAndOversizedDocumentsAreRejectedBeforeMaterialization()
    {
        var aliases = "schemaVersion: 1\nconfig: &x { spatial: [" + string.Join(',', Enumerable.Repeat("*x", 10000)) + "] }";
        Assert.That(StationDefinitionYaml.Import(aliases).Diagnostics.Any(d => d.Message.Contains("aliases")), Is.True);
        Assert.That(StationDefinitionYaml.Import(new string('x', 2 * 1024 * 1024 + 1)).Diagnostics.Any(d => d.Message.Contains("2 MiB")), Is.True);
    }

    [Test]
    public void InvalidReferencesDuplicateIdsCountsAndCyclesAreRejected()
    {
        var config = new StationGenerationConfig();
        var service = config.Departments.Single(d => d.Id == "Service");
        service.Subdepartments.Add(new SubdepartmentDefinition { Id = "Kitchen" });
        service.Subdepartments[0].Rooms =
        [
            new() { Id = "A", MinCount = -1, AccessibleFrom = ["Service/Kitchen/B", "Missing/Place"] },
            new() { Id = "B", AccessibleFrom = ["Service/Kitchen/A"] }
        ];
        var errors = StationHierarchy.Validate(config);
        Assert.That(errors.Select(d => d.Code), Does.Contain("hierarchy-duplicate"));
        Assert.That(errors.Select(d => d.Code), Does.Contain("hierarchy-count"));
        Assert.That(errors.Select(d => d.Code), Does.Contain("hierarchy-reference"));
        Assert.That(errors.Select(d => d.Code), Does.Contain("hierarchy-cycle"));
        Assert.That(StationDefinitionYaml.Import(StationDefinitionYaml.Export(new StationProject { Config = config })).Success, Is.False);
    }

    [Test]
    public void InvalidWeightsAreaBudgetsAndScopedDependencyEndpointsAreRejected()
    {
        var config = new StationGenerationConfig();
        config.Departments.Single(d => d.Id == "Command").MaxArea = 150;
        config.HierarchyRelations = [new TopologyEdge { From = "Service/Unknown", To = "Service/Kitchen", Weight = 0 }];
        var errors = StationHierarchy.Validate(config);
        Assert.That(errors.Select(d => d.Code), Does.Contain("hierarchy-capacity"));
        Assert.That(errors.Select(d => d.Code), Does.Contain("hierarchy-reference"));
        Assert.That(errors.Select(d => d.Code), Does.Contain("hierarchy-weight"));
    }

    [Test]
    public void NullNestedJsonEntriesProduceDiagnosticsWithoutCrashingBudgetCalculation()
    {
        var config = new StationGenerationConfig();
        var engineering = config.Departments.Single(d => d.Id == "Engineering");
        engineering.Subdepartments[0].Rooms.Add(null);
        engineering.Subdepartments.Add(null);
        config.HierarchyRelations.Add(null);
        Assert.DoesNotThrow(() => StationHierarchy.RequiredFloorArea(engineering));
        Assert.That(StationHierarchy.Validate(config).Count(d => d.Code == "hierarchy-null"), Is.EqualTo(3));
    }

    [Test]
    public void StrictAvoidanceConflictsOnlyWhenEveryRequiredAlternativeIsBlocked()
    {
        var config = new StationGenerationConfig();
        config.HierarchyRelations =
        [
            new() { Id = "require", From = "Service/Botany", To = "Service/Kitchen", Priority = "required", Purpose = "items", AllowedModes = ["staffPassage", "mail"] },
            new() { Id = "avoid", From = "Service/Botany", To = "Service/Kitchen", Priority = "avoided", Purpose = "items", AllowedModes = ["staffPassage"], StrictAvoidance = true }
        ];
        Assert.That(StationHierarchy.Validate(config).Any(d => d.Code == "hierarchy-conflict"), Is.False);
        config.HierarchyRelations[1].AllowedModes.Add("mail");
        Assert.That(StationHierarchy.Validate(config).Any(d => d.Code == "hierarchy-conflict"), Is.True);
    }
}
