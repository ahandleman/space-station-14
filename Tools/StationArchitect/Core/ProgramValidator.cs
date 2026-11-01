using System.Text.RegularExpressions;

namespace Content.StationArchitect;

public static partial class ProgramValidator
{
    public static List<ProgramDiagnostic> Validate(StationProgramDocument document) => RequirementCompiler.Compile(document).Diagnostics;

    internal static List<ProgramDiagnostic> ValidateStructure(StationProgramDocument? document)
    {
        var errors = new List<ProgramDiagnostic>();
        void Error(string path, string code, string message) => errors.Add(new(path, code, message));
        if (document == null) { Error("$", "missing", "A station program is required."); return errors; }
        if (document.SchemaVersion != 2) Error("schemaVersion", "version", "Station Architect expects schemaVersion: 2.");
        Id(document.Id, "id", errors);
        Text(document.Name, "name", 160, errors); Text(document.Description, "description", 4096, errors);
        Sources(document.SourceRefs, "sourceRefs", errors);
        if (document.Settings == null) Error("settings", "missing", "Pressure settings are required.");
        else
        {
            Weight(document.Settings.RoomPressureWeight, "settings.roomPressureWeight", errors);
            Weight(document.Settings.DepartmentPressureWeight, "settings.departmentPressureWeight", errors);
            Weight(document.Settings.ConnectionPressureWeight, "settings.connectionPressureWeight", errors);
        }
        if (document.Departments == null || document.Departments.Count is < 1 or > 64)
            Error("departments", "count", "Define 1–64 departments.");
        else
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < document.Departments.Count; i++)
            {
                var path = $"departments[{i}]";
                var department = document.Departments[i];
                if (department == null) { Error(path, "missing", "Null departments are not allowed."); continue; }
                Id(department.Id, path + ".id", errors);
                if (!ids.Add(department.Id ?? "")) Error(path + ".id", "duplicate", "Department IDs must be unique.");
                if (department.Id == "_maintenance") Error(path + ".id", "reserved", "_maintenance is reserved for true maintenance rooms.");
                Text(department.Name, path + ".name", 160, errors); Text(department.Description, path + ".description", 4096, errors);
                Sources(department.SourceRefs, path + ".sourceRefs", errors);
                if (department.Color == null || !ColorPattern().IsMatch(department.Color)) Error(path + ".color", "color", "Use a six-digit #RRGGBB color.");
                Tags(department.Tags, path + ".tags", errors);
                Area(department.Area, path + ".area", true, errors); Shape(department.Shape, path + ".shape", errors);
                if (department.Split == null) Error(path + ".split", "missing", "A split policy is required.");
                else
                {
                    if (department.Split.MaxComponents is < 1 or > 64) Error(path + ".split.maxComponents", "range", "Maximum components must be 1–64.");
                    Weight(department.Split.ComponentPenalty, path + ".split.componentPenalty", errors);
                    Weight(department.Split.HallCrossingPenalty, path + ".split.hallCrossingPenalty", errors);
                    Weight(department.Split.MaintenanceCrossingPenalty, path + ".split.maintenanceCrossingPenalty", errors);
                }
                Rooms(department.Rooms, path + ".rooms", errors, requireRooms: true);
            }
        }
        if (document.Maintenance == null) Error("maintenance", "missing", "A maintenance program is required.");
        else
        {
            Area(document.Maintenance.Area, "maintenance.area", true, errors);
            if (document.Maintenance.PassageWidth == null || document.Maintenance.PassageWidth.Min < 2 || document.Maintenance.PassageWidth.Max > 3 || document.Maintenance.PassageWidth.Min > document.Maintenance.PassageWidth.Max)
                Error("maintenance.passageWidth", "range", "Maintenance passages must be between 2 and 3 tiles wide.");
            Rooms(document.Maintenance.Rooms, "maintenance.rooms", errors, requireRooms: false);
            var connectivity = document.Maintenance.Connectivity;
            if (connectivity == null || connectivity.Policy is not ("continuous" or "controlled") || connectivity.MaxComponents is < 1 or > 64 ||
                !double.IsFinite(connectivity.ComponentPenalty) || connectivity.ComponentPenalty is < 0 or > 1000000 ||
                string.IsNullOrWhiteSpace(connectivity.TransitionAccess) || connectivity.TransitionAccess.Length > 128 || connectivity.MinTransitionsPerComponent is < 1 or > 8)
                Error("maintenance.connectivity", "range", "Use continuous or controlled maintenance connectivity, 1-64 components, a finite nonnegative penalty, an access label and 1-8 real transitions per component.");
        }
        if (document.Connections == null || document.Connections.Count > 4096) Error("connections", "count", "Use at most 4096 station connection definitions.");
        if (document.Choices == null || document.Choices.Count > 256) Error("choices", "count", "Use at most 256 room choice groups.");
        Gameflow(document.Gameflow, errors);
        errors.AddRange(DesignSystems.ValidateDefinition(document));
        var templates = RoomHierarchy.Templates(document).Take(RoomHierarchy.MaxTemplates + 1).ToList();
        if (templates.Count > RoomHierarchy.MaxTemplates)
            Error("departments", "count", "Use at most 4096 room templates in a station program.");
        var connectionCount = (document.Connections?.Count ?? 0) +
            templates.Sum(pair => (long) (pair.Room.Connections?.Count ?? 0));
        if (connectionCount > 8192) Error("connections", "count", "Use at most 8192 connection definitions across the entire program.");
        return errors;
    }

    private static void Rooms(List<RoomDefinition>? rooms, string path, List<ProgramDiagnostic> errors, bool requireRooms, int depth = 1, HashSet<RoomDefinition>? active = null)
    {
        if (depth > RoomHierarchy.MaxDepth) { if (rooms is { Count: > 0 }) errors.Add(new(path, "nesting-depth", $"Room nesting supports at most {RoomHierarchy.MaxDepth} levels.")); return; }
        active ??= [];
        if (rooms == null || rooms.Count > 256 || requireRooms && rooms.Count == 0)
        { errors.Add(new(path, "count", requireRooms ? "Define 1–256 room templates per department." : "Define at most 256 true maintenance room templates.")); return; }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < rooms.Count; i++)
        {
            var room = rooms[i]; var p = $"{path}[{i}]";
            if (room == null) { errors.Add(new(p, "missing", "Null rooms are not allowed.")); continue; }
            if (!active.Add(room)) { errors.Add(new(p, "nesting-cycle", "Room declarations must form a tree without cyclic object references.")); continue; }
            Id(room.Id, p + ".id", errors);
            if (!ids.Add(room.Id ?? "")) errors.Add(new(p + ".id", "duplicate", "Room IDs must be unique within their owner."));
            Text(room.Name, p + ".name", 160, errors); Text(room.Description, p + ".description", 4096, errors);
            Sources(room.SourceRefs, p + ".sourceRefs", errors);
            Tags(room.Tags, p + ".tags", errors);
            if (room.Role is not ("activity" or "filler")) errors.Add(new(p + ".role", "enum", "Room role is activity or filler."));
            if (room.Count == null || room.Count.Min < 0 || room.Count.Max > 64 || room.Count.Min > room.Count.Target || room.Count.Target > room.Count.Max)
                errors.Add(new(p + ".count", "range", "Room count must satisfy 0 ≤ min ≤ target ≤ max ≤ 64."));
            else Weight(room.Count.Weight, p + ".count.weight", errors);
            Area(room.Area, p + ".area", false, errors); Shape(room.Shape, p + ".shape", errors);
            if (room.Connections == null || room.Connections.Count > 256) errors.Add(new(p + ".connections", "count", "Use at most 256 connections per room template."));
            if (room.Exterior == null) errors.Add(new(p + ".exterior", "missing", "An exterior policy is required."));
            else if (room.Exterior.MinSpaceFrontage < 0 || room.Exterior.MinSpaceFrontage > 1000000 || !room.Exterior.AllowSpaceExposure && room.Exterior.MinSpaceFrontage != 0)
                errors.Add(new(p + ".exterior", "range", "Space frontage must be nonnegative; rooms excluding exterior frontage cannot require it."));
            if (room.Exterior != null) Docks(room, p + ".exterior.docks", errors);
            if (room.Access == null) errors.Add(new(p + ".access", "missing", "An access policy is required."));
            else
            {
                Text(room.Access.Level, p + ".access.level", 64, errors, allowEmpty: false);
                if (room.Access.EntryOwners == null || room.Access.EntryOwners.Count > 65 || room.Access.EntryOwners.Any(string.IsNullOrWhiteSpace) || room.Access.EntryOwners.Distinct(StringComparer.Ordinal).Count() != room.Access.EntryOwners.Count)
                    errors.Add(new(p + ".access.entryOwners", "range", "Use at most 65 unique nonempty owner references, or [] to allow any owner."));
                if (room.Access.RequireNetworkApproach == null || room.Access.RequireNetworkApproach.Count > 2 || room.Access.RequireNetworkApproach.Any(mode => mode is not ("publicHall" or "maintenance")) || room.Access.RequireNetworkApproach.Distinct(StringComparer.Ordinal).Count() != room.Access.RequireNetworkApproach.Count)
                    errors.Add(new(p + ".access.requireNetworkApproach", "range", "Require zero, one or both unique publicHall/maintenance approach networks."));
            }
            if (room.Environment == null) errors.Add(new(p + ".environment", "missing", "An environment policy is required."));
            else
            {
                if (room.Environment.Pressure is not ("standard" or "low" or "vacuum" or "custom")) errors.Add(new(p + ".environment.pressure", "enum", "Pressure is standard, low, vacuum or custom."));
                if (room.Environment.Placement is not ("interior" or "exterior" or "either")) errors.Add(new(p + ".environment.placement", "enum", "Placement is interior, exterior or either."));
                var minTemperature = room.Environment.MinTemperatureKelvin;
                var maxTemperature = room.Environment.MaxTemperatureKelvin;
                if (minTemperature.HasValue && (!double.IsFinite(minTemperature.Value) || minTemperature.Value is < 0 or > 100000) || maxTemperature.HasValue && (!double.IsFinite(maxTemperature.Value) || maxTemperature.Value is < 0 or > 100000) || minTemperature.HasValue && maxTemperature.HasValue && minTemperature > maxTemperature)
                    errors.Add(new(p + ".environment", "temperature-range", "Temperature bounds must be finite, 0–100000 Kelvin, and minimum cannot exceed maximum."));
            }
            Contents(room, p, errors);
            if (room.Placement == null || room.Placement.Policy is not ("grouped" or "distributed" or "stationwide")) errors.Add(new(p + ".placement", "enum", "Placement policy is grouped, distributed or stationwide."));
            else
            {
                Text(room.Placement.Group, p + ".placement.group", 64, errors);
                if (room.Placement.Anchor is not ("any" or "perimeter")) errors.Add(new(p + ".placement.anchor", "enum", "Room anchor is any or perimeter."));
            }
            if (room.Nesting == null) errors.Add(new(p + ".nesting", "missing", "A nesting policy is required."));
            if (room.ComplexArea != null) Area(room.ComplexArea, p + ".complexArea", false, errors);
            if (room.ComplexShape != null) Shape(room.ComplexShape, p + ".complexShape", errors);
            Rooms(room.Rooms, p + ".rooms", errors, false, depth + 1, active);
            active.Remove(room);
        }
    }

    private static void Contents(RoomDefinition room, string path, List<ProgramDiagnostic> errors)
    {
        if (room.Contents == null || room.Contents.Count > 128)
        { errors.Add(new(path + ".contents", "count", "Use at most 128 equipment requirements per room.")); return; }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long reserved = 0;
        for (var i = 0; i < room.Contents.Count; i++)
        {
            var content = room.Contents[i]; var p = path + $".contents[{i}]";
            if (content == null) { errors.Add(new(p, "missing", "Null equipment requirements are not allowed.")); continue; }
            Id(content.Id, p + ".id", errors);
            if (!ids.Add(content.Id ?? "")) errors.Add(new(p + ".id", "duplicate", "Equipment requirement IDs must be unique within a room."));
            if (content.Prototypes == null || content.Prototypes.Count is < 1 or > 128 || content.Prototypes.Any(value => value == null || !PrototypePattern().IsMatch(value)) || content.Prototypes.Distinct(StringComparer.Ordinal).Count() != content.Prototypes.Count)
                errors.Add(new(p + ".prototypes", "prototype", "List 1–128 unique entity prototype IDs (letters, numbers, underscores and hyphens)."));
            BoundedCount(content.Count, p + ".count", 100000, errors);
            if (content.MinArea is < 0 or > 1000000) errors.Add(new(p + ".minArea", "range", "Equipment floor reservation must be 0–1000000 usable tiles per instance."));
            if (content.Placement is not ("floor" or "boundary" or "spaceBoundary")) errors.Add(new(p + ".placement", "enum", "Equipment placement is floor, boundary or spaceBoundary."));
            if (content.InterfaceType is not ("" or "doorway" or "airlock" or "counter" or "window" or "dockingPort" or "pipe" or "cable")) errors.Add(new(p + ".interfaceType", "enum", "Equipment interface type must be empty or a supported boundary interface."));
            if (content.InterfaceType != "" && content.Placement == "floor") errors.Add(new(p + ".interfaceType", "placement", "An equipment boundary interface requires boundary or spaceBoundary placement."));
            if (content.Count != null) reserved += (long) Math.Max(0, content.Count.Min) * Math.Max(0, content.MinArea);
        }
        if (room.Area != null && reserved > room.Area.Max)
            errors.Add(new(path + ".contents", "equipment-area", $"Required equipment reserves {reserved} usable tiles, exceeding the room maximum {room.Area.Max}."));
    }

    private static void Docks(RoomDefinition room, string path, List<ProgramDiagnostic> errors)
    {
        if (room.Exterior.Docks == null || room.Exterior.Docks.Count > 16) { errors.Add(new(path, "count", "Define at most 16 non-null docking rules per room.")); return; }
        var ids = new HashSet<string>(StringComparer.Ordinal); var contents = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < room.Exterior.Docks.Count; index++)
        {
            var rule = room.Exterior.Docks[index]; var key = path + $"[{index}]";
            if (rule == null) { errors.Add(new(key, "missing", "Docking rules cannot be null.")); continue; }
            Id(rule.Id, key + ".id", errors); Id(rule.ContentId, key + ".contentId", errors);
            if (!ids.Add(rule.Id)) errors.Add(new(key, "duplicate", "Docking rule IDs must be unique within the room."));
            if (!contents.Add(rule.ContentId)) errors.Add(new(key, "duplicate", "Each content family can bind only one docking geometry rule."));
            if (rule.BerthWidth is < 1 or > 128 || rule.BerthDepth is < 1 or > 128 || rule.BerthOffset is < -128 or > 128 || rule.Clearance is < 0 or > 8 || rule.ApproachLength is < 1 or > 512 || rule.ApproachWidth is < 0 or > 256 || rule.ApproachWidth != 0 && rule.ApproachWidth < rule.BerthWidth + 2 * rule.Clearance)
                errors.Add(new(key, "dock-range", "Use berth width/depth 1–128, lateral offset ±128, clearance 0–8, approach length 1–512 and width 0(auto) or at least the cleared berth width up to 256."));
            if (-rule.BerthWidth / 2 + rule.BerthOffset > 0 || -rule.BerthWidth / 2 + rule.BerthOffset + rule.BerthWidth <= 0) errors.Add(new(key + ".berthOffset", "dock-alignment", "The unpadded berth's tangent span must include its docking airlock."));
            if (rule.Directions == null || rule.Directions.Count is < 1 or > 4 || rule.Directions.Any(direction => direction is not ("north" or "east" or "south" or "west")) || rule.Directions.Distinct(StringComparer.Ordinal).Count() != rule.Directions.Count)
                errors.Add(new(key + ".directions", "enum", "Dock directions must be unique cardinal names: north, east, south, west."));
            Sources(rule.SourceRefs, key + ".sourceRefs", errors);
            var content = room.Contents?.FirstOrDefault(content => content?.Id == rule.ContentId);
            if (content == null || content.Placement != "spaceBoundary" || content.InterfaceType != "dockingPort") errors.Add(new(key + ".contentId", "dock-content", "A dock binds a declared spaceBoundary content family with interfaceType:dockingPort."));
            if (content?.Count is { Max: > 64 }) errors.Add(new(key + ".contentId", "dock-count", "A docking content family supports at most 64 physical docking ports per room instance."));
            if (!room.Exterior.AllowSpaceExposure) errors.Add(new(key, "dock-exterior", "A docking room must permit a boundary facing space."));
        }
    }

    internal static void BoundedCount(CountRequirement? count, string path, int maximum, List<ProgramDiagnostic> errors)
    {
        if (count == null || count.Min < 0 || count.Min > count.Target || count.Target > count.Max || count.Max > maximum)
        { errors.Add(new(path, "range", $"Counts must satisfy 0 ≤ min ≤ target ≤ max ≤ {maximum}.")); return; }
        Weight(count.Weight, path + ".weight", errors);
    }

    private static void Sources(List<string>? sources, string path, List<ProgramDiagnostic> errors)
    {
        if (sources == null || sources.Count > 128) { errors.Add(new(path, "count", "Use at most 128 source references.")); return; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            Text(source, path, 2048, errors, allowEmpty: false);
            if (!seen.Add(source ?? "")) errors.Add(new(path, "duplicate", "Source references must be unique."));
        }
    }

    internal static void Area(AreaRequirement? area, string path, bool zeroAllowed, List<ProgramDiagnostic> errors)
    {
        if (area == null || area.Min < (zeroAllowed ? 0 : 1) || area.Min > area.Target || area.Target > area.Max || area.Max > 1000000)
        { errors.Add(new(path, "range", $"Area must satisfy {(zeroAllowed ? "0" : "1")} ≤ min ≤ target ≤ max ≤ 1000000.")); return; }
        Weight(area.Weight, path + ".weight", errors);
    }

    private static void Shape(ShapeRequirement? shape, string path, List<ProgramDiagnostic> errors)
    {
        if (shape == null) { errors.Add(new(path, "missing", "A shape policy is required.")); return; }
        if (!double.IsFinite(shape.MaxAspectRatio) || shape.MaxAspectRatio is < 1 or > 100) errors.Add(new(path + ".maxAspectRatio", "range", "Maximum aspect ratio must be finite and between 1 and 100."));
        if (!double.IsFinite(shape.MinFillRatio) || shape.MinFillRatio is <= 0 or > 1) errors.Add(new(path + ".minFillRatio", "range", "Minimum fill ratio must be finite and in (0, 1]."));
        Weight(shape.Weight, path + ".weight", errors);
    }

    internal static void Weight(double value, string path, List<ProgramDiagnostic> errors)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1000000) errors.Add(new(path, "range", "A pressure weight or penalty must be finite and between 0 and 1000000."));
    }

    private static void Id(string? id, string path, List<ProgramDiagnostic> errors)
    {
        if (id == null || !IdPattern().IsMatch(id)) errors.Add(new(path, "id", "IDs must start with a letter or number and contain 1–64 letters, numbers, spaces, underscores or hyphens; '/' separates paths."));
    }

    internal static void Text(string? text, string path, int max, List<ProgramDiagnostic> errors, bool allowEmpty = true)
    {
        if (text == null || text.Length > max || !allowEmpty && string.IsNullOrWhiteSpace(text)) errors.Add(new(path, "text", $"Provide {(allowEmpty ? "at most" : "1–")} {max} characters."));
    }

    private static void Tags(List<string>? tags, string path, List<ProgramDiagnostic> errors)
    {
        if (tags == null || tags.Count > 64) { errors.Add(new(path, "count", "Use at most 64 tags.")); return; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            Text(tag, path, 64, errors, allowEmpty: false);
            if (!seen.Add(tag ?? "")) errors.Add(new(path, "duplicate", "Tags must be unique."));
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 _-]{0,63}$", RegexOptions.CultureInvariant)] private static partial Regex IdPattern();
    [GeneratedRegex("^#[A-Fa-f0-9]{6}$", RegexOptions.CultureInvariant)] private static partial Regex ColorPattern();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$", RegexOptions.CultureInvariant)] private static partial Regex PrototypePattern();
}
