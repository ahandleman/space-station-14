using System.Text.Json;
using System.Text.Json.Serialization;

namespace Content.StationGeneration;

public readonly record struct Cell(int X, int Y)
{
    public static Cell operator +(Cell a, Cell b) => new(a.X + b.X, a.Y + b.Y);
    public static Cell operator *(Cell a, int n) => new(a.X * n, a.Y * n);
    public int Distance(Cell other) => Math.Abs(X - other.X) + Math.Abs(Y - other.Y);
    public static readonly Cell[] Directions = [new(1, 0), new(0, 1), new(-1, 0), new(0, -1)];
}

/// <summary>Half-open interior bounds. The one-tile expanded bounds reserve walls.</summary>
public readonly record struct Rect(int X, int Y, int Width, int Height)
{
    [JsonIgnore] public int Right => X + Width;
    [JsonIgnore] public int Bottom => Y + Height;
    [JsonIgnore] public int Area => Width * Height;
    [JsonIgnore] public Cell Center => new(X + Width / 2, Y + Height / 2);
    public bool Contains(Cell p) => p.X >= X && p.Y >= Y && p.X < Right && p.Y < Bottom;
    public bool Intersects(Rect b) => X < b.Right && b.X < Right && Y < b.Bottom && b.Y < Bottom;
    public Rect Expand(int n) => new(X - n, Y - n, Width + 2 * n, Height + 2 * n);
    public IEnumerable<Cell> Cells()
    {
        for (var y = Y; y < Bottom; y++)
        for (var x = X; x < Right; x++)
            yield return new Cell(x, y);
    }
}

public sealed partial class DepartmentDefinition
{
    public string Id { get; set; } = "Department";
    public string Description { get; set; } = "";
    public int Width { get; set; } = 14;
    public int Height { get; set; } = 10;
    public int MinArea { get; set; } = 40;
    public int MaxArea { get; set; } = 1200;
    public string Access { get; set; } = "public";
    public bool Exterior { get; set; }
    public int Entrances { get; set; } = 1;
    public string Color { get; set; } = "#6e90b0";
    public List<string> RequiredConnections { get; set; } = [];
    public List<string> PreferredNeighbors { get; set; } = [];
}

public sealed class ScoreWeights
{
    public double Distance { get; set; } = 1;
    public double Compactness { get; set; } = 0.015;
    public double Corridor { get; set; } = 0.2;
    public double Turns { get; set; } = 2;
}

public sealed partial class StationGenerationConfig
{
    public int Seed { get; set; } = 14014;
    public int Width { get; set; } = 144;
    public int Height { get; set; } = 112;
    public string Archetype { get; set; } = "ring";
    public int CorridorWidth { get; set; } = 3;
    public int CandidateBudget { get; set; } = 4;
    public int OptimizationSteps { get; set; } = 150;
    public int MaintenanceWidth { get; set; } = 2;
    public int HullThickness { get; set; } = 1;
    // Defaults used when authoring new dependencies; existing per-edge modes are explicit.
    public bool DefaultAllowDisposalShortcuts { get; set; }
    public bool DefaultAllowMailNetwork { get; set; }
    public ScoreWeights Weights { get; set; } = new();
    public SpatialPlanningSettings Spatial { get; set; } = new();
    public List<DepartmentDefinition> Departments { get; set; } = Defaults();

    private static List<DepartmentDefinition> Defaults() => StationHierarchy.DefaultDepartments();
}

/// <summary>
/// A design dependency, distinct from the eventual physical route that satisfies it.
/// Priority is authoritative. Hard remains a version-1 import/export compatibility alias.
/// </summary>
public sealed record TopologyEdge
{
    private string? _priority;
    private bool _legacyHard = true;

    [JsonConstructor]
    public TopologyEdge() { }

    public TopologyEdge(string From, string To, bool Hard = true, double Weight = 1)
    {
        this.From = From;
        this.To = To;
        Priority = Hard ? "required" : "desired";
        this.Weight = Weight;
    }

    public string Id { get; set; } = "";
    // Missing provenance in imported projects means authored/custom, protecting old edits.
    public string Origin { get; set; } = "custom";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Priority
    {
        get => _priority ?? (_legacyHard ? "required" : "desired");
        set => _priority = value;
    }
    public bool Hard
    {
        get => Priority == "required";
        set => _legacyHard = value;
    }
    public double Weight { get; set; } = 1;
    public string Description { get; set; } = "";
    public string Relation { get; set; } = "walking";
    public string Purpose { get; set; } = "crew";
    public List<string> AllowedModes { get; set; } = ["publicHall"];
    public string Direction { get; set; } = "both";
    public bool StrictAvoidance { get; set; }
    // Required/Desired: upper route target. Avoided: separation radius to prefer.
    public int? MaxDistance { get; set; }
}

