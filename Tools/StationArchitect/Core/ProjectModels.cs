using System.Text.Json.Serialization;

namespace Content.StationArchitect;

/// <summary>One tile in the station's integer planning grid.</summary>
public readonly record struct Cell(int X, int Y)
{
    public static readonly Cell[] Directions = [new(1, 0), new(0, 1), new(-1, 0), new(0, -1)];
    public static Cell operator +(Cell a, Cell b) => new(a.X + b.X, a.Y + b.Y);
    public static Cell operator -(Cell a, Cell b) => new(a.X - b.X, a.Y - b.Y);
    public int Distance(Cell other) => Math.Abs(X - other.X) + Math.Abs(Y - other.Y);
}

public readonly record struct GridBounds(int X, int Y, int Width, int Height)
{
    [JsonIgnore] public int Right => X + Width;
    [JsonIgnore] public int Bottom => Y + Height;
    [JsonIgnore] public int Area => Width * Height;
    [JsonIgnore] public Cell Center => new(X + Width / 2, Y + Height / 2);
    public bool Contains(Cell cell) => cell.X >= X && cell.Y >= Y && cell.X < Right && cell.Y < Bottom;
    public IEnumerable<Cell> Cells()
    {
        for (var y = Y; y < Bottom; y++)
        for (var x = X; x < Right; x++) yield return new(x, y);
    }
    public GridBounds Expand(int amount) => new(X - amount, Y - amount, Width + 2 * amount, Height + 2 * amount);
}

/// <summary>Portable design state. Runtime requirements are rebuilt from Definition, never trusted from saved reports.</summary>
public sealed class StationDesignProject
{
    public int Version { get; set; } = 1;
    public StationProgramDocument Definition { get; set; } = new();
    public DesignSettings Settings { get; set; } = new();
    public DesignSelection Selection { get; set; } = new();
    public string Stage { get; set; } = "program";
    public int Revision { get; set; }
    public DesignLayout Layout { get; set; } = new();
    public List<DesignStageState> StageStates { get; set; } = DesignStages.All.Select(key => new DesignStageState { Stage = key }).ToList();
    public DesignTreeNode? Tree { get; set; }
    public DesignReport? Report { get; set; }
    /// <summary>Recomputed structural gameplay observations; saved reports are never accepted as proof.</summary>
    public GameflowReport? Gameflow { get; set; }
    public NativeSystemsReport Systems { get; set; } = new();
}

public sealed class DesignSettings
{
    public int Seed { get; set; } = 14014;
    public int Width { get; set; } = 176;
    public int Height { get; set; } = 144;
    public string Topology { get; set; } = "ring";
    public int PublicWidth { get; set; } = 3;
    public int MaintenanceWidth { get; set; } = 2;
    public int MaintenanceMaxWidth { get; set; } = 3;
    public int DepartmentGap { get; set; } = 9;
    public int RoomGap { get; set; } = 1;
    public DesignPackingSettings Packing { get; set; } = new();
    public int EnvelopePadding { get; set; } = 8;
    public int GrowthIterations { get; set; } = 40000;
    public int OptimizationIterations { get; set; } = 5000;
    public int CandidateBudget { get; set; } = 4;
    public bool AutoSize { get; set; } = true;
    public int MaxRoomInstances { get; set; } = 4096;
    /// <summary>Optional footprint profiles; absent prototypes use one planning tile until a verified profile is supplied.</summary>
    public Dictionary<string, EntityFootprintSize> PrototypeFootprints { get; set; } = [];
    public DesignExportSettings Export { get; set; } = new();
    public List<DesignTransportProfile> TransportProfiles { get; set; } = DesignTransportDefaults.Profiles();
}

public sealed class EntityFootprintSize
{
    public int Width { get; set; } = 1;
    public int Height { get; set; } = 1;
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
}

