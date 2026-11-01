using System.Text.Json;
using System.Text.Json.Serialization;

namespace Content.StationArchitect;

/// <summary>A new room-first authoring format; unrelated to the first tool's geometry/project format.</summary>
public sealed class StationProgramDocument
{
    public int SchemaVersion { get; set; } = 2;
    public string Id { get; set; } = "Station";
    public string Name { get; set; } = "Station program";
    public string Description { get; set; } = "";
    public List<string> SourceRefs { get; set; } = [];
    public PressureSettings Settings { get; set; } = new();
    public List<DepartmentProgram> Departments { get; set; } = [];
    public List<ConnectionRequirement> Connections { get; set; } = [];
    public List<RoomChoiceRequirement> Choices { get; set; } = [];
    public MaintenanceProgram Maintenance { get; set; } = new();
    public GameflowProgram Gameflow { get; set; } = new();
    public SystemsProgram Systems { get; set; } = new();
}

public sealed class DepartmentProgram
{
    public string Id { get; set; } = "Department";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> SourceRefs { get; set; } = [];
    public string Color { get; set; } = "#88aabb";
    public List<string> Tags { get; set; } = [];
    public AreaRequirement Area { get; set; } = new() { Min = 16, Target = 120, Max = 500 };
    public ShapeRequirement Shape { get; set; } = new();
    public SplitRequirement Split { get; set; } = new();
    public List<RoomDefinition> Rooms { get; set; } = [];
}

