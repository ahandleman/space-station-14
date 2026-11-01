namespace Content.StationArchitect;

/// <summary>Reconstructs conservative empty berths and straight approaches. A saved rectangle is never clearance evidence.</summary>
public static class DesignDockGeometry
{
    public const int MaximumReservationCells = 262144;
    public static Cell Tangent(Cell outward) => new(-outward.Y, outward.X);
    public static string Direction(Cell outward) => outward switch { { X: 0, Y: 1 } => "north", { X: 1, Y: 0 } => "east", { X: 0, Y: -1 } => "south", { X: -1, Y: 0 } => "west", _ => "" };
    public static Cell Normal(string direction) => direction switch { "north" => new(0, 1), "east" => new(1, 0), "south" => new(0, -1), "west" => new(-1, 0), _ => new() };
    public static int RotationForOutward(Cell outward) => Direction(outward) switch { "south" => 0, "east" => 90, "north" => 180, "west" => 270, _ => throw new ArgumentException("A dock requires a cardinal outward normal.") };
    public static Cell OutwardForRotation(int rotation) => (((rotation % 360) + 360) % 360) switch { 0 => new(0, -1), 90 => new(1, 0), 180 => new(0, 1), 270 => new(-1, 0), _ => new() };

    public static DesignDock Create(string roomId, DockRequirement requirement, int instance, Cell boundaryCell, Cell outward, GridBounds canvas)
    {
        ValidateRule(requirement, instance, outward);
        var tangent = Tangent(outward); var width = requirement.BerthWidth + 2 * requirement.Clearance;
        var berthStart = -requirement.BerthWidth / 2 + requirement.BerthOffset - requirement.Clearance;
        var far = requirement.BerthDepth + requirement.Clearance;
        var approachWidth = requirement.ApproachWidth == 0 ? width : requirement.ApproachWidth;
        var approachStart = -approachWidth / 2 + requirement.BerthOffset;
        var exit = outward.X == 1 ? canvas.Right - boundaryCell.X : outward.X == -1 ? boundaryCell.X - canvas.X + 1 : outward.Y == 1 ? canvas.Bottom - boundaryCell.Y : boundaryCell.Y - canvas.Y + 1;
        var end = Math.Max(far + requirement.ApproachLength, exit);
        if ((long) width * far + (long) approachWidth * (end - far) > MaximumReservationCells) throw new ArgumentException("The declared docking space exceeds its bounded reservation size.");
        Cell Point(int normal, int lateral) => new(boundaryCell.X + outward.X * normal + tangent.X * lateral, boundaryCell.Y + outward.Y * normal + tangent.Y * lateral);
        var berth = Enumerable.Range(1, far).SelectMany(normal => Enumerable.Range(berthStart, width).Select(lateral => Point(normal, lateral))).ToList();
        var approach = Enumerable.Range(far + 1, end - far).SelectMany(normal => Enumerable.Range(approachStart, approachWidth).Select(lateral => Point(normal, lateral))).ToList();
        if (berth.Concat(approach).Append(boundaryCell).Any(cell => Math.Abs((long) cell.X) > 4096 || Math.Abs((long) cell.Y) > 4096)) throw new ArgumentException("The dock reservation exceeds supported planning coordinates.");
        var entityId = roomId + "/" + requirement.ContentId + "#" + instance;
        return new()
        {
            Id = roomId + "/dock:" + requirement.Id + "#" + instance, RoomId = roomId, RequirementId = requirement.Id, ContentId = requirement.ContentId, Instance = instance,
            BoundaryCells = [boundaryCell], Outward = outward, BerthCells = berth, ApproachCells = approach, EntityId = entityId, InterfaceId = entityId + "/interface"
        };
    }

