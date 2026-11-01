namespace Content.StationArchitect;

/// <summary>Author-defined spatial gameplay heuristics. These are planning observations, never simulated balance.</summary>
public sealed class GameflowProgram
{
    public List<GameflowAccessProfile> AccessProfiles { get; set; } = [new()];
    public List<GameflowRule> Rules { get; set; } = [];
    public GameflowSettings Settings { get; set; } = new();
}

public sealed class GameflowAccessProfile
{
    public string Id { get; set; } = "public";
    public string Description { get; set; } = "";
    /// <summary>levels respects the listed authored access labels; allDoors explicitly assumes access to every physical walking door.</summary>
    public string AccessPolicy { get; set; } = "levels";
    public List<string> AllowedLevels { get; set; } = ["public"];
    public List<string> Networks { get; set; } = ["publicHall"];
    public bool AllowExterior { get; set; }
    public bool AllowCrewTubes { get; set; }
    public int DoorPenalty { get; set; }
    public int CrewTubeCost { get; set; } = 8;
}

public sealed class GameflowRule
{
    public string Id { get; set; } = "Rule";
    public string Description { get; set; } = "";
    public List<string> SourceRefs { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public string Kind { get; set; } = "chaseLoops";
    public string Priority { get; set; } = "desired";
    public double Weight { get; set; } = 1;
    public string AccessProfile { get; set; } = "public";
    public string Aggregation { get; set; } = "max";
    public GameflowSelector From { get; set; } = new() { Networks = ["publicHall"] };
    public GameflowSelector To { get; set; } = new();
    public GameflowRange Bounds { get; set; } = new() { Min = 0, Target = 1 };
    public GameflowPathPolicy Routes { get; set; } = new();
}

/// <summary>Room filter dimensions combine according to Match; Networks adds named passage targets. Empty selectors select all eligible rooms.</summary>
public sealed class GameflowSelector
{
    public List<string> Paths { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<string> Owners { get; set; } = [];
    public List<string> Roles { get; set; } = [];
    public List<string> Networks { get; set; } = [];
    public string Match { get; set; } = "any";
}

public sealed class GameflowRange
{
    public double? Min { get; set; }
    public double? Target { get; set; }
    public double? Max { get; set; }
}

public sealed class GameflowPathPolicy
{
    public int MinSeparation { get; set; } = 4;
    public int EndpointRadius { get; set; } = 5;
    public int MinPathLength { get; set; } = 12;
    public int MinLoopLength { get; set; } = 32;
    public int MinEnclosedBlockedArea { get; set; } = 12;
    public double MaxDetourRatio { get; set; } = 4;
    public int ChokepointWidth { get; set; } = 3;
    public int DeduplicationRadius { get; set; } = 6;
}

public sealed class GameflowSettings
{
    public int MaxGraphCells { get; set; } = 65536;
    public int MaxSearchVisits { get; set; } = 100000;
    public int MaxTotalSearchVisits { get; set; } = 1000000;
    public int MaxPairTests { get; set; } = 64;
    public int MaxTerminals { get; set; } = 64;
    public int MaxWitnesses { get; set; } = 32;
    public int MaxWitnessCells { get; set; } = 4096;
    public int MaxIndependentRoutes { get; set; } = 8;
    public bool AllowRepairs { get; set; } = true;
    public int RepairBudget { get; set; } = 2;
    public int CandidateBudget { get; set; } = 12;
    public int MaxRepairLength { get; set; } = 48;
    public List<string> RepairNetworks { get; set; } = ["publicHall", "maintenance"];
}

public sealed class GameflowReport
{
    public bool Success => Diagnostics.All(issue => issue.Severity != "error");
    public bool Complete { get; set; }
    public double TotalCost { get; set; }
    public string GeometryHash { get; set; } = "";
    public string DefinitionHash { get; set; } = "";
    public List<string> Assumptions { get; set; } = [];
    public List<GameflowRuleResult> Rules { get; set; } = [];
    public List<GameflowRepair> Repairs { get; set; } = [];
    public List<ProgramDiagnostic> Diagnostics { get; set; } = [];
}

public sealed class GameflowRuleResult
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string AccessProfile { get; set; } = "";
    public bool Observed { get; set; }
    public bool Satisfied { get; set; }
    public bool TargetMet { get; set; }
    public bool CoverageComplete { get; set; }
    public double? Value { get; set; }
    public double Cost { get; set; }
    public int PairsTested { get; set; }
    public int PairsTotal { get; set; }
    public List<string> Assumptions { get; set; } = [];
    public List<GameflowWitness> Witnesses { get; set; } = [];
    public List<GameflowRepairHint> RepairHints { get; set; } = [];
    public List<ProgramDiagnostic> Diagnostics { get; set; } = [];
}

public sealed class GameflowWitness
{
    public string Kind { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string AccessProfile { get; set; } = "";
    public List<string> Assumptions { get; set; } = [];
    public List<Cell> Path { get; set; } = [];
    public List<Cell> AlternatePath { get; set; } = [];
    public List<Cell> CutCells { get; set; } = [];
    public List<Cell> EnclosedBlockedCells { get; set; } = [];
    public int Length { get; set; }
    public int AlternateLength { get; set; }
    public int TravelCost { get; set; }
    public int AlternateTravelCost { get; set; }
    public int Separation { get; set; }
    public int EnclosedBlockedArea { get; set; }
    public bool GeometryTruncated { get; set; }
    public List<string> CrewTubeNetworks { get; set; } = [];
}

public sealed class GameflowRepairHint
{
    public string Network { get; set; } = "publicHall";
    public Cell From { get; set; }
    public Cell To { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class GameflowRepair
{
    public string Network { get; set; } = "";
    public List<Cell> Centerline { get; set; } = [];
    public List<Cell> CellsAdded { get; set; } = [];
    public double CostBefore { get; set; }
    public double CostAfter { get; set; }
    public List<string> RuleIds { get; set; } = [];
}
