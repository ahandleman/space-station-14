using Content.StationArchitect;

namespace StationArchitect.Tests;

internal static class ProgramTestData
{
    public static StationProgramDocument Minimal() => new()
    {
        Id = "TestStation",
        Departments =
        [
            new()
            {
                Id = "Alpha", Name = "Alpha department",
                Area = Area(16, 40, 120),
                Rooms = [Room("Work", 16, 40, 120)]
            }
        ]
    };

    public static AreaRequirement Area(int min, int target, int max, double weight = 1) =>
        new() { Min = min, Target = target, Max = max, Weight = weight };

    public static RoomDefinition Room(string id, int min = 9, int target = 25, int max = 100) =>
        new() { Id = id, Area = Area(min, target, max) };

    public static RoomDefinition Optional(string id, int maxCount = 1) => new()
    {
        Id = id,
        Count = new() { Min = 0, Target = 0, Max = maxCount },
        Area = Area(9, 16, 25)
    };

    public static string Errors(IEnumerable<ProgramDiagnostic> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Select(d => $"{d.Path}: {d.Code}: {d.Message}"));
}
