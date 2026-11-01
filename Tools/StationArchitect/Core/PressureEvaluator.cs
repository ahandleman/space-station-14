namespace Content.StationArchitect;

public sealed class PressureState
{
    public List<RoomMeasurement> Rooms { get; set; } = [];
    public List<DepartmentMeasurement> Departments { get; set; } = [];
    public List<ConnectionMeasurement> Connections { get; set; } = [];
}

public sealed class RoomMeasurement
{
    public string Path { get; set; } = "";
    public string InstanceId { get; set; } = "1";
    public string ParentInstanceId { get; set; } = "";
    /// <summary>Actual complex floor/seam area for shape evaluation of a parent with children; own Area remains exclusive.</summary>
    public int? ComplexArea { get; set; }
    public long? SubtreeFloorArea { get; set; }
    public bool? ContainedInParent { get; set; }
    public bool? ParentConnectionVerified { get; set; }
    public bool? ParentApproachVerified { get; set; }
    public int Area { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Components { get; set; } = 1;
    public int SpaceFrontage { get; set; }
    public int PublicEntries { get; set; }
    public int MaintenanceEntries { get; set; }
    public int ThroughTrafficPaths { get; set; }
    public List<ContentMeasurement>? Contents { get; set; }
    public List<DockMeasurement>? Docks { get; set; }
    public RoomEnvironmentObservation? Environment { get; set; }
    /// <summary>Owners adjacent through actual direct room entrances; null means not observed, [] means observed with none.</summary>
    public List<string>? EntryOwners { get; set; }
    /// <summary>Required approach networks proven unavoidable by the actual walking graph; null is unobserved.</summary>
    public List<string>? VerifiedApproachNetworks { get; set; }
}

public sealed class ContentMeasurement
{
    public string Id { get; set; } = "";
    public string Prototype { get; set; } = "";
    public int Count { get; set; }
    public string Placement { get; set; } = "floor";
    public string InterfaceType { get; set; } = "";
}

/// <summary>Counts only docks whose actual exterior geometry and bound docking hardware were independently verified.</summary>
public sealed class DockMeasurement
{
    public string Id { get; set; } = "";
    public int Count { get; set; }
}

public sealed class RoomEnvironmentObservation
{
    public string Pressure { get; set; } = "standard";
    public string Placement { get; set; } = "interior";
    public bool Sealed { get; set; } = true;
    public double? TemperatureKelvin { get; set; }
}

public sealed class DepartmentMeasurement
{
    /// <summary>Department ID or _maintenance. Area is always derived from owned room instances.</summary>
    public string Path { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int Components { get; set; } = 1;
    public int HallCrossings { get; set; }
    public int MaintenanceCrossings { get; set; }
}

public sealed class ConnectionMeasurement
{
    public string Id { get; set; } = "";
    public string SourceInstance { get; set; } = "";
    public string TargetInstance { get; set; } = "";
    public string Mode { get; set; } = "direct";
    public int Distance { get; set; }
    public string Kind { get; set; } = "walking";
    public string InterfaceType { get; set; } = "";
    public int Width { get; set; }
}

public sealed record PressureTerm(string Path, string Metric, double Value, double Target, double Weight, double Cost);

public sealed class PressureReport
{
    public bool Success => Diagnostics.All(d => d.Severity != "error");
    /// <summary>False when a supplied partial snapshot lacks geometry/route observations needed for final checks.</summary>
    public bool Complete { get; set; } = true;
    public double Cost => Terms.Sum(t => t.Cost);
    public List<PressureTerm> Terms { get; set; } = [];
    public List<ProgramDiagnostic> Diagnostics { get; set; } = [];
}

public sealed class TransferEvaluation
{
    public PressureReport Before { get; set; } = new();
    public PressureReport After { get; set; } = new();
    public double Delta => After.Cost - Before.Cost;
    /// <summary>Hard legality only; a search may intentionally accept a positive delta to escape local minima.</summary>
    public bool Acceptable => After.Success;
}

/// <summary>
/// Pure scoring foundation, not a placement generator. Each room instance contributes local pressure;
/// its owner's summed room floor contributes ancestor pressure. Comparing entire snapshots accounts
/// for both the donor and recipient, so a same-owner transfer preserves the owner's area cost.
/// Growth permits unfinished minima and shape, but never max area/count or illegal disconnected rooms.
/// Final checks enforce minima, shape, exterior/access policy and required connection evidence.
/// Measurements are supplied by a future geometric generator, not inferred from YAML budgets.
/// </summary>
public static class PressureEvaluator
{
    public static TransferEvaluation EvaluateTransfer(StationProgramDocument document, PressureState before, PressureState after, bool final = false) =>
        new() { Before = Evaluate(document, before, final), After = Evaluate(document, after, final) };