public sealed class DesignExportSettings
{
    public string EngineVersion { get; set; } = "290.0.0";
    public string FloorTile { get; set; } = "FloorSteel";
    public string MaintenanceTile { get; set; } = "FloorTechMaint2";
    public string ExteriorTile { get; set; } = "Lattice";
    public string WallPrototype { get; set; } = "WallSolid";
    public string WindowPrototype { get; set; } = "Window";
    public string DoorPrototype { get; set; } = "Airlock";
    public string SpaceAirlockPrototype { get; set; } = "AirlockExternal";
    public string DockingPortPrototype { get; set; } = "AirlockShuttle";
    public string CounterPrototype { get; set; } = "TableCounterMetal";
    public string CablePrototype { get; set; } = "CableMV";
    public string PipePrototype { get; set; } = "GasPipeStraight";
    public string PipeBendPrototype { get; set; } = "GasPipeBend";
    public string PipeJunctionPrototype { get; set; } = "GasPipeTJunction";
    public string PipeCrossPrototype { get; set; } = "GasPipeFourway";
    public string StationPrototype { get; set; } = "StandardNanotrasenStation";
    public string LatejoinPrototype { get; set; } = "SpawnPointLatejoin";
    public bool IncludeRoomBeacons { get; set; } = true;
    public string BeaconPrototype { get; set; } = "DefaultStationBeacon";
    public Dictionary<string, List<string>> AccessTags { get; set; } = new(StringComparer.Ordinal)
    {
        ["public"] = [], ["engineering"] = ["Engineering"], ["atmospherics"] = ["Atmospherics"],
        ["medical"] = ["Medical"], ["science"] = ["Research"], ["security"] = ["Security"],
        ["command"] = ["Command"], ["captain"] = ["Captain"], ["armory"] = ["Armory"],
        ["detention"] = ["Brig"], ["detective"] = ["Detective"], ["evidence"] = ["Security"],
        ["lawyer"] = ["Lawyer"], ["cargo"] = ["Cargo"], ["salvage"] = ["Salvage"],
        ["maintenance"] = ["Maintenance"], ["ai"] = ["StationAi"]
    };
    /// <summary>Optional fallback for generic staff room labels, selected by authored owner ID.</summary>
    public Dictionary<string, List<string>> OwnerAccessTags { get; set; } = [];
    public Dictionary<string, int[]> Jobs { get; set; } = [];
}

