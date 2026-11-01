using System.Text.Json;

namespace Content.StationArchitect;

/// <summary>Optional native infrastructure requirements. Old programs remain a no-op.</summary>
public sealed class SystemsProgram
{
    public bool Enabled { get; set; }
    public SystemGenerationSettings Generation { get; set; } = new();
    public List<NativeSystemProfile> Profiles { get; set; } = [];
    public List<SystemDeviceRule> Devices { get; set; } = [];
    public List<SystemNetworkRule> Networks { get; set; } = [];
    public List<SystemControlRule> Controls { get; set; } = [];
}

public sealed class SystemGenerationSettings
{
    public int MaxStatesPerConnection { get; set; } = 50000;
    public int MaxTotalSearchStates { get; set; } = 2000000;
    public int MaxGeneratedSegments { get; set; } = 65536;
    public int MaxDeviceCandidatesPerScope { get; set; } = 4096;
    public int MaxDevices { get; set; } = 4096;
    public int MaxWitnessSteps { get; set; } = 32;
}

public sealed class SystemRoomSelector
{
    /// <summary>Exact template paths or a prefix ending in /*. Nonempty filters combine with AND.</summary>
    public List<string> Paths { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<string> Roles { get; set; } = [];
    public List<string> Pressures { get; set; } = [];
    public List<string> Placements { get; set; } = [];
    /// <summary>Empty selects real rooms. Explicit values select the union of rooms, publicHall, maintenance and exteriorMaintenance.</summary>
    public List<string> Networks { get; set; } = [];
    public List<string> InterfaceTypes { get; set; } = [];
    public bool IncludeNested { get; set; } = true;
}

public sealed class SystemDeviceRule
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public SystemRoomSelector Selector { get; set; } = new();
    /// <summary>room, compartment, cluster, owner or station; count applies per selected scope.</summary>
    public string Scope { get; set; } = "room";
    public string ProfileId { get; set; } = "";
    public List<string> ProfileIds { get; set; } = [];
    public string Role { get; set; } = "";
    public RoomContentRequirement Content { get; set; } = new();
    public bool BindExisting { get; set; }
    public bool BindInterfaces { get; set; }
    public bool BindTransport { get; set; }
    public List<string> ContentIds { get; set; } = [];
    public double? MaxCoverageDistance { get; set; }
    public List<string> SourceRefs { get; set; } = [];
}

public sealed class NativeSystemProfile
{
    public string Id { get; set; } = "";
    public string Prototype { get; set; } = "";
    /// <summary>device, cable, pipe or disposal. Port behavior follows native node types.</summary>
    public string Kind { get; set; } = "device";
    public List<string> RequiredComponents { get; set; } = [];
    public List<NativeSystemPort> Ports { get; set; } = [];
    public List<NativeSystemTransfer> Transfers { get; set; } = [];
    public List<NativeSystemSource> Sources { get; set; } = [];
    public bool RequiresPower { get; set; }
    /// <summary>Null derives legacy native component facts; explicit values describe whether entry hardware can accept crew. Item-only mail is false.</summary>
    public bool? CarriesCrew { get; set; }
    /// <summary>auto preserves ordinary placement exclusivity; explicit native layers can represent subfloor or ceiling hardware.</summary>
    public string OccupancyLayer { get; set; } = "auto";
    public bool BlocksCrew { get; set; } = true;
    /// <summary>Explicit native component data, merged by the native map loader with the chosen prototype.</summary>
    public Dictionary<string, JsonElement> Components { get; set; } = new(StringComparer.Ordinal);
    public List<string> SourceRefs { get; set; } = [];
}

public sealed class NativeSystemPort
{
    public string Id { get; set; } = "";
    /// <summary>power, gas or disposal.</summary>
    public string Kind { get; set; } = "power";
    /// <summary>cable, cableDevice, terminal, terminalPort, pipe, pipePort, portable, rangeProvider, rangeReceiver or disposal.</summary>
    public string Connection { get; set; } = "cable";
    public string NodeGroup { get; set; } = "";
    public int PipeLayer { get; set; }
    /// <summary>Native unrotated directions, ordered for disposal exit priority; South is rotation zero.</summary>
    public List<string> Directions { get; set; } = [];
    public Cell Offset { get; set; }
    public double Range { get; set; }
    public double VolumeLitres { get; set; }
}