public sealed class StationTopology
{
    public List<string> Nodes { get; set; } = [];
    public List<TopologyEdge> Edges { get; set; } = [];
    public Dictionary<string, Cell> NodePositions { get; set; } = new(StringComparer.Ordinal);
    public List<string> SuppressedTemplateIds { get; set; } = [];
}

public sealed class PlacedDepartment
{
    public string Id { get; set; } = "";
    public Rect Bounds { get; set; }
    public int Rotation { get; set; }
    // -1 chooses legal sides automatically. 0=east, 1=south, 2=west, 3=north.
    public int EntranceSide { get; set; } = -1;
}

public sealed class DoorPort
{
    public string Department { get; set; } = "";
    public int Side { get; set; }
    public int Width { get; set; }
    public string Access { get; set; } = "public";
    public Cell Center { get; set; }
    public Cell Approach { get; set; }
    public List<Cell> Cells { get; set; } = [];
    public List<Cell> Throat { get; set; } = [];
}

public sealed class RoutedConnection
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public List<Cell> Centerline { get; set; } = [];
    public int Turns { get; set; }
}

/// <summary>Explicit, disjoint tile ownership layers; empty cells are space, not maintenance.</summary>
public sealed class StationLayout
{
    public List<PlacedDepartment> Departments { get; set; } = [];
    public List<DoorPort> Doors { get; set; } = [];
    public List<Cell> PublicCorridors { get; set; } = [];
    public List<Cell> Maintenance { get; set; } = [];
    public List<Cell> Hull { get; set; } = [];
    public List<Cell> ReservedExterior { get; set; } = [];
    public List<RoutedConnection> Routes { get; set; } = [];
}

public sealed record Diagnostic(string Stage, string Code, string Message, string Severity = "error");

public sealed class LayoutMetrics
{
    public int InteriorArea { get; set; }
    public int PublicArea { get; set; }
    public int MaintenanceArea { get; set; }
    public int HullArea { get; set; }
    public int FilledArea { get; set; }
    public int LongestEntranceTravel { get; set; }
    public int TopologyBridges { get; set; }
    public int TopologyArticulations { get; set; }
    public int CirculationArticulations { get; set; }
    public int RouteTurns { get; set; }
    public double Score { get; set; }
    public string Hash { get; set; } = "";
    public int EnvelopeArea { get; set; }
    public int StructuralArea { get; set; }
    public int UnallocatedArea { get; set; }
    public int NonRectangularRegions { get; set; }
    public int MaintenanceAccessPoints { get; set; }
    public int DeepMaintenanceRooms { get; set; }
    public int CommonMaintenanceRooms { get; set; }
    public int SolarInstallations { get; set; }
    public int MaintenanceSpaceAccess { get; set; }
    public int SubdepartmentAreas { get; set; }
    public int PlannedRoomFeatures { get; set; }
    public int TouchingDepartmentPairs { get; set; }
    public Dictionary<string, int> ExteriorFrontageByDepartment { get; set; } = new(StringComparer.Ordinal);
}

public sealed class GenerationResult
{
    public bool Success { get; set; }
    public int Seed { get; set; }
    public int Attempts { get; set; }
    public List<Diagnostic> Diagnostics { get; set; } = [];
    public LayoutMetrics Metrics { get; set; } = new();
}

public sealed class StationProject
{
    public int Version { get; set; } = 1;
    public StationGenerationConfig Config { get; set; } = new();
    public StationTopology? Topology { get; set; }
    public StationLayout? Layout { get; set; }
    public StationSpatialPlan? SpatialPlan { get; set; }
    public string Stage { get; set; } = "specification";
    public GenerationResult? Result { get; set; }
}

public sealed class SpatialPlanningSettings
{
    /// <summary>Atomic growth parcel size. Whole parcels prevent one-tile territory tendrils.</summary>
    public int CellSize { get; set; } = 5;
    public double FillRatio { get; set; } = 0.78;
    public double MaintenanceFraction { get; set; } = 0.30;
    public int MaintenancePassageMaxWidth { get; set; } = 3;
    public int DepartmentBoundaryMaxDepth { get; set; } = 1;
    public string MaintenancePocketPreference { get; set; } = "departments";
    public int DeepMaintenanceMin { get; set; } = 2;
    public int DeepMaintenanceMax { get; set; } = 4;
    public int DeepMaintenanceRoomMinArea { get; set; } = 9;
    public int DeepMaintenanceRoomTargetArea { get; set; } = 49;
    public int DeepMaintenanceRoomMaxArea { get; set; } = 300;
    public int CommonMaintenanceMin { get; set; } = 0;
    public int CommonMaintenanceMax { get; set; } = 8;
    public int CommonMaintenanceRoomMinArea { get; set; } = 9;
    public int CommonMaintenanceRoomTargetArea { get; set; } = 49;
    public int CommonMaintenanceRoomMaxArea { get; set; } = 300;
    public int SolarMin { get; set; } = 2;
    public int SolarMax { get; set; } = 4;
    public int SpaceAccessMin { get; set; } = 2;
    public int SpaceAccessMax { get; set; } = 3;
    public int GrowthSteps { get; set; } = 100000;
}

