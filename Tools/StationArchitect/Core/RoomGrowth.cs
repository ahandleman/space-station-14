namespace Content.StationArchitect;

/// <summary>
/// Grows connected room-owned floor and negotiates local and ancestor area pressures.
/// Floors and circulation are never borrowed from anonymous department leftovers.
/// </summary>
public static class RoomGrowth
{
    internal static List<ProgramDiagnostic> ReservePlanningFrontages(StationDesignProject project)
    {
        var view = ProgramJson.Clone(project);
        var definitions = LayoutGeometry.Definitions(view);
        var rooms = view.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && definitions.ContainsKey(room.DefinitionPath)).ToList();
        foreach (var room in rooms) if (!Protected(room)) room.Cells = [.. room.GrowthReservation];
        rooms = rooms.Where(room => room.Cells.Count > 0).ToList();
        var diagnostics = new List<ProgramDiagnostic>();
        ReserveSpaceFrontage(view, rooms, definitions, diagnostics);
        foreach (var room in project.Layout.Rooms)
        {
            var planned = rooms.FirstOrDefault(candidate => candidate.Id == room.Id);
            if (planned == null) continue;
            room.FrontageFloor = planned.FrontageFloor; room.FrontageSpace = planned.FrontageSpace;
        }
        project.Layout.ReservedSpace = view.Layout.ReservedSpace;
        return diagnostics;
    }
    public static StationDesignProject Run(StationDesignProject source) => RunOnce(source, true);

    private static StationDesignProject RunOnce(StationDesignProject source, bool repairOwnerAspect, int? remainingGrowthBudget = null)
    {
        var project = ProgramJson.Clone(source);
        var compilation = RequirementCompiler.Compile(project.Definition);
        var diagnostics = new List<ProgramDiagnostic>(compilation.Diagnostics);
        if (!compilation.Success) return Finish(project, diagnostics);
        if (project.Layout.Rooms.Any(room => room.Enabled && !room.Orphaned && !room.HasSeed)) project = DesignPlacement.Run(project);
        var definitions = LayoutGeometry.Definitions(project);
        var rooms = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && definitions.ContainsKey(room.DefinitionPath)).OrderBy(room => room.Id, StringComparer.Ordinal).ToList();
        diagnostics.AddRange(RoomAreaAllocation.Allocate(project, preserveGeometricAllocation: true));
        var mandatory = CollectMandatoryFloors(project, rooms, compilation);
        var occupied = new Dictionary<Cell, string>();
        foreach (var room in rooms.Where(Protected))
            foreach (var cell in room.Cells)
                if (!occupied.TryAdd(cell, room.Id)) diagnostics.Add(new(room.Id, "room-overlap", $"Preserved room floor overlaps '{occupied[cell]}'. Correct the edited geometry before continuing."));
        foreach (var room in rooms.Where(room => room.Pinned && !Protected(room)))
            foreach (var cell in room.GrowthReservation)
                if (!occupied.TryAdd(cell, room.Id) && occupied[cell] != room.Id)
                    diagnostics.Add(new(room.Id, "pinned-room-overlap", $"The pinned planning allocation conflicts with '{occupied[cell]}'; its seed and allocation were retained."));
        if (project.Layout.PublicEdited) foreach (var cell in project.Layout.PublicCorridors) occupied.TryAdd(cell, "$public");
        if (project.Layout.MaintenanceEdited) foreach (var cell in project.Layout.Maintenance) occupied.TryAdd(cell, "$maintenance");
        var envelope = project.Layout.Envelope.ToHashSet();
        var canvas = new GridBounds(0, 0, project.Settings.Width, project.Settings.Height);
        var forbiddenFloor = ForbiddenFloor(project);
        var growthBudget = Math.Max(1, remainingGrowthBudget ?? project.Settings.GrowthIterations);
        foreach (var room in rooms)
        {
            var definition = definitions[room.DefinitionPath];
            if (Protected(room)) continue;
            var reservation = (room.GrowthReservation.Count > 0 ? room.GrowthReservation : room.Parcel.Cells()).ToHashSet();
            reservation.ExceptWith(forbiddenFloor);
            if (reservation.RemoveWhere(cell => !canvas.Contains(cell)) > 0) diagnostics.Add(new(room.Id, "growth-canvas", "The planning allocation extends outside the supported canvas. Only available canvas cells were grown; its room instance and allocation remain editable."));
            if (definition.Environment.Placement != "exterior" && project.Layout.EnvelopeEdited) reservation.IntersectWith(envelope);
            reservation.ExceptWith(occupied.Where(item => item.Value != room.Id).Select(item => item.Key));
            var desired = room.PlannedArea ?? DesignPlacement.Goal(room, definition);
            var goal = Math.Clamp(desired, RequirementCompiler.EffectiveMinimumArea(definition), definition.Area.Max);
            if (definition.Role == "filler" && room.GrowthSide == "spine" && goal < room.GrowthReservation.Count)
                diagnostics.Add(new(room.Id, "circulation-capacity", "The planned circulation footprint exceeds this room's hard maximum. Its floor was not enlarged beyond the requirement."));
            if (reservation.Count == 0) { room.Cells.Clear(); diagnostics.Add(new(room.Id, "growth-blocked", "No free cells remain in this room's planning allocation.")); continue; }
            var start = reservation.Contains(room.Seed) ? room.Seed : reservation.OrderBy(cell => cell.Distance(room.Seed)).ThenBy(cell => cell.Y).ThenBy(cell => cell.X).First();
            if (room.Pinned && start != room.Seed)
            {
                diagnostics.Add(new(room.Id, "pinned-seed-blocked", "The pinned seed is blocked or outside the edited envelope; it was not moved and growth waits for a valid seed."));
                room.Cells.Clear(); continue;
            }
            if (!room.Pinned) room.Seed = start;
            var floor = new HashSet<Cell> { start };
            var anchors = mandatory[room.Id];
            foreach (var anchor in anchors)
            {
                var path = AllocationPath(start, anchor, reservation);
                if (path == null) { diagnostics.Add(new(room.Id, "entry-allocation", "The protected docking or circulation entrance has no connected room-floor allocation.")); continue; }
                floor.UnionWith(path);
            }
            if (floor.Count > goal) diagnostics.Add(new(room.Id, "entry-area-capacity", "The exact entrance anchors and their internal approach need more floor than this room's allocated area. Its area requirements were retained for review."));
            var queued = new HashSet<Cell>();
            var frontier = new PriorityQueue<Cell, (long Priority, int Y, int X)>();
            void Enqueue(Cell cell)
            {
                if (!reservation.Contains(cell) || floor.Contains(cell) || !queued.Add(cell)) return;
                frontier.Enqueue(cell, (GrowthPriority(room, cell), cell.Y, cell.X));
            }
            foreach (var seedCell in floor) foreach (var direction in Cell.Directions) Enqueue(seedCell + direction);
            while (floor.Count < goal && growthBudget > 0 && frontier.TryDequeue(out var next, out _))
            {
                growthBudget--; floor.Add(next);
                foreach (var direction in Cell.Directions) Enqueue(next + direction);
            }
            room.Cells = LayoutGeometry.Sorted(floor); room.Bounds = LayoutGeometry.Bounds(floor);
            foreach (var cell in floor) occupied[cell] = room.Id;
            if (floor.Count < RequirementCompiler.EffectiveMinimumArea(definition)) diagnostics.Add(new(room.Id, "growth-area", "Blocked or disconnected planning cells prevented this room from reaching its required minimum."));
        }
        if (growthBudget == 0 && rooms.Any(room => !Protected(room) && room.Cells.Count < (room.PlannedArea ?? DesignPlacement.Goal(room, definitions[room.DefinitionPath]))))
            diagnostics.Add(new("settings.growthIterations", "growth-budget", "The configured growth work budget was exhausted; increase it or continue after editing the planning allocations."));
        RepairPublicFrontages(project, rooms, definitions, mandatory);
        // A truncated outer room can shorten an owner's narrow axis even though
        // every individual room fits. Retry once using real allocation anchors;
        // the first pass has not spent the boundary-optimization budget yet.
        if (repairOwnerAspect && growthBudget > 0 && project.Definition.Departments.Any(owner => NeedsOwnerAspectRepair(owner, rooms)))
            return RunOnce(project, false, growthBudget);
        RelaxBoundaries(project, rooms, definitions, mandatory);
        LayoutGeometry.RefreshBounds(project);
        ReserveSpaceFrontage(project, rooms, definitions, diagnostics);
        var floors = rooms.SelectMany(room => room.Cells).ToHashSet();
        project.Layout.Walls = LayoutGeometry.Sorted(floors.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Where(cell => !floors.Contains(cell)));
        project.Layout.Hull = [];
        project.Layout.Windows = [];
        foreach (var room in rooms)
        {
            var definition = definitions[room.DefinitionPath];
            if (!LayoutGeometry.Connected(room.Cells)) diagnostics.Add(new(room.Id, "room-components", "The room's actual floor is disconnected."));
            if (room.Cells.Count > definition.Area.Max) diagnostics.Add(new(room.Id, "room-area", "Preserved floor exceeds the room's maximum area."));
            var complex = project.Layout.Rooms.Any(child => child.Enabled && !child.Orphaned && child.ParentInstanceId == room.Id);
            if (room.Cells.Count > 0 && !LayoutGeometry.ShapeFits(complex ? LayoutGeometry.ComplexFloor(project, room) : room.Cells, definition.ComplexShape ?? definition.Shape)) diagnostics.Add(new(room.Id, "room-shape", "The grown or preserved room violates its aspect-ratio or fill limit."));
        }
        foreach (var owner in project.Definition.Departments)
        {
            var floor = rooms.Where(room => room.OwnerId == owner.Id).SelectMany(room => room.Cells).ToHashSet();
            if (floor.Count > 0 && !LayoutGeometry.ShapeFits(floor, owner.Shape))
                diagnostics.Add(new(owner.Id, "department-shape", "The actual department floor violates its authored aspect-ratio or fill bound; adjust its allocation or requirements."));
        }
        return Finish(project, diagnostics);
    }

    private static bool Protected(DesignRoom room) => room.FloorLocked || room.FloorEdited;
    private static Cell GrowthNormal(string side) => side switch { "left" => new(-1, 0), "right" => new(1, 0), "top" => new(0, -1), "bottom" => new(0, 1), _ => new() };
    private static HashSet<Cell> MandatoryFloor(StationDesignProject project, string roomId) => project.Layout.Docks.Where(dock => dock.RoomId == roomId)
        .SelectMany(dock => dock.BoundaryCells.Select(cell => cell - dock.Outward)).Concat(project.Layout.CirculationReservations.Where(port => port.RoomId == roomId).SelectMany(port => port.BoundaryCells.Select(cell => cell - port.Outward)))
        .Concat(project.Layout.Rooms.Where(room => room.Id == roomId).SelectMany(room => room.FrontageFloor)).ToHashSet();
    private static HashSet<Cell> ForbiddenFloor(StationDesignProject project) => DesignDockGeometry.ReservedCells(project).Concat(project.Layout.ReservedSpace).Concat(project.Layout.CirculationReservations.SelectMany(port => port.ApproachCells))
        .SelectMany(cell => Cell.Directions.Select(direction => cell + direction).Append(cell)).Concat(project.Layout.Docks.SelectMany(dock => dock.BoundaryCells)).Concat(project.Layout.CirculationReservations.SelectMany(port => port.BoundaryCells)).ToHashSet();

    internal static Dictionary<string, HashSet<Cell>> CollectMandatoryFloors(StationDesignProject project, List<DesignRoom> rooms, ProgramCompilation compilation)
    {
        var result = rooms.ToDictionary(room => room.Id, room => MandatoryFloor(project, room.Id), StringComparer.Ordinal);
        var masks = rooms.ToDictionary(room => room.Id, room => (Protected(room) ? room.Cells : room.GrowthReservation).ToHashSet(), StringComparer.Ordinal);
        var all = masks.Values.SelectMany(mask => mask).ToHashSet(); var work = 200000;
        foreach (var requirement in compilation.Connections.Where(connection => connection.Priority == "required" && connection.Modes.Contains("direct")))
        foreach (var from in rooms.Where(room => room.DefinitionPath == requirement.From || room.OwnerId == requirement.From))
        {
            var fulfilled = false;
            foreach (var target in requirement.To)
            {
                foreach (var to in rooms.Where(room => room.Id != from.Id && (room.DefinitionPath == target || room.OwnerId == target)))
                {
                    if (--work < 0) return result;
                    if (!RoomHierarchy.ConnectionScopeAllowed(requirement, from.DefinitionPath, from.Id, from.ParentInstanceId, to.DefinitionPath, to.Id, to.ParentInstanceId)) continue;
                    var desired = Math.Max(requirement.MinWidth, requirement.Kind == "interaction" ? 2 : 1);
                    List<(Cell From, Cell To)>? selected = null;
                    foreach (var width in new[] { desired, Math.Max(1, requirement.MinWidth) }.Distinct())
                    {
                        foreach (var normal in Cell.Directions)
                        {
                            var tangent = new Cell(-normal.Y, normal.X);
                            foreach (var edge in masks[from.Id].OrderBy(cell => cell.Y).ThenBy(cell => cell.X))
                            {
                                var run = Enumerable.Range(0, width).Select(index => edge + new Cell(tangent.X * index, tangent.Y * index)).ToList();
                                if (!run.All(cell => masks[from.Id].Contains(cell) && !all.Contains(cell + normal) && masks[to.Id].Contains(cell + normal + normal))) continue;
                                selected = run.Select(cell => (From: cell, To: cell + normal + normal)).ToList(); break;
                            }
                            if (selected != null) break;
                        }
                        if (selected != null) break;
                    }
                    if (selected == null) continue;
                    result[from.Id].UnionWith(selected.Select(pair => pair.From)); result[to.Id].UnionWith(selected.Select(pair => pair.To)); fulfilled = true; break;
                }
                if (fulfilled && requirement.Selection == "any") break;
            }
        }
        AddOwnerAspectAnchors(project, rooms, masks, result);
        return result;
    }

    private static bool NeedsOwnerAspectRepair(DepartmentProgram owner, List<DesignRoom> rooms)
    {
        var owned = rooms.Where(room => room.OwnerId == owner.Id).ToList();
        if (owned.All(Protected)) return false;
        var actual = LayoutGeometry.Bounds(owned.SelectMany(room => room.Cells));
        var planned = LayoutGeometry.Bounds(owned.SelectMany(room => Protected(room) ? room.Cells : room.GrowthReservation));
        return actual.Width > 0 && actual.Height > 0 && planned.Width > 0 && planned.Height > 0 &&
            Math.Max((double)actual.Width / actual.Height, (double)actual.Height / actual.Width) > owner.Shape.MaxAspectRatio + 1e-9 &&
            Math.Max((double)planned.Width / planned.Height, (double)planned.Height / planned.Width) <= owner.Shape.MaxAspectRatio + 1e-9;
    }

    private static void AddOwnerAspectAnchors(StationDesignProject project, List<DesignRoom> rooms,
        Dictionary<string, HashSet<Cell>> masks, Dictionary<string, HashSet<Cell>> mandatory)
    {
        var forbidden = ForbiddenFloor(project);
        foreach (var owner in project.Definition.Departments.Where(owner => NeedsOwnerAspectRepair(owner, rooms)))
        {
            var owned = rooms.Where(room => room.OwnerId == owner.Id).ToList();
            var allocation = owned.SelectMany(room => masks[room.Id].Where(cell => !forbidden.Contains(cell)).Select(cell => (Room: room, Cell: cell))).ToList();
            if (allocation.Count == 0) continue;
            var bounds = LayoutGeometry.Bounds(allocation.Select(item => item.Cell));
            var retained = owned.SelectMany(room => mandatory[room.Id].Concat(Protected(room) ? room.Cells : masks[room.Id].Contains(room.Seed) ? [room.Seed] : [])).ToHashSet();
            foreach (var horizontal in new[] { true, false })
            {
                int Coordinate(Cell cell) => horizontal ? cell.X : cell.Y;
                var needed = (int)Math.Ceiling((horizontal ? bounds.Height : bounds.Width) / owner.Shape.MaxAspectRatio);
                var low = retained.Count > 0 ? retained.Min(Coordinate) : int.MaxValue;
                var high = retained.Count > 0 ? retained.Max(Coordinate) : int.MinValue;
                if (retained.Count > 0 && high - low + 1 >= needed) continue;
                void Keep((DesignRoom Room, Cell Cell) anchor) { mandatory[anchor.Room.Id].Add(anchor.Cell); retained.Add(anchor.Cell); }
                var single = allocation.Where(item => retained.Count > 0 && Math.Max(high, Coordinate(item.Cell)) - Math.Min(low, Coordinate(item.Cell)) + 1 >= needed)
                    .OrderBy(item => item.Cell.Distance(item.Room.Seed)).ThenBy(item => item.Room.Id, StringComparer.Ordinal).ThenBy(item => item.Cell.Y).ThenBy(item => item.Cell.X).FirstOrDefault();
                if (single.Room != null) { Keep(single); continue; }
                foreach (var extreme in new[] { allocation.Min(item => Coordinate(item.Cell)), allocation.Max(item => Coordinate(item.Cell)) }.Distinct())
                    Keep(allocation.Where(item => Coordinate(item.Cell) == extreme).OrderBy(item => item.Cell.Distance(item.Room.Seed)).ThenBy(item => item.Room.Id, StringComparer.Ordinal)
                        .ThenBy(item => item.Cell.Y).ThenBy(item => item.Cell.X).First());
            }
        }
    }

    private static long GrowthPriority(DesignRoom room, Cell cell)
    {
        var bounds = room.Parcel;
        var outward = room.GrowthSide switch { "left" => bounds.Right - 1 - cell.X, "right" => cell.X - bounds.X, "top" => bounds.Bottom - 1 - cell.Y, "bottom" => cell.Y - bounds.Y, _ => -1 };
        if (outward >= 0)
        {
            var horizontal = room.GrowthSide is "left" or "right";
            return (long)outward * Math.Max(1, horizontal ? bounds.Height : bounds.Width) * 4 + Math.Abs(horizontal ? cell.Y - room.Seed.Y : cell.X - room.Seed.X);
        }
        var dx = cell.X - room.Seed.X; var dy = cell.Y - room.Seed.Y;
        return 4L * dx * dx + 4L * dy * dy;
    }

    internal static List<Cell>? AllocationPath(Cell start, Cell end, HashSet<Cell> allowed)
    {
        if (!allowed.Contains(start) || !allowed.Contains(end)) return null;
        var queue = new Queue<Cell>(); var previous = new Dictionary<Cell, Cell>(); var seen = new HashSet<Cell> { start }; queue.Enqueue(start);
        while (queue.TryDequeue(out var cell))
        {
            if (cell == end) { var path = new List<Cell> { cell }; while (previous.TryGetValue(cell, out var parent)) { cell = parent; path.Add(cell); } path.Reverse(); return path; }
            foreach (var direction in Cell.Directions) { var next = cell + direction; if (allowed.Contains(next) && seen.Add(next)) { previous[next] = cell; queue.Enqueue(next); } }
        }
        return null;
    }

    private static double AreaCost(StationDesignProject project, DesignRoom room, RoomDefinition definition, int area)
    {
        var target = room.TargetAreaOverride ?? definition.Area.Target;
        return definition.Area.Weight * project.Definition.Settings.RoomPressureWeight * Math.Pow((double) (area - target) / Math.Max(1, target), 2);
    }

    private static double OwnerCost(StationDesignProject project, AreaRequirement area, int value) =>
        area.Weight * project.Definition.Settings.DepartmentPressureWeight * Math.Pow((double) (value - area.Target) / Math.Max(1, area.Target), 2);

    private static double ShapeCost(IEnumerable<Cell> cells, ShapeRequirement shape, double levelWeight)
    {
        var floor = cells.ToHashSet(); if (floor.Count == 0) return 0;
        var bounds = LayoutGeometry.Bounds(floor);
        var aspect = Math.Max((double) bounds.Width / bounds.Height, (double) bounds.Height / bounds.Width);
        var fill = (double) floor.Count / bounds.Area;
        return shape.Weight * levelWeight * (Math.Pow(aspect - 1, 2) + Math.Pow(Math.Max(0, shape.MinFillRatio - fill), 2));
    }

    private static void ReserveSpaceFrontage(StationDesignProject project, List<DesignRoom> rooms,
        Dictionary<string, RoomDefinition> definitions, List<ProgramDiagnostic> diagnostics)
    {
        var occupied = rooms.SelectMany(room => room.Cells).ToHashSet();
        if (project.Layout.PublicEdited) occupied.UnionWith(project.Layout.PublicCorridors);
        if (project.Layout.MaintenanceEdited) occupied.UnionWith(project.Layout.Maintenance);
        // The intervening wall belongs to structure. Only cells beyond that wall are reserved as vacuum.
        var blocked = occupied.Concat(occupied.SelectMany(cell => Cell.Directions.Select(direction => cell + direction))).ToHashSet();
        blocked.UnionWith(DesignNetworkGeometry.Halo(project.Layout.CirculationReservations.SelectMany(reservation => reservation.BoundaryCells.Concat(reservation.ApproachCells))));
        var bounds = new GridBounds(0, 0, project.Settings.Width, project.Settings.Height);
        int EdgeDistance(Cell cell) => Math.Min(Math.Min(cell.X, bounds.Right - 1 - cell.X), Math.Min(cell.Y, bounds.Bottom - 1 - cell.Y));
        var reserved = new HashSet<Cell>();
        foreach (var room in rooms)
        {
            var priorFloor = room.FrontageFloor.ToHashSet(); var priorSpace = room.FrontageSpace.ToHashSet();
            room.FrontageFloor = []; room.FrontageSpace = [];
            var definition = definitions[room.DefinitionPath];
            if (definition.Environment.Placement == "exterior") continue;
            var needed = definition.Exterior.MinSpaceFrontage +
                definition.Contents.Where(content => content.Placement == "spaceBoundary").Sum(content => content.Count.Target);
            if (needed == 0) continue;
            var candidates = room.Cells.SelectMany(cell => Cell.Directions.Select((direction, index) =>
                    (Floor: cell, Wall: cell + direction, Beyond: cell + direction + direction, Direction: index)))
                .Where(item => !occupied.Contains(item.Wall) && bounds.Contains(item.Beyond) && !blocked.Contains(item.Beyond))
                .Distinct().ToList();
            var groups = candidates.GroupBy(item => (item.Direction, Line: item.Direction % 2 == 0 ? item.Wall.X : item.Wall.Y))
                .OrderByDescending(group => group.Count() >= needed).ThenBy(group => group.Min(item => EdgeDistance(item.Beyond)))
                .ThenByDescending(group => group.Count()).ThenBy(group => group.Key.Direction).ThenBy(group => group.Key.Line);
            var selectedWalls = new HashSet<Cell>();
            // Keep valid owned pockets stable across packing, special-room insertion and
            // growth. Choosing a new nearest-canvas pocket can sever a proven passage.
            foreach (var item in candidates.Where(item => priorFloor.Contains(item.Floor) && priorSpace.Contains(item.Beyond)).OrderBy(item => item.Wall.Y).ThenBy(item => item.Wall.X))
            {
                if (!selectedWalls.Add(item.Wall)) continue;
                reserved.Add(item.Beyond); room.FrontageFloor.Add(item.Floor); room.FrontageSpace.Add(item.Beyond);
                if (selectedWalls.Count >= needed) break;
            }
            foreach (var group in groups)
            {
                if (selectedWalls.Count >= needed) break;
                foreach (var item in group.OrderBy(item => EdgeDistance(item.Beyond)).ThenBy(item => item.Wall.Y).ThenBy(item => item.Wall.X))
                {
                    if (!selectedWalls.Add(item.Wall)) continue;
                    // A compact unplated vacuum pocket is space even when a station encloses it.
                    // Long canals to the canvas edge would unnecessarily sever circulation.
                    reserved.Add(item.Beyond);
                    room.FrontageFloor.Add(item.Floor); room.FrontageSpace.Add(item.Beyond);
                    if (selectedWalls.Count >= needed) break;
                }
                if (selectedWalls.Count >= needed) break;
            }
            if (selectedWalls.Count < needed)
                diagnostics.Add(new(room.Id, "space-frontage-capacity", $"Only {selectedWalls.Count} potential wall edges face unplated space; this room requires {needed}. Move or reshape it rather than weakening its frontage rule."));
            room.FrontageFloor = LayoutGeometry.Sorted(room.FrontageFloor); room.FrontageSpace = LayoutGeometry.Sorted(room.FrontageSpace);
        }
        project.Layout.ReservedSpace = LayoutGeometry.Sorted(reserved);
        if (!project.Layout.EnvelopeEdited) project.Layout.Envelope = LayoutGeometry.Sorted(project.Layout.Envelope.Except(reserved));
    }

    private static void RelaxBoundaries(StationDesignProject project, List<DesignRoom> rooms, Dictionary<string, RoomDefinition> definitions, Dictionary<string, HashSet<Cell>> mandatory)
    {
        var byId = rooms.ToDictionary(room => room.Id, StringComparer.Ordinal);
        var owners = project.Definition.Departments.ToDictionary(owner => owner.Id, owner => owner.Area, StringComparer.Ordinal);
        owners[RequirementCompiler.MaintenanceOwner] = project.Definition.Maintenance.Area;
        var crossOwnerChanges = 0;
        var forbiddenFloor = ForbiddenFloor(project);
        for (var step = 0; step < Math.Max(0, project.Settings.OptimizationIterations); step++)
        {
            var occupancy = new Dictionary<Cell, string>();
            foreach (var room in rooms) foreach (var cell in room.Cells) occupancy.TryAdd(cell, room.Id);
            var contacts = new Dictionary<(string Recipient, string Donor, int Direction, int Line), List<(Cell Wall, Cell Donor)>>();
            foreach (var room in rooms.Where(room => !Protected(room)))
            foreach (var cell in room.Cells)
            for (var directionIndex = 0; directionIndex < 4; directionIndex++)
            {
                var direction = Cell.Directions[directionIndex]; var wall = cell + direction;
                if (occupancy.ContainsKey(wall) || !occupancy.TryGetValue(wall + direction, out var donorId) || donorId == room.Id) continue;
                var donor = byId[donorId]; if (Protected(donor)) continue;
                if (room.OwnerId != donor.OwnerId && crossOwnerChanges >= project.Settings.Packing.BoundaryGrowthBudget) continue;
                if (project.Layout.Departments.Any(owner => owner.Locked && (owner.OwnerId == room.OwnerId || owner.OwnerId == donor.OwnerId))) continue;
                var line = direction.X == 0 ? wall.Y : wall.X;
                var key = (room.Id, donorId, directionIndex, line);
                if (!contacts.TryGetValue(key, out var list)) contacts[key] = list = [];
                list.Add((wall, wall + direction));
            }
            DesignRoom? bestRecipient = null; DesignRoom? bestDonor = null;
            HashSet<Cell>? bestRecipientFloor = null; HashSet<Cell>? bestDonorFloor = null;
            var bestDelta = -1e-9;
            foreach (var contact in contacts.OrderBy(item => item.Key.Recipient, StringComparer.Ordinal).ThenBy(item => item.Key.Donor, StringComparer.Ordinal).ThenBy(item => item.Key.Direction).ThenBy(item => item.Key.Line))
            {
                var recipient = byId[contact.Key.Recipient]; var donor = byId[contact.Key.Donor];
                var recipientDefinition = definitions[recipient.DefinitionPath]; var donorDefinition = definitions[donor.DefinitionPath];
                var run = contact.Value;
                var nextRecipient = recipient.Cells.Concat(run.Select(item => item.Wall)).ToHashSet();
                var nextDonor = donor.Cells.Except(run.Select(item => item.Donor)).ToHashSet();
                if (run.Any(item => forbiddenFloor.Contains(item.Wall)) || !mandatory[donor.Id].IsSubsetOf(nextDonor)) continue;
                // Moving one shared wall must not erase another room's corner seam.
                // Keep physical room separation until the routing stage installs an explicit
                // doorway/counter; otherwise a legal two-room trade can silently merge a
                // third room's incompatible atmosphere or temperature compartment.
                if (run.Any(item => Cell.Directions.Any(direction =>
                    nextDonor.Contains(item.Wall + direction) ||
                    occupancy.TryGetValue(item.Wall + direction, out var other) && other != recipient.Id && other != donor.Id))) continue;
                if (nextRecipient.Count > recipientDefinition.Area.Max || nextDonor.Count < RequirementCompiler.EffectiveMinimumArea(donorDefinition)) continue;
                if (donor.Pinned && !nextDonor.Contains(donor.Seed) || recipient.Pinned && !nextRecipient.Contains(recipient.Seed)) continue;
                if (!LayoutGeometry.Connected(nextRecipient) || !LayoutGeometry.Connected(nextDonor)) continue;
                var replacements = new Dictionary<string, HashSet<Cell>> { [recipient.Id] = nextRecipient, [donor.Id] = nextDonor };
                // Access-capable rooms need a real full-width outward doorway and throat,
                // not merely enough floor area. Do not turn a usable entrance into a
                // two-tile ragged edge while optimizing an otherwise favorable wall trade.
                bool LosesPublicFrontage(DesignRoom room, RoomDefinition definition, HashSet<Cell> next) =>
                    definition.Access.AllowPublicEntry && string.IsNullOrEmpty(room.ParentInstanceId) && room.GrowthSide is "left" or "right" or "top" or "bottom" &&
                    HasPublicFrontage(project, room, room.Cells.ToHashSet(), occupancy, null) && !HasPublicFrontage(project, room, next, occupancy, replacements);
                if (LosesPublicFrontage(recipient, recipientDefinition, nextRecipient) || LosesPublicFrontage(donor, donorDefinition, nextDonor)) continue;
                bool Fits(DesignRoom candidate, RoomDefinition definition, HashSet<Cell> floor) =>
                    project.Layout.Rooms.Any(child => child.Enabled && !child.Orphaned && child.ParentInstanceId == candidate.Id) ?
                        LayoutGeometry.ShapeFits(LayoutGeometry.ComplexFloor(project, candidate, replacements), definition.ComplexShape ?? definition.Shape) : LayoutGeometry.ShapeFits(floor, definition.ComplexShape ?? definition.Shape);
                if (!Fits(recipient, recipientDefinition, nextRecipient) || !Fits(donor, donorDefinition, nextDonor)) continue;
                if (Ancestors(recipient).Skip(1).Any(parent => Protected(parent) && !nextRecipient.IsSubsetOf(parent.ComplexReservation))) continue;
                var delta = AreaCost(project, recipient, recipientDefinition, nextRecipient.Count) - AreaCost(project, recipient, recipientDefinition, recipient.Cells.Count) +
                            AreaCost(project, donor, donorDefinition, nextDonor.Count) - AreaCost(project, donor, donorDefinition, donor.Cells.Count);
                double CandidateShapeCost(DesignRoom room, RoomDefinition definition, HashSet<Cell>? floor)
                {
                    var complex = rooms.Any(child => child.ParentInstanceId == room.Id);
                    return ShapeCost(complex ? LayoutGeometry.ComplexFloor(project, room, floor == null ? null : replacements) : floor ?? room.Cells.ToHashSet(), definition.ComplexShape ?? definition.Shape, project.Definition.Settings.RoomPressureWeight);
                }
                delta += CandidateShapeCost(recipient, recipientDefinition, nextRecipient) - CandidateShapeCost(recipient, recipientDefinition, null) +
                         CandidateShapeCost(donor, donorDefinition, nextDonor) - CandidateShapeCost(donor, donorDefinition, null);
                var affectedAncestors = Ancestors(recipient).Concat(Ancestors(donor)).DistinctBy(room => room.Id).ToList();
                var ancestorValid = true;
                foreach (var ancestor in affectedAncestors)
                {
                    var ancestorDefinition = definitions[ancestor.DefinitionPath];
                    var subtree = RoomHierarchy.Descendants(project, ancestor, true).Where(room => room.Enabled && !room.Orphaned).ToList();
                    if (ancestorDefinition.ComplexArea is { } budget)
                    {
                        var before = subtree.Sum(room => room.Cells.Count);
                        var after = subtree.Sum(room => room.Id == recipient.Id ? nextRecipient.Count : room.Id == donor.Id ? nextDonor.Count : room.Cells.Count);
                        if (after < budget.Min && (before >= budget.Min || after <= before) || after > budget.Max && (before <= budget.Max || after >= before)) { ancestorValid = false; break; }
                        double ComplexCost(int value) => budget.Weight * project.Definition.Settings.RoomPressureWeight * Math.Pow((double)(value - budget.Target) / Math.Max(1, budget.Target), 2);
                        delta += ComplexCost(after) - ComplexCost(before);
                        if (before < budget.Min && after > before || before > budget.Max && after < before) delta -= 1e6;
                    }
                    if (subtree.Count > 1)
                    {
                        var before = LayoutGeometry.ComplexFloor(project, ancestor);
                        var after = LayoutGeometry.ComplexFloor(project, ancestor, replacements);
                        var shape = ancestorDefinition.ComplexShape ?? ancestorDefinition.Shape;
                        if (!LayoutGeometry.ShapeFits(after, shape)) { ancestorValid = false; break; }
                        // The direct candidate was already charged above; charge each higher complex once.
                        if (ancestor.Id != recipient.Id && ancestor.Id != donor.Id)
                            delta += ShapeCost(after, shape, project.Definition.Settings.RoomPressureWeight) - ShapeCost(before, shape, project.Definition.Settings.RoomPressureWeight);
                    }
                }
                if (!ancestorValid) continue;
                if (recipient.OwnerId != donor.OwnerId)
                {
                    var recipientTotal = rooms.Where(room => room.OwnerId == recipient.OwnerId).Sum(room => room.Cells.Count);
                    var donorTotal = rooms.Where(room => room.OwnerId == donor.OwnerId).Sum(room => room.Cells.Count);
                    var amount = nextRecipient.Count - recipient.Cells.Count;
                    if (recipientTotal + amount > owners[recipient.OwnerId].Max || donorTotal - amount < owners[donor.OwnerId].Min) continue;
                    delta += OwnerCost(project, owners[recipient.OwnerId], recipientTotal + amount) - OwnerCost(project, owners[recipient.OwnerId], recipientTotal) +
                             OwnerCost(project, owners[donor.OwnerId], donorTotal - amount) - OwnerCost(project, owners[donor.OwnerId], donorTotal);
                    foreach (var ownerId in new[] { recipient.OwnerId, donor.OwnerId })
                    {
                        var ownerDefinition = project.Definition.Departments.FirstOrDefault(owner => owner.Id == ownerId);
                        if (ownerDefinition == null) continue;
                        var beforeFloor = rooms.Where(room => room.OwnerId == ownerId).SelectMany(room => room.Cells).ToHashSet();
                        var afterFloor = rooms.Where(room => room.OwnerId == ownerId).SelectMany(room => room.Id == recipient.Id ? nextRecipient : room.Id == donor.Id ? nextDonor : (IEnumerable<Cell>) room.Cells).ToHashSet();
                        if (!LayoutGeometry.ShapeFits(afterFloor, ownerDefinition.Shape)) { delta = double.PositiveInfinity; break; }
                        delta += ShapeCost(afterFloor, ownerDefinition.Shape, project.Definition.Settings.DepartmentPressureWeight) - ShapeCost(beforeFloor, ownerDefinition.Shape, project.Definition.Settings.DepartmentPressureWeight);
                    }
                }
                if (delta >= bestDelta) continue;
                bestDelta = delta; bestRecipient = recipient; bestDonor = donor; bestRecipientFloor = nextRecipient; bestDonorFloor = nextDonor;
            }
            if (bestRecipient == null || bestDonor == null) break;
            if (bestRecipient.OwnerId != bestDonor.OwnerId) crossOwnerChanges++;
            var gains = bestRecipientFloor!.Except(bestRecipient.Cells).ToList();
            // A negotiated boundary moves the allocation as well as floor ownership. Only
            // cells of this accepted physical trade change; pre-existing foreign intrusions
            // remain visible to the independent validator.
            foreach (var ancestor in Ancestors(bestRecipient).Concat(Ancestors(bestDonor)).DistinctBy(room => room.Id))
            {
                if (!rooms.Any(child => child.ParentInstanceId == ancestor.Id) || ancestor.FloorLocked || ancestor.FloorEdited) continue;
                var subtreeIds = RoomHierarchy.Descendants(project, ancestor, true).Select(room => room.Id).ToHashSet(StringComparer.Ordinal);
                var mask = ancestor.ComplexReservation.ToHashSet();
                if (subtreeIds.Contains(bestRecipient.Id)) mask.UnionWith(gains);
                else mask.ExceptWith(gains);
                ancestor.ComplexReservation = LayoutGeometry.Sorted(mask);
            }
            bestRecipient.Cells = LayoutGeometry.Sorted(bestRecipientFloor!); bestDonor.Cells = LayoutGeometry.Sorted(bestDonorFloor!);
        }

        IEnumerable<DesignRoom> Ancestors(DesignRoom room)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (seen.Add(room.Id))
            {
                yield return room;
                if (string.IsNullOrEmpty(room.ParentInstanceId) || !byId.TryGetValue(room.ParentInstanceId, out var parent)) yield break;
                room = parent;
            }
        }
    }

    private static void RepairPublicFrontages(StationDesignProject project, List<DesignRoom> rooms, Dictionary<string, RoomDefinition> definitions, Dictionary<string, HashSet<Cell>> mandatory)
    {
        var occupancy = new Dictionary<Cell, string>();
        foreach (var room in rooms) foreach (var cell in room.Cells) occupancy.TryAdd(cell, room.Id);
        var editedNetwork = (project.Layout.PublicEdited ? project.Layout.PublicCorridors : []).Concat(project.Layout.MaintenanceEdited ? project.Layout.Maintenance : []).ToHashSet();
        var forbidden = ForbiddenFloor(project);
        foreach (var room in rooms.Where(room => !Protected(room) && string.IsNullOrEmpty(room.ParentInstanceId) && room.GrowthSide is "left" or "right" or "top" or "bottom"))
        {
            var definition = definitions[room.DefinitionPath];
            if (!definition.Access.AllowPublicEntry || room.Cells.Count == 0 || project.Layout.Departments.Any(owner => owner.OwnerId == room.OwnerId && owner.Locked)) continue;
            var floor = room.Cells.ToHashSet();
            if (HasPublicFrontage(project, room, floor, occupancy, null)) continue;
            var bounds = LayoutGeometry.Bounds(floor); var width = project.Settings.PublicWidth;
            var normal = GrowthNormal(room.GrowthSide); var horizontal = normal.X != 0;
            var edge = horizontal ? normal.X < 0 ? bounds.X : bounds.Right - 1 : normal.Y < 0 ? bounds.Y : bounds.Bottom - 1;
            var first = horizontal ? bounds.Y : bounds.X; var length = horizontal ? bounds.Height : bounds.Width; var seedAxis = horizontal ? room.Seed.Y : room.Seed.X;
            var allowed = room.GrowthReservation.Concat(floor).ToHashSet();
            var required = mandatory[room.Id];
            foreach (var firstY in Enumerable.Range(first, Math.Max(0, length - width + 1)).OrderBy(y => Math.Abs(y + width / 2 - seedAxis)).ThenBy(y => y))
            {
                var frontage = Enumerable.Range(firstY, width).Select(y => horizontal ? new Cell(edge, y) : new Cell(y, edge)).ToHashSet();
                var missing = frontage.Except(floor).ToList();
                if (missing.Count == 0 || !frontage.IsSubsetOf(allowed) || missing.Any(cell => forbidden.Contains(cell) || editedNetwork.Contains(cell) ||
                    Cell.Directions.Append(new Cell()).Any(direction => occupancy.TryGetValue(cell + direction, out var owner) && owner != room.Id))) continue;
                var candidate = floor.Concat(missing).ToHashSet();
                var removed = 0;
                foreach (var cell in floor.Where(cell => cell != room.Seed && !required.Contains(cell) && !frontage.Contains(cell))
                    .Where(cell => Cell.Directions.Any(direction => !floor.Contains(cell + direction)))
                    .Where(cell => !Cell.Directions.Any(direction => occupancy.TryGetValue(cell + direction + direction, out var owner) && owner != room.Id))
                    .OrderByDescending(cell => cell.Distance(room.Seed)).ThenBy(cell => cell.Y).ThenBy(cell => cell.X))
                {
                    candidate.Remove(cell);
                    if (!LayoutGeometry.Connected(candidate) || !LayoutGeometry.ShapeFits(candidate, definition.ComplexShape ?? definition.Shape)) { candidate.Add(cell); continue; }
                    if (++removed == missing.Count) break;
                }
                if (removed != missing.Count) continue;
                var replacement = new Dictionary<string, HashSet<Cell>> { [room.Id] = candidate };
                if (!HasPublicFrontage(project, room, candidate, occupancy, replacement)) continue;
                foreach (var cell in floor) occupancy.Remove(cell);
                foreach (var cell in candidate) occupancy[cell] = room.Id;
                room.Cells = LayoutGeometry.Sorted(candidate);
                break;
            }
        }
    }

    private static bool HasPublicFrontage(StationDesignProject project, DesignRoom room, HashSet<Cell> floor,
        IReadOnlyDictionary<Cell, string> occupancy, IReadOnlyDictionary<string, HashSet<Cell>>? replacements)
    {
        var normal = GrowthNormal(room.GrowthSide);
        var tangent = normal.X == 0 ? new Cell(1, 0) : new Cell(0, 1); var width = project.Settings.PublicWidth;
        bool Occupied(Cell cell)
        {
            if (replacements != null && replacements.Values.Any(cells => cells.Contains(cell))) return true;
            return occupancy.TryGetValue(cell, out var owner) && (replacements == null || !replacements.ContainsKey(owner));
        }
        foreach (var start in floor)
        {
            var valid = true;
            for (var offset = 0; offset < width && valid; offset++)
            {
                var edge = start + new Cell(tangent.X * offset, tangent.Y * offset);
                if (!floor.Contains(edge) || Occupied(edge + normal)) { valid = false; break; }
                for (var depth = 2; depth <= width + 1; depth++)
                {
                    var cell = edge + new Cell(normal.X * depth, normal.Y * depth);
                    if (Occupied(cell) || Cell.Directions.Any(direction => Occupied(cell + direction))) { valid = false; break; }
                }
            }
            if (valid) return true;
        }
        return false;
    }

    private static StationDesignProject Finish(StationDesignProject project, List<ProgramDiagnostic> diagnostics)
    {
        project.Stage = "growth";
        project.Report = new() { Stage = "growth", Complete = false, Diagnostics = diagnostics };
        project.Tree = ProjectTree.Build(project);
        return project;
    }
}
