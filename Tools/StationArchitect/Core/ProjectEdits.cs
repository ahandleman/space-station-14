namespace Content.StationArchitect;

/// <summary>Explicit, reversible edits. Geometry remains a draft with visible constraint violations; requirements are never weakened to accommodate an edit.</summary>
public static class ProjectEdits
{
    public static StationDesignProject Apply(StationDesignProject source, DesignEdit edit)
    {
        if (edit == null) throw new ArgumentException("An edit is required.");
        if (edit.Cells == null || edit.Cells.Count > 65536) throw new ArgumentException("An edit supports at most 65536 cells.");
        var project = ProgramJson.Clone(source);
        var diagnostics = new List<ProgramDiagnostic>();
        var room = project.Layout.Rooms.FirstOrDefault(item => item.Id == edit.NodeId);
        var owner = project.Layout.Departments.FirstOrDefault(item => item.OwnerId == edit.NodeId);
        var stage = "placement";
        var cells = edit.Cells.Distinct().ToHashSet();
        if (cells.Any(cell => Math.Abs((long) cell.X) > 4096 || Math.Abs((long) cell.Y) > 4096) || edit.Position is { } position && (Math.Abs((long) position.X) > 4096 || Math.Abs((long) position.Y) > 4096))
            throw new ArgumentException("Edit coordinates must remain within the bounded planning grid.");
        switch (edit.Kind)
        {
            case "set-count":
                var countOverrides = edit.NodeId.Contains('#') ? project.Selection.ParentRoomCounts : project.Selection.RoomCounts;
                if (edit.Value == null) countOverrides.Remove(edit.NodeId);
                else countOverrides[edit.NodeId] = edit.Value.Value;
                project = InstanceSelection.Resolve(project); diagnostics.AddRange(project.Report!.Diagnostics); stage = "selection";
                break;
            case "set-target":
                RequireRoom(room); room!.TargetAreaOverride = edit.Value; stage = "growth";
                break;
            case "move-room":
                RequireRoom(room); RequirePosition(edit);
                MoveGroup(project, RoomHierarchy.Descendants(project, room!, true).ToList(), edit.Position!.Value - room!.Seed); room.Pinned = true; room.HasSeed = true;
                break;
            case "move-owner":
                if (owner == null) throw new ArgumentException("Select a placed owner to move.");
                RequirePosition(edit);
                var delta = edit.Position!.Value - owner.Anchor;
                MoveGroup(project, project.Layout.Rooms.Where(item => item.OwnerId == owner.OwnerId).ToList(), delta);
                owner.Anchor += delta; owner.Bounds = Translate(owner.Bounds, delta); owner.Cells = Translate(owner.Cells, delta); owner.Pinned = true;
                break;
            case "pin-room": RequireRoom(room); room!.Pinned = edit.Enabled ?? !room.Pinned; break;
            case "lock-room":
                RequireRoom(room); var locked = edit.Enabled ?? !room!.FloorLocked;
                foreach (var child in RoomHierarchy.Descendants(project, room!, true)) child.FloorLocked = locked;
                stage = "growth"; break;
            case "pin-owner":
                if (owner == null) throw new ArgumentException("Select a placed owner to pin.");
                owner.Pinned = edit.Enabled ?? !owner.Pinned; break;
            case "lock-owner":
                if (owner == null) throw new ArgumentException("Select a placed owner to lock.");
                owner.Locked = edit.Enabled ?? !owner.Locked;
                foreach (var child in project.Layout.Rooms.Where(item => item.OwnerId == owner.OwnerId)) child.FloorLocked = owner.Locked;
                stage = "growth"; break;
            case "paint-room":
            case "erase-room":
                RequireRoom(room); stage = "growth";
                var floor = room!.Cells.ToHashSet();
                foreach (var cell in cells)
                {
                    if (edit.Kind == "erase-room") { floor.Remove(cell); continue; }
                    var donor = project.Layout.Rooms.FirstOrDefault(item => item.Id != room.Id && item.Enabled && !item.Orphaned && item.Cells.Contains(cell));
                    if (donor?.FloorLocked == true)
                    { diagnostics.Add(new(room.Id, "locked-donor", $"Tile {cell.X},{cell.Y} belongs to locked room {donor.Id}; it was retained by that room.")); continue; }
                    if (donor != null) { donor.Cells.RemoveAll(item => item == cell); donor.FloorEdited = true; donor.Bounds = Bounds(donor.Cells); }
                    floor.Add(cell);
                }
                room.Cells = Ordered(floor); room.Bounds = Bounds(room.Cells); room.FloorEdited = true; room.FloorLocked = true;
                if (room.Cells.Count > 0 && !room.HasSeed) { room.Seed = room.Cells[0]; room.HasSeed = true; room.Pinned = true; room.GrowthReservation = [.. room.Cells]; }
                break;
            case "paint-envelope":
            case "erase-envelope":
                project.Layout.Envelope = Paint(project.Layout.Envelope, cells, edit.Kind == "paint-envelope");
                project.Layout.EnvelopeEdited = true; break;
            case "paint-public":
            case "erase-public":
                project.Layout.PublicCorridors = Paint(project.Layout.PublicCorridors, cells, edit.Kind == "paint-public");
                project.Layout.PublicEdited = true; stage = "circulation"; break;
            case "paint-maintenance":
            case "erase-maintenance":
                project.Layout.Maintenance = Paint(project.Layout.Maintenance, cells, edit.Kind == "paint-maintenance");
                project.Layout.MaintenanceEdited = true; stage = "circulation"; break;
            case "remove-instance":
                RequireRoom(room);
                var removed = RoomHierarchy.Descendants(project, room!, true).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                var removedFrontage = project.Layout.Rooms.Where(item => removed.Contains(item.Id)).SelectMany(item => item.FrontageSpace).ToHashSet();
                project.Layout.Rooms.RemoveAll(item => removed.Contains(item.Id));
                project.Layout.ReservedSpace = Ordered(project.Layout.ReservedSpace.Where(cell => !removedFrontage.Contains(cell)).Concat(project.Layout.Rooms.SelectMany(item => item.FrontageSpace)).Distinct());
                project.Layout.Equipment.RemoveAll(item => removed.Contains(item.RoomId));
                project.Layout.EnvironmentZones.RemoveAll(item => removed.Contains(item.RoomId));
                project.Layout.Interfaces.RemoveAll(item => removed.Contains(item.From) || removed.Contains(item.To));
                project.Layout.Routes.RemoveAll(item => removed.Contains(item.From) || removed.Contains(item.To));
                project.Layout.Utilities.RemoveAll(item => removed.Contains(item.From) || removed.Contains(item.To));
                project.Layout.TransportNetworks.RemoveAll(item => removed.Contains(item.From) || removed.Contains(item.To));
                project.Layout.Docks.RemoveAll(item => removed.Contains(item.RoomId));
                project.Layout.CirculationReservations.RemoveAll(item => removed.Contains(item.RoomId));
                foreach (var key in project.Selection.ParentRoomCounts.Keys.Where(key => removed.Any(id => key.StartsWith(id + "/", StringComparison.Ordinal))).ToList()) project.Selection.ParentRoomCounts.Remove(key);
                if (RequirementCompiler.Rooms(project.Definition).Any(pair => pair.Path == room!.DefinitionPath))
                {
                    var remaining = project.Layout.Rooms.Count(item => item.DefinitionPath == room!.DefinitionPath && item.ParentInstanceId == room.ParentInstanceId && item.Enabled && !item.Orphaned);
                    if (room!.ParentInstanceId.Length == 0) project.Selection.RoomCounts[room.DefinitionPath] = remaining;
                    else project.Selection.ParentRoomCounts[RoomHierarchy.CountKey(room.ParentInstanceId, room.DefinitionPath)] = remaining;
                }
                else { project.Selection.RoomCounts.Remove(room!.DefinitionPath); project.Selection.AutoRoomCounts.Remove(room.DefinitionPath); }
                DesignRouting.RebuildBoundaries(project);
                stage = "selection"; break;
            case "pin-interface":
            {
                var port = FindInterface(project, edit.NodeId);
                port.Edited = edit.Enabled ?? !port.Edited;
                foreach (var entity in project.Layout.Equipment.Where(entity => entity.InterfaceId == port.Id)) entity.Pinned = port.Edited;
                stage = "circulation"; break;
            }
            case "move-interface":
            {
                var port = FindInterface(project, edit.NodeId); RequirePosition(edit);
                if (port.Cells.Count == 0) throw new ArgumentException("An interface needs boundary cells before it can move.");
                ValidateInterfaceCoordinates([port.FromApproach, port.ToApproach]);
                var movement = edit.Position!.Value - port.Cells[0];
                var translated = Translate(port.Cells, movement); ValidateInterfaceCoordinates(translated);
                var fromApproach = port.FromApproach + movement; var toApproach = port.ToApproach + movement;
                ValidateInterfaceCoordinates([fromApproach, toApproach]);
                port.Cells = translated; port.FromApproach = fromApproach; port.ToApproach = toApproach; port.Edited = true;
                foreach (var entity in project.Layout.Equipment.Where(entity => entity.InterfaceId == port.Id))
                { entity.Position += movement; entity.Footprint = Translate(entity.Footprint, movement); entity.WorkArea = Translate(entity.WorkArea, movement); entity.Pinned = true; }
                DesignRouting.RebuildBoundaries(project); stage = "circulation"; break;
            }
            case "remove-interface":
            {
                var port = FindInterface(project, edit.NodeId);
                project.Layout.Interfaces.Remove(port);
                project.Layout.Equipment.RemoveAll(entity => entity.InterfaceId == port.Id);
                DesignRouting.RebuildBoundaries(project); stage = "circulation"; break;
            }
            case "set-interface":
            {
                var port = FindInterface(project, edit.NodeId);
                var type = edit.TextValue ?? port.Type; var kind = string.IsNullOrEmpty(edit.Layer) ? port.Kind : edit.Layer;
                if (type is not ("doorway" or "airlock" or "counter" or "window" or "dockingPort" or "pipe" or "cable")) throw new ArgumentException("Choose a supported physical interface type.");
                if (kind is not ("walking" or "interaction" or "utility")) throw new ArgumentException("Interface kind must be walking, interaction or utility.");
                if (edit.Value is < 1 or > 12) throw new ArgumentException("Interface width must be between 1 and 12 cells.");
                List<Cell> resized = edit.Value == null ? [.. port.Cells] : ResizeInterface(port, edit.Value.Value);
                ValidateInterfaceCoordinates(resized);
                port.Type = type; port.Kind = kind; port.Cells = resized; port.Width = edit.Value ?? port.Width; port.Edited = true;
                foreach (var entity in project.Layout.Equipment.Where(entity => entity.InterfaceId == port.Id)) entity.Pinned = true;
                var supplied = project.Layout.Equipment.Where(entity => entity.InterfaceId == port.Id).SelectMany(entity => entity.Footprint).ToHashSet();
                if (supplied.Count > 0 && port.Cells.Any(cell => !supplied.Contains(cell))) diagnostics.Add(new(port.Id, "interface-equipment-width", "The resized interface is wider than its linked equipment footprint. Update or unpin the materialized hardware before rebuilding contents."));
                DesignRouting.RebuildBoundaries(project); stage = "circulation"; break;
            }
            case "move-equipment":
            {
                var entity = project.Layout.Equipment.FirstOrDefault(item => item.Id == edit.NodeId) ?? throw new ArgumentException("Select an equipment instance to move.");
                RequirePosition(edit); var movement = edit.Position!.Value - entity.Position;
                entity.Position += movement; entity.Footprint = Translate(entity.Footprint, movement); entity.WorkArea = Translate(entity.WorkArea, movement); entity.Pinned = true; stage = "contents"; break;
            }
            case "pin-equipment":
            {
                var entity = project.Layout.Equipment.FirstOrDefault(item => item.Id == edit.NodeId) ?? throw new ArgumentException("Select an equipment instance to pin.");
                entity.Pinned = edit.Enabled ?? !entity.Pinned; stage = "contents"; break;
            }
            case "remove-equipment":
                project.Layout.Equipment.RemoveAll(item => item.Id == edit.NodeId); stage = "contents"; break;
            case "lock-system-network":
            {
                var network = project.Layout.SystemNetworks.FirstOrDefault(item => item.Id == edit.NodeId) ?? throw new ArgumentException("Select a native utility network.");
                network.Locked = edit.Enabled ?? !network.Locked;
                if (!network.Locked) { network.Edited = false; foreach (var segment in network.Segments) { segment.Edited = false; segment.Locked = false; } }
                stage = NativeStage(project, network.RequirementId); break;
            }
            case "move-system-segment":
            case "rotate-system-segment":
            case "remove-system-segment":
            {
                var network = project.Layout.SystemNetworks.FirstOrDefault(item => item.Segments.Any(segment => segment.Id == edit.NodeId)) ?? throw new ArgumentException("Select a native utility segment.");
                var segment = network.Segments.Single(item => item.Id == edit.NodeId);
                if (edit.Kind == "move-system-segment") { RequirePosition(edit); segment.Position = edit.Position!.Value; }
                else if (edit.Kind == "rotate-system-segment")
                {
                    if (edit.Value == null || edit.Value % 90 != 0) throw new ArgumentException("Native segments require quarter-turn rotations.");
                    segment.Rotation = (edit.Value.Value % 360 + 360) % 360;
                }
                else network.Segments.Remove(segment);
                segment.Edited = true; segment.Locked = true; network.Edited = true; network.Locked = true;
                stage = NativeStage(project, network.RequirementId); break;
            }
            case "set-zone-temperature":
            {
                var zone = project.Layout.EnvironmentZones.FirstOrDefault(item => item.RoomId == edit.NodeId) ?? throw new ArgumentException("Build room contents before editing its environment zone.");
                if (edit.NumberValue is { } temperature && (!double.IsFinite(temperature) || temperature < 0 || temperature > 100000)) throw new ArgumentException("Temperature must be finite and between 0 and 100000 K.");
                zone.TemperatureKelvin = edit.NumberValue; zone.Edited = true; stage = "contents"; break;
            }
            case "reset-zone":
            {
                RequireRoom(room);
                var definition = RequirementCompiler.Rooms(project.Definition).FirstOrDefault(pair => pair.Path == room!.DefinitionPath).Room
                    ?? throw new ArgumentException("The room has no current environment definition.");
                var zone = DesignContents.InitialZone(project, room!, definition);
                project.Layout.EnvironmentZones.RemoveAll(item => item.RoomId == room!.Id);
                project.Layout.EnvironmentZones.Add(zone); stage = "contents"; break;
            }
            case "set-zone-mixture":
            {
                var zone = project.Layout.EnvironmentZones.FirstOrDefault(item => item.RoomId == edit.NodeId) ?? throw new ArgumentException("Build room contents before editing its environment zone.");
                var policy = room == null ? null : RequirementCompiler.Rooms(project.Definition).FirstOrDefault(pair => pair.Path == room.DefinitionPath).Room?.Environment;
                if (zone.Pressure != "custom" || policy?.Pressure != "custom") throw new ArgumentException("Change the room definition's pressure category to custom and rebuild its zone before assigning a custom mixture.");
                var mixture = DesignEnvironmentGases.ParseMixture(edit.TextValue);
                zone.PressureKpa = mixture.PressureKpa; zone.GasFractions = mixture.Fractions; zone.Edited = true; stage = "contents"; break;
            }
            default: throw new ArgumentException($"Unknown design edit '{edit.Kind}'.");
        }
        project.Revision++;
        Invalidate(project, stage, "Edited " + edit.Kind + "; downstream layers remain available for review and require rebuilding.");
        diagnostics.AddRange(Review(project));
        project.Report = new() { Stage = stage, Complete = false, Diagnostics = diagnostics.Distinct().ToList() };
        ProjectTree.Refresh(project);
        return project;
    }