    public static PressureReport Evaluate(StationProgramDocument document, PressureState state, bool final = false)
    {
        var compilation = RequirementCompiler.Compile(document);
        var report = new PressureReport { Diagnostics = [.. compilation.Diagnostics] };
        if (!compilation.Success) { report.Complete = false; return report; }
        if (state == null || state.Rooms == null || state.Departments == null || state.Connections == null)
        { report.Diagnostics.Add(new("state", "missing", "Measurements need room, department and connection lists.")); report.Complete = false; return report; }
        if (state.Rooms.Count > 262144 || state.Departments.Count > 65 || state.Connections.Count > 262144)
        { report.Diagnostics.Add(new("state", "count", "The measurement snapshot exceeds the format's bounded instance counts.")); report.Complete = false; return report; }
        var roomDefinitions = RequirementCompiler.Rooms(document).ToDictionary(r => r.Path, r => r.Room, StringComparer.Ordinal);
        var measurements = new Dictionary<string, List<RoomMeasurement>>(StringComparer.Ordinal);
        var instances = new Dictionary<string, RoomMeasurement>(StringComparer.Ordinal);
        foreach (var room in state.Rooms)
        {
            if (room == null || room.Path == null || !roomDefinitions.ContainsKey(room.Path))
            { report.Diagnostics.Add(new(room?.Path ?? "state.rooms", "measurement-reference", "Room measurements must refer to a declared room template.")); continue; }
            var key = RoomHierarchy.MeasurementKey(room);
            if (string.IsNullOrWhiteSpace(room.InstanceId) || room.InstanceId.Length > 64 || room.InstanceId.Contains('#') || room.InstanceId.Contains('/') || room.ParentInstanceId == null || room.ParentInstanceId.Length > 2048 || !instances.TryAdd(key, room))
            { report.Diagnostics.Add(new(key, "measurement-instance", "Instance IDs must be nonempty, at most 64 characters, unique within a room template and contain no '#'.")); continue; }
            if (room.Area < 0 || room.Area > 1000000 || room.ComplexArea is < 0 or > 1000000 || room.SubtreeFloorArea is < 0 or > 1000000 || room.Width < 0 || room.Width > 1000000 || room.Height < 0 || room.Height > 1000000 || room.Components < 0 || room.SpaceFrontage < 0 || room.PublicEntries < 0 || room.MaintenanceEntries < 0 || room.ThroughTrafficPaths < 0)
                report.Diagnostics.Add(new(key, "measurement-range", "Room measurements must be nonnegative, and area/bounding-box sides cannot exceed 1000000."));
            if (!measurements.TryGetValue(room.Path, out var list)) measurements[room.Path] = list = [];
            list.Add(room);
        }
        var owners = document.Departments.Select(d => d.Id).Append(RequirementCompiler.MaintenanceOwner).ToHashSet(StringComparer.Ordinal);
        var ownerMeasurements = new Dictionary<string, DepartmentMeasurement>(StringComparer.Ordinal);
        foreach (var owner in state.Departments)
        {
            if (owner == null || owner.Path == null || !owners.Contains(owner.Path))
            { report.Diagnostics.Add(new(owner?.Path ?? "state.departments", "measurement-reference", "Owner measurements must refer to a declared department or _maintenance.")); continue; }
            if (!ownerMeasurements.TryAdd(owner.Path, owner)) report.Diagnostics.Add(new(owner.Path, "measurement-duplicate", "Each owner has one aggregate measurement."));
            if (owner.Width < 0 || owner.Height < 0 || owner.Width > 1000000 || owner.Height > 1000000 || owner.Components < 0 || owner.HallCrossings < 0 || owner.MaintenanceCrossings < 0)
                report.Diagnostics.Add(new(owner.Path, "measurement-range", "Owner measurements must be nonnegative, with bounding-box sides at most 1000000."));
        }
        if (report.Diagnostics.Any(d => d.Severity == "error")) { report.Complete = false; return report; }

        long SubtreeArea(string id, HashSet<string> seen)
        {
            if (!seen.Add(id) || !instances.TryGetValue(id, out var instance)) return 0;
            return instance.Area + instances.Where(pair => pair.Value.ParentInstanceId == id).Sum(pair => SubtreeArea(pair.Key, seen));
        }

        foreach (var (path, definition) in roomDefinitions)
        {
            var rooms = measurements.GetValueOrDefault(path) ?? [];
            var parentPath = RoomHierarchy.ParentTemplatePath(path);
            var scopes = parentPath == null ? new[] { "" } : measurements.GetValueOrDefault(parentPath)?.Select(RoomHierarchy.MeasurementKey).ToArray() ?? [];
            foreach (var parent in scopes)
                Range(rooms.Count(room => room.ParentInstanceId == parent), definition.Count.Min, definition.Count.Target, definition.Count.Max, definition.Count.Weight * document.Settings.RoomPressureWeight, parent.Length == 0 ? path : RoomHierarchy.CountKey(parent, path), "count", final, report);
            foreach (var room in rooms)
            {
                var key = RoomHierarchy.MeasurementKey(room);
                if (parentPath == null && room.ParentInstanceId.Length != 0 || parentPath != null && (!instances.TryGetValue(room.ParentInstanceId, out var parent) || parent.Path != parentPath))
                    report.Diagnostics.Add(new(key, "measurement-parent", "A nested room must identify an existing instance of its immediate parent template; top-level rooms have no room parent."));
                if (parentPath != null)
                {
                    void Proof(bool needed, bool? observed, string code, string message)
                    {
                        if (!needed) return;
                        if (observed == null) Missing(key, code + "-measurement", message, final, report);
                        else if (observed == false) { if (final) report.Diagnostics.Add(new(key, code, message)); else report.Complete = false; }
                    }
                    Proof(definition.Nesting.Internal, room.ContainedInParent, "nested-containment", "Actual internal parent containment/enclosure has not been proven.");
                    Proof(definition.Nesting.RequireParentConnection, room.ParentConnectionVerified, "nested-parent-connection", "Actual walking access from this room's own parent has not been proven.");
                    Proof(!definition.Nesting.AllowExternalEntry, room.ParentApproachVerified, "nested-parent-approach", "Every station approach must traverse this room's own parent floor.");
                }
                Range(room.Area, RequirementCompiler.EffectiveMinimumArea(definition), Math.Max(definition.Area.Target, RequirementCompiler.EffectiveMinimumArea(definition)), definition.Area.Max, definition.Area.Weight * document.Settings.RoomPressureWeight, key, "area", final, report);
                if (room.Area > 0 && room.Components != 1) report.Diagnostics.Add(new(key, "room-components", "Every individual room must remain connected; use multiple instances for separate rooms."));
                if (definition.Rooms.Count > 0 && room.ComplexArea == null) Missing(key, "complex-shape-measurement", "A room with nested templates needs actual complex-area geometry for shape evaluation.", final, report);
                var subtree = SubtreeArea(key, []);
                if (room.SubtreeFloorArea is { } measuredSubtree && measuredSubtree != subtree || room.ComplexArea is { } measuredComplex && measuredComplex < subtree)
                    report.Diagnostics.Add(new(key, "complex-area-measurement", "Complex observations must include exclusive descendant floor exactly once and gross area cannot be smaller than usable subtree floor."));
                Shape(definition.Rooms.Count > 0 ? room.ComplexArea ?? room.Area : room.Area, room.Width, room.Height, definition.ComplexShape ?? definition.Shape, key, document.Settings.RoomPressureWeight, final, report);
                if (definition.ComplexArea is { } complex)
                {
                    if (room.SubtreeFloorArea is not { } observed) Missing(key, "complex-area-measurement", "The room complex needs its measured exclusive own-and-descendant floor total.", final, report);
                    else Range(observed, complex.Min, complex.Target, complex.Max, complex.Weight * document.Settings.RoomPressureWeight, key, "complexArea", final, report);
                }
                if (final && room.SpaceFrontage < definition.Exterior.MinSpaceFrontage) report.Diagnostics.Add(new(key, "space-frontage", "The room does not meet its minimum sealed space frontage."));
                if (!definition.Exterior.AllowSpaceExposure && room.SpaceFrontage > 0) report.Diagnostics.Add(new(key, "space-frontage", "This room excludes exterior space frontage."));
                if (!definition.Access.AllowPublicEntry && room.PublicEntries > 0) report.Diagnostics.Add(new(key, "public-entry", "This room forbids direct public hall entrances."));
                if (!definition.Access.AllowMaintenanceEntry && room.MaintenanceEntries > 0) report.Diagnostics.Add(new(key, "maintenance-entry", "This room forbids direct maintenance entrances."));
                if (!definition.AllowThroughTraffic && room.ThroughTrafficPaths > 0) report.Diagnostics.Add(new(key, "through-traffic", "This room cannot be used as a transit route between other rooms."));
                RoomPolicies(document, definition, room, key, owners, report, final);
            }
        }
        foreach (var choice in compilation.Choices)
        {
            var active = choice.Rooms.Count(path => measurements.TryGetValue(path, out var list) && list.Count > 0);
            Range(active, choice.Count.Min, choice.Count.Target, choice.Count.Max, choice.Count.Weight * document.Settings.RoomPressureWeight, "choices:" + choice.Id, "activeRooms", final, report);
        }
        foreach (var owner in compilation.Tree!.Children)
        {
            var area = state.Rooms.Where(r => RequirementCompiler.Owner(r.Path) == owner.Path).Sum(r => (long) r.Area);
            var definition = owner.Area!;
            Range(area, definition.Min, definition.Target, definition.Max, definition.Weight * document.Settings.DepartmentPressureWeight, owner.Path, "area", final, report);
            if (owner.Kind == "maintenance") continue;
            var measured = ownerMeasurements.GetValueOrDefault(owner.Path);
            if (area > 0 && measured == null)
                Missing(owner.Path, "owner-geometry", "Owner shape and split measurements are missing.", final, report);
            if (measured == null) continue;
            Shape(area, measured.Width, measured.Height, owner.Shape!, owner.Path, document.Settings.DepartmentPressureWeight, final, report);
            if (area > 0 && measured.Components < 1) report.Diagnostics.Add(new(owner.Path, "owner-components", "A nonempty owner needs at least one connected component."));
            var split = owner.Split!;
            if (measured.Components > (split.Allow ? split.MaxComponents : 1)) report.Diagnostics.Add(new(owner.Path, "owner-components", "The owner exceeds its permitted number of connected components."));
            if (!split.Allow && (measured.HallCrossings > 0 || measured.MaintenanceCrossings > 0)) report.Diagnostics.Add(new(owner.Path, "owner-split", "This department cannot be split across halls or maintenance."));
            Linear(owner.Path, "components", Math.Max(0, measured.Components - 1), split.ComponentPenalty * document.Settings.DepartmentPressureWeight, report);
            Linear(owner.Path, "hallCrossings", measured.HallCrossings, split.HallCrossingPenalty * document.Settings.DepartmentPressureWeight, report);
            Linear(owner.Path, "maintenanceCrossings", measured.MaintenanceCrossings, split.MaintenanceCrossingPenalty * document.Settings.DepartmentPressureWeight, report);
        }
        Connections(document, compilation, state, instances, report, final);
        return report;
    }