    private static void ValidateRule(DockRequirement requirement, int instance, Cell outward)
    {
        if (instance is < 1 or > 64 || Direction(outward).Length == 0 || requirement.Directions == null || !requirement.Directions.Contains(Direction(outward), StringComparer.Ordinal)) throw new ArgumentException("Use a declared cardinal direction and bounded content ordinal for a dock.");
        if (requirement.BerthWidth is < 1 or > 128 || requirement.BerthDepth is < 1 or > 128 || requirement.BerthOffset is < -128 or > 128 || requirement.Clearance is < 0 or > 8 || requirement.ApproachLength is < 1 or > 512 || requirement.ApproachWidth is < 0 or > 256 || requirement.ApproachWidth != 0 && requirement.ApproachWidth < requirement.BerthWidth + 2 * requirement.Clearance)
            throw new ArgumentException("Dock dimensions are outside the bounded berth/approach contract.");
        if (-requirement.BerthWidth / 2 + requirement.BerthOffset > 0 || -requirement.BerthWidth / 2 + requirement.BerthOffset + requirement.BerthWidth <= 0) throw new ArgumentException("The shuttle envelope must include its own docking airlock along the tangent axis.");
    }

    public static bool TryCreate(string roomId, DockRequirement requirement, int instance, Cell boundaryCell, Cell outward, GridBounds canvas, out DesignDock? dock)
    {
        try { dock = Create(roomId, requirement, instance, boundaryCell, outward, canvas); return true; }
        catch (ArgumentException) { dock = null; return false; }
    }

    public static DockRequirement? Requirement(StationDesignProject project, DesignDock dock)
    {
        var room = project.Layout.Rooms.FirstOrDefault(room => room.Id == dock.RoomId);
        return room == null ? null : RequirementCompiler.Rooms(project.Definition).FirstOrDefault(pair => pair.Path == room.DefinitionPath).Room?.Exterior?.Docks?.FirstOrDefault(rule => rule.Id == dock.RequirementId);
    }

    public static DesignDock Expected(StationDesignProject project, DesignDock dock)
    {
        var rule = Requirement(project, dock) ?? throw new ArgumentException("The saved dock no longer identifies a declared room docking rule.");
        if (dock.BoundaryCells.Count != 1) throw new ArgumentException("A docking hardware instance requires one exact boundary anchor.");
        return Create(dock.RoomId, rule, dock.Instance, dock.BoundaryCells[0], dock.Outward, new(0, 0, project.Settings.Width, project.Settings.Height));
    }

    public static bool GeometryMatches(StationDesignProject project, DesignDock dock)
    {
        try
        {
            var expected = Expected(project, dock);
            return dock.ContentId == expected.ContentId && UniqueEqual(dock.BoundaryCells, expected.BoundaryCells) && UniqueEqual(dock.BerthCells, expected.BerthCells) && UniqueEqual(dock.ApproachCells, expected.ApproachCells);
        }
        catch (ArgumentException) { return false; }
    }

    public static HashSet<Cell> ReservedCells(DesignDock dock) => dock.BerthCells.Concat(dock.ApproachCells).ToHashSet();
    public static HashSet<Cell> ReservedCells(StationDesignProject project)
    {
        var reserved = new HashSet<Cell>();
        foreach (var dock in project.Layout.Docks)
        {
            reserved.UnionWith(ReservedCells(dock));
            try { reserved.UnionWith(ReservedCells(Expected(project, dock))); } catch (ArgumentException) { }
        }
        return reserved;
    }

    /// <summary>Checks padded berths and unbounded outward flight lanes, independently of a provisional canvas or saved masks. Invalid declarations fail closed.</summary>
    public static bool PlanningOverlap(StationDesignProject project, DesignDock left, DesignDock right)
    {
        if (!TryPlanningRectangles(project, left, out var first) || !TryPlanningRectangles(project, right, out var second)) return true;
        foreach (var a in first) foreach (var b in second) if (a.Overlaps(b)) return true;
        return false;
    }

    /// <summary>Tests material against the same canvas-independent planning volumes. The optional halo includes the four future structural neighbors of each floor cell.</summary>
    public static bool PlanningIntersects(StationDesignProject project, DesignDock dock, IEnumerable<Cell> cells, bool includeWallHalo = false)
    {
        if (!TryPlanningRectangles(project, dock, out var volumes)) return true;
        foreach (var cell in cells)
        {
            var x = (long) cell.X; var y = (long) cell.Y;
            foreach (var volume in volumes)
                if (volume.Contains(x, y) || includeWallHalo && (volume.Contains(x - 1, y) || volume.Contains(x + 1, y) || volume.Contains(x, y - 1) || volume.Contains(x, y + 1))) return true;
        }
        return false;
    }

