using System.Text.Json;

namespace Content.StationArchitect;

/// <summary>Stage coordinator. Authoring requirements remain authoritative in every saved and edited state.</summary>
public static class DesignPipeline
{
    public static StationDesignProject New(StationProgramDocument? definition = null)
    {
        var project = new StationDesignProject { Definition = ProgramJson.Clone(definition ?? StationProgramDefaults.Create()) };
        project = InstanceSelection.Resolve(project);
        var selectionDiagnostics = project.Report?.Diagnostics.ToList() ?? [];
        project.Stage = "program";
        project.Report = DefinitionReport(project);
        project.Report.Diagnostics = project.Report.Diagnostics.Concat(selectionDiagnostics).Distinct().ToList();
        var programState = project.StageStates.First(state => state.Stage == "program");
        programState.Status = project.Report.Success ? "ready" : "needsWork";
        programState.BuiltRevision = project.Revision;
        project.Tree = ProjectTree.Build(project);
        return project;
    }

    public static StationDesignProject Import(StationDesignProject source)
    {
        CheckStructure(source);
        var project = ProgramJson.Clone(source);
        project.Tree = null;
        project.Gameflow = null;
        project.Systems = new();
        EnsureStageStates(project);
        // Saved acceptance and tree annotations are not evidence about the imported geometry.
        project.Report = project.Layout.Rooms.Any(room => room.Cells.Count > 0)
            ? DesignGeometryValidator.Validate(project, project.Stage == "validation", refreshReports: true)
            : DefinitionReport(project);
        project.Tree = ProjectTree.Build(project);
        return project;
    }

    public static StationDesignProject UpdateDefinition(StationDesignProject source, StationProgramDocument definition)
    {
        CheckStructure(source);
        var project = ProgramJson.Clone(source);
        var oldWithNewGameflow = ProgramJson.Clone(project.Definition);
        oldWithNewGameflow.Gameflow = ProgramJson.Clone(definition.Gameflow);
        var gameflowOnly = JsonSerializer.Serialize(oldWithNewGameflow, ProgramJson.Options) == JsonSerializer.Serialize(definition, ProgramJson.Options);
        var oldWithNewSystems = ProgramJson.Clone(project.Definition);
        oldWithNewSystems.Systems = ProgramJson.Clone(definition.Systems);
        var systemsOnly = JsonSerializer.Serialize(oldWithNewSystems, ProgramJson.Options) == JsonSerializer.Serialize(definition, ProgramJson.Options);
        project.Definition = ProgramJson.Clone(definition);
        project.Revision++;
        Invalidate(project, gameflowOnly ? "gameflow" : systemsOnly ? "systemsDevices" : "selection", "Requirements changed; existing geometry is retained for review.");
        if (!gameflowOnly && !systemsOnly) project = InstanceSelection.Resolve(project);
        var selectionDiagnostics = project.Report?.Diagnostics.ToList() ?? [];
        project.Stage = source.Stage;
        project.Report = project.Layout.Rooms.Any(room => room.Cells.Count > 0)
            ? DesignGeometryValidator.Validate(project, refreshReports: true)
            : DefinitionReport(project);
        project.Report.Diagnostics = project.Report.Diagnostics.Concat(selectionDiagnostics).Concat(ProjectEdits.Review(project)).Distinct().ToList();
        project.Tree = ProjectTree.Build(project);
        return project;
    }

    public static StationDesignProject UpdateSettings(StationDesignProject source, DesignSettings settings)
    {
        CheckStructure(source);
        var project = ProgramJson.Clone(source);
        EnsureStageStates(project);
        project.Settings = ProgramJson.Clone(settings);
        CheckStructure(project);
        project.Revision++;
        Invalidate(project, "placement", "Planning settings changed; rerun placement to use them.");
        project.Report = project.Layout.Rooms.Any(room => room.Cells.Count > 0)
            ? DesignGeometryValidator.Validate(project, refreshReports: true)
            : DefinitionReport(project);
        project.Tree = ProjectTree.Build(project);
        return project;
    }

