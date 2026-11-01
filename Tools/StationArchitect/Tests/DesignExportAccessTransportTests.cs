using Content.StationArchitect;
using NUnit.Framework;
using YamlDotNet.RepresentationModel;

namespace StationArchitect.Tests;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class DesignExportAccessTransportTests
{
    private static StationDesignProject TwoRooms(bool sharedDoor)
    {
        var definition = ProgramTestData.Minimal();
        definition.Departments[0].Rooms[0].Access.Level = "security";
        var second = ProgramTestData.Room("Secure", 16, 25, 60); second.Access.Level = "armory";
        definition.Departments[0].Rooms.Add(second); definition.Departments[0].Area.Max = 200;
        var project = InstanceSelection.Resolve(new() { Definition = definition });
        project.Layout.Rooms.Single(room => room.DefinitionPath == "Alpha/Work").Cells = new GridBounds(10, 10, 4, 4).Cells().ToList();
        project.Layout.Rooms.Single(room => room.DefinitionPath == "Alpha/Secure").Cells = new GridBounds(sharedDoor ? 15 : 16, 10, 4, 4).Cells().ToList();
        project.Layout.Envelope = new GridBounds(9, 9, 12, 6).Cells().ToList();
        if (sharedDoor)
        {
            project.Layout.Interfaces = [new() { Id = "SecurityArmoryDoor", From = "Alpha/Work#1", To = "Alpha/Secure#1", Access = "Alpha", Cells = [new(14, 11)], FromApproach = new(13, 11), ToApproach = new(15, 11) }];
            DesignRouting.RebuildBoundaries(project);
        }
        else project.Layout.PublicCorridors = new GridBounds(14, 10, 2, 4).Cells().ToList();
        return project;
    }

    private static List<(string Prototype, YamlMappingNode Entity)> Entities(string yaml)
    {
        var stream = new YamlStream(); stream.Load(new StringReader(yaml));
        var root = (YamlMappingNode) stream.Documents.Single().RootNode;
        return ((YamlSequenceNode) root[new YamlScalarNode("entities")]).Children.Cast<YamlMappingNode>()
            .SelectMany(group => ((YamlSequenceNode) group[new YamlScalarNode("entities")]).Children.Cast<YamlMappingNode>()
                .Select(entity => (group[new YamlScalarNode("proto")].ToString(), entity))).ToList();
    }
    private static List<YamlMappingNode> Components(YamlMappingNode entity) => ((YamlSequenceNode) entity[new YamlScalarNode("components")]).Children.Cast<YamlMappingNode>().ToList();

    [Test]
    public void DoorAccessUsesBothAuthoredRoomPoliciesInsteadOfTheCapitalizedOwnerLabel()
    {
        var project = TwoRooms(true);
        List<string> Tags(DesignMapExport exported)
        {
            var door = Entities(exported.MapYaml).Single(item => item.Prototype == "Airlock");
            var reader = Components(door.Entity).Single(component => component[new YamlScalarNode("type")].ToString() == "AccessReader");
            return ((YamlSequenceNode) ((YamlSequenceNode) reader[new YamlScalarNode("access")]).Children.Single()).Children.Select(node => node.ToString()).ToList();
        }
        Assert.That(Tags(DesignMapExporter.Export(project)), Is.EquivalentTo(new[] { "Security", "Armory" }));
        project.Definition.Departments[0].Rooms[0].Access.Level = "public";
        Assert.That(Tags(DesignMapExporter.Export(project)), Is.EqualTo(new[] { "Armory" }));
        project.Definition.Departments[0].Rooms[0].Access.Level = "CustomClearance";
        var unknown = DesignMapExporter.Export(project);
        Assert.That(Tags(unknown), Is.EqualTo(new[] { "Armory" }), "The known private requirement still applies when the other endpoint is unresolved.");
        Assert.That(unknown.Diagnostics.Any(diagnostic => diagnostic.Code == "export-access" && diagnostic.Path == "CustomClearance"), Is.True);
    }

    [TestCase("disposalShortcut", "walking", "DisposalUnit")]
    [TestCase("mail", "utility", "MailingUnit")]
    public void ExportMaterializesTerminalAndTrunkOnTheSameTileWithoutTreatingTheTubeAsWalkingFloor(string mode, string kind, string unitPrototype)
    {
        var project = TwoRooms(false);
        project.Definition.Connections = [new() { Id = "Transport", From = "Alpha/Work", To = ["Alpha/Secure"], Kind = kind, Modes = [mode] }];
        var requirement = RequirementCompiler.Compile(project.Definition).Connections.Single();
        var from = project.Layout.Rooms.Single(room => room.DefinitionPath == "Alpha/Work");
        var to = project.Layout.Rooms.Single(room => room.DefinitionPath == "Alpha/Secure");
        var route = DesignTransport.TryBuild(project, from, to, requirement, mode);
        Assert.That(route, Is.Not.Null); project.Layout.Routes.Add(route!);
        var network = project.Layout.TransportNetworks.Single();
        var exported = DesignMapExporter.Export(project); var rows = Entities(exported.MapYaml);
        var terminal = network.Terminals[0]; var position = FormattableString.Invariant($"{terminal.Position.X + .5},{terminal.Position.Y + .5}");
        var atPosition = rows.Where(item => Components(item.Entity).Any(component => component[new YamlScalarNode("type")].ToString() == "Transform" && component.Children.TryGetValue(new YamlScalarNode("pos"), out var value) && value.ToString() == position)).ToList();
        Assert.That(atPosition.Select(item => item.Prototype), Is.SupersetOf(new[] { unitPrototype, "DisposalTrunk", "CableApcExtension" }));
        if (mode == "mail")
        {
            var unit = atPosition.Single(item => item.Prototype == unitPrototype);
            var mail = Components(unit.Entity).Single(component => component[new YamlScalarNode("type")].ToString() == "MailingUnit");
            Assert.That(mail[new YamlScalarNode("tag")].ToString(), Is.EqualTo(terminal.Tag));
            Assert.That(mail[new YamlScalarNode("target")].ToString(), Is.EqualTo(terminal.TargetTag));
            Assert.That(((YamlSequenceNode) mail[new YamlScalarNode("targetList")]).Children.Select(node => node.ToString()), Is.EqualTo(new[] { terminal.TargetTag }));
            var device = Components(unit.Entity).Single(component => component[new YamlScalarNode("type")].ToString() == "DeviceNetwork");
            Assert.That(device[new YamlScalarNode("receiveFrequency")].ToString(), Is.EqualTo(terminal.DeviceFrequency!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.That(device[new YamlScalarNode("transmitFrequency")].ToString(), Is.EqualTo(terminal.DeviceFrequency!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.That(((YamlScalarNode) device[new YamlScalarNode("receiveFrequency")]).Style, Is.Not.EqualTo(YamlDotNet.Core.ScalarStyle.DoubleQuoted), "The native frequency serializer expects a numeric scalar.");
        }
        var floorBefore = project.Layout.Rooms.SelectMany(room => room.Cells).ToHashSet();
        project.Definition.Departments[0].Rooms[0].Contents = [new() { Id = "Fabricator", Prototypes = ["Autolathe"], MinArea = 4 }];
        var materialized = DesignContents.Run(project);
        var reservedTerminals = DesignTransport.EndpointFloorCells(materialized);
        Assert.That(materialized.Layout.Equipment.SelectMany(entity => entity.Footprint.Concat(entity.WorkArea)).Any(reservedTerminals.Contains), Is.False);
        Assert.That(materialized.Layout.Rooms.SelectMany(room => room.Cells), Is.EquivalentTo(floorBefore));
    }
}