    // Nullable bounds represent actual infinity, rather than a large temporary map.
    // Consequently simultaneous translation and future canvas resizing cannot hide a crossing.
    private readonly record struct PlanningRectangle(long? MinX, long? MinY, long? MaxX, long? MaxY)
    {
        public bool Contains(long x, long y) => (!MinX.HasValue || x >= MinX.Value) && (!MaxX.HasValue || x <= MaxX.Value) &&
            (!MinY.HasValue || y >= MinY.Value) && (!MaxY.HasValue || y <= MaxY.Value);
        public bool Overlaps(PlanningRectangle other) =>
            (!MinX.HasValue || !other.MaxX.HasValue || MinX.Value <= other.MaxX.Value) &&
            (!other.MinX.HasValue || !MaxX.HasValue || other.MinX.Value <= MaxX.Value) &&
            (!MinY.HasValue || !other.MaxY.HasValue || MinY.Value <= other.MaxY.Value) &&
            (!other.MinY.HasValue || !MaxY.HasValue || other.MinY.Value <= MaxY.Value);
    }

    private static bool TryPlanningRectangles(StationDesignProject project, DesignDock dock, out PlanningRectangle[] rectangles)
    {
        rectangles = [];
        var rule = Requirement(project, dock);
        if (rule == null || dock.BoundaryCells.Count != 1 || dock.ContentId != rule.ContentId) return false;
        try { ValidateRule(rule, dock.Instance, dock.Outward); }
        catch (ArgumentException) { return false; }
        var anchor = dock.BoundaryCells[0]; var normal = dock.Outward; var tangent = Tangent(normal);
        var far = rule.BerthDepth + rule.Clearance;
        var berthWidth = rule.BerthWidth + 2 * rule.Clearance;
        var berthStart = -rule.BerthWidth / 2 + rule.BerthOffset - rule.Clearance;
        var approachWidth = rule.ApproachWidth == 0 ? berthWidth : rule.ApproachWidth;
        var approachStart = -approachWidth / 2 + rule.BerthOffset;
        PlanningRectangle Rectangle(long firstDepth, long? lastDepth, long firstLateral, long lastLateral)
        {
            if (normal.X != 0)
            {
                var y1 = (long) anchor.Y + tangent.Y * firstLateral; var y2 = (long) anchor.Y + tangent.Y * lastLateral;
                return new(normal.X > 0 ? anchor.X + firstDepth : lastDepth.HasValue ? anchor.X - lastDepth.Value : null,
                    Math.Min(y1, y2), normal.X < 0 ? anchor.X - firstDepth : lastDepth.HasValue ? anchor.X + lastDepth.Value : null, Math.Max(y1, y2));
            }
            var x1 = (long) anchor.X + tangent.X * firstLateral; var x2 = (long) anchor.X + tangent.X * lastLateral;
            return new(Math.Min(x1, x2), normal.Y > 0 ? anchor.Y + firstDepth : lastDepth.HasValue ? anchor.Y - lastDepth.Value : null,
                Math.Max(x1, x2), normal.Y < 0 ? anchor.Y - firstDepth : lastDepth.HasValue ? anchor.Y + lastDepth.Value : null);
        }
        rectangles = [Rectangle(1, far, berthStart, berthStart + berthWidth - 1), Rectangle(far + 1, null, approachStart, approachStart + approachWidth - 1)];
        return true;
    }

