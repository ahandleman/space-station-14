namespace Content.StationGeneration;

public sealed partial class DepartmentDefinition
{
    /// <summary>Placement ordering independent of IDs and input list order; lower values are considered first.</summary>
    public int PlacementOrder { get; set; } = 100;
    /// <summary>Optional department selected as the hub in a hub topology template.</summary>
    public bool TopologyHub { get; set; }
    /// <summary>Authored grouping policy: grouped, preferGrouped or independent. Spatial support is checked by the hierarchy planner.</summary>
    public string Grouping { get; set; } = "preferGrouped";
    public bool AllowSplit { get; set; } = true;
    public List<SubdepartmentDefinition> Subdepartments { get; set; } = [];
}

public sealed partial class StationGenerationConfig
{
    /// <summary>Dependencies use canonical paths: Department/Subdepartment/Room. Department-only dependencies remain in Topology.</summary>
    public List<TopologyEdge> HierarchyRelations { get; set; } = [];
}

public sealed class SubdepartmentDefinition
{
    public string Id { get; set; } = "Subdepartment";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int MinArea { get; set; } = 25;
    public int TargetArea { get; set; } = 70;
    public int MaxArea { get; set; } = 400;
    /// <summary>Every entrance must be reachable through the parent department without halls or maintenance.</summary>
    public bool RequireInternalAccess { get; set; }
    public bool AllowSplit { get; set; } = true;
    public List<RoomFeatureDefinition> Rooms { get; set; } = [];
}

public sealed class RoomFeatureDefinition
{
    public string Id { get; set; } = "Room";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>room is enclosed usable floor; feature reserves floor for later equipment materialization.</summary>
    public string Kind { get; set; } = "room";
    public int MinCount { get; set; } = 1;
    public int MaxCount { get; set; } = 1;
    public int MinArea { get; set; } = 16;
    public int MaxArea { get; set; } = 120;
    /// <summary>Required access origins, expressed as Department[/Subdepartment[/Room]] paths.</summary>
    public List<string> AccessibleFrom { get; set; } = [];
}

public sealed class StationDefinitionDocument
{
    public int SchemaVersion { get; set; } = 1;
    public StationGenerationConfig Config { get; set; } = new();
    public StationTopology? Topology { get; set; }

    public static StationDefinitionDocument FromProject(StationProject project) => new()
    {
        Config = ProjectJson.Clone(project.Config),
        Topology = project.Topology == null ? null : ProjectJson.Clone(project.Topology)
    };

    public StationProject ToProject() => new()
    {
        Config = ProjectJson.Clone(Config),
        Topology = Topology == null ? null : ProjectJson.Clone(Topology),
        Stage = "topology"
    };
}