public sealed class NativeSystemTransfer
{
    public string FromPort { get; set; } = "";
    public string ToPort { get; set; } = "";
    public bool Bidirectional { get; set; }
    public bool RequiresPower { get; set; }
}

public sealed class NativeSystemSource
{
    public string PortId { get; set; } = "";
    /// <summary>power, gas or gasSink. These facts must match exported native component data and repository prototype audit.</summary>
    public string Kind { get; set; } = "power";
    public double SupplyWatts { get; set; }
    public double StoredEnergyJoules { get; set; }
    public double VolumeLitres { get; set; }
    public double PressureKpa { get; set; }
    public double TemperatureKelvin { get; set; } = 293.15;
    public Dictionary<string, double> GasFractions { get; set; } = new(StringComparer.Ordinal);
}

public sealed class SystemNetworkRule
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public string Kind { get; set; } = "power";
    public string Scope { get; set; } = "station";
    public string Priority { get; set; } = "required";
    public List<string> SegmentProfiles { get; set; } = [];
    public List<SystemTerminalSelector> Terminals { get; set; } = [];
    public bool AllowSpace { get; set; }
    public int MaxLength { get; set; } = 4096;
    public double MinPressureKpa { get; set; }
}

public sealed class SystemTerminalSelector
{
    public string DeviceRuleId { get; set; } = "";
    public string PortId { get; set; } = "";
    /// <summary>source, consumer, inlet or outlet.</summary>
    public string Role { get; set; } = "consumer";
    /// <summary>Requires at least one matching device. Every present selected terminal must still retain a valid connected, supplied native path.</summary>
    public bool Required { get; set; } = true;
}

public sealed class SystemControlRule
{
    public string Id { get; set; } = "";
    public string ControllerRuleId { get; set; } = "";
    public List<string> DeviceRuleIds { get; set; } = [];
    public string Scope { get; set; } = "room";
    public string Component { get; set; } = "DeviceList";
    public int MaxDevices { get; set; } = 32;
    public string Priority { get; set; } = "required";
}

/// <summary>Binding to real Layout.Equipment, including retained ordinary room content.</summary>
public sealed class DesignSystemDevice
{
    public string Id { get; set; } = "";
    public string RequirementId { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string ScopeId { get; set; } = "";
    public bool Edited { get; set; }
    public bool Locked { get; set; }
}

public sealed class DesignSystemNetwork
{
    public string Id { get; set; } = "";
    public string RequirementId { get; set; } = "";
    public string ScopeId { get; set; } = "";
    public List<DesignSystemSegment> Segments { get; set; } = [];
    public List<DesignSystemTerminal> Terminals { get; set; } = [];
    public bool Edited { get; set; }
    public bool Locked { get; set; }
}

public sealed class DesignSystemSegment
{
    public string Id { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public Cell Position { get; set; }
    public int Rotation { get; set; }
    public bool Edited { get; set; }
    public bool Locked { get; set; }
}

public sealed class DesignSystemTerminal
{
    public string DeviceId { get; set; } = "";
    public string PortId { get; set; } = "";
    public string Role { get; set; } = "consumer";
}

public sealed class DesignSystemControl
{
    public string Id { get; set; } = "";
    public string RequirementId { get; set; } = "";
    public string ControllerDeviceId { get; set; } = "";
    public List<string> DeviceIds { get; set; } = [];
    public bool Edited { get; set; }
    public bool Locked { get; set; }
}

public sealed class NativeSystemsReport
{
    public bool Complete { get; set; }
    public bool Success => Diagnostics.All(issue => issue.Severity != "error");
    public bool SimulationVerified => false;
    public List<ProgramDiagnostic> Diagnostics { get; set; } = [];
    public List<SystemTerminalObservation> Terminals { get; set; } = [];
    public Dictionary<string, double> Metrics { get; set; } = new(StringComparer.Ordinal);
}

public sealed class SystemTerminalObservation
{
    public string NetworkId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string PortId { get; set; } = "";
    public string Role { get; set; } = "";
    public bool Connected { get; set; }
    public bool SourceReachable { get; set; }
    public List<string> Witness { get; set; } = [];
    public bool WitnessTruncated { get; set; }
}

public sealed record NativeUtilityConnection(bool Verified, int Distance)
{
    public List<Cell> Cells { get; init; } = [];
    public List<string> PortIds { get; init; } = [];
    public List<string> Connections { get; init; } = [];
}