    private static void Range(long value, int min, int target, int max, double weight, string path, string metric, bool final, PressureReport report)
    {
        var difference = (double) value - target;
        var normalized = difference / Math.Max(1, target);
        report.Terms.Add(new(path, metric, value, target, weight, normalized * normalized * weight));
        if (value > max || final && value < min) report.Diagnostics.Add(new(path, metric + "-bound", $"Measured {metric} {value} is outside {(final ? min : 0)}–{max}."));
        if (!final && value < min) report.Complete = false;
    }

    private static void Shape(long area, int width, int height, ShapeRequirement shape, string path, double ownerWeight, bool final, PressureReport report)
    {
        if (area == 0) return;
        if (width < 1 || height < 1)
        { Missing(path, "shape-measurement", "Positive floor area needs a positive floor bounding box for shape checks.", final, report); return; }
        var box = (long) width * height;
        if (area > box) { report.Diagnostics.Add(new(path, "shape-measurement", "Owned floor cannot exceed its bounding box area.")); return; }
        var aspect = Math.Max((double) width / height, (double) height / width);
        var fill = (double) area / box;
        // Compact shapes are preferred even within hard bounds; the fill term only penalizes a deficit.
        var weight = shape.Weight * ownerWeight;
        report.Terms.Add(new(path, "aspectRatio", aspect, 1, weight, Math.Pow(aspect - 1, 2) * weight));
        report.Terms.Add(new(path, "fillRatio", fill, shape.MinFillRatio, weight, Math.Pow(Math.Max(0, shape.MinFillRatio - fill), 2) * weight));
        if (aspect > shape.MaxAspectRatio || fill < shape.MinFillRatio)
        {
            if (final) report.Diagnostics.Add(new(path, "shape-bound", $"Shape aspect {aspect:0.###}, fill {fill:0.###} violates maximum aspect {shape.MaxAspectRatio} or minimum fill {shape.MinFillRatio}."));
            else report.Complete = false;
        }
    }