/// <summary>Explicit author overrides; absent entries request the program's preferred count.</summary>
public sealed class DesignSelection
{
    public Dictionary<string, int> RoomCounts { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Planner-selected deviations from preferred counts, still bounded and subordinate to user overrides.</summary>
    public Dictionary<string, int> AutoRoomCounts { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Per-parent overrides keyed by parent runtime ID + '/' + child template ID.</summary>
    public Dictionary<string, int> ParentRoomCounts { get; set; } = new(StringComparer.Ordinal);
}

public sealed class DesignLayout
{
    public int PlacementAttempt { get; set; }
    public List<Cell> Envelope { get; set; } = [];
    public bool EnvelopeEdited { get; set; }
    public List<DepartmentPlacement> Departments { get; set; } = [];
    public List<DesignRoom> Rooms { get; set; } = [];
    public List<Cell> PublicCorridors { get; set; } = [];
    public List<Cell> Maintenance { get; set; } = [];
    public List<Cell> Walls { get; set; } = [];
    public List<Cell> Hull { get; set; } = [];
    public List<Cell> Windows { get; set; } = [];
    public List<DesignInterface> Interfaces { get; set; } = [];
    public List<DesignRoute> Routes { get; set; } = [];
    public List<DesignEntity> Equipment { get; set; } = [];
    public List<DesignUtility> Utilities { get; set; } = [];
    public List<DesignEnvironmentZone> EnvironmentZones { get; set; } = [];
    public List<Cell> ReservedSpace { get; set; } = [];
    /// <summary>Topology planning guides; routing must reach them through actual clear corridor cells.</summary>
    public List<Cell> PublicWaypoints { get; set; } = [];
    /// <summary>Ordered physical planning centerline; a ring repeats its first cell at the end.</summary>
    public List<Cell> PublicBackbone { get; set; } = [];
    public List<DesignTransportNetwork> TransportNetworks { get; set; } = [];
    public List<DesignDock> Docks { get; set; } = [];
    public List<DesignCirculationReservation> CirculationReservations { get; set; } = [];
    public List<DesignSystemDevice> SystemDevices { get; set; } = [];
    public List<DesignSystemNetwork> SystemNetworks { get; set; } = [];
    public List<DesignSystemControl> SystemControls { get; set; } = [];
    public bool PublicEdited { get; set; }
    public bool MaintenanceEdited { get; set; }
}

public sealed class DepartmentPlacement
{
    public string OwnerId { get; set; } = "";
    public Cell Anchor { get; set; }
    public GridBounds Bounds { get; set; }
    public List<Cell> Cells { get; set; } = [];
    public bool Pinned { get; set; }
    public bool Locked { get; set; }
}

/// <summary>Persistent empty space and exact boundary hardware anchor for one declared docking content instance.</summary>
public sealed class DesignDock
{
    public string Id { get; set; } = "";
    public string RoomId { get; set; } = "";
    public string RequirementId { get; set; } = "";
    public string ContentId { get; set; } = "";
    public int Instance { get; set; } = 1;
    public List<Cell> BoundaryCells { get; set; } = [];
    public Cell Outward { get; set; }
    public List<Cell> BerthCells { get; set; } = [];
    public List<Cell> ApproachCells { get; set; } = [];
    public string InterfaceId { get; set; } = "";
    public string EntityId { get; set; } = "";
    public bool Edited { get; set; }
    public bool Locked { get; set; }
}

/// <summary>Planning-only corridor entrance reservation; no floor or connectivity is implied by the record.</summary>
public sealed class DesignCirculationReservation
{
    public string Id { get; set; } = "";
    public string RoomId { get; set; } = "";
    public string Network { get; set; } = "publicHall";
    public List<Cell> BoundaryCells { get; set; } = [];
    public List<Cell> ApproachCells { get; set; } = [];
    public Cell Outward { get; set; }
    public int Width { get; set; } = 1;
    public bool Edited { get; set; }
    public bool Locked { get; set; }
}

public sealed class DesignRoom
{
    public string Id { get; set; } = "";
    public string DefinitionPath { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string Instance { get; set; } = "1";
    public string ParentInstanceId { get; set; } = "";
    public Cell Seed { get; set; }
    public bool HasSeed { get; set; }
    public bool Pinned { get; set; }
    public bool FloorLocked { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Retained stale geometry for a removed definition or protected count reduction; never silently reassigned.</summary>
    public bool Orphaned { get; set; }
    public int? TargetAreaOverride { get; set; }
    public List<Cell> Cells { get; set; } = [];
    public GridBounds Bounds { get; set; }
    public GridBounds Parcel { get; set; }
    public bool FloorEdited { get; set; }
    /// <summary>Precise planning allocation, distinct from grown floor and allowed to be nonrectangular.</summary>
    public List<Cell> GrowthReservation { get; set; } = [];
    /// <summary>Owned floor anchors that preserve planned exterior windows and access faces through growth.</summary>
    public List<Cell> FrontageFloor { get; set; } = [];
    /// <summary>Local exterior void reserved for those faces; translated with the room and included in ReservedSpace.</summary>
    public List<Cell> FrontageSpace { get; set; } = [];
    /// <summary>Gross room-complex allocation, containing this room, nested rooms and their boundaries.</summary>
    public List<Cell> ComplexReservation { get; set; } = [];
    public int? PlannedArea { get; set; }
    public string GrowthSide { get; set; } = "auto";
}

public sealed class DesignInterface
{
    public string Id { get; set; } = "";
    public string RequirementId { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Kind { get; set; } = "walking";
    public string Type { get; set; } = "doorway";
    public string Access { get; set; } = "public";
    public List<Cell> Cells { get; set; } = [];
    public int Width { get; set; } = 1;
    public Cell FromApproach { get; set; }
    public Cell ToApproach { get; set; }
    public bool Edited { get; set; }
}

public sealed class DesignRoute
{
    public string Id { get; set; } = "";
    public string RequirementId { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Mode { get; set; } = "department";
    public string Kind { get; set; } = "walking";
    public List<Cell> Cells { get; set; } = [];
    public List<string> InterfaceIds { get; set; } = [];
    public string TransportNetworkId { get; set; } = "";
    public bool Edited { get; set; }
    public bool Locked { get; set; }
}

public sealed class DesignEntity
{
    public string Id { get; set; } = "";
    public string RoomId { get; set; } = "";
    public string RequirementId { get; set; } = "";
    public string SystemRuleId { get; set; } = "";
    public string Prototype { get; set; } = "";
    public Cell Position { get; set; }
    public int Rotation { get; set; }
    public string Placement { get; set; } = "floor";
    public List<Cell> Footprint { get; set; } = [];
    public List<Cell> WorkArea { get; set; } = [];
    public string InterfaceId { get; set; } = "";
    public bool Pinned { get; set; }
}

public sealed class DesignUtility
{
    public string Id { get; set; } = "";
    public string RequirementId { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Type { get; set; } = "cable";
    public List<Cell> Cells { get; set; } = [];
    public bool Edited { get; set; }
}

/// <summary>Explicit atmosphere plan attached to actual floor cells, not evidence of an executed simulation.</summary>
public sealed class DesignEnvironmentZone
{
    public string Id { get; set; } = "";
    public string RoomId { get; set; } = "";
    public List<Cell> Cells { get; set; } = [];
    public string Pressure { get; set; } = "standard";
    public string Placement { get; set; } = "interior";
    public bool Sealed { get; set; } = true;
    public double? TemperatureKelvin { get; set; }
    public double? PressureKpa { get; set; }
    public Dictionary<string, double> GasFractions { get; set; } = [];
    public bool Edited { get; set; }
}

public sealed class DesignTreeNode
{
    public string Id { get; set; } = "";
    public string? ParentId { get; set; }
    public string DefinitionPath { get; set; } = "";
    public string Kind { get; set; } = "station";
    public string Name { get; set; } = "";
    public RequirementNode? Requirements { get; set; }
    public string? InstanceId { get; set; }
    public long FloorArea { get; set; }
    public long OwnFloorArea { get; set; }
    public FloorBudget SelectionFloorBudget { get; set; } = new(0, 0, 0);
    public int SelectedCount { get; set; }
    public bool Pinned { get; set; }
    public bool Locked { get; set; }
    public bool Orphaned { get; set; }
    public List<ProgramDiagnostic> Diagnostics { get; set; } = [];
    public List<DesignTreeNode> Children { get; set; } = [];
}

public sealed class DesignReport
{
    public bool Success => Diagnostics.All(d => d.Severity != "error");
    public bool Complete { get; set; }
    public string Stage { get; set; } = "program";
    public List<ProgramDiagnostic> Diagnostics { get; set; } = [];
    public PressureReport? Pressure { get; set; }
    public string Hash { get; set; } = "";
    public Dictionary<string, double> Metrics { get; set; } = [];
}

public sealed class DesignStageState
{
    public string Stage { get; set; } = "program";
    public string Status { get; set; } = "pending";
    public int? BuiltRevision { get; set; }
    public string Message { get; set; } = "";
}

public static class DesignStages
{
    public static readonly string[] All = ["program", "selection", "placement", "growth", "circulation", "gameflow", "contents", "systemsDevices", "power", "atmos", "disposals", "validation"];
    public static int Index(string stage) => Array.IndexOf(All, stage);
}

public sealed class DesignEdit
{
    public string Kind { get; set; } = "";
    public string NodeId { get; set; } = "";
    public Cell? Position { get; set; }
    public List<Cell> Cells { get; set; } = [];
    public int? Value { get; set; }
    public double? NumberValue { get; set; }
    public string? TextValue { get; set; }
    public bool? Enabled { get; set; }
    public string Layer { get; set; } = "";
}