public sealed class RoomDefinition
{
    public string Id { get; set; } = "Room";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> SourceRefs { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    /// <summary>activity or filler. Both own real floor; filler may support internal circulation.</summary>
    public string Role { get; set; } = "activity";
    public CountRequirement Count { get; set; } = new();
    public AreaRequirement Area { get; set; } = new();
    public ShapeRequirement Shape { get; set; } = new();
    public List<ConnectionRequirement> Connections { get; set; } = [];
    public ExteriorRequirement Exterior { get; set; } = new();
    public AccessRequirement Access { get; set; } = new();
    public List<RoomContentRequirement> Contents { get; set; } = [];
    public RoomEnvironmentRequirement Environment { get; set; } = new();
    public bool AllowThroughTraffic { get; set; }
    /// <summary>Nested templates are instantiated separately for each parent room instance.</summary>
    public List<RoomDefinition> Rooms { get; set; } = [];
    public RoomPlacementPolicy Placement { get; set; } = new();
    public RoomNestingRequirement Nesting { get; set; } = new();
    /// <summary>Optional budget for exclusive own plus descendant usable floor, counted once.</summary>
    public AreaRequirement? ComplexArea { get; set; }
    public ShapeRequirement? ComplexShape { get; set; }
}

public sealed class RoomPlacementPolicy
{
    public string Policy { get; set; } = "grouped";
    public string Anchor { get; set; } = "any";
    /// <summary>Optional author-defined cluster shared by rooms with distributed placement.</summary>
    public string Group { get; set; } = "";
}

public sealed class RoomNestingRequirement
{
    public bool Internal { get; set; } = true;
    public bool RequireParentConnection { get; set; } = true;
    public bool AllowExternalEntry { get; set; }
}

/// <summary>Usable floor only; all instances are measured separately. Min/max are final hard limits.</summary>
public sealed class AreaRequirement
{
    public int Min { get; set; } = 9;
    public int Target { get; set; } = 25;
    public int Max { get; set; } = 100;
    public double Weight { get; set; } = 1;
}

public sealed class CountRequirement
{
    public int Min { get; set; } = 1;
    public int Target { get; set; } = 1;
    public int Max { get; set; } = 1;
    public double Weight { get; set; } = 1;
}

public sealed class ShapeRequirement
{
    /// <summary>Long/short side of axis-aligned floor bounding box; 1 is square.</summary>
    public double MaxAspectRatio { get; set; } = 3;
    /// <summary>Usable owned floor / bounding box area. Prevents long snakes inside square boxes.</summary>
    public double MinFillRatio { get; set; } = .4;
    public double Weight { get; set; } = 1;
}

public sealed class SplitRequirement
{
    public bool Allow { get; set; } = true;
    public int MaxComponents { get; set; } = 3;
    public double ComponentPenalty { get; set; } = 1000;
    public double HallCrossingPenalty { get; set; } = 2000;
    public double MaintenanceCrossingPenalty { get; set; } = 1500;
}

public sealed class ExteriorRequirement
{
    public int MinSpaceFrontage { get; set; }
    /// <summary>Space frontage is sealed wall/window boundary; this flag permits exterior frontage, never open vacuum floor.</summary>
    public bool AllowSpaceExposure { get; set; } = true;
    /// <summary>Shuttle berths bind exact space-boundary contents; their counts come from those content requirements.</summary>
    public List<DockRequirement> Docks { get; set; } = [];
}

public sealed class DockRequirement
{
    public string Id { get; set; } = "Dock";
    public string ContentId { get; set; } = "DockAirlock";
    /// <summary>Unpadded shuttle envelope: tangent width and outward depth, measured in tiles.</summary>
    public int BerthWidth { get; set; } = 15;
    public int BerthDepth { get; set; } = 9;
    /// <summary>Shift from the centered tangent span; tangent=(-outward.Y,outward.X).</summary>
    public int BerthOffset { get; set; }
    public int ApproachLength { get; set; } = 16;
    /// <summary>Zero uses the berth's width plus lateral clearance; otherwise at least that wide.</summary>
    public int ApproachWidth { get; set; }
    public int Clearance { get; set; } = 1;
    public List<string> Directions { get; set; } = ["north", "east", "south", "west"];
    public List<string> SourceRefs { get; set; } = [];
}

public sealed class AccessRequirement
{
    /// <summary>An author-defined label; new access levels do not require code changes.</summary>
    public string Level { get; set; } = "public";
    public bool AllowPublicEntry { get; set; } = true;
    public bool AllowMaintenanceEntry { get; set; } = true;
    /// <summary>Eligible owners for direct room-to-room entries; empty permits any declared owner. Hall and passage entries use their own flags.</summary>
    public List<string> EntryOwners { get; set; } = [];
    /// <summary>Each named network must be traversed on every approach from public or foreign-owner station floor.</summary>
    public List<string> RequireNetworkApproach { get; set; } = [];
}

/// <summary>A functional equipment family. Any listed prototype may fulfill its bounded count; MinArea reserves usable footprint and working floor per instance.</summary>
public sealed class RoomContentRequirement
{
    public string Id { get; set; } = "Equipment";
    public List<string> Prototypes { get; set; } = [];
    public CountRequirement Count { get; set; } = new();
    public int MinArea { get; set; }
    /// <summary>floor, any boundary, or specifically a boundary facing space.</summary>
    public string Placement { get; set; } = "floor";
    /// <summary>Optional physical boundary interface supplied by this equipment; empty means ordinary equipment.</summary>
    public string InterfaceType { get; set; } = "";
}

public sealed class RoomEnvironmentRequirement
{
    public string Pressure { get; set; } = "standard";
    public string Placement { get; set; } = "interior";
    public bool Sealed { get; set; } = true;
    public double? MinTemperatureKelvin { get; set; }
    public double? MaxTemperatureKelvin { get; set; }
}

/// <summary>Cardinality of distinct active room templates, independent of room-instance count and route requirements.</summary>
public sealed class RoomChoiceRequirement
{
    public string Id { get; set; } = "Choice";
    public string Description { get; set; } = "";
    public List<string> Rooms { get; set; } = [];
    public CountRequirement Count { get; set; } = new();
}

public sealed class ConnectionRequirement
{
    public string Id { get; set; } = "Connection";
    /// <summary>Blank only inside a room: inferred as that room. Otherwise canonical Department[/Room].</summary>
    public string From { get; set; } = "";
    public List<string> To { get; set; } = [];
    public string Selection { get; set; } = "all";
    public string Priority { get; set; } = "required";
    public double Weight { get; set; } = 1;
    /// <summary>ifPresent skips absent target templates; requireTarget activates targets while the source exists.</summary>
    public string Presence { get; set; } = "ifPresent";
    public List<string> Modes { get; set; } = ["direct", "department"];
    public int? MaxDistance { get; set; }
    public string Description { get; set; } = "";
    /// <summary>Only walking connections provide crew movement; interaction and utility connections cannot substitute for them.</summary>
    public string Kind { get; set; } = "walking";
    public List<string> InterfaceTypes { get; set; } = [];
    public int MinWidth { get; set; }
}

public sealed class PressureSettings
{
    public double RoomPressureWeight { get; set; } = 1;
    public double DepartmentPressureWeight { get; set; } = 1;
    public double ConnectionPressureWeight { get; set; } = 1;
}

public sealed class PassageWidthRequirement
{
    public int Min { get; set; } = 2;
    public int Max { get; set; } = 3;
}

public sealed class MaintenanceProgram
{
    public PassageWidthRequirement PassageWidth { get; set; } = new();
    public MaintenanceConnectivityPolicy Connectivity { get; set; } = new();
    /// <summary>True maintenance room floor only. Passage area is not a room and is not included.</summary>
    public AreaRequirement Area { get; set; } = new() { Min = 0, Target = 0, Max = 10000 };
    public List<RoomDefinition> Rooms { get; set; } = [];
}

public sealed class MaintenanceConnectivityPolicy
{
    /// <summary>continuous requires a maintenance-only component; controlled requires explicit station access for each measured component.</summary>
    public string Policy { get; set; } = "continuous";
    public int MaxComponents { get; set; } = 1;
    public double ComponentPenalty { get; set; } = 100;
    public bool AllowPublicTransitions { get; set; } = true;
    public string TransitionAccess { get; set; } = "maintenance";
    public int MinTransitionsPerComponent { get; set; } = 1;
}

public sealed record ProgramDiagnostic(string Path, string Code, string Message, string Severity = "error");

public static class ProgramJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}