public sealed class DepartmentSeed
{
    public string Id { get; set; } = "";
    public Cell Position { get; set; }
    public int TargetArea { get; set; }
    public bool Pinned { get; set; }
    public bool AllowMaintenanceAccess { get; set; } = true;
    /// <summary>Constructive room-fit kernel. Growth recomputes it after incompatible seed edits.</summary>
    public List<Cell> CoreFootprint { get; set; } = [];
}

public sealed class DepartmentRegion
{
    public string Id { get; set; } = "";
    public Cell Seed { get; set; }
    public int TargetArea { get; set; }
    /// <summary>Authoritative region ownership, including the boundary allowance.</summary>
    public List<Cell> Territory { get; set; } = [];
    /// <summary>Derived walkable department floor; walls and ports are separate layers.</summary>
    public List<Cell> Cells { get; set; } = [];
}

public sealed class SpatialPort
{
    public string Department { get; set; } = "";
    public string? OtherDepartment { get; set; }
    public string Kind { get; set; } = "public";
    public Cell Center { get; set; }
    public List<Cell> Cells { get; set; } = [];
    public int Width { get; set; }
    public string Access { get; set; } = "public";
}

/// <summary>
/// Station-shaped planning masks, independent of rectangular debug layouts. Envelope
/// cells have explicit ownership: department territory, public/service reservation,
/// or structural reserve. Floor, wall, port and hull layers are derived separately.
/// </summary>
public sealed partial class StationSpatialPlan
{
    public int CellSize { get; set; } = 5;
    public int PlacementAttempts { get; set; } = 1;
    public bool EnvelopeEdited { get; set; }
    public List<Cell> Envelope { get; set; } = [];
    public List<DepartmentSeed> Seeds { get; set; } = [];
    public List<DepartmentRegion> Regions { get; set; } = [];
    public List<Cell> PublicReservations { get; set; } = [];
    public List<Cell> ServiceReservations { get; set; } = [];
    public List<Cell> InitialServiceReservations { get; set; } = [];
    public List<Cell> PublicCorridors { get; set; } = [];
    public List<Cell> Maintenance { get; set; } = [];
    public List<Cell> Walls { get; set; } = [];
    public List<Cell> Hull { get; set; } = [];
    public List<Cell> ReservedExterior { get; set; } = [];
    public List<Cell> ExteriorBoundary { get; set; } = [];
    public List<Cell> StructuralReserve { get; set; } = [];
    public List<SpatialPort> Ports { get; set; } = [];
    public List<DeepMaintenanceRoom> DeepMaintenanceRooms { get; set; } = [];
    public List<CommonMaintenanceRoom> CommonMaintenanceRooms { get; set; } = [];
    public List<SolarInstallation> SolarInstallations { get; set; } = [];
    public List<MaintenanceSpaceAccess> MaintenanceSpaceAccess { get; set; } = [];
    public string Stage { get; set; } = "placement";
}

/// <summary>Enclosed service room. Its only planned door opens into maintenance.</summary>
public sealed class DeepMaintenanceRoom
{
    public string Id { get; set; } = "";
    public List<Cell> Cells { get; set; } = [];
    public List<Cell> Walls { get; set; } = [];
    public List<Cell> PortCells { get; set; } = [];
    public Cell MaintenanceApproach { get; set; }
}

/// <summary>External panel footprint and access catwalk, joined to maintenance through a planned hull airlock.</summary>
public sealed class SolarInstallation
{
    public string Id { get; set; } = "";
    public List<Cell> Cells { get; set; } = [];
    public List<Cell> Catwalk { get; set; } = [];
    public List<Cell> ConnectionCells { get; set; } = [];
    public Cell MaintenanceApproach { get; set; }
}

/// <summary>A planned exterior maintenance frontage; barriers stay closed until detailed map construction.</summary>
public sealed class MaintenanceSpaceAccess
{
    public string Id { get; set; } = "";
    public Cell MaintenanceApproach { get; set; }
    public List<Cell> BarrierCells { get; set; } = [];
    public List<Cell> ExteriorCells { get; set; } = [];
    public string Barrier { get; set; } = "window";
}

public static class ProjectJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.Strict
    };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(Serialize(value), Options)!;
}

/// <summary>Specified PRNG, independent of System.Random implementation versions.</summary>
internal sealed class SeedRandom(int seed, int attempt = 0)
{
    private uint _state = unchecked((uint) seed * 747796405u + (uint) attempt * 2891336453u + 277803737u) | 1u;
    public int Next(int max)
    {
        _state ^= _state << 13;
        _state ^= _state >> 17;
        _state ^= _state << 5;
        return (int) (_state % (uint) max);
    }
    public void Shuffle<T>(IList<T> values)
    {
        for (var i = values.Count - 1; i > 0; i--)
        {
            var j = Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }
}
