using System.Text.Json;

namespace Content.StationArchitect;

public static partial class DesignSystems
{
    internal static void TraceNative(string message)
    { if (Environment.GetEnvironmentVariable("STATION_ARCHITECT_NATIVE_TRACE") == "1") Console.Error.WriteLine(DateTime.UtcNow.ToString("O") + " [native] " + message); }

    public static List<ProgramDiagnostic> ValidateDefinition(StationProgramDocument document)
    {
        var errors = new List<ProgramDiagnostic>(); var program = document.Systems;
        void Error(string path, string message) => errors.Add(new("systems." + path, "systems-definition", message));
        if (program == null) { Error("", "A systems program is required; Enabled=false keeps old programs unchanged."); return errors; }
        if (program.Profiles == null || program.Devices == null || program.Networks == null || program.Controls == null || program.Generation == null)
        { Error("", "Native profile, device, network, control lists and generation settings cannot be null."); return errors; }
        var limits = program.Generation;
        if (limits.MaxStatesPerConnection is < 1 or > 1000000 || limits.MaxTotalSearchStates is < 1 or > 10000000 || limits.MaxGeneratedSegments is < 1 or > 262144 || limits.MaxDeviceCandidatesPerScope is < 1 or > 1000000 || limits.MaxDevices is < 1 or > 4096 || limits.MaxWitnessSteps is < 1 or > 256)
            Error("generation", "Native search, placement and materialization budgets exceed their supported positive bounds.");
        if (program.Profiles.Count > 256 || program.Devices.Count > 256 || program.Networks.Count > 128 || program.Controls.Count > 128) { Error("", "Native infrastructure authoring exceeds its profile/rule count limits."); return errors; }
        var profileIds = new HashSet<string>(StringComparer.Ordinal); var deviceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in program.Profiles)
        {
            if (profile == null) { Error("profiles", "Null native profiles are not allowed."); continue; }
            if (!Identifier(profile.Id) || !Identifier(profile.Prototype) || !profileIds.Add(profile.Id)) Error("profiles", "Native profile IDs/prototypes must be bounded nonempty identifiers and IDs unique.");
            if (profile.Kind is not ("device" or "cable" or "pipe" or "disposal") || profile.Ports == null || profile.Transfers == null || profile.Sources == null || profile.Components == null || profile.RequiredComponents == null || profile.SourceRefs == null)
            { Error(profile.Id, "A profile needs a supported native kind and non-null port, transfer, source, component and evidence collections."); continue; }
            if (profile.Ports.Count > 32 || profile.Transfers.Count > 64 || profile.Sources.Count > 16 || profile.Components.Count > 64 || profile.RequiredComponents.Count > 64) { Error(profile.Id, "A native profile exceeds bounded component/port counts."); continue; }
            if (!Identifier(profile.OccupancyLayer)) Error(profile.Id, "A native occupancy layer must be a bounded nonempty identifier.");
            var ports = new HashSet<string>(StringComparer.Ordinal);
            foreach (var port in profile.Ports)
            {
                if (port == null) { Error(profile.Id, "Null native ports are not allowed."); continue; }
                if (!Identifier(port.Id) || !ports.Add(port.Id) || !Identifier(port.NodeGroup) || port.Kind is not ("power" or "gas" or "disposal") || port.Directions == null || port.Directions.Count > 4 || port.Directions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != port.Directions.Count || port.Directions.Any(direction => string.IsNullOrWhiteSpace(direction) || Direction(direction) == new Cell()))
                    Error(profile.Id, "Native ports need unique IDs, a group/kind, and at most four distinct cardinal directions.");
                if (port.Connection is not ("cable" or "cableDevice" or "terminal" or "terminalPort" or "pipe" or "pipePort" or "portable" or "rangeProvider" or "rangeReceiver" or "disposal" or "disposalUnit" or "disposalEntry")) Error(profile.Id, "Unknown native port contact type.");
                if (Math.Abs((long)port.Offset.X) > 16 || Math.Abs((long)port.Offset.Y) > 16 || port.PipeLayer is < 0 or > 8 || !Finite(port.Range, 0, 128) || !Finite(port.VolumeLitres, 0, 1000000)) Error(profile.Id, "Native offsets, pipe layers, range or internal volume are outside bounded values.");
            }
            if (profile.CarriesCrew == true && (!profile.RequiredComponents.Contains("DisposalUnit", StringComparer.Ordinal) && !profile.Components.ContainsKey("DisposalUnit") ||
                !profile.Ports.Any(port => port is { Kind: "disposal", Connection: "disposalUnit" }) || !NativeProfileCarriesCrew(profile)))
                Error(profile.Id, "Crew carrier declarations need actual native DisposalUnit component/entry facts; mail hardware cannot carry crew.");
            foreach (var transfer in profile.Transfers)
                if (transfer == null || !ports.Contains(transfer.FromPort) || !ports.Contains(transfer.ToPort) || transfer.FromPort == transfer.ToPort) Error(profile.Id, "Every native transfer must identify two distinct declared ports.");
            foreach (var source in profile.Sources)
                if (source == null || !ports.Contains(source.PortId) || source.Kind is not ("power" or "gas" or "gasSink") || !Finite(source.SupplyWatts, 0, 1e12) || !Finite(source.StoredEnergyJoules, 0, 1e15) || !Finite(source.VolumeLitres, 0, 1e9) || !Finite(source.PressureKpa, 0, 1e9) || !Finite(source.TemperatureKelvin, .01, 1e6) || source.GasFractions == null || source.GasFractions.Count > 16 || source.GasFractions.Any(pair => !Identifier(pair.Key) || !Finite(pair.Value, 0, 1)) || source.GasFractions.Count > 0 && Math.Abs(source.GasFractions.Values.Sum() - 1) > .000001)
                    Error(profile.Id, "Native source/sink facts need a real port and finite bounded energy/gas data with normalized fractions.");
            foreach (var component in profile.Components)
                if (!Identifier(component.Key) || component.Value.ValueKind != JsonValueKind.Object || component.Value.GetRawText().Length > 65536) Error(profile.Id, "Explicit native component data must be bounded named mappings.");
        }
        foreach (var group in program.Profiles.Where(profile => profile != null).GroupBy(profile => profile.Prototype))
            if (group.Select(profile => (profile.OccupancyLayer, profile.BlocksCrew)).Distinct().Count() > 1) Error(group.Key, "Native aliases of one prototype must agree about occupancy layer and crew collision.");
        foreach (var rule in program.Devices)
        {
            if (rule == null) { Error("devices", "Null device rules are not allowed."); continue; }
            if (!Identifier(rule.Id) || !deviceIds.Add(rule.Id) || !profileIds.Contains(rule.ProfileId) || rule.ProfileIds == null || rule.ProfileIds.Count > 64 || rule.ProfileIds.Any(id => !profileIds.Contains(id))) Error(rule.Id, "Device IDs must be unique and every profile variant must exist.");
            if (rule.Scope is not ("room" or "compartment" or "cluster" or "owner" or "station") || rule.Selector == null || rule.Content == null || rule.Content.Count == null || rule.Content.Prototypes == null || rule.ContentIds == null || rule.SourceRefs == null)
            { Error(rule.Id, "Device scope, selector and content requirement collections must be valid."); continue; }
            var selector = rule.Selector;
            if (selector.Paths == null || selector.Tags == null || selector.Roles == null || selector.Pressures == null || selector.Placements == null || selector.Networks == null || selector.InterfaceTypes == null || selector.Paths.Count > 256 || selector.Networks.Any(network => network is not ("rooms" or "publicHall" or "maintenance" or "exteriorMaintenance"))) Error(rule.Id, "Native target selector lists must be bounded; service zones must use declared network names.");
            if (rule.Content.Count.Min < 0 || rule.Content.Count.Min > rule.Content.Count.Target || rule.Content.Count.Target > rule.Content.Count.Max || rule.Content.Count.Max > 4096 || rule.Content.MinArea < 0 || rule.Content.MinArea > 1000000 || rule.Content.Placement is not ("floor" or "boundary" or "spaceBoundary")) Error(rule.Id, "Native device content has invalid count, work-floor or placement bounds.");
            if (rule.MaxCoverageDistance is { } coverage && !Finite(coverage, .1, 4096)) Error(rule.Id, "Native coverage distance must be finite and positive.");
            if ((rule.BindExisting ? 1 : 0) + (rule.BindInterfaces ? 1 : 0) + (rule.BindTransport ? 1 : 0) > 1) Error(rule.Id, "Select one existing content, generated interface or transport binding mode.");
        }
        var networkIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in program.Networks)
        {
            if (rule == null) { Error("networks", "Null network rules are not allowed."); continue; }
            if (!Identifier(rule.Id) || !networkIds.Add(rule.Id) || rule.Kind is not ("power" or "gas" or "disposal") || rule.Priority is not ("required" or "desired") || rule.Scope is not ("room" or "compartment" or "cluster" or "owner" or "station") || rule.SegmentProfiles == null || rule.Terminals == null)
            { Error(rule.Id, "A native network needs a unique ID, supported kind/scope and non-null segment/terminal selections."); continue; }
            if (rule.MaxLength is < 1 or > 65536 || !Finite(rule.MinPressureKpa, 0, 1e6) || rule.SegmentProfiles.Count > 64 || rule.SegmentProfiles.Any(id => !profileIds.Contains(id)) || rule.Terminals.Count > 256) Error(rule.Id, "Network profile references, pressure or routing limits are invalid.");
            foreach (var terminal in rule.Terminals)
                if (terminal == null || !deviceIds.Contains(terminal.DeviceRuleId) || !Identifier(terminal.PortId) || terminal.Role is not ("source" or "consumer" or "inlet" or "outlet")) Error(rule.Id, "Every native terminal must select a real device rule, native port and source/consumer/inlet/outlet role.");
        }
        var controlIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in program.Controls)
            if (rule == null || !Identifier(rule.Id) || !controlIds.Add(rule.Id) || !deviceIds.Contains(rule.ControllerRuleId) || rule.DeviceRuleIds == null || rule.DeviceRuleIds.Any(id => !deviceIds.Contains(id)) || rule.MaxDevices is < 1 or > 4096 || !Identifier(rule.Component) || rule.Priority is not ("required" or "desired")) Error("controls", "Native controller rules need valid unique IDs, device references and bounded device lists.");
        return errors;
    }

    private static bool Identifier(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 256 && !text.Contains('\0');
    private static bool Finite(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
}
