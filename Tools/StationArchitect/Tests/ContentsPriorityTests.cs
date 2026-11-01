using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
public sealed class ContentsPriorityTests
{
    [Test]
    public void PreferredExtrasCannotStarveAnotherMandatoryFeature()
    {
        var definition = ProgramTestData.Minimal();
        var room = definition.Departments[0].Rooms[0];
        room.Contents =
        [
            new() { Id = "Bulk", Prototypes = ["BulkMachine"], Count = new() { Min = 20, Target = 45, Max = 50 }, Placement = "floor", MinArea = 1 },
            new() { Id = "RequiredControl", Prototypes = ["ControlMachine"], Count = new() { Min = 1, Target = 1, Max = 1 }, Placement = "floor", MinArea = 1 }
        ];
        var project = new StationDesignProject { Definition = definition, Stage = "circulation", Settings = new() { Width = 32, Height = 24 } };
        project.Layout.Rooms.Add(new() { Id = "Alpha/" + room.Id + "#1", DefinitionPath = "Alpha/" + room.Id, OwnerId = "Alpha", Cells = new GridBounds(5, 5, 7, 8).Cells().ToList() });
        var result = DesignContents.Run(project);
        Assert.That(result.Layout.Equipment.Count(entity => entity.RequirementId == "Bulk"), Is.GreaterThanOrEqualTo(20));
        Assert.That(result.Layout.Equipment.Count(entity => entity.RequirementId == "RequiredControl"), Is.EqualTo(1));
        Assert.That(result.Report!.Diagnostics.Any(issue => issue.Code == "equipment-fit"), Is.False);
        Assert.That(result.Definition.Departments[0].Rooms[0].Contents[0].Count.Target, Is.EqualTo(45));
    }
}