    private static void Linear(string path, string metric, int value, double weight, PressureReport report) =>
        report.Terms.Add(new(path, metric, value, 0, weight, value * weight));

    private static void RoomPolicies(StationProgramDocument document, RoomDefinition definition, RoomMeasurement room, string key,
        HashSet<string> owners, PressureReport report, bool final)
    {
        if (definition.Exterior.Docks.Count > 0)
        {
            if (room.Docks == null) Missing(key, "dock-measurement", "Actual empty berth, straight exterior approach and bound docking hardware observations are missing.", final, report);
            else
            {
                var seenDocks = new HashSet<string>(StringComparer.Ordinal);
                foreach (var observed in room.Docks)
                    if (observed == null || observed.Count is < 0 or > 64 || !seenDocks.Add(observed.Id) || !definition.Exterior.Docks.Any(rule => rule.Id == observed.Id)) report.Diagnostics.Add(new(key, "dock-measurement", "Dock observations need a unique declared docking rule and bounded verified count."));
                foreach (var dock in definition.Exterior.Docks)
                {
                    var count = room.Docks.FirstOrDefault(observed => observed?.Id == dock.Id)?.Count ?? 0;
                    var requirement = definition.Contents.Single(content => content.Id == dock.ContentId).Count;
                    Range(count, requirement.Min, requirement.Target, requirement.Max, requirement.Weight * document.Settings.RoomPressureWeight, key + "/docks:" + dock.Id, "count", final, report);
                }
            }
        }
        if (room.Environment == null)
            Missing(key, "environment-measurement", "Room pressure, placement and sealing measurements are missing.", final, report);
        else
        {
            if (room.Environment.Pressure is not ("standard" or "low" or "vacuum" or "custom") || room.Environment.Placement is not ("interior" or "exterior"))
                report.Diagnostics.Add(new(key, "environment-measurement", "Observed environment needs an actual pressure class and interior/exterior placement."));
            else if (room.Environment.Pressure != definition.Environment.Pressure || definition.Environment.Placement != "either" && room.Environment.Placement != definition.Environment.Placement || definition.Environment.Sealed && !room.Environment.Sealed)
            {
                if (final) report.Diagnostics.Add(new(key, "environment-bound", "Observed pressure, placement or sealing violates the room environment policy."));
                else report.Complete = false;
            }
            var temperature = room.Environment.TemperatureKelvin;
            if (temperature.HasValue && (!double.IsFinite(temperature.Value) || temperature.Value is < 0 or > 100000))
                report.Diagnostics.Add(new(key, "temperature-measurement", "Observed temperature must be finite and between 0 and 100000 Kelvin."));
            else if (definition.Environment.MinTemperatureKelvin.HasValue || definition.Environment.MaxTemperatureKelvin.HasValue)
            {
                if (!temperature.HasValue) Missing(key, "temperature-measurement", "This room requires an observed temperature to evaluate its configured bounds.", final, report);
                else if (definition.Environment.MinTemperatureKelvin.HasValue && temperature < definition.Environment.MinTemperatureKelvin || definition.Environment.MaxTemperatureKelvin.HasValue && temperature > definition.Environment.MaxTemperatureKelvin)
                {
                    if (final) report.Diagnostics.Add(new(key, "temperature-bound", "Observed temperature violates the configured Kelvin bounds."));
                    else report.Complete = false;
                }
            }
        }
        if (room.EntryOwners == null)
        {
            if (definition.Access.EntryOwners.Count > 0) Missing(key, "entry-owner-measurement", "Direct entrance owner measurements are required for this restricted room.", final, report);
        }
        else
        {
            if (room.EntryOwners.Count > 65 || room.EntryOwners.Any(owner => owner == null || !owners.Contains(owner)) || room.EntryOwners.Distinct(StringComparer.Ordinal).Count() != room.EntryOwners.Count)
                report.Diagnostics.Add(new(key, "entry-owner-measurement", "Observed direct entry owners must be unique declared owner IDs."));
            else if (definition.Access.EntryOwners.Count > 0 && room.EntryOwners.Any(owner => !definition.Access.EntryOwners.Contains(owner, StringComparer.Ordinal)))
                report.Diagnostics.Add(new(key, "entry-owner", "A direct room entrance leads to an owner excluded by this room's entry policy."));
        }
        if (definition.Access.RequireNetworkApproach.Count > 0)
        {
            if (room.VerifiedApproachNetworks == null) Missing(key, "approach-network-measurement", "Unavoidable approach-network observations are missing.", final, report);
            else if (room.VerifiedApproachNetworks.Any(mode => mode is not ("publicHall" or "maintenance")) || room.VerifiedApproachNetworks.Distinct(StringComparer.Ordinal).Count() != room.VerifiedApproachNetworks.Count)
                report.Diagnostics.Add(new(key, "approach-network-measurement", "Verified approach networks must be unique publicHall or maintenance modes."));
            else if (definition.Access.RequireNetworkApproach.Any(mode => !room.VerifiedApproachNetworks.Contains(mode, StringComparer.Ordinal)))
                report.Diagnostics.Add(new(key, "approach-network", "A station approach bypasses a network that this room requires every approach to traverse."));
        }
        if (room.Contents == null)
        {
            if (definition.Contents.Count > 0) Missing(key, "content-measurement", "Equipment counts and prototype measurements are missing.", final, report);
            return;
        }
        if (room.Contents.Count > 16384) { report.Diagnostics.Add(new(key, "content-measurement", "Use at most 16384 equipment observations per room.")); return; }
        var requirements = definition.Contents.ToDictionary(content => content.Id, StringComparer.Ordinal);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var seen = new HashSet<(string Id, string Prototype, string Placement)>();
        foreach (var content in room.Contents)
        {
            if (content == null || content.Id == null || !requirements.TryGetValue(content.Id, out var requirement) || content.Prototype == null || !requirement.Prototypes.Contains(content.Prototype, StringComparer.Ordinal))
            { report.Diagnostics.Add(new(key, "content-reference", "Equipment observations must identify a declared requirement and an allowed prototype.")); continue; }
            if (content.Count is < 0 or > 100000 || !seen.Add((content.Id, content.Prototype, content.Placement)) || content.Placement is not ("floor" or "boundary" or "spaceBoundary"))
            { report.Diagnostics.Add(new(key, "content-measurement", "Equipment observations need bounded nonnegative counts and one row per requirement/prototype/placement combination.")); continue; }
            if (content.Count > 0 && !(content.Placement == requirement.Placement || requirement.Placement == "boundary" && content.Placement == "spaceBoundary"))
            { report.Diagnostics.Add(new(key, "content-placement", $"Equipment '{content.Id}' is not placed on its required {requirement.Placement}.")); continue; }
            if (content.Count > 0 && requirement.InterfaceType.Length > 0 && content.InterfaceType != requirement.InterfaceType)
            {
                if (content.InterfaceType == "") Missing(key, "content-interface-measurement", "This equipment needs an observed physical boundary interface.", final, report);
                else report.Diagnostics.Add(new(key, "content-interface", "Equipment does not provide its required boundary interface."));
                continue;
            }
            counts[content.Id] = counts.GetValueOrDefault(content.Id) + content.Count;
        }
        long reserved = 0;
        foreach (var content in definition.Contents)
        {
            var count = counts.GetValueOrDefault(content.Id);
            Range(count, content.Count.Min, content.Count.Target, content.Count.Max, content.Count.Weight * document.Settings.RoomPressureWeight, key + "/contents:" + content.Id, "count", final, report);
            reserved += count * content.MinArea;
        }
        if (reserved > room.Area)
            report.Diagnostics.Add(new(key, "content-area", $"Observed equipment reserves {reserved} floor tiles but the room owns {room.Area}."));
    }