/// <summary>Authoring helpers shared by YAML, editors and the spatial hierarchy pass.</summary>
public static class StationHierarchy
{
    public static string Path(string department, string? subdepartment = null, string? room = null) =>
        string.Join('/', new[] { department, subdepartment, room }.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>Conservative floor budget: required rooms, wall allowance and internal circulation.</summary>
    public static int RequiredFloorArea(DepartmentDefinition department)
    {
        long total = 0;
        foreach (var subdepartment in department.Subdepartments ?? [])
        {
            if (subdepartment == null) continue;
            long roomArea = 0;
            foreach (var room in subdepartment.Rooms ?? [])
            {
                if (room == null) continue;
                var area = Math.Clamp(room.MinArea, 0, 100000);
                var side = (int) Math.Ceiling(Math.Sqrt(area));
                roomArea += Math.Clamp(room.MinCount, 0, 32) * (area + 4L * side + 4);
            }
            total += Math.Max(Math.Max(0, subdepartment.MinArea), roomArea);
        }
        return total == 0 ? 0 : (int) Math.Min(int.MaxValue, Math.Ceiling(total * 1.2) + 12);
    }

    public static List<Diagnostic> Validate(StationGenerationConfig config) => StationHierarchyValidation.Validate(config);

    public static List<DepartmentDefinition> DefaultDepartments() =>
    [
        new() { Id = "Commons", PlacementOrder = 1, TopologyHub = true, Description = "Public station commons and shared circulation.", Width = 18, Height = 14, Color = "#c4af72", Entrances = 2 },
        new() { Id = "Arrivals", PlacementOrder = 0, Description = "Arrival docking and passenger reception.", Width = 14, Height = 10, Color = "#71b9ac", Exterior = true },
        new() { Id = "Service", PlacementOrder = 2, Description = "Civilian services; keep the group together when practical, permit satellites.", Width = 16, Height = 12, Color = "#9cb86a", PreferredNeighbors = ["Commons", "Cargo"], Subdepartments =
        [
            Sub("Kitchen", "Food preparation and food service.", 40, 65),
            Sub("Botany", "Hydroponics and kitchen crop supply.", 40, 65),
            Sub("Theatre", "Performance, costumes and public entertainment.", 30, 50)
        ] },
        new() { Id = "Medical", PlacementOrder = 3, Description = "Clinical care and emergency treatment.", Width = 16, Height = 12, Color = "#79b8cf", PreferredNeighbors = ["Commons"] },
        new() { Id = "Engineering", PlacementOrder = 5, Description = "Station power, repairs and atmosphere support.", Width = 18, Height = 12, Color = "#dfa45c", Access = "restricted", PreferredNeighbors = ["Cargo"], Subdepartments =
        [
            Sub("Atmos", "Atmospherics must be reachable from inside Engineering.", 75, 105, true,
                new RoomFeatureDefinition { Id = "TEG", Name = "Thermoelectric generator", Description = "Reserve the generator and working clearance, with access from Atmos and Engineering.", Kind = "feature", MinArea = 25, AccessibleFrom = ["Engineering", "Engineering/Atmos"] })
        ] },
        new() { Id = "Science", PlacementOrder = 4, Description = "Research and experimental work.", Width = 16, Height = 12, Color = "#b894d5", Access = "restricted" },
        new() { Id = "Security", PlacementOrder = 7, Description = "Secure detention, evidence and security operations.", Width = 14, Height = 12, Color = "#d67d83", Access = "secure", PreferredNeighbors = ["Command"], Grouping = "grouped", AllowSplit = false, Subdepartments =
        [
            Sub("Brig", "Security rooms connected by secured internal circulation.", 150, 240, true,
                Room("Armory", "Armory", "Secured weapons and equipment storage.", 25),
                Room("Perma", "Permanent detention", "Long-term secured detention and inmate living area.", 35),
                Room("Sentencing", "Sentencing room", "Custody processing and sentencing.", 20),
                Room("WardensOffice", "Warden's office", "Warden administration with access to detention.", 16),
                Room("Interrogation", "Interrogation room", "Private secured interview and interrogation.", 16))
        ] },
        new() { Id = "Cargo", PlacementOrder = 6, Description = "Cargo handling, logistics and salvage support.", Width = 18, Height = 12, Color = "#bc956b", Access = "restricted", Exterior = true, Subdepartments =
        [
            Sub("Salvage", "Salvage staging must be reachable from within Cargo.", 55, 80, true)
        ] },
        new() { Id = "Command", PlacementOrder = 8, Description = "Bridge, command offices and coordination.", Width = 12, Height = 10, Color = "#6d99d7", Access = "secure", Grouping = "grouped", AllowSplit = false, Subdepartments =
        [
            Sub("Operations", "Connected command rooms behind command access.", 160, 245, true,
                Room("Bridge", "Bridge", "Station command and status monitoring.", 36),
                Room("CaptainsRoom", "Captain's room", "Captain office and private quarters.", 25),
                Room("HoPOffice", "Head of Personnel's office", "Personnel and access administration.", 25),
                Room("MeetingRoom", "Meeting room", "Command and department coordination meetings.", 35))
        ] }
    ];

    private static SubdepartmentDefinition Sub(string id, string description, int minArea, int targetArea,
        bool internalAccess = false, params RoomFeatureDefinition[] rooms) => new()
    {
        Id = id, Name = id, Description = description, MinArea = minArea, TargetArea = targetArea,
        RequireInternalAccess = internalAccess, AllowSplit = !internalAccess, Rooms = [.. rooms]
    };

    private static RoomFeatureDefinition Room(string id, string name, string description, int minArea) =>
        new() { Id = id, Name = name, Description = description, MinArea = minArea };
}