    public static void Invalidate(StationDesignProject project, string stage, string message = "An upstream edit requires this stage to be rebuilt.")
    {
        var index = DesignStages.Index(stage);
        if (index < 0) throw new ArgumentException("Unknown stage.");
        foreach (var key in DesignStages.All)
            if (project.StageStates.All(item => item.Stage != key)) project.StageStates.Add(new() { Stage = key });
        foreach (var state in project.StageStates.Where(item => DesignStages.Index(item.Stage) >= index))
        { state.Status = state.BuiltRevision == null ? "pending" : "dirty"; state.Message = message; }
        project.Stage = stage; project.Report = null; project.Gameflow = null; project.Systems = new();
    }

    private static string NativeStage(StationDesignProject project, string requirementId) =>
        project.Definition.Systems.Networks.FirstOrDefault(rule => rule.Id == requirementId)?.Kind switch
        { "power" => "power", "gas" => "atmos", "disposal" => "disposals", _ => "systemsDevices" };

    public static List<ProgramDiagnostic> Review(StationDesignProject project)
    {
        var compilation = RequirementCompiler.Compile(project.Definition);
        var diagnostics = new List<ProgramDiagnostic>(compilation.Diagnostics);
        if (!compilation.Success) return diagnostics;
        var definitions = RequirementCompiler.Rooms(project.Definition).ToDictionary(pair => pair.Path, pair => pair.Room, StringComparer.Ordinal);
        var rooms = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned).ToList();
        var minimumCounts = RoomHierarchy.Nodes(compilation.Tree!).Where(node => node.Kind == "room").ToDictionary(node => node.Path, node => node.EffectiveMinCount ?? node.Count!.Min, StringComparer.Ordinal);
        var grown = DesignStages.Index(project.Stage) >= DesignStages.Index("growth");
        foreach (var (path, requested) in project.Selection.RoomCounts.Concat(project.Selection.AutoRoomCounts.Where(pair => !project.Selection.RoomCounts.ContainsKey(pair.Key))))
        {
            if (!definitions.TryGetValue(path, out var definition)) diagnostics.Add(new(path, "selection-reference", "This count selection refers to a removed room template."));
            else if (requested < minimumCounts[path] || requested > definition.Count.Max)
                diagnostics.Add(new(path, "selection-count", $"Requested count {requested} lies outside required bounds {minimumCounts[path]}–{definition.Count.Max}; the explicit value was not accepted as a valid selection."));
        }
        if (project.Settings.MaxRoomInstances is < 1 or > 4096 || rooms.Count > project.Settings.MaxRoomInstances)
            diagnostics.Add(new("selection", "selection-limit", "The selected instance total exceeds the configured bounded instance limit."));
        var localCounts = definitions.ToDictionary(pair => pair.Key, pair => project.Selection.RoomCounts.GetValueOrDefault(pair.Key, project.Selection.AutoRoomCounts.GetValueOrDefault(pair.Key, Math.Max(minimumCounts[pair.Key], pair.Value.Count.Target))), StringComparer.Ordinal);
        var requestedTotal = RoomHierarchy.RequestedTotal(project, definitions, localCounts);
        if (requestedTotal > project.Settings.MaxRoomInstances) diagnostics.Add(new("selection", "selection-limit", $"The requested selection contains {requestedTotal} instances, above configured limit {project.Settings.MaxRoomInstances}; a smaller saved layout is not evidence that the request is valid."));
        foreach (var (path, definition) in definitions)
        {
            if (RoomHierarchy.ParentTemplatePath(path) != null) continue;
            var count = rooms.Count(room => room.DefinitionPath == path);
            if (project.Selection.RoomCounts.TryGetValue(path, out var requestedCount) && requestedCount != count) diagnostics.Add(new(path, "selection-count", $"The explicit request is {requestedCount} instances but the retained draft contains {count}. Resolve or edit the request before accepting this selection."));
            if (count < minimumCounts[path] || count > definition.Count.Max) diagnostics.Add(new(path + ".count", "selection-count", $"There are {count} selected instances; the program permits effective count {minimumCounts[path]}–{definition.Count.Max}."));
        }
        bool Selected(string path) => definitions.ContainsKey(path) ? rooms.Any(room => room.DefinitionPath == path) : rooms.Any(room => room.OwnerId == path);
        var validCountKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, definition) in definitions.Where(pair => RoomHierarchy.ParentTemplatePath(pair.Key) != null))
        foreach (var parent in rooms.Where(parent => parent.DefinitionPath == RoomHierarchy.ParentTemplatePath(path)))
        {
            var key = RoomHierarchy.CountKey(parent.Id, path); validCountKeys.Add(key);
            var count = rooms.Count(room => room.DefinitionPath == path && room.ParentInstanceId == parent.Id);
            if ((project.Selection.ParentRoomCounts.TryGetValue(key, out var expected) || project.Selection.RoomCounts.TryGetValue(path, out expected)) && expected != count) diagnostics.Add(new(key, "selection-count", $"This parent's explicit request is {expected} children but its retained draft contains {count}. The requested value was not silently applied."));
            if (count < minimumCounts[path] || count > definition.Count.Max) diagnostics.Add(new(key, "selection-count", $"This parent has {count} selected children; permitted bounds are {minimumCounts[path]} to {definition.Count.Max}."));
            if (project.Selection.ParentRoomCounts.TryGetValue(key, out var requested) && (requested < minimumCounts[path] || requested > definition.Count.Max)) diagnostics.Add(new(key, "selection-count", $"Requested per-parent count {requested} lies outside bounds {minimumCounts[path]} to {definition.Count.Max}."));
        }
        foreach (var key in project.Selection.ParentRoomCounts.Keys.Where(key => !validCountKeys.Contains(key))) diagnostics.Add(new(key, "selection-reference", "This per-parent count refers to a missing parent instance or child template."));
        foreach (var choice in compilation.Choices)
        {
            var count = choice.Rooms.Count(Selected);
            if (count < choice.Count.Min || count > choice.Count.Max) diagnostics.Add(new("choices:" + choice.Id, "selection-choice", $"Selected {count} distinct room templates; the choice requires {choice.Count.Min}–{choice.Count.Max}."));
        }
        foreach (var connection in compilation.Connections.Where(connection => connection.Priority == "required" && connection.Presence == "requireTarget" && Selected(connection.From)))
            if (connection.Selection == "all" ? connection.To.Any(path => !Selected(path)) : !connection.To.Any(Selected))
                diagnostics.Add(new(connection.Id, "selection-presence", "An active required source has no selected target required by this rule."));
        foreach (var room in rooms)
        {
            if (!definitions.TryGetValue(room.DefinitionPath, out var definition)) { diagnostics.Add(new(room.Id, "room-reference", "This room has no current definition.")); continue; }
            var parentPath = RoomHierarchy.ParentTemplatePath(room.DefinitionPath);
            if (room.Id != RoomHierarchy.InstanceId(room.DefinitionPath, room.Instance, room.ParentInstanceId) || parentPath == null && room.ParentInstanceId.Length != 0 || parentPath != null && !rooms.Any(parent => parent.Id == room.ParentInstanceId && parent.DefinitionPath == parentPath)) diagnostics.Add(new(room.Id, "room-parent", "The room must keep its canonical runtime identity and an active instance of its immediate parent template."));
            var area = room.Cells.Distinct().Count();
            if (definition.ComplexArea is { } complex)
            {
                var subtree = RoomHierarchy.Descendants(project, room, true).Where(child => child.Enabled && !child.Orphaned).Sum(child => child.Cells.Distinct().LongCount());
                if (subtree > complex.Max || (grown || subtree > 0) && subtree < complex.Min) diagnostics.Add(new(room.Id, "complex-area-bound", $"The complex owns {subtree} usable tiles, outside {complex.Min} to {complex.Max}."));
            }
            if (room.TargetAreaOverride is { } target && (target < RequirementCompiler.EffectiveMinimumArea(definition) || target > definition.Area.Max)) diagnostics.Add(new(room.Id, "target-bound", "The local target override lies outside the room's effective hard floor bounds."));
            if (area > definition.Area.Max || (grown || area > 0) && area < RequirementCompiler.EffectiveMinimumArea(definition)) diagnostics.Add(new(room.Id, "area-bound", $"The edited room owns {area} tiles; its effective floor bounds are {RequirementCompiler.EffectiveMinimumArea(definition)}–{definition.Area.Max}."));
        }
        foreach (var owner in compilation.Tree!.Children)
        {
            var area = rooms.Where(room => room.OwnerId == owner.Path).Sum(room => room.Cells.Distinct().LongCount());
            if (area > owner.Area!.Max || (grown || area > 0) && area < owner.Area.Min) diagnostics.Add(new(owner.Path, "owner-area-bound", $"Owned room floor {area} lies outside department bounds {owner.Area.Min}–{owner.Area.Max}."));
        }
        foreach (var room in project.Layout.Rooms.Where(room => room.Orphaned)) diagnostics.Add(new(room.Id, "orphaned-instance", "Preserved stale geometry must be removed or restored to the current selection."));
        return diagnostics;
    }

    private static void Move(StationDesignProject project, DesignRoom room, Cell movement)
    {
        room.Seed += movement; room.Cells = Translate(room.Cells, movement); room.GrowthReservation = Translate(room.GrowthReservation, movement); room.FrontageFloor = Translate(room.FrontageFloor, movement); room.FrontageSpace = Translate(room.FrontageSpace, movement); room.ComplexReservation = Translate(room.ComplexReservation, movement); room.Bounds = Translate(room.Bounds, movement); room.Parcel = Translate(room.Parcel, movement);
        foreach (var entity in project.Layout.Equipment.Where(item => item.RoomId == room.Id)) { entity.Position += movement; entity.Footprint = Translate(entity.Footprint, movement); entity.WorkArea = Translate(entity.WorkArea, movement); }
        foreach (var zone in project.Layout.EnvironmentZones.Where(item => item.RoomId == room.Id)) zone.Cells = Translate(zone.Cells, movement);
    }
    private static void RequireRoom(DesignRoom? room) { if (room == null) throw new ArgumentException("Select a room instance."); }
    private static void MoveGroup(StationDesignProject project, List<DesignRoom> rooms, Cell movement)
    {
        var ids = rooms.Select(room => room.Id).ToHashSet(StringComparer.Ordinal);
        var previousFrontage = rooms.SelectMany(room => room.FrontageSpace).ToHashSet();
        foreach (var room in rooms) Move(project, room, movement);
        project.Layout.ReservedSpace = Ordered(project.Layout.ReservedSpace.Where(cell => !previousFrontage.Contains(cell)).Concat(project.Layout.Rooms.SelectMany(room => room.FrontageSpace)).Distinct());
        foreach (var dock in project.Layout.Docks.Where(dock => ids.Contains(dock.RoomId))) DesignDockGeometry.Translate(project, dock, movement);
        foreach (var reservation in project.Layout.CirculationReservations.Where(reservation => ids.Contains(reservation.RoomId))) DesignDockGeometry.Translate(reservation, movement);
        foreach (var port in project.Layout.Interfaces.Where(port => ids.Contains(port.From) || ids.Contains(port.To)))
        { port.Cells = Translate(port.Cells, movement); port.FromApproach += movement; port.ToApproach += movement; port.Edited = true; }
        foreach (var route in project.Layout.Routes.Where(route => ids.Contains(route.From) && ids.Contains(route.To))) route.Cells = Translate(route.Cells, movement);
        foreach (var utility in project.Layout.Utilities.Where(utility => ids.Contains(utility.From) && ids.Contains(utility.To))) utility.Cells = Translate(utility.Cells, movement);
        foreach (var network in project.Layout.TransportNetworks.Where(network => network.Terminals.Any(terminal => ids.Contains(terminal.RoomId))))
        {
            foreach (var terminal in network.Terminals.Where(terminal => ids.Contains(terminal.RoomId))) { terminal.Position += movement; terminal.Footprint = Translate(terminal.Footprint, movement); }
            if (network.Terminals.All(terminal => ids.Contains(terminal.RoomId)))
            {
                network.Cells = Translate(network.Cells, movement);
                foreach (var segment in network.Segments) segment.Position += movement;
            }
            network.Edited = true;
        }
        DesignRouting.RebuildBoundaries(project);
    }
    private static void RequirePosition(DesignEdit edit) { if (edit.Position == null) throw new ArgumentException("This edit needs a tile position."); }
    private static DesignInterface FindInterface(StationDesignProject project, string id) => project.Layout.Interfaces.FirstOrDefault(port => port.Id == id) ?? throw new ArgumentException("Select a physical interface.");
    private static List<Cell> ResizeInterface(DesignInterface port, int width)
    {
        if (port.Cells.Count == 0) throw new ArgumentException("An interface needs an existing boundary cell before it can be resized.");
        var first = port.Cells[0]; var distinct = port.Cells.Distinct().ToList(); Cell tangent;
        if (distinct.Count > 1)
        {
            var horizontal = distinct.All(cell => cell.Y == first.Y); var vertical = distinct.All(cell => cell.X == first.X);
            if (!horizontal && !vertical) throw new ArgumentException("Resize requires one straight existing interface run.");
            if (distinct.Count != (horizontal ? distinct.Max(cell => cell.X) - distinct.Min(cell => cell.X) + 1 : distinct.Max(cell => cell.Y) - distinct.Min(cell => cell.Y) + 1))
                throw new ArgumentException("Resize requires a contiguous existing interface run.");
            var next = distinct.First(cell => cell != first);
            tangent = horizontal ? new(Math.Sign(next.X - first.X), 0) : new(0, Math.Sign(next.Y - first.Y));
        }
        else
        {
            var normal = port.ToApproach - port.FromApproach;
            if (normal.X == 0 && normal.Y != 0) tangent = new(1, 0);
            else if (normal.Y == 0 && normal.X != 0) tangent = new(0, 1);
            else throw new ArgumentException("A one-cell interface needs opposing approach cells to derive its resizing direction.");
        }
        return Enumerable.Range(0, width).Select(index => new Cell(first.X + tangent.X * index, first.Y + tangent.Y * index)).ToList();
    }
    private static void ValidateInterfaceCoordinates(List<Cell> cells)
    {
        if (cells.Any(cell => Math.Abs((long) cell.X) > 4096 || Math.Abs((long) cell.Y) > 4096)) throw new ArgumentException("An interface edit exceeds the bounded planning coordinates.");
    }
    private static List<Cell> Translate(IEnumerable<Cell> cells, Cell movement) => cells.Select(cell => cell + movement).ToList();
    private static GridBounds Translate(GridBounds bounds, Cell movement) => new(bounds.X + movement.X, bounds.Y + movement.Y, bounds.Width, bounds.Height);
    private static List<Cell> Paint(IEnumerable<Cell> original, HashSet<Cell> brush, bool add) { var cells = original.ToHashSet(); if (add) cells.UnionWith(brush); else cells.ExceptWith(brush); return Ordered(cells); }
    private static List<Cell> Ordered(IEnumerable<Cell> cells) => cells.OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToList();
    private static GridBounds Bounds(List<Cell> cells) => cells.Count == 0 ? default : new(cells.Min(cell => cell.X), cells.Min(cell => cell.Y), cells.Max(cell => cell.X) - cells.Min(cell => cell.X) + 1, cells.Max(cell => cell.Y) - cells.Min(cell => cell.Y) + 1);
}