    private static void Missing(string path, string code, string message, bool final, PressureReport report)
    { report.Complete = false; report.Diagnostics.Add(new(path, code, message, final ? "error" : "warning")); }

    private static void Connections(StationProgramDocument document, ProgramCompilation compilation, PressureState state,
        Dictionary<string, RoomMeasurement> instances, PressureReport report, bool final)
    {
        var definitions = compilation.Connections.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var roomDefinitions = RequirementCompiler.Rooms(document).ToDictionary(r => r.Path, r => r.Room, StringComparer.Ordinal);
        var ownerAreas = state.Rooms.GroupBy(r => RequirementCompiler.Owner(r.Path)).ToDictionary(g => g.Key, g => g.Sum(r => (long) r.Area), StringComparer.Ordinal);
        var existingPaths = instances.Where(p => p.Value.Area > 0).GroupBy(p => p.Value.Path).ToDictionary(g => g.Key, g => g.Select(p => p.Key).ToList(), StringComparer.Ordinal);
        foreach (var owner in ownerAreas.Where(p => p.Value > 0)) existingPaths[owner.Key] = [owner.Key];
        List<string> Existing(string path) => existingPaths.GetValueOrDefault(path) ?? [];
        string Template(string key) => instances.TryGetValue(key, out var room) ? room.Path : key;
        bool Endpoint(string key) => instances.TryGetValue(key, out var room) ? room.Area > 0 : ownerAreas.GetValueOrDefault(key) > 0;
        var valid = new List<ConnectionMeasurement>();
        foreach (var connection in state.Connections)
        {
            if (connection == null || connection.Id == null || !definitions.TryGetValue(connection.Id, out var definition))
            { report.Diagnostics.Add(new(connection?.Id ?? "state.connections", "measurement-reference", "Connection evidence must use a compiled canonical connection ID.")); continue; }
            if (connection.SourceInstance == null || connection.TargetInstance == null || !Endpoint(connection.SourceInstance) || !Endpoint(connection.TargetInstance) ||
                Template(connection.SourceInstance) != definition.From || !definition.To.Contains(Template(connection.TargetInstance), StringComparer.Ordinal))
            { report.Diagnostics.Add(new(connection.Id, "measurement-reference", "Connection evidence must join an existing source and target instance declared by this connection.")); continue; }
            if (connection.Distance < 0 || connection.Distance > 1000000 || connection.Mode == null || !definition.Modes.Contains(connection.Mode, StringComparer.Ordinal))
            { report.Diagnostics.Add(new(connection.Id, "measurement-mode", "Connection evidence needs a nonnegative bounded distance and an allowed route mode.")); continue; }
            if (instances.TryGetValue(connection.SourceInstance, out var sourceRoom) && instances.TryGetValue(connection.TargetInstance, out var targetRoom) &&
                !RoomHierarchy.ConnectionScopeAllowed(definition, sourceRoom.Path, connection.SourceInstance, sourceRoom.ParentInstanceId, targetRoom.Path, connection.TargetInstance, targetRoom.ParentInstanceId))
            { report.Diagnostics.Add(new(connection.Id, "measurement-parent-scope", "A room-local nested connection must join the corresponding parent or sibling in its own parent instance.")); continue; }
            if (connection.Mode == "department" && RequirementCompiler.Owner(Template(connection.SourceInstance)) != RequirementCompiler.Owner(Template(connection.TargetInstance)))
            { report.Diagnostics.Add(new(connection.Id, "measurement-mode", "A department-only route cannot cross owner boundaries.")); continue; }
            if (connection.Mode == "direct" && connection.Kind == "walking")
            {
                var sourcePath = Template(connection.SourceInstance); var targetPath = Template(connection.TargetInstance);
                bool Allowed(string path, string opposite) => !roomDefinitions.TryGetValue(path, out var room) || room.Access.EntryOwners.Count == 0 || room.Access.EntryOwners.Contains(RequirementCompiler.Owner(opposite), StringComparer.Ordinal);
                if (!Allowed(sourcePath, targetPath) || !Allowed(targetPath, sourcePath))
                { report.Diagnostics.Add(new(connection.Id, "measurement-access", "A direct walking connection violates an endpoint's eligible entry owners.")); continue; }
            }
            if (connection.Kind != definition.Kind)
            { report.Diagnostics.Add(new(connection.Id, "measurement-kind", "An interaction or utility interface cannot fulfill a walking connection, or vice versa.")); continue; }
            if (connection.Mode == "mail" && connection.Kind == "walking")
            { report.Diagnostics.Add(new(connection.Id, "measurement-kind", "Item-only mail delivery cannot satisfy a crew walking connection.")); continue; }
            if (connection.Width < 0 || connection.Width > 1000 || connection.InterfaceType == null || connection.InterfaceType != "" && connection.InterfaceType is not ("doorway" or "airlock" or "counter" or "window" or "dockingPort" or "pipe" or "cable"))
            { report.Diagnostics.Add(new(connection.Id, "measurement-interface", "Observed interfaces need a supported type and width from 0 to 1000.")); continue; }
            if (definition.InterfaceTypes.Count > 0 && connection.InterfaceType == "" || definition.MinWidth > 0 && connection.Width == 0)
            { Missing(connection.Id, "interface-measurement", "This connection needs observed interface type/width measurements.", final, report); continue; }
            if (definition.InterfaceTypes.Count > 0 && !definition.InterfaceTypes.Contains(connection.InterfaceType, StringComparer.Ordinal) || connection.Width < definition.MinWidth)
            { report.Diagnostics.Add(new(connection.Id, "interface-bound", "The observed interface type or width violates the connection requirement.")); continue; }
            if (connection.Kind == "walking" && connection.InterfaceType is "counter" or "window" or "pipe" or "cable")
            { report.Diagnostics.Add(new(connection.Id, "measurement-kind", "A counter, window, pipe or cable does not provide a traversable walking interface.")); continue; }
            // Network mode describes the route, not a direct doorway at each endpoint. A private
            // room may reach the network through owned circulation. Actual direct entries are
            // checked separately against PublicEntries/MaintenanceEntries above.
            valid.Add(connection);
        }
        foreach (var definition in compilation.Connections)
        {
            var sources = Existing(definition.From);
            foreach (var source in sources)
            {
                var availableTargets = definition.To.ToDictionary(t => t, Existing, StringComparer.Ordinal);
                var obligations = definition.Selection == "all" ? definition.To.Select(t => new List<string> { t }).ToList() : [definition.To];
                foreach (var group in obligations)
                {
                    var existing = group.SelectMany(t => availableTargets[t]).ToHashSet(StringComparer.Ordinal);
                    if (existing.Count == 0 && definition.Presence == "ifPresent") continue;
                    var routes = valid.Where(c => c.Id == definition.Id && c.SourceInstance == source && existing.Contains(c.TargetInstance)).ToList();
                    var inRange = routes.Where(c => definition.MaxDistance == null || c.Distance <= definition.MaxDistance).ToList();
                    var weight = definition.Weight * document.Settings.ConnectionPressureWeight;
                    if (definition.Priority == "avoided")
                    {
                        // Avoided is explicitly a soft cost. No evidence cannot establish separation.
                        Linear(definition.Id + "@" + source, "avoidedConnection", inRange.Count > 0 ? 1 : 0, weight, report);
                        if (existing.Count > 0 && routes.Count == 0) report.Complete = false;
                        continue;
                    }
                    var fulfilled = inRange.Count > 0;
                    Linear(definition.Id + "@" + source, "connection", fulfilled ? 0 : 1, weight, report);
                    if (!fulfilled)
                    {
                        report.Complete = false;
                        if (definition.Priority == "required" && final)
                            report.Diagnostics.Add(new(definition.Id, existing.Count == 0 ? "required-presence" : "required-connection", $"Source '{source}' needs {(definition.Selection == "any" ? "one of" : "the target")} [{string.Join(", ", group)}] via its allowed modes within the distance bound."));
                    }
                }
            }
        }
    }
}