    public static StationDesignProject RunStage(StationDesignProject source, string stage, int? placementAttempt = null)
    {
        CheckStructure(source);
        if (DesignStages.Index(stage) < 0) throw new ArgumentException("Unknown design stage.");
        var project = ProgramJson.Clone(source);
        EnsureStageStates(project);
        if (placementAttempt != null)
        {
            if (stage != "placement") throw new ArgumentException("Only placement accepts a candidate-attempt override.");
            if (placementAttempt < 0 || placementAttempt >= project.Settings.CandidateBudget) throw new ArgumentException("The placement attempt exceeds its configured candidate budget.");
            project.Layout.PlacementAttempt = placementAttempt.Value;
        }
        if (!RequirementCompiler.Compile(project.Definition).Success)
        {
            project.Report = DefinitionReport(project);
            project.Tree = ProjectTree.Build(project);
            return project;
        }
        var nextStage = DesignStages.Index(stage) + 1;
        if (nextStage < DesignStages.All.Length) Invalidate(project, DesignStages.All[nextStage], "An earlier stage was rebuilt; review dependent stages.");
        project.Report = null;
        if (DesignStages.Index(stage) <= DesignStages.Index("gameflow")) project.Gameflow = null;
        if (DesignStages.Index(stage) >= DesignStages.Index("placement") && stage != "validation")
        {
            if (!project.Layout.Rooms.Any(room => room.Enabled && !room.Orphaned))
            {
                project = InstanceSelection.Resolve(project);
                if (!project.Report!.Success) return project;
                project.Report = null;
            }
            var definitions = RequirementCompiler.Rooms(project.Definition).ToDictionary(pair => pair.Path, pair => pair.Room, StringComparer.Ordinal);
            var capacity = project.Settings.AutoSize ? 512L * 512 : (long) project.Settings.Width * project.Settings.Height;
            var floorDemand = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && definitions.ContainsKey(room.DefinitionPath))
                .Sum(room => (long) Math.Max(RequirementCompiler.EffectiveMinimumArea(definitions[room.DefinitionPath]), room.TargetAreaOverride ?? definitions[room.DefinitionPath].Area.Target));
            if (floorDemand > capacity)
            {
                project.Report = new() { Stage = stage, Diagnostics = [new("layout", "planning-capacity", $"Selected room growth targets request {floorDemand} usable tiles before walls and networks, exceeding this designer's {capacity}-tile canvas capacity. Adjust counts, targets or canvas settings; existing geometry is retained.")] };
                project.Tree = ProjectTree.Build(project);
                return project;
            }
        }
        project = stage switch
        {
            "program" => project,
            "selection" => InstanceSelection.Resolve(project),
            "placement" => DesignPlacement.Run(InstanceSelection.Resolve(project), project.Layout.PlacementAttempt),
            "growth" => RoomGrowth.Run(EnsurePlacement(project)),
            "circulation" => DesignRouting.Run(EnsureGrowth(project)),
            "gameflow" => DesignGameflow.Run(EnsureCirculation(project)),
            "contents" => DesignContents.Run(EnsureGameflow(project)),
            "systemsDevices" => DesignSystemsGeneration.RunDevices(EnsureContents(project)),
            "power" => DesignSystemsGeneration.Run(EnsureSystemsDevices(project), "power"),
            "atmos" => DesignSystemsGeneration.Run(EnsurePower(project), "gas"),
            "disposals" => DesignSystemsGeneration.Run(EnsureAtmos(project), "disposal"),
            "validation" => project,
            _ => throw new ArgumentException("Unknown design stage.")
        };
        project.Stage = stage;
        var stageDiagnostics = project.Report?.Diagnostics.ToList() ?? [];
        var stageGameflow = stage == "gameflow" ? project.Gameflow : null;
        project.Report = stage is "program" or "selection" ? DefinitionReport(project) : DesignGeometryValidator.Validate(project, stage == "validation", refreshReports: true);
        // Only retain repair provenance produced by this operation, never a submitted saved report.
        if (stageGameflow != null && project.Gameflow?.GeometryHash == stageGameflow.GeometryHash && project.Gameflow.DefinitionHash == stageGameflow.DefinitionHash)
            project.Gameflow.Repairs = stageGameflow.Repairs;
        project.Report.Diagnostics = project.Report.Diagnostics.Concat(ProjectEdits.Review(project)).Distinct().ToList();
        // Solver diagnostics explain why a stage could not complete. Validation is independently rerun.
        if (stage != "validation")
            project.Report.Diagnostics = project.Report.Diagnostics.Concat(stageDiagnostics).Distinct().ToList();
        var state = project.StageStates.FirstOrDefault(item => item.Stage == stage);
        if (state != null)
        {
            state.Status = project.Report.Success ? "ready" : "needsWork";
            state.BuiltRevision = project.Revision;
            state.Message = project.Report.Success ? "Built from the current requirements." : "Review the stage diagnostics; the draft is retained.";
        }
        project.Tree = ProjectTree.Build(project);
        return project;
    }

    public static StationDesignProject Generate(StationDesignProject source)
    {
        CheckStructure(source);
        var count = HasProtectedGeometry(source) ? 1 : source.Settings.CandidateBudget;
        StationDesignProject? best = null;
        for (var attempt = 0; attempt < count; attempt++)
        {
            var project = ProgramJson.Clone(source);
            project = RunStage(project, "selection");
            if (!project.Report!.Success) return project;
            project.Layout.PlacementAttempt = count == 1 ? source.Layout.PlacementAttempt : attempt;
            foreach (var stage in DesignStages.All.Skip(2)) project = RunStage(project, stage);
            if (best == null || Better(project, best)) best = project;
            best.Report!.Metrics["candidatesTried"] = attempt + 1;
            if (project.Report!.Success && GameflowTargetsMet(project)) return best;
        }
        return best!;
    }

    public static bool HasProtectedGeometry(StationDesignProject project) => project.Layout.EnvelopeEdited || project.Layout.PublicEdited || project.Layout.MaintenanceEdited ||
        project.Layout.Rooms.Any(room => room.Pinned || room.FloorLocked || room.FloorEdited) || project.Layout.Departments.Any(owner => owner.Pinned || owner.Locked) ||
        project.Layout.Interfaces.Any(port => port.Edited) || project.Layout.Routes.Any(route => route.Edited || route.Locked) || project.Layout.Equipment.Any(entity => entity.Pinned) ||
        project.Layout.EnvironmentZones.Any(zone => zone.Edited) || project.Layout.TransportNetworks.Any(network => network.Edited || network.Locked) ||
        project.Layout.Docks.Any(dock => dock.Edited || dock.Locked) || project.Layout.CirculationReservations.Any(reservation => reservation.Edited || reservation.Locked) ||
        project.Layout.SystemDevices.Any(device => device.Edited || device.Locked) || project.Layout.SystemNetworks.Any(network => network.Edited || network.Locked || network.Segments.Any(segment => segment.Edited || segment.Locked)) ||
        project.Layout.SystemControls.Any(control => control.Edited || control.Locked);

    private static bool Better(StationDesignProject candidate, StationDesignProject previous)
    {
        var errors = candidate.Report!.Diagnostics.Count(item => item.Severity == "error");
        var previousErrors = previous.Report!.Diagnostics.Count(item => item.Severity == "error");
        return errors < previousErrors || errors == previousErrors && GenerationCost(candidate) < GenerationCost(previous);
    }

    public static double GenerationCost(StationDesignProject project) => (project.Report?.Pressure?.Cost ?? double.MaxValue / 2) + (project.Gameflow?.TotalCost ?? 0);
    private static bool GameflowTargetsMet(StationDesignProject project) => !project.Definition.Gameflow.Rules.Any(rule => rule.Enabled) ||
        project.Gameflow is { Complete: true } flow && flow.Rules.All(rule => rule.Observed && rule.Satisfied && rule.TargetMet);

    public static void Invalidate(StationDesignProject project, string fromStage, string reason)
    {
        var first = DesignStages.Index(fromStage);
        EnsureStageStates(project);
        foreach (var state in project.StageStates.Where(state => DesignStages.Index(state.Stage) >= first))
        {
            state.Status = state.BuiltRevision == null ? "pending" : "stale";
            state.Message = reason;
        }
        project.Report = null;
        project.Systems = new();
        if (first <= DesignStages.Index("gameflow")) project.Gameflow = null;
    }

    private static void EnsureStageStates(StationDesignProject project)
    {
        foreach (var stage in DesignStages.All)
            if (project.StageStates.All(state => state.Stage != stage)) project.StageStates.Add(new() { Stage = stage });
        project.StageStates = project.StageStates.OrderBy(state => DesignStages.Index(state.Stage)).ToList();
    }

    public static void CheckStructure(StationDesignProject? project)
    {
        if (project == null || project.Version != 1 || project.Definition == null || project.Settings == null || project.Selection == null || project.Selection.RoomCounts == null || project.Selection.AutoRoomCounts == null || project.Selection.ParentRoomCounts == null || project.Layout == null || project.StageStates == null)
            throw new ArgumentException("Provide a version-1 Station Architect design project.");
        if (DesignStages.Index(project.Stage) < 0) throw new ArgumentException("The project has an unknown stage.");
        if (project.StageStates.Count > 16 || project.StageStates.Any(state => state == null || DesignStages.Index(state.Stage) < 0) || project.StageStates.Select(state => state.Stage).Distinct(StringComparer.Ordinal).Count() != project.StageStates.Count)
            throw new ArgumentException("Design stage records must use unique, supported stage names.");
        var settings = project.Settings;
        var packing = settings.Packing;
        if (packing == null || !double.IsFinite(packing.InternalVariation) || packing.InternalVariation is < 0 or > 1 || !double.IsFinite(packing.SilhouetteVariation) || packing.SilhouetteVariation is < 0 or > 1 || !double.IsFinite(packing.Interlock) || packing.Interlock is < 0 or > 1 || !double.IsFinite(packing.Compactness) || packing.Compactness is < 0 or > 1 || !double.IsFinite(packing.DepartmentBundling) || packing.DepartmentBundling is < 0 or > 1 || !double.IsFinite(packing.PreferredAspectRatio) || packing.PreferredAspectRatio is < .5 or > 2 || packing.DistributedClusters is < 1 or > 12 || packing.MaxRoomsPerCluster is < 1 or > 64 || packing.StationwideSpacing is < 2 or > 64 || packing.BoundaryGrowthBudget is < 0 or > 20000 || !double.IsFinite(packing.MaintenanceCoverage) || packing.MaintenanceCoverage is < 0 or > 1 || packing.MaintenanceWrapDistance is < 2 or > 12 || packing.MaintenanceConnectionsPerCluster is < 1 or > 8)
            throw new ArgumentException("Packing settings need bounded compactness, bundling, cluster size, growth and maintenance coverage values.");
        if (settings.MaxRoomInstances is < 1 or > 4096 || project.Selection.RoomCounts.Count > 4096 || project.Selection.AutoRoomCounts.Count > 4096 || project.Selection.ParentRoomCounts.Count > 4096)
            throw new ArgumentException("The interactive designer supports 1–4096 selected room instances and bounded count overrides.");
        if (settings.TransportProfiles == null || settings.TransportProfiles.Count > 64 || settings.TransportProfiles.Any(profile => profile == null || profile.TrunkExits == null || profile.StraightExits == null || profile.BendExits == null || profile.SourceRefs == null) || settings.PrototypeFootprints == null || settings.Export == null || settings.Export.AccessTags == null || settings.Export.OwnerAccessTags == null || settings.Export.Jobs == null || settings.PrototypeFootprints.Count > 16384 || settings.PrototypeFootprints.Any(pair => pair.Value == null || pair.Value.Width is < 1 or > 16 || pair.Value.Height is < 1 or > 16 || pair.Value.OffsetX is < -16 or > 16 || pair.Value.OffsetY is < -16 or > 16))
            throw new ArgumentException("Export and footprint profiles need bounded, non-null values.");
        if (settings.Export.AccessTags.Count > 256 || settings.Export.OwnerAccessTags.Count > 256 || settings.Export.Jobs.Count > 256 || settings.Export.AccessTags.Concat(settings.Export.OwnerAccessTags).Any(pair => pair.Key.Length > 128 || pair.Value == null || pair.Value.Count > 64 || pair.Value.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 128)) || settings.Export.Jobs.Any(pair => pair.Key.Length > 128 || pair.Value == null || pair.Value.Length != 2 || pair.Value.Any(count => count is < -1 or > 1000)))
            throw new ArgumentException("Access and job profiles have invalid labels, tags or slot bounds.");
        foreach (var property in typeof(DesignExportSettings).GetProperties().Where(property => property.PropertyType == typeof(string)))
            if (property.GetValue(settings.Export) is not string value || string.IsNullOrWhiteSpace(value) || value.Length > 128)
                throw new ArgumentException("Export prototype and tile names must contain 1–128 characters.");
        if (settings.Width is < 32 or > 512 || settings.Height is < 32 or > 512 || settings.PublicWidth is < 2 or > 6 || settings.MaintenanceWidth is < 2 or > 3 || settings.MaintenanceMaxWidth < settings.MaintenanceWidth || settings.MaintenanceMaxWidth > 3 || settings.DepartmentGap is < 3 or > 32 || settings.RoomGap is < 1 or > 4 || settings.CandidateBudget is < 1 or > 32 || settings.GrowthIterations is < 0 or > 2000000 || settings.OptimizationIterations is < 0 or > 500000 || settings.EnvelopePadding is < 2 or > 32)
            throw new ArgumentException("Planning dimensions, widths or solver budgets are outside their supported bounds.");
        if (settings.Topology is not ("ring" or "hub" or "spine")) throw new ArgumentException("Topology is ring, hub or spine.");
        var layout = project.Layout;
        if (layout.Rooms == null || layout.Departments == null || layout.Envelope == null || layout.PublicCorridors == null || layout.PublicWaypoints == null || layout.PublicBackbone == null || layout.Maintenance == null || layout.Walls == null || layout.Hull == null || layout.Windows == null || layout.ReservedSpace == null || layout.Interfaces == null || layout.Routes == null || layout.Equipment == null || layout.Utilities == null || layout.EnvironmentZones == null || layout.TransportNetworks == null || layout.Docks == null || layout.CirculationReservations == null)
            throw new ArgumentException("Design layer collections cannot be null.");
        if (layout.PlacementAttempt is < 0 or > 31 || layout.Rooms.Count > 4096 || layout.Departments.Count > 65 || layout.Interfaces.Count > 32768 || layout.Routes.Count > 32768 || layout.Equipment.Count > 100000 || layout.Utilities.Count > 32768 || layout.Docks.Count > 32768 || layout.CirculationReservations.Count > 32768)
            throw new ArgumentException("The design exceeds its bounded instance, route or equipment count.");
        if (layout.Rooms.Any(room => room == null || room.Id == null || room.DefinitionPath == null || room.ParentInstanceId == null || room.Instance == null || room.Cells == null || room.GrowthReservation == null || room.FrontageFloor == null || room.FrontageSpace == null || room.ComplexReservation == null) || layout.Departments.Any(department => department == null || department.Cells == null) || layout.Interfaces.Any(port => port == null || port.Cells == null) || layout.Routes.Any(route => route == null || route.Cells == null || route.InterfaceIds == null) || layout.Equipment.Any(entity => entity == null || entity.Footprint == null || entity.WorkArea == null) || layout.Utilities.Any(utility => utility == null || utility.Cells == null) || layout.EnvironmentZones.Any(zone => zone == null || zone.Cells == null || zone.GasFractions == null))
            throw new ArgumentException("Design records and their cell collections cannot be null.");
        if (layout.Docks.Any(dock => dock == null || dock.Id == null || dock.RoomId == null || dock.RequirementId == null || dock.ContentId == null || dock.InterfaceId == null || dock.EntityId == null || dock.BoundaryCells == null || dock.BerthCells == null || dock.ApproachCells == null || dock.Instance is < 1 or > 64) ||
            layout.CirculationReservations.Any(reservation => reservation == null || reservation.Id == null || reservation.RoomId == null || reservation.Network is not ("publicHall" or "maintenance") || reservation.BoundaryCells == null || reservation.ApproachCells == null || reservation.Width is < 1 or > 12))
            throw new ArgumentException("Dock and circulation reservations need bounded, non-null records and cell collections; circulation networks must be publicHall or maintenance.");
        foreach (var bounds in layout.Rooms.SelectMany(room => new[] { room.Bounds, room.Parcel }).Concat(layout.Departments.Select(owner => owner.Bounds)))
            if (Math.Abs((long) bounds.X) > 4096 || Math.Abs((long) bounds.Y) > 4096 || bounds.Width is < 0 or > 4096 || bounds.Height is < 0 or > 4096)
                throw new ArgumentException("Planning bounds must stay within the supported coordinate range.");
        if (layout.TransportNetworks.Count > 8192 || layout.TransportNetworks.Any(network => network == null || network.Cells == null || network.Segments == null || network.Terminals == null || network.Segments.Count > 65536 || network.Terminals.Count > 64 || network.Segments.Any(segment => segment == null) || network.Terminals.Any(terminal => terminal == null || terminal.Footprint == null)))
            throw new ArgumentException("Transport networks require bounded, non-null paths, segments and terminals.");
        if (layout.SystemDevices == null || layout.SystemNetworks == null || layout.SystemControls == null || layout.SystemDevices.Count > 16384 || layout.SystemNetworks.Count > 8192 || layout.SystemControls.Count > 16384 ||
            layout.SystemDevices.Any(device => device == null || device.Id == null || device.RequirementId == null || device.EntityId == null || device.ProfileId == null || device.ScopeId == null) ||
            layout.SystemControls.Any(control => control == null || control.Id == null || control.RequirementId == null || control.ControllerDeviceId == null || control.DeviceIds == null || control.DeviceIds.Count > 4096 || control.DeviceIds.Any(id => id == null)) ||
            layout.SystemNetworks.Any(network => network == null || network.Id == null || network.RequirementId == null || network.ScopeId == null || network.Terminals == null || network.Segments == null || network.Terminals.Count > 16384 || network.Segments.Count > 65536 ||
                network.Terminals.Any(terminal => terminal == null || terminal.DeviceId == null || terminal.PortId == null || terminal.Role == null) ||
                network.Segments.Any(segment => segment == null || segment.Id == null || segment.ProfileId == null || segment.Rotation % 90 != 0 || Math.Abs((long)segment.Position.X) > 4096 || Math.Abs((long)segment.Position.Y) > 4096)) ||
            layout.SystemNetworks.Sum(network => (long)network.Segments.Count) > 262144 || layout.Equipment.Any(entity => entity.SystemRuleId == null))
            throw new ArgumentException("Native systems require bounded, non-null device, segment, terminal and controller records with cardinal rotations.");
        if (layout.PublicWaypoints.Count > 256) throw new ArgumentException("Use at most 256 topology waypoints.");
        var layers = new[] { layout.Envelope, layout.PublicCorridors, layout.PublicWaypoints, layout.PublicBackbone, layout.Maintenance, layout.Walls, layout.Hull, layout.Windows, layout.ReservedSpace }
            .Concat(layout.Rooms.Select(room => room.Cells)).Concat(layout.Routes.Select(route => route.Cells))
            .Concat(layout.Docks.SelectMany(dock => new[] { dock.BoundaryCells, dock.BerthCells, dock.ApproachCells }))
            .Concat(layout.CirculationReservations.SelectMany(reservation => new[] { reservation.BoundaryCells, reservation.ApproachCells }))
            .Concat(layout.Rooms.Select(room => room.GrowthReservation)).Concat(layout.Rooms.Select(room => room.FrontageFloor)).Concat(layout.Rooms.Select(room => room.FrontageSpace)).Concat(layout.Rooms.Select(room => room.ComplexReservation)).Concat(layout.Equipment.Select(entity => entity.Footprint)).Concat(layout.Equipment.Select(entity => entity.WorkArea))
            .Concat(layout.Interfaces.Select(port => port.Cells)).Concat(layout.Utilities.Select(utility => utility.Cells)).Concat(layout.EnvironmentZones.Select(zone => zone.Cells))
            .Concat(layout.TransportNetworks.Select(network => network.Cells)).Concat(layout.TransportNetworks.SelectMany(network => network.Terminals.Select(terminal => terminal.Footprint)))
            .Append(layout.TransportNetworks.SelectMany(network => network.Segments.Select(segment => segment.Position)).ToList());
        long total = 0;
        foreach (var cells in layers)
        {
            if (cells.Count > 262144 || (total += cells.Count) > 2000000 || cells.Any(cell => Math.Abs((long) cell.X) > 4096 || Math.Abs((long) cell.Y) > 4096))
                throw new ArgumentException("Design masks or coordinates exceed the supported planning bounds.");
        }
    }

    private static StationDesignProject EnsurePlacement(StationDesignProject project) => project.Layout.Rooms.Any(room => room.Enabled && !room.Orphaned && !room.HasSeed)
        || project.Layout.Envelope.Count == 0 ? DesignPlacement.Run(InstanceSelection.Resolve(project), project.Layout.PlacementAttempt) : project;
    private static StationDesignProject EnsureGrowth(StationDesignProject project) => project.Layout.Rooms.Any(room => room.Enabled && !room.Orphaned && room.Cells.Count == 0)
        ? RoomGrowth.Run(EnsurePlacement(project)) : project;
    private static StationDesignProject EnsureCirculation(StationDesignProject project) => project.Layout.Interfaces.Count == 0
        ? DesignRouting.Run(EnsureGrowth(project)) : project;
    private static StationDesignProject EnsureGameflow(StationDesignProject project)
    {
        project = EnsureCirculation(project);
        // A previously submitted report cannot prove evaluation; rerun analysis without repeating repairs.
        return DesignGameflow.Run(project, allowRepairs: false);
    }
    private static StationDesignProject EnsureContents(StationDesignProject project) => project.Layout.EnvironmentZones.Count == 0
        ? DesignContents.Run(EnsureGameflow(project)) : project;
    private static StationDesignProject EnsureSystemsDevices(StationDesignProject project) => project.Definition.Systems.Enabled && project.Layout.SystemDevices.Count == 0
        ? DesignSystemsGeneration.RunDevices(EnsureContents(project)) : EnsureContents(project);
    private static StationDesignProject EnsurePower(StationDesignProject project)
    {
        project = EnsureSystemsDevices(project);
        return MissingSystemKind(project, "power") ? DesignSystemsGeneration.Run(project, "power") : project;
    }
    private static StationDesignProject EnsureAtmos(StationDesignProject project)
    {
        project = EnsurePower(project);
        return MissingSystemKind(project, "gas") ? DesignSystemsGeneration.Run(project, "gas") : project;
    }
    private static bool MissingSystemKind(StationDesignProject project, string kind) => project.Definition.Systems.Enabled &&
        project.Definition.Systems.Networks.Any(rule => rule.Kind == kind && project.Layout.SystemNetworks.All(network => network.RequirementId != rule.Id));

    private static DesignReport DefinitionReport(StationDesignProject project)
    {
        var compiled = RequirementCompiler.Compile(project.Definition);
        return new() { Stage = project.Stage, Complete = false, Diagnostics = compiled.Diagnostics.Concat(ProjectEdits.Review(project)).Distinct().ToList() };
    }
}