    public static IEnumerable<DesignDock> Candidates(StationDesignProject project, DesignRoom room, DockRequirement requirement, int instance, bool usePlanningFloor = true)
    {
        IEnumerable<Cell> Floor(DesignRoom candidate) => usePlanningFloor && candidate.GrowthReservation.Count > 0 ? candidate.GrowthReservation : candidate.Cells;
        var floor = Floor(room).ToHashSet();
        var allFloor = project.Layout.Rooms.Where(candidate => candidate.Enabled && !candidate.Orphaned).SelectMany(Floor).ToHashSet();
        var blocked = allFloor.Concat(allFloor.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)))
            .Concat(project.Layout.PublicCorridors).Concat(project.Layout.Maintenance).Concat(project.Layout.Walls).Concat(project.Layout.Hull).Concat(project.Layout.Windows)
            .Concat(project.Layout.Equipment.SelectMany(entity => entity.Footprint)).Concat(ReservedCells(project))
            .Concat(project.Layout.CirculationReservations.SelectMany(reservation => reservation.BoundaryCells.Concat(reservation.ApproachCells))).ToHashSet();
        var canvas = new GridBounds(0, 0, project.Settings.Width, project.Settings.Height);
        var candidates = new List<DesignDock>();
        foreach (var name in requirement.Directions)
        {
            var outward = Normal(name);
            foreach (var anchor in floor.Select(cell => cell + outward).Where(cell => !allFloor.Contains(cell)).Distinct())
                if (TryCreate(room.Id, requirement, instance, anchor, outward, canvas, out var candidate) && ReservedCells(candidate!).All(cell => !blocked.Contains(cell))) candidates.Add(candidate!);
        }
        return candidates.OrderBy(candidate => candidate.ApproachCells.Count).ThenBy(candidate => Direction(candidate.Outward), StringComparer.Ordinal).ThenBy(candidate => candidate.BoundaryCells[0].Y).ThenBy(candidate => candidate.BoundaryCells[0].X);
    }

    public static List<ProgramDiagnostic> ValidateDock(StationDesignProject project, DesignDock dock, bool hardwareRequired = false)
    {
        var issues = new List<ProgramDiagnostic>();
        void Error(string code, string message) => issues.Add(new(dock.Id, code, message));
        var room = project.Layout.Rooms.FirstOrDefault(room => room.Id == dock.RoomId && room.Enabled && !room.Orphaned);
        var rule = Requirement(project, dock);
        if (room == null || rule == null) { Error("dock-reference", "A dock needs an active room and current authored docking rule."); return issues; }
        var definition = RequirementCompiler.Rooms(project.Definition).First(pair => pair.Path == room.DefinitionPath).Room;
        var content = definition.Contents.FirstOrDefault(content => content.Id == rule.ContentId);
        if (content == null || content.InterfaceType != "dockingPort" || content.Placement != "spaceBoundary" || dock.ContentId != rule.ContentId || dock.Instance < 1 || dock.Instance > content.Count.Max)
        { Error("dock-reference", "Dock content, ordinal and declared native docking interface binding must agree."); return issues; }
        if (project.Layout.Docks.Count(other => other.Id == dock.Id || other.RoomId == dock.RoomId && other.ContentId == dock.ContentId && other.Instance == dock.Instance) != 1)
            Error("dock-duplicate", "Dock IDs and bound room/content ordinals must be unique.");
        if (!GeometryMatches(project, dock)) { Error("dock-geometry", "Berth and straight approach masks must match the current authored dimensions, boundary and outward direction."); return issues; }
        var expected = Expected(project, dock); var reserved = ReservedCells(expected); var boundary = expected.BoundaryCells[0];
        var floor = room.Cells.Count > 0 ? room.Cells.ToHashSet() : room.GrowthReservation.ToHashSet();
        if (!floor.Contains(boundary - dock.Outward) || floor.Contains(boundary) || floor.Contains(boundary + dock.Outward)) Error("dock-boundary", "The docking anchor must replace a boundary immediately between its owning room and exterior space.");
        var physical = ActualOccupied(project);
        if (physical.Any(reserved.Contains)) Error("dock-obstructed", "A berth or approach contains materialized floor, walls, windows, equipment or utilities; it is not empty space.");
        foreach (var other in project.Layout.Docks.Where(other => !ReferenceEquals(other, dock) && other.Id != dock.Id))
        {
            try { if (reserved.Overlaps(ReservedCells(Expected(project, other)))) { Error("dock-overlap", "Docking berth/approach reservations overlap another declared shuttle reservation."); break; } }
            catch (ArgumentException) { }
        }
        // Continue the same full-width lane past every actual station object, including
        // invalid out-of-canvas objects. A narrow void connection cannot prove flyable access.
        var tangent = Tangent(dock.Outward); var width = rule.ApproachWidth == 0 ? rule.BerthWidth + 2 * rule.Clearance : rule.ApproachWidth;
        var start = -width / 2 + rule.BerthOffset; var far = rule.BerthDepth + rule.Clearance;
        var furthest = physical.Select(cell => (long) (cell.X - boundary.X) * dock.Outward.X + (long) (cell.Y - boundary.Y) * dock.Outward.Y).DefaultIfEmpty(far).Max();
        if (furthest > 8192) Error("dock-exterior", "The station geometry exceeds the bounded straight exterior clearance proof.");
        else if (physical.Any(cell =>
        {
            var delta = cell - boundary; var normal = delta.X * dock.Outward.X + delta.Y * dock.Outward.Y; var lateral = delta.X * tangent.X + delta.Y * tangent.Y;
            return normal > far && normal <= furthest && lateral >= start && lateral < start + width;
        })) Error("dock-exterior", "The full-width straight approach is blocked before reaching open exterior; a vacuum pocket is insufficient.");
        if (hardwareRequired)
        {
            var entity = project.Layout.Equipment.FirstOrDefault(entity => entity.Id == dock.EntityId && entity.RoomId == room.Id && entity.RequirementId == rule.ContentId);
            var port = project.Layout.Interfaces.FirstOrDefault(port => port.Id == dock.InterfaceId);
            var entityMatches = entity != null && content.Prototypes.Contains(entity.Prototype, StringComparer.Ordinal) && entity.InterfaceId == dock.InterfaceId && entity.Placement == "spaceBoundary" && entity.Position == boundary && OutwardForRotation(entity.Rotation) == dock.Outward && UniqueEqual(entity.Footprint, dock.BoundaryCells);
            entityMatches &= entity != null && DesignEquipmentGeometry.TryExpectedFootprint(project.Settings, entity.Prototype, entity.Position, entity.Rotation, out var expectedFootprint) && UniqueEqual(entity.Footprint, expectedFootprint);
            var portMatches = port != null && port.Type == "dockingPort" && port.Kind == "walking" && port.From == room.Id && port.To == "$space" && port.Width == 1 && UniqueEqual(port.Cells, dock.BoundaryCells) && port.FromApproach == boundary - dock.Outward && port.ToApproach == boundary + dock.Outward;
            portMatches &= !project.Layout.Walls.Contains(boundary) && !project.Layout.Hull.Contains(boundary) && !project.Layout.Windows.Contains(boundary) && !project.Layout.Equipment.Any(other => other.Id != dock.EntityId && DesignSystems.BlocksCrew(project, other) && other.Footprint.Contains(boundary - dock.Outward));
            if (!entityMatches || !portMatches) Error("dock-hardware", "This dock needs its exact bound prototype and aligned native docking interface at the reserved boundary; an unrelated exterior airlock cannot substitute.");
        }
        return issues;
    }

    public static List<ProgramDiagnostic> Validate(StationDesignProject project, bool hardwareRequired = false)
    {
        var diagnostics = new List<ProgramDiagnostic>();
        if (project.Layout.Docks.Count > 4096) return [new("layout.docks", "dock-limit", "Use at most 4096 bounded docking reservations.")];
        if (DesignStages.Index(project.Stage) >= DesignStages.Index("growth"))
        {
            var definitions = RequirementCompiler.Rooms(project.Definition).ToDictionary(pair => pair.Path, pair => pair.Room, StringComparer.Ordinal);
            foreach (var room in project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && definitions.ContainsKey(room.DefinitionPath)))
            foreach (var rule in definitions[room.DefinitionPath].Exterior.Docks)
            {
                var content = definitions[room.DefinitionPath].Contents.FirstOrDefault(content => content.Id == rule.ContentId);
                if (content == null) continue;
                var validReservations = project.Layout.Docks.Count(dock => dock.RoomId == room.Id && dock.RequirementId == rule.Id && ValidateDock(project, dock).Count == 0);
                if (validReservations < content.Count.Min) diagnostics.Add(new(room.Id, "dock-reservation-missing", $"Dock rule {rule.Id} requires at least {content.Count.Min} valid protected berth/approach reservations before circulation; only {validReservations} are present."));
            }
        }
        foreach (var dock in project.Layout.Docks)
        {
            var issues = ValidateDock(project, dock, hardwareRequired);
            if (hardwareRequired && issues.Any(issue => issue.Code == "dock-hardware"))
            {
                var rule = Requirement(project, dock);
                var room = project.Layout.Rooms.FirstOrDefault(room => room.Id == dock.RoomId);
                var content = rule == null || room == null ? null : RequirementCompiler.Rooms(project.Definition).FirstOrDefault(pair => pair.Path == room.DefinitionPath).Room?.Contents.FirstOrDefault(content => content.Id == rule.ContentId);
                var valid = project.Layout.Docks.Count(other => other.RoomId == dock.RoomId && other.RequirementId == dock.RequirementId && ValidateDock(project, other, true).Count == 0);
                if (content != null && valid >= content.Count.Min && !project.Layout.Equipment.Any(entity => entity.Id == dock.EntityId)) issues = issues.Select(issue => issue.Code == "dock-hardware" ? issue with { Severity = "warning" } : issue).ToList();
            }
            diagnostics.AddRange(issues);
        }
        return diagnostics.Distinct().ToList();
    }

    public static List<DockMeasurement> Measure(StationDesignProject project, DesignRoom room)
    {
        var definition = RequirementCompiler.Rooms(project.Definition).FirstOrDefault(pair => pair.Path == room.DefinitionPath).Room;
        return definition?.Exterior.Docks.Select(rule => new DockMeasurement { Id = rule.Id, Count = project.Layout.Docks.Count(dock => dock.RoomId == room.Id && dock.RequirementId == rule.Id && ValidateDock(project, dock, true).Count == 0) }).ToList() ?? [];
    }

    public static void Translate(DesignDock dock, Cell delta)
    { dock.BoundaryCells = dock.BoundaryCells.Select(cell => cell + delta).ToList(); dock.BerthCells = dock.BerthCells.Select(cell => cell + delta).ToList(); dock.ApproachCells = dock.ApproachCells.Select(cell => cell + delta).ToList(); }
    public static void Translate(StationDesignProject project, DesignDock dock, Cell delta)
    {
        // Canonical masks are derived from a protected anchor and current canvas, not
        // independently painted geometry. Keep a previously stale/edited mask stale.
        var canonical = GeometryMatches(project, dock);
        Translate(dock, delta);
        if (!canonical) return;
        try
        {
            var expected = Expected(project, dock);
            dock.BerthCells = expected.BerthCells; dock.ApproachCells = expected.ApproachCells;
        }
        catch (ArgumentException) { }
    }
    public static void Translate(DesignCirculationReservation reservation, Cell delta)
    { reservation.BoundaryCells = reservation.BoundaryCells.Select(cell => cell + delta).ToList(); reservation.ApproachCells = reservation.ApproachCells.Select(cell => cell + delta).ToList(); }
    private static bool UniqueEqual(List<Cell> actual, List<Cell> expected) => actual.Count == actual.Distinct().Count() && actual.ToHashSet().SetEquals(expected);
    private static HashSet<Cell> ActualOccupied(StationDesignProject project) => project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned).SelectMany(room => room.Cells)
        .Concat(project.Layout.PublicCorridors).Concat(project.Layout.Maintenance).Concat(project.Layout.Walls).Concat(project.Layout.Hull).Concat(project.Layout.Windows)
        .Concat(project.Layout.Interfaces.SelectMany(port => port.Cells)).Concat(project.Layout.Equipment.SelectMany(entity => entity.Footprint)).Concat(project.Layout.Utilities.SelectMany(utility => utility.Cells))
        .Concat(project.Layout.TransportNetworks.SelectMany(network => network.Segments.Select(segment => segment.Position).Concat(network.Terminals.SelectMany(terminal => terminal.Footprint))))
        .Concat(project.Layout.SystemNetworks.SelectMany(network => network.Segments.Select(segment => segment.Position))).ToHashSet();
}
