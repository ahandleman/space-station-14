namespace Content.StationArchitect;

/// <summary>
/// Plans room instances around actual circulation-room spines. Department rectangles are only
/// packing guides; growth owns floor per room and may form nonrectangular footprints.
/// </summary>
public static partial class DesignPlacement
{
    private sealed class Slot(DesignRoom room, GridBounds bounds, string side, List<Cell>? shape = null)
    {
        public DesignRoom Room = room;
        public GridBounds Bounds = bounds;
        public string Side = side;
        public List<Cell> Shape = shape ?? bounds.Cells().ToList();
    }

    private sealed class OwnerPlan(string id)
    {
        public string Id = id;
        public string OwnerId = id;
        public int Width;
        public int Height;
        public List<Slot> Slots = [];
        public Cell Origin;
        public bool Protected;
        public int Rotation;
        public int OriginalWidth;
        public int OriginalHeight;
    }

    private sealed class Pair(string left, string right, string leftRoom, string rightRoom, int width)
    {
        public string Left = left;
        public string Right = right;
        public string LeftRoom = leftRoom;
        public string RightRoom = rightRoom;
        public int Width = width;
    }

    public static StationDesignProject Run(StationDesignProject source, int attempt = 0)
    {
        var project = ProgramJson.Clone(source);
        project.Layout.PlacementAttempt = attempt;
        project.Layout.ReservedSpace.Clear();
        foreach (var room in project.Layout.Rooms) { room.FrontageFloor.Clear(); room.FrontageSpace.Clear(); }
        var compilation = RequirementCompiler.Compile(project.Definition);
        var diagnostics = new List<ProgramDiagnostic>(compilation.Diagnostics);
        if (!compilation.Success) return Finish(project, diagnostics);
        if (!project.Layout.Rooms.Any(room => room.Enabled && !room.Orphaned)) project = InstanceSelection.Resolve(project);
        diagnostics.AddRange(RoomAreaAllocation.Allocate(project));
        var definitions = LayoutGeometry.Definitions(project);
        var active = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && definitions.ContainsKey(room.DefinitionPath)).ToList();
        var pairs = RequiredPairs(compilation, active, definitions);
        var plans = new List<OwnerPlan>();
        foreach (var owner in project.Definition.Departments)
        {
            var rooms = active.Where(room => room.OwnerId == owner.Id && string.IsNullOrEmpty(room.ParentInstanceId) && definitions[room.DefinitionPath].Environment.Placement != "exterior").ToList();
            if (rooms.Count == 0) continue;
            var pair = pairs.FirstOrDefault(item => item.Left == owner.Id || item.Right == owner.Id);
            plans.AddRange(BuildClusters(project, owner.Id, rooms, definitions, pair, compilation, diagnostics));
        }
        var minimumGap = Math.Max(project.Settings.DepartmentGap, project.Settings.PublicWidth + project.Settings.MaintenanceWidth + 3);
        var gap = minimumGap + (int)Math.Round(Math.Pow(1 - project.Settings.Packing.Compactness, 2) * minimumGap * 4);
        PackOwners(project, plans, pairs, gap, attempt, diagnostics);
        if (plans.Any(plan => Math.Abs((long)plan.Origin.X) > 4096 || Math.Abs((long)plan.Origin.Y) > 4096 || plan.Origin.X + (long)plan.Width > 4096 || plan.Origin.Y + (long)plan.Height > 4096))
        {
            project.Layout.PublicWaypoints = [.. source.Layout.PublicWaypoints];
            diagnostics.Add(new("settings", "placement-capacity", "The proposed packing exceeds supported planning coordinates. All room instances were retained without allocating out-of-range masks; increase compactness or reduce the room program."));
            return Finish(project, diagnostics);
        }
        foreach (var plan in plans) PlacePlan(project, plan, diagnostics);
        PlaceNestedRooms(project, definitions, diagnostics);
        ApplyPlanRotations(project, plans);
        ClearGeneratedSpecialGeometry(project, definitions);
        var expectedApproaches = ExpectedApproachIds(project, plans, definitions);
        PlanDenseLayout(project, plans, pairs, definitions, gap, diagnostics);
        RefreshOwnerPlanning(project);
        PlaceMaintenanceAndExterior(project, definitions, plans, gap, diagnostics);
        RefreshGeneratedDocks(project);
        expectedApproaches.UnionWith(ReserveSpecialApproaches(project, definitions));
        diagnostics.AddRange(RoomGrowth.ReservePlanningFrontages(project));
        var circulationPlan = DesignCirculationPlanning.Connect(project, expectedApproaches,
            guide => AlternativeApproaches(project, plans, pairs, definitions, guide));
        circulationPlan = RepairCirculationPacking(project, plans, pairs, definitions, expectedApproaches, circulationPlan);
        SaveFinalPlanningWitness(project, circulationPlan);
        diagnostics.AddRange(circulationPlan.Diagnostics);
        if (!circulationPlan.Complete) diagnostics.Add(new("layout.circulation", "planning-incomplete", "Local entrance guides are retained, but a complete connected circulation reservation has not been established.", "warning"));
        // The envelope is an ALLOWED building region, not a physical floor mask. Keeping
        // interstitial route space here means a manual cut does not freeze private parcels
        // as the only legal public-corridor cells. Unbuilt cells still export as Space.
        if (!project.Layout.EnvelopeEdited)
        {
            var interior = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && definitions.TryGetValue(room.DefinitionPath, out var definition) && definition.Environment.Placement != "exterior")
                .SelectMany(room => room.GrowthReservation.Concat(room.Cells)).ToHashSet();
            interior.UnionWith(project.Layout.PublicWaypoints);
            var canvas = new GridBounds(0, 0, project.Settings.Width, project.Settings.Height);
            var desiredEnvelope = LayoutGeometry.Bounds(interior).Expand(Math.Max(2, project.Settings.EnvelopePadding));
            var left = Math.Max(canvas.X, desiredEnvelope.X); var top = Math.Max(canvas.Y, desiredEnvelope.Y);
            var allowed = new GridBounds(left, top, Math.Max(0, Math.Min(canvas.Right, desiredEnvelope.Right) - left), Math.Max(0, Math.Min(canvas.Bottom, desiredEnvelope.Bottom) - top));
            var envelope = allowed.Cells().ToHashSet();
            foreach (var room in project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && definitions.TryGetValue(room.DefinitionPath, out var definition) && definition.Environment.Placement == "exterior"))
            {
                var exterior = room.GrowthReservation.Concat(room.Cells).ToHashSet();
                envelope.ExceptWith(exterior.Concat(exterior.SelectMany(cell => Cell.Directions.Select(direction => cell + direction))));
            }
            project.Layout.Envelope = LayoutGeometry.Sorted(envelope.Except(project.Layout.ReservedSpace));
        }
        else
        {
            var envelope = project.Layout.Envelope.ToHashSet();
            foreach (var room in project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && definitions.TryGetValue(room.DefinitionPath, out var definition) && definition.Environment.Placement != "exterior"))
                if (!envelope.Contains(room.Seed)) diagnostics.Add(new(room.Id, "seed-envelope", "The pinned or planned seed is outside the edited station envelope."));
        }
        return Finish(project, diagnostics);
    }

    private static List<OwnerPlan> BuildClusters(StationDesignProject project, string ownerId, List<DesignRoom> rooms,
        Dictionary<string, RoomDefinition> definitions, Pair? pair, ProgramCompilation compilation, List<ProgramDiagnostic> diagnostics)
    {
        var critical = pair == null ? "" : pair.Left == ownerId ? pair.LeftRoom : pair.RightRoom;
        var fillers = rooms.Where(room => definitions[room.DefinitionPath].Role == "filler" && definitions[room.DefinitionPath].Placement.Policy != "stationwide" && room.DefinitionPath != critical).OrderBy(room => room.Id, StringComparer.Ordinal).ToList();
        var activity = rooms.Except(fillers).ToList();
        var groups = new List<List<DesignRoom>>();
        var grouped = activity.Where(room => definitions[room.DefinitionPath].Placement.Policy == "grouped" || room.DefinitionPath == critical).ToList();
        // An explicitly named group is the author's spatial unit; no department/room-name rules.
        var criticalGroup = grouped.Select(room => definitions[room.DefinitionPath].Placement.Group).Where(group => group.Length > 0).ToHashSet(StringComparer.Ordinal);
        grouped.AddRange(activity.Where(room => !grouped.Contains(room) && criticalGroup.Contains(definitions[room.DefinitionPath].Placement.Group)));
        if (grouped.Count > 0) groups.Add(grouped);
        var distributed = activity.Except(grouped).Where(room => definitions[room.DefinitionPath].Placement.Policy == "distributed").ToList();
        foreach (var named in distributed.Where(room => definitions[room.DefinitionPath].Placement.Group.Length > 0).GroupBy(room => definitions[room.DefinitionPath].Placement.Group, StringComparer.Ordinal)) groups.Add(named.ToList());
        var loose = distributed.Where(room => definitions[room.DefinitionPath].Placement.Group.Length == 0).ToList();
        var clusterCount = Math.Min(loose.Count, Math.Max(1, Math.Min(project.Settings.Packing.DistributedClusters,
            (int)Math.Ceiling((double)loose.Count / Math.Max(1, project.Settings.Packing.MaxRoomsPerCluster)))));
        // A minimum of several physical clusters is intentional for distributed policies.
        if (loose.Count > 1) clusterCount = Math.Min(loose.Count, Math.Max(clusterCount, Math.Min(project.Settings.Packing.DistributedClusters, (loose.Count + 1) / 2)));
        var looseGroups = Enumerable.Range(0, clusterCount).Select(_ => new List<DesignRoom>()).ToList();
        foreach (var room in loose.OrderByDescending(room => Goal(room, definitions[room.DefinitionPath])).ThenBy(room => room.Id, StringComparer.Ordinal))
        {
            var chosen = looseGroups.OrderBy(group => group.Count == 0 ? -1e6 : group.Sum(other => Goal(other, definitions[other.DefinitionPath])) -
                compilation.Connections.Where(connection => connection.From == room.DefinitionPath && connection.To.Any(target => group.Any(other => other.DefinitionPath == target)) ||
                    group.Any(other => other.DefinitionPath == connection.From) && connection.To.Contains(room.DefinitionPath)).Sum(connection => connection.Weight) * 4).First();
            chosen.Add(room);
        }
        groups.AddRange(looseGroups.Where(group => group.Count > 0));
        var stationwide = activity.Except(grouped).Where(room => definitions[room.DefinitionPath].Placement.Policy == "stationwide").ToList();
        if (groups.Count == 0 && fillers.Count > 0 && stationwide.Count == 0) groups.Add([]);
        for (var index = 0; index < fillers.Count; index++)
        {
            if (groups.Count == 0) groups.Add([]);
            groups[index % groups.Count].Add(fillers[index]);
        }
        foreach (var group in groups.Where(group => group.Count > 1 && !group.Any(room => definitions[room.DefinitionPath].Role == "filler")))
        {
            var template = rooms.Where(room => definitions[room.DefinitionPath].Role == "filler").Select(room => room.DefinitionPath).Distinct().FirstOrDefault(path =>
                !project.Selection.RoomCounts.ContainsKey(path) && project.Layout.Rooms.Count(room => room.Enabled && !room.Orphaned && room.DefinitionPath == path) < definitions[path].Count.Max);
            if (template == null) continue;
            var instanceOrdinal = 1; while (project.Layout.Rooms.Any(room => room.Id == template + "#" + instanceOrdinal)) instanceOrdinal++;
            var added = new DesignRoom { Id = template + "#" + instanceOrdinal, DefinitionPath = template, OwnerId = ownerId, Instance = instanceOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            project.Layout.Rooms.Add(added); group.Add(added);
            project.Selection.AutoRoomCounts[template] = project.Layout.Rooms.Count(room => room.Enabled && !room.Orphaned && room.DefinitionPath == template);
        }
        var result = new List<OwnerPlan>(); var ordinal = 0;
        foreach (var group in groups.Where(group => group.Count > 0))
        {
            var plan = BuildOwner(project, ownerId, group, definitions, group.Any(room => room.DefinitionPath == critical) ? pair : null, diagnostics);
            plan.Id = ownerId + "@" + ordinal++; result.Add(plan);
        }
        foreach (var room in stationwide)
        {
            var shape = ComplexPlan(project, room, definitions).Bounds;
            var plan = new OwnerPlan(ownerId) { Id = ownerId + "@" + ordinal++, Width = shape.Width, Height = shape.Height };
            plan.Slots.Add(new(room, shape, "stationwide")); result.Add(plan);
        }
        return result;
    }

    private static OwnerPlan BuildOwner(StationDesignProject project, string ownerId, List<DesignRoom> rooms,
        Dictionary<string, RoomDefinition> definitions, Pair? pair, List<ProgramDiagnostic> diagnostics)
    {
        var plan = new OwnerPlan(ownerId);
        var activity = rooms.Where(room => definitions[room.DefinitionPath].Role != "filler").ToList();
        var fillers = rooms.Where(room => definitions[room.DefinitionPath].Role == "filler").OrderBy(room => room.Id, StringComparer.Ordinal).ToList();
        var criticalPath = pair == null ? null : pair.Left == ownerId ? pair.LeftRoom : pair.RightRoom;
        var critical = activity.FirstOrDefault(room => room.DefinitionPath == criticalPath);
        var targetFiller = fillers.FirstOrDefault(room => room.DefinitionPath == criticalPath);
        var shape = activity.ToDictionary(room => room.Id, room => ComplexPlan(project, room, definitions).Bounds);
        var dockingRooms = activity.Where(room => room != critical && definitions[room.DefinitionPath].Exterior.Docks.Count > 0).ToList();
        activity.RemoveAll(dockingRooms.Contains);
        foreach (var room in dockingRooms)
        {
            var definition = definitions[room.DefinitionPath]; var area = Goal(room, definition); var rule = definition.ComplexShape ?? definition.Shape;
            var frontage = definition.Exterior.MinSpaceFrontage + definition.Contents.Where(content => content.Placement == "spaceBoundary").Sum(content => content.Count.Target);
            var width = Math.Max(shape[room.Id].Width, frontage);
            var height = Math.Max((int)Math.Ceiling((double)area / width), (int)Math.Ceiling(width / rule.MaxAspectRatio));
            shape[room.Id] = new(0, 0, width, height);
        }
        var columnWidth = shape.Count == 0 ? 5 : shape.Values.Max(bounds => bounds.Width);
        if (critical != null) columnWidth = shape[critical.Id].Width;
        foreach (var room in activity)
        {
            if (project.Layout.Rooms.Any(child => child.Enabled && !child.Orphaned && child.ParentInstanceId == room.Id)) continue;
            var definition = definitions[room.DefinitionPath];
            var area = Goal(room, definition);
            var width = Math.Min(columnWidth, shape[room.Id].Width);
            var height = (int) Math.Ceiling((double) area / width);
            var shapeRule = definition.ComplexShape ?? definition.Shape;
            if ((double) height / width > shapeRule.MaxAspectRatio) width = Math.Min(columnWidth, (int) Math.Ceiling(Math.Sqrt((double) area / shapeRule.MaxAspectRatio)));
            var frontage = definition.Exterior.MinSpaceFrontage + definition.Contents.Where(content => content.Placement == "spaceBoundary").Sum(content => content.Count.Target);
            if (frontage > height)
            {
                height = frontage;
                width = Math.Min(columnWidth, Math.Max((int)Math.Ceiling((double)area / height), (int)Math.Ceiling(height / shapeRule.MaxAspectRatio)));
            }
            shape[room.Id] = new(0, 0, width, Math.Max(height, (int) Math.Ceiling((double) area / width)));
        }
        var variation = project.Settings.Packing.InternalVariation;
        var maximumHeight = shape.Values.Select(bounds => bounds.Height).DefaultIfEmpty(1).Max();
        activity = activity.OrderByDescending(room => room == critical).ThenByDescending(room => shape[room.Id].Height * (1 - variation * .6) +
                UnitNoise(project, room.Id + ":order") * maximumHeight * variation)
            .ThenBy(room => room.Id, StringComparer.Ordinal).ToList();
        var spineWidth = fillers.Count == 0 ? 0 : Math.Clamp((int) Math.Ceiling(Math.Sqrt(fillers.Average(room => Goal(room, definitions[room.DefinitionPath])) / 2)), 3, 5);
        var roomGap = Math.Max(1, project.Settings.RoomGap);
        var branchHeight = targetFiller == null ? 0 : Math.Max(pair!.Width, Math.Min(7, shape.Count == 0 ? 5 : shape.Values.Max(bounds => bounds.Height)));
        var rows = new List<(DesignRoom? Left, DesignRoom? Right, int Y, int Height)>();
        var y = 0;
        while (activity.Count > 0)
        {
            var left = activity[0]; activity.RemoveAt(0);
            DesignRoom? right = null;
            if (activity.Count > 0 && !(targetFiller != null && y < branchHeight + roomGap)) { right = activity[0]; activity.RemoveAt(0); }
            if (critical != null && (left == critical || right == critical))
            {
                if (pair!.Left == ownerId && left == critical) (left, right) = (right!, left);
                if (pair.Right == ownerId && right == critical) (left, right) = (right, left);
            }
            else if (right != null && UnitNoise(project, ownerId + ":side:" + y) < variation * .5) (left, right) = (right, left);
            var height = Math.Max(left == null ? 0 : shape[left.Id].Height, right == null ? 0 : shape[right.Id].Height);
            rows.Add((left, right, y, height)); y += height + roomGap;
        }
        var length = Math.Max(1, y - roomGap);
        if (fillers.Count > 0)
        {
            if (targetFiller != null) { fillers.Remove(targetFiller); fillers.Insert(0, targetFiller); }
            int Capacity(DesignRoom room, bool first)
            {
                var definition = definitions[room.DefinitionPath];
                var branch = first && targetFiller != null ? (columnWidth + roomGap) * branchHeight : 0;
                return Math.Max(1, Math.Min((definition.Area.Max - branch) / spineWidth, (int) Math.Floor(spineWidth * definition.Shape.MaxAspectRatio)));
            }
            while (fillers.Select((room, index) => Capacity(room, index == 0)).Sum() + roomGap * (fillers.Count - 1) < length)
            {
                var template = fillers.Select(room => room.DefinitionPath).Distinct().Order(StringComparer.Ordinal)
                    .FirstOrDefault(path => !project.Selection.RoomCounts.ContainsKey(path) && project.Layout.Rooms.Count(room => room.Enabled && !room.Orphaned && room.DefinitionPath == path) < definitions[path].Count.Max);
                if (template == null) { diagnostics.Add(new(ownerId, "circulation-capacity", "Selected circulation rooms cannot span the department within their area/shape/count limits. Add a filler instance, increase its bounds or reduce room sizes.")); break; }
                var used = project.Layout.Rooms.Where(room => room.DefinitionPath == template).Select(room => room.Instance).ToHashSet(StringComparer.Ordinal);
                var instance = 1; while (used.Contains(instance.ToString(System.Globalization.CultureInfo.InvariantCulture))) instance++;
                var suffix = instance.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var added = new DesignRoom { Id = template + "#" + suffix, DefinitionPath = template, OwnerId = ownerId, Instance = suffix };
                project.Layout.Rooms.Add(added); rooms.Add(added); fillers.Add(added);
                project.Selection.AutoRoomCounts[template] = project.Layout.Rooms.Count(room => room.Enabled && !room.Orphaned && room.DefinitionPath == template);
            }
            var remaining = length - roomGap * (fillers.Count - 1); var start = 0;
            for (var index = 0; index < fillers.Count; index++)
            {
                var room = fillers[index]; var definition = definitions[room.DefinitionPath];
                var cap = Capacity(room, index == 0);
                var laterCapacity = fillers.Skip(index + 1).Select((other, offset) => Capacity(other, false)).Sum();
                var height = Math.Clamp((int) Math.Ceiling((double) remaining / (fillers.Count - index)), Math.Min(Math.Max(1, cap), Math.Max(1, remaining - laterCapacity)), Math.Max(1, cap));
                var minimumHeight = (int) Math.Ceiling((double) RequirementCompiler.EffectiveMinimumArea(definition) / spineWidth);
                height = Math.Max(height, Math.Min(cap, minimumHeight));
                var bounds = new GridBounds(columnWidth + roomGap, start, spineWidth, height);
                var cells = bounds.Cells().ToHashSet();
                if (index == 0 && targetFiller != null)
                    cells.UnionWith(new GridBounds(bounds.Right, 0, columnWidth + roomGap, Math.Min(branchHeight, height)).Cells());
                plan.Slots.Add(new(room, LayoutGeometry.Bounds(cells), "spine", LayoutGeometry.Sorted(cells)));
                start += height + roomGap; remaining -= height;
            }
            length = Math.Max(length, start - roomGap);
        }
        foreach (var row in rows)
        {
            if (row.Left != null)
            {
                var dimensions = shape[row.Left.Id]; var forced = row.Left == critical;
                var width = forced ? columnWidth : dimensions.Width;
                var offset = forced ? 0 : (int)Math.Round((row.Height - dimensions.Height) * UnitNoise(project, row.Left.Id + ":align") * variation);
                plan.Slots.Add(new(row.Left, new(columnWidth - width, row.Y + offset, width, dimensions.Height), "left"));
            }
            if (row.Right != null)
            {
                var dimensions = shape[row.Right.Id]; var forced = row.Right == critical;
                var width = forced ? columnWidth : dimensions.Width;
                var offset = forced ? 0 : (int)Math.Round((row.Height - dimensions.Height) * UnitNoise(project, row.Right.Id + ":align") * variation);
                plan.Slots.Add(new(row.Right, new(columnWidth + roomGap + spineWidth + (spineWidth == 0 ? 0 : roomGap), row.Y + offset, width, dimensions.Height), "right"));
            }
        }
        plan.Width = spineWidth == 0 ? 2 * columnWidth + roomGap : 2 * columnWidth + spineWidth + 2 * roomGap;
        plan.Height = length;
        var headerHeight = 0;
        if (dockingRooms.Count > 0)
        {
            headerHeight = dockingRooms.Max(room => shape[room.Id].Height) + roomGap;
            foreach (var slot in plan.Slots)
            {
                slot.Shape = LayoutGeometry.Sorted(slot.Shape.Select(cell => new Cell(cell.X, cell.Y + headerHeight)));
                slot.Bounds = LayoutGeometry.Bounds(slot.Shape);
            }
            var headerWidth = dockingRooms.Sum(room => shape[room.Id].Width) + roomGap * (dockingRooms.Count - 1);
            var x = columnWidth + roomGap + spineWidth / 2 - headerWidth / 2;
            foreach (var room in dockingRooms)
            {
                var dimensions = shape[room.Id];
                plan.Slots.Add(new(room, new(x, headerHeight - roomGap - dimensions.Height, dimensions.Width, dimensions.Height), "top"));
                x += dimensions.Width + roomGap;
            }
            plan.Height += headerHeight;
        }
        VaryPlan(project, plan, pair, rows.Select(row => row.Y + headerHeight).ToList(), spineWidth);
        return plan;
    }

    private static List<Pair> RequiredPairs(ProgramCompilation compilation, List<DesignRoom> rooms, Dictionary<string, RoomDefinition> definitions)
    {
        var result = new List<Pair>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requirement in compilation.Connections.Where(connection => connection.Priority == "required" && connection.Modes.Count == 1 && connection.Modes[0] == "direct"))
        foreach (var target in requirement.To)
        {
            if (!definitions.ContainsKey(requirement.From) || !definitions.ContainsKey(target)) continue;
            var fromOwner = RequirementCompiler.Owner(requirement.From); var toOwner = RequirementCompiler.Owner(target);
            if (fromOwner == toOwner || fromOwner == RequirementCompiler.MaintenanceOwner || toOwner == RequirementCompiler.MaintenanceOwner) continue;
            if (!rooms.Any(room => room.DefinitionPath == requirement.From) || !rooms.Any(room => room.DefinitionPath == target)) continue;
            var fromFiller = definitions[requirement.From].Role == "filler";
            var leftOwner = fromFiller ? fromOwner : toOwner; var rightOwner = fromFiller ? toOwner : fromOwner;
            if (!seen.Add(leftOwner + ":" + rightOwner)) continue;
            result.Add(new(leftOwner, rightOwner, fromFiller ? requirement.From : target, fromFiller ? target : requirement.From, Math.Max(1, requirement.MinWidth)));
        }
        return result;
    }

    private static void PackOwners(StationDesignProject project, List<OwnerPlan> plans, List<Pair> pairs, int gap, int attempt, List<ProgramDiagnostic> diagnostics)
    {
        var grouped = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<List<OwnerPlan>>();
        foreach (var pair in pairs)
        {
            var left = plans.FirstOrDefault(plan => plan.OwnerId == pair.Left && plan.Slots.Any(slot => slot.Room.DefinitionPath == pair.LeftRoom));
            var right = plans.FirstOrDefault(plan => plan.OwnerId == pair.Right && plan.Slots.Any(slot => slot.Room.DefinitionPath == pair.RightRoom));
            if (left == null || right == null || grouped.Contains(left.Id) || grouped.Contains(right.Id)) continue;
            groups.Add([left, right]); grouped.Add(left.Id); grouped.Add(right.Id);
        }
        groups.AddRange(plans.Where(plan => !grouped.Contains(plan.Id)).Select(plan => new List<OwnerPlan> { plan }));
        if (groups.Count == 0) return;
        var ordered = OrderGroups(project, groups, attempt);
        var padding = PlanningPadding(project);
        int Width(List<OwnerPlan> group) => group.Sum(plan => plan.Width) + group.Count - 1;
        int Height(List<OwnerPlan> group) => group.Max(plan => plan.Height);
        int RowWidth(List<List<OwnerPlan>> row) => row.Sum(Width) + Math.Max(0, row.Count - 1) * gap;
        int RowHeight(List<List<OwnerPlan>> row) => row.Select(Height).DefaultIfEmpty(0).Max();
        if (project.Settings.Topology == "hub" && ordered.Count >= 5)
            ordered = ordered.Take(1).Concat(ordered.Skip(1).OrderByDescending(Height).ThenBy(group => LayoutGeometry.StableOrder(group[0].Id, project.Settings.Seed + attempt))).ToList();
        var requested = new Dictionary<string, Cell>(StringComparer.Ordinal);
        project.Layout.PublicWaypoints.Clear();
        void Waypoint(int x, int y) => project.Layout.PublicWaypoints.Add(new(x + padding, y + padding));
        void PlaceGroup(List<OwnerPlan> group, int x, int y)
        {
            foreach (var plan in group) { requested[plan.Id] = new(x + padding, y + padding); x += plan.Width + 1; }
        }
        void PlaceRow(List<List<OwnerPlan>> row, int width, int y, bool alignBottom = false)
        {
            var x = (width - RowWidth(row)) / 2; var height = RowHeight(row);
            foreach (var group in row) { PlaceGroup(group, x, y + (alignBottom ? height - Height(group) : 0)); x += Width(group) + gap; }
        }
        void Arrange()
        {
        requested.Clear(); project.Layout.PublicWaypoints.Clear();
        if (project.Settings.Topology == "spine" && ordered.Count > 1)
        {
            // Parallel department banks leave a real continuous trunk between them.
            var upper = ordered.Where((_, index) => index % 2 == 0).ToList();
            var lower = ordered.Where((_, index) => index % 2 != 0).ToList();
            var upperHeight = RowHeight(upper); var x = 0;
            for (var index = 0; index < upper.Count; index++)
            {
                PlaceGroup(upper[index], x, upperHeight - Height(upper[index]));
                if (index < lower.Count) PlaceGroup(lower[index], x, upperHeight + gap);
                x += Math.Max(Width(upper[index]), index < lower.Count ? Width(lower[index]) : 0) + gap;
            }
            Waypoint(0, upperHeight + gap / 2);
            Waypoint(Math.Max(0, x - gap - 1), upperHeight + gap / 2);
        }
        else if (project.Settings.Topology == "hub" && ordered.Count >= 5)
        {
            // The highest weighted-degree cluster is the hub. The topology is derived from
            // authored relationships, rather than assuming a department with a particular name.
            var center = ordered[0]; var neighbors = ordered.Skip(1).ToList();
            var topCount = (int) Math.Ceiling((double) (neighbors.Count - 2) / 2);
            var top = neighbors.Take(topCount).ToList(); var left = neighbors[topCount]; var right = neighbors[topCount + 1];
            var bottom = neighbors.Skip(topCount + 2).ToList();
            var width = Math.Max(Math.Max(RowWidth(top), RowWidth(bottom)), Width(left) + gap + Width(center) + gap + Width(right));
            var middleHeight = Math.Max(Height(center), Math.Max(Height(left), Height(right))); var middleY = RowHeight(top) + gap;
            PlaceRow(top, width, 0, true);
            PlaceGroup(left, 0, middleY + (middleHeight - Height(left)) / 2);
            PlaceGroup(center, (width - Width(center)) / 2, middleY + (middleHeight - Height(center)) / 2);
            PlaceGroup(right, width - Width(right), middleY + (middleHeight - Height(right)) / 2);
            PlaceRow(bottom, width, middleY + middleHeight + gap);
            Waypoint((width - Width(center)) / 2 - gap / 2, middleY + middleHeight / 2);
        }
        else if (project.Settings.Topology == "ring" && ordered.Count >= 2)
        {
            // Public-only facilities occupy the public side of the cycle. Maintenance-
            // served suites stay outside, so the passage networks need not weave through
            // each other's terminal facilities. The center is usable program floor.
            var definitions = LayoutGeometry.Definitions(project);
            bool CanUsePublicCore(List<OwnerPlan> group) => group.SelectMany(plan => plan.Slots).All(slot =>
                definitions[slot.Room.DefinitionPath].Access.AllowPublicEntry && !definitions[slot.Room.DefinitionPath].Access.AllowMaintenanceEntry &&
                definitions[slot.Room.DefinitionPath].Exterior.Docks.Count == 0 && definitions[slot.Room.DefinitionPath].Exterior.MinSpaceFrontage == 0 &&
                !definitions[slot.Room.DefinitionPath].Contents.Any(content => content.Placement == "spaceBoundary" && content.Count.Target > 0) &&
                definitions[slot.Room.DefinitionPath].Placement.Anchor != "perimeter" && definitions[slot.Room.DefinitionPath].Environment.Placement != "exterior");
            var internalGroups = project.Definition.Maintenance.Connectivity.Policy == "continuous" ? ordered.Where(CanUsePublicCore).ToList() : [];
            var positions = new List<(List<OwnerPlan> Group, Cell Origin)>();
            var internalGap = project.Settings.PublicWidth + 2;
            var targetInternalWidth = internalGroups.Count == 0 ? 0 : Math.Max(internalGroups.Max(Width),
                (int)Math.Ceiling(Math.Sqrt(internalGroups.Sum(group => (double)(Width(group) + internalGap) * (Height(group) + internalGap)) * project.Settings.Packing.PreferredAspectRatio)));
            var innerX = 0; var innerY = 0; var innerRowHeight = 0; var innerWidth = 0;
            foreach (var group in internalGroups.OrderByDescending(Height).ThenBy(group => LayoutGeometry.StableOrder(group[0].Id, project.Settings.Seed + attempt)))
            {
                if (innerX > 0 && innerX + Width(group) > targetInternalWidth) { innerY += innerRowHeight + internalGap; innerX = 0; innerRowHeight = 0; }
                positions.Add((group, new(innerX, innerY)));
                innerWidth = Math.Max(innerWidth, innerX + Width(group)); innerRowHeight = Math.Max(innerRowHeight, Height(group));
                innerX += Width(group) + internalGap;
            }
            var innerHeight = internalGroups.Count == 0 ? 0 : innerY + innerRowHeight;
            var leftLoop = -internalGap; var topLoop = -internalGap;
            var rightLoop = Math.Max(8, innerWidth + internalGap - 1); var bottomLoop = Math.Max(8, innerHeight + internalGap - 1);
            var publicHalo = project.Settings.PublicWidth / 2 + 2;
            var coreCenter = new Cell((leftLoop + rightLoop) / 2, (topLoop + bottomLoop) / 2);
            var facesByGroup = ordered.ToDictionary(group => group, group => PlanningPublicFaces(project, group, definitions));
            var occupied = new List<(GridBounds Bounds, List<OwnerPlan>? Group)>
            { (new(leftLoop - publicHalo, topLoop - publicHalo, rightLoop - leftLoop + 2 * publicHalo + 1, bottomLoop - topLoop + 2 * publicHalo + 1), null) };
            var targetWidth = Math.Max(ordered.Max(Width) + gap * 2, (int)Math.Ceiling(Math.Sqrt(ordered.Sum(group => (double)(Width(group) + gap) * (Height(group) + gap)) * project.Settings.Packing.PreferredAspectRatio)));
            bool Overlap(GridBounds a, GridBounds b) => a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;
            bool Perimeter(List<OwnerPlan> group) => group.Any(plan => plan.Slots.Any(slot => definitions[slot.Room.DefinitionPath].Placement.Anchor == "perimeter"));
            foreach (var group in ordered.Except(internalGroups).OrderBy(Perimeter))
            {
                var width = Width(group); var height = Height(group);
                var candidates = new HashSet<Cell>();
                foreach (var item in occupied)
                {
                    var r = item.Bounds;
                    candidates.Add(new(r.Right + gap, r.Y)); candidates.Add(new(r.Right + gap, r.Bottom - height));
                    candidates.Add(new(r.X - width - gap, r.Y)); candidates.Add(new(r.X - width - gap, r.Bottom - height));
                    candidates.Add(new(r.X, r.Bottom + gap)); candidates.Add(new(r.Right - width, r.Bottom + gap));
                    candidates.Add(new(r.X, r.Y - height - gap)); candidates.Add(new(r.Right - width, r.Y - height - gap));
                }
                var before = LayoutGeometry.Bounds(occupied.SelectMany(item => item.Bounds.Cells()));
                var sameOwner = positions.Where(item => item.Group.Any(plan => group.Any(other => other.OwnerId == plan.OwnerId))).ToList();
                var stationwide = group.Any(plan => plan.Slots.Any(slot => slot.Side == "stationwide"));
                var legal = candidates.Where(origin => !occupied.Any(item => Overlap(new GridBounds(origin.X, origin.Y, width, height).Expand(gap), item.Bounds))).ToList();
                var choice = legal
                    .Select(origin =>
                    {
                        var rect = new GridBounds(origin.X, origin.Y, width, height);
                        var left = Math.Min(before.X, rect.X); var top = Math.Min(before.Y, rect.Y);
                        var bounds = new GridBounds(left, top, Math.Max(before.Right, rect.Right) - left, Math.Max(before.Bottom, rect.Bottom) - top);
                        var aspect = (double)bounds.Width / bounds.Height;
                        var score = bounds.Area * (1 + .8 * Math.Abs(Math.Log(aspect / project.Settings.Packing.PreferredAspectRatio)));
                        score += Math.Pow(Math.Max(0, (double)bounds.Width / targetWidth - 1), 2) * targetWidth * targetWidth * 2;
                        score += bounds.Center.Distance(coreCenter) * (bounds.Width + bounds.Height) * 3;
                        score += PublicFacingCost(facesByGroup[group], origin, coreCenter, before.Area);
                        if (sameOwner.Count > 0)
                        {
                            var distance = sameOwner.Min(item => rect.Center.Distance(new GridBounds(item.Origin.X, item.Origin.Y, Width(item.Group), Height(item.Group)).Center));
                            score += stationwide ? Math.Max(0, (before.Width + before.Height) * .25 + project.Settings.Packing.StationwideSpacing - distance) * 20 : distance * project.Settings.Packing.DepartmentBundling * 5;
                        }
                        if (Perimeter(group) && rect.X > before.X && rect.Y > before.Y && rect.Right < before.Right && rect.Bottom < before.Bottom) score += before.Area;
                        return (Origin: origin, Score: score);
                    }).OrderBy(item => item.Score).ThenBy(item => LayoutGeometry.StableOrder(group[0].Id + item.Origin, project.Settings.Seed + attempt)).First();
                positions.Add((group, choice.Origin)); occupied.Add((new(choice.Origin.X, choice.Origin.Y, width, height), group));
            }
            var minimumX = occupied.Min(item => item.Bounds.X); var minimumY = occupied.Min(item => item.Bounds.Y);
            foreach (var item in positions) PlaceGroup(item.Group, item.Origin.X - minimumX, item.Origin.Y - minimumY);
            Waypoint(leftLoop - minimumX, topLoop - minimumY); Waypoint(rightLoop - minimumX, topLoop - minimumY);
            Waypoint(rightLoop - minimumX, bottomLoop - minimumY); Waypoint(leftLoop - minimumX, bottomLoop - minimumY);
        }
        else
        {
            var width = RowWidth(ordered); PlaceRow(ordered, width, 0);
        }
        }
        Arrange();
        if (project.Settings.Topology is "hub" or "spine" &&
            !project.Layout.Departments.Any(owner => owner.Pinned || owner.Locked) && !project.Layout.Rooms.Any(room => room.Pinned || room.FloorLocked || room.FloorEdited))
        {
            // A topology defines passage roles, not permission to violate a distributed
            // owner's envelope. Swap complete roles, preserving paired direct interfaces.
            var ownerShapes = project.Definition.Departments.ToDictionary(owner => owner.Id, owner => owner.Shape, StringComparer.Ordinal);
            var ownerPlans = plans.GroupBy(plan => plan.OwnerId).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
            var definitions = LayoutGeometry.Definitions(project);
            var floorEstimates = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && definitions.ContainsKey(room.DefinitionPath))
                .GroupBy(room => room.OwnerId).ToDictionary(group => group.Key, group => group.Sum(room => (long) Goal(room, definitions[room.DefinitionPath])), StringComparer.Ordinal);
            (int Count, double Excess, double Extent, HashSet<string> Owners) Score()
            {
                var violations = new HashSet<string>(StringComparer.Ordinal); var excess = 0d;
                foreach (var (ownerId, parts) in ownerPlans.Where(pair => pair.Value.Count > 1))
                {
                    var bounds = parts.SelectMany(plan => plan.Slots.Select(slot => new GridBounds(requested[plan.Id].X + slot.Bounds.X, requested[plan.Id].Y + slot.Bounds.Y, slot.Bounds.Width, slot.Bounds.Height))).ToList();
                    var width = bounds.Max(rect => rect.Right) - bounds.Min(rect => rect.X);
                    var height = bounds.Max(rect => rect.Bottom) - bounds.Min(rect => rect.Y);
                    if (width < 1 || height < 1) continue;
                    var shape = ownerShapes[ownerId]; var aspect = Math.Max((double) width / height, (double) height / width);
                    var fill = (double) floorEstimates.GetValueOrDefault(ownerId) / ((long) width * height);
                    var aspectExcess = Math.Max(0, aspect / shape.MaxAspectRatio - 1);
                    var fillExcess = Math.Max(0, 1 - fill / shape.MinFillRatio);
                    if (aspectExcess > 1e-9 || fillExcess > 1e-9) violations.Add(ownerId);
                    excess += aspectExcess * aspectExcess + fillExcess * fillExcess;
                }
                var left = plans.Min(plan => requested[plan.Id].X); var top = plans.Min(plan => requested[plan.Id].Y);
                var extent = (double) (plans.Max(plan => requested[plan.Id].X + plan.Width) - left) * (plans.Max(plan => requested[plan.Id].Y + plan.Height) - top);
                return (violations.Count, excess, extent, violations);
            }
            static bool Better((int Count, double Excess, double Extent, HashSet<string> Owners) candidate, (int Count, double Excess, double Extent, HashSet<string> Owners) current) =>
                candidate.Count < current.Count || candidate.Count == current.Count && (candidate.Excess < current.Excess - 1e-9 || Math.Abs(candidate.Excess - current.Excess) <= 1e-9 && candidate.Extent < current.Extent - 1e-9);
            var current = Score(); var evaluations = Math.Clamp(project.Settings.CandidateBudget * 512, 512, 8192);
            var first = project.Settings.Topology == "hub" ? 1 : 0;
            while (current.Count > 0 && evaluations > 0)
            {
                var best = current; var bestLeft = -1; var bestRight = -1;
                for (var left = first; left < ordered.Count && evaluations > 0; left++)
                for (var right = left + 1; right < ordered.Count && evaluations > 0; right++)
                {
                    if (!ordered[left].Concat(ordered[right]).Any(plan => current.Owners.Contains(plan.OwnerId))) continue;
                    (ordered[left], ordered[right]) = (ordered[right], ordered[left]); Arrange(); evaluations--;
                    var candidate = Score();
                    if (Better(candidate, best)) { best = candidate; bestLeft = left; bestRight = right; }
                    (ordered[left], ordered[right]) = (ordered[right], ordered[left]);
                }
                if (bestLeft < 0) break;
                (ordered[bestLeft], ordered[bestRight]) = (ordered[bestRight], ordered[bestLeft]); Arrange(); current = best;
            }
            Arrange();
            if (current.Count > 0)
                foreach (var owner in current.Owners) diagnostics.Add(new(owner, "owner-shape-packing", "The bounded topology-role search could not fit this distributed owner's authored shape limits. Existing requirements were retained."));
        }
        foreach (var ownerPlans in plans.GroupBy(plan => plan.OwnerId))
        {
            var existing = project.Layout.Departments.FirstOrDefault(owner => owner.OwnerId == ownerPlans.Key);
            var pinned = existing is { Pinned: true } or { Locked: true };
            var bounds = LayoutGeometry.Bounds(ownerPlans.SelectMany(plan => new GridBounds(requested[plan.Id].X, requested[plan.Id].Y, plan.Width, plan.Height).Cells()));
            var delta = pinned ? existing!.Anchor - bounds.Center : new Cell();
            foreach (var plan in ownerPlans) { plan.Protected = pinned; plan.Origin = requested[plan.Id] + delta; }
        }
        var neededWidth = plans.Max(plan => plan.Origin.X + plan.Width) + padding;
        var neededHeight = plans.Max(plan => plan.Origin.Y + plan.Height) + padding;
        if (project.Settings.AutoSize)
        {
            project.Settings.Width = Math.Min(512, Math.Max(project.Settings.Width, neededWidth)); project.Settings.Height = Math.Min(512, Math.Max(project.Settings.Height, neededHeight));
            if (neededWidth > 512 || neededHeight > 512) diagnostics.Add(new("settings", "placement-capacity", "This packing exceeds the supported 512×512 canvas. Its room instances and planning allocations remain editable; increase compactness or reduce the room program."));
        }
        else if (neededHeight > project.Settings.Height || neededWidth > project.Settings.Width)
            diagnostics.Add(new("settings", "placement-capacity", "The authored canvas cannot contain the selected topology and room program with its required spacing. Enlarge the canvas, choose a different topology, or enable automatic sizing."));
    }

    private static List<List<OwnerPlan>> OrderGroups(StationDesignProject project, List<List<OwnerPlan>> groups, int attempt)
    {
        var groupByOwner = groups.SelectMany((group, index) => group.Select(plan => (plan.OwnerId, Index: index))).GroupBy(item => item.OwnerId).ToDictionary(group => group.Key, group => group.Select(item => item.Index).Distinct().ToList(), StringComparer.Ordinal);
        var weights = new Dictionary<(int A, int B), double>(); var degree = new double[groups.Count];
        foreach (var connection in RequirementCompiler.Compile(project.Definition).Connections)
        foreach (var target in connection.To)
        {
            if (!groupByOwner.TryGetValue(RequirementCompiler.Owner(connection.From), out var sources) || !groupByOwner.TryGetValue(RequirementCompiler.Owner(target), out var targets)) continue;
            var sourceGroups = connection.From.Contains('/') ? sources.Where(index => groups[index].Any(plan => plan.Slots.Any(slot => slot.Room.DefinitionPath == connection.From))) : sources;
            var targetGroups = target.Contains('/') ? targets.Where(index => groups[index].Any(plan => plan.Slots.Any(slot => slot.Room.DefinitionPath == target))) : targets;
            foreach (var a in sourceGroups) foreach (var b in targetGroups)
            {
            if (a == b) continue;
            var weight = connection.Weight * (connection.Priority == "required" ? 25 : connection.Priority == "avoided" ? -1 : 1);
            var key = (Math.Min(a, b), Math.Max(a, b)); weights[key] = weights.GetValueOrDefault(key) + weight;
            degree[a] += Math.Max(0, weight); degree[b] += Math.Max(0, weight);
            }
        }
        var remaining = Enumerable.Range(0, groups.Count).ToHashSet(); var result = new List<List<OwnerPlan>>();
        var previous = -1;
        while (remaining.Count > 0)
        {
            var chosen = remaining.OrderByDescending(index => previous < 0 ? degree[index] : weights.GetValueOrDefault((Math.Min(previous, index), Math.Max(previous, index))) + degree[index] * .001 +
                    (groups[index].Any(plan => groups[previous].Any(other => other.OwnerId == plan.OwnerId)) ?
                        groups[index].Any(plan => plan.Slots.Any(slot => slot.Side == "stationwide")) ? -100 : project.Settings.Packing.DepartmentBundling * 30 : 0))
                .ThenBy(index => LayoutGeometry.StableOrder(string.Join("/", groups[index].Select(plan => plan.Id)), project.Settings.Seed + attempt * 7919)).First();
            result.Add(groups[chosen]); remaining.Remove(chosen); previous = chosen;
        }
        return result;
    }

    private static void PlacePlan(StationDesignProject project, OwnerPlan plan, List<ProgramDiagnostic> diagnostics)
    {
        var placement = project.Layout.Departments.FirstOrDefault(owner => owner.OwnerId == plan.OwnerId);
        if (placement == null) { placement = new() { OwnerId = plan.OwnerId }; project.Layout.Departments.Add(placement); }
        foreach (var slot in plan.Slots)
        {
            var room = slot.Room;
            if (room.Pinned && room.HasSeed && !room.FloorLocked && !room.FloorEdited && room.GrowthReservation.Count == 0 && room.Cells.Count == 0)
            {
                var planned = slot.Shape.Select(cell => cell + plan.Origin).ToList();
                var plannedBounds = LayoutGeometry.Bounds(planned);
                var plannedSeed = new Cell(slot.Side == "left" ? plannedBounds.Right - 1 : slot.Side == "right" ? plannedBounds.X : plannedBounds.Center.X, plannedBounds.Center.Y);
                if (!planned.Contains(plannedSeed)) plannedSeed = planned.OrderBy(cell => cell.Distance(plannedSeed)).First();
                var delta = room.Seed - plannedSeed;
                room.GrowthReservation = LayoutGeometry.Sorted(planned.Select(cell => cell + delta));
                room.Parcel = LayoutGeometry.Bounds(room.GrowthReservation); room.GrowthSide = slot.Side;
                room.PlannedArea = slot.Side == "spine" ? room.GrowthReservation.Count : null;
                continue;
            }
            if (room.FloorLocked || room.FloorEdited || room.Pinned && room.HasSeed || room.HasSeed && (room.GrowthReservation.Count > 0 || room.Cells.Count > 0) && HasFixedPhysicalDependency(project, room))
            {
                if (room.GrowthReservation.Count == 0) room.GrowthReservation = room.Cells.Count > 0 ? LayoutGeometry.Sorted(room.Cells) : room.Parcel.Cells().ToList();
                continue;
            }
            room.GrowthReservation = LayoutGeometry.Sorted(slot.Shape.Select(cell => cell + plan.Origin));
            room.Parcel = LayoutGeometry.Bounds(room.GrowthReservation);
            room.Seed = new(room.Parcel.X + room.Parcel.Width / 2, room.Parcel.Y + room.Parcel.Height / 2);
            if (slot.Side == "left") room.Seed = new(room.Parcel.Right - 1, room.Seed.Y);
            if (slot.Side == "right") room.Seed = new(room.Parcel.X, room.Seed.Y);
            if (!room.GrowthReservation.Contains(room.Seed)) room.Seed = room.GrowthReservation.OrderBy(cell => cell.Distance(room.Seed)).First();
            room.HasSeed = true; room.GrowthSide = slot.Side;
            room.PlannedArea = slot.Side == "spine" ? room.GrowthReservation.Count : null;
            room.Cells.Clear(); room.Bounds = new();
        }
    }

    private static void PlaceMaintenanceAndExterior(StationDesignProject project, Dictionary<string, RoomDefinition> definitions, List<OwnerPlan> plans, int gap, List<ProgramDiagnostic> diagnostics)
    {
        var core = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && room.OwnerId != RequirementCompiler.MaintenanceOwner && definitions[room.DefinitionPath].Environment.Placement != "exterior")
            .SelectMany(room => room.GrowthReservation.Concat(room.ComplexReservation).Concat(room.Cells)).ToHashSet();
        var coreBounds = LayoutGeometry.Bounds(core);
        var connections = RequirementCompiler.Compile(project.Definition).Connections;
        var special = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && string.IsNullOrEmpty(room.ParentInstanceId) && definitions.ContainsKey(room.DefinitionPath) &&
            (room.OwnerId == RequirementCompiler.MaintenanceOwner || definitions[room.DefinitionPath].Environment.Placement == "exterior"))
            .OrderBy(room => definitions[room.DefinitionPath].Environment.Placement == "exterior").ThenBy(room => LayoutGeometry.StableOrder(room.Id, project.Settings.Seed)).ToList();
        var maintenanceOwner = project.Layout.Departments.FirstOrDefault(owner => owner.OwnerId == RequirementCompiler.MaintenanceOwner);
        bool Preserved(DesignRoom room) => room.FloorLocked || room.FloorEdited || room.Pinned && room.HasSeed || room.HasSeed && (room.GrowthReservation.Count > 0 || room.Cells.Count > 0) && HasFixedPhysicalDependency(project, room) || room.OwnerId == RequirementCompiler.MaintenanceOwner && maintenanceOwner is { Pinned: true } or { Locked: true };
        var occupied = core.Concat(special.Where(Preserved).SelectMany(room => room.GrowthReservation.Concat(room.Cells))).ToHashSet();
        foreach (var room in special.Where(room => !Preserved(room))) { room.GrowthReservation.Clear(); room.ComplexReservation.Clear(); room.Cells.Clear(); }
        var dockForbidden = Halo(DockSpaceForCanvas(project, new(0, 0, 512, 512))); dockForbidden.UnionWith(BackboneReservation(project));
        var clearance = Math.Max(4, project.Settings.MaintenanceWidth + 2) + (int)Math.Round((1 - project.Settings.Packing.Compactness) * gap);
        foreach (var room in special)
        {
            if (Preserved(room)) continue;
            var forbidden = dockForbidden.Concat(Halo(project.Layout.CirculationReservations.SelectMany(reservation => reservation.BoundaryCells.Concat(reservation.ApproachCells))))
                .Concat(Halo(project.Layout.ReservedSpace)).ToHashSet();
            var definition = definitions[room.DefinitionPath]; var dimensions = RoomDimensions(project, room, definition);
            var blocked = occupied.ToHashSet(); for (var layer = 0; layer < clearance; layer++) blocked = Halo(blocked);
            var exterior = definition.Environment.Placement == "exterior";
            bool Matches(string reference, DesignRoom candidate) => reference == candidate.DefinitionPath || reference == candidate.OwnerId;
            var relations = connections.Where(connection => connection.Priority != "avoided" && (Matches(connection.From, room) || connection.To.Any(target => Matches(target, room))))
                .Select(connection => (Connection: connection, Targets: project.Layout.Rooms.Where(candidate => candidate.Id != room.Id && candidate.Enabled && !candidate.Orphaned &&
                    (candidate.GrowthReservation.Count > 0 || candidate.Cells.Count > 0) && (Matches(connection.From, room) ? connection.To.Any(target => Matches(target, candidate)) : Matches(connection.From, candidate))).ToList()))
                .Where(relation => relation.Targets.Count > 0).ToList();
            var hostsExterior = !exterior && connections.Any(connection => connection.Priority == "required" &&
                (Matches(connection.From, room) && connection.To.Any(path => definitions.TryGetValue(path, out var target) && target.Environment.Placement == "exterior") ||
                 connection.To.Any(path => Matches(path, room)) && definitions.TryGetValue(connection.From, out var source) && source.Environment.Placement == "exterior"));
            var margin = Math.Max(dimensions.Width, dimensions.Height) + clearance + 4;
            var sealingMargin = Math.Max(4, project.Settings.MaintenanceWidth + 2);
            var exteriorMargin = Math.Max(project.Settings.EnvelopePadding, sealingMargin);
            var x0 = Math.Max(sealingMargin, coreBounds.X - margin); var y0 = Math.Max(sealingMargin, coreBounds.Y - margin);
            var x1 = Math.Min(512 - dimensions.Width - exteriorMargin, coreBounds.Right + margin);
            var y1 = Math.Min(512 - dimensions.Height - exteriorMargin, coreBounds.Bottom + margin);
            var extent = LayoutGeometry.Bounds(occupied);
            var candidates = new List<(GridBounds Bounds, double Score)>();
            for (var y = y0; y <= y1; y += 2)
            for (var x = x0; x <= x1; x += 2)
            {
                var rect = new GridBounds(x, y, dimensions.Width, dimensions.Height);
                if (exterior && rect.X < coreBounds.Right + 1 && rect.Right > coreBounds.X - 1 && rect.Y < coreBounds.Bottom + 1 && rect.Bottom > coreBounds.Y - 1) continue;
                var left = Math.Min(extent.X, rect.X); var top = Math.Min(extent.Y, rect.Y);
                var area = (Math.Max(extent.Right, rect.Right) - left) * (Math.Max(extent.Bottom, rect.Bottom) - top);
                var relationshipCost = relations.Sum(relation => relation.Targets.Min(target => rect.Center.Distance(target.Seed)) * relation.Connection.Weight * (relation.Connection.Priority == "required" ? 4 : 1));
                var hullDepth = Math.Max(0, Math.Min(Math.Min(rect.Center.X - coreBounds.X, coreBounds.Right - 1 - rect.Center.X), Math.Min(rect.Center.Y - coreBounds.Y, coreBounds.Bottom - 1 - rect.Center.Y)));
                candidates.Add((rect, area + relationshipCost + (hostsExterior ? hullDepth * 10 : 0) + rect.Center.Distance(coreBounds.Center) * .2 + UnitNoise(project, room.Id + rect.X + ":" + rect.Y) * .01));
            }
            GridBounds? chosen = null;
            var entryChecks = 0;
            foreach (var candidate in candidates.OrderBy(candidate => candidate.Score).Take(8192))
            {
                if (!project.Settings.AutoSize && (candidate.Bounds.Right + 1 >= project.Settings.Width || candidate.Bounds.Bottom + 1 >= project.Settings.Height)) continue;
                if (candidate.Bounds.Cells().Any(cell => blocked.Contains(cell) || forbidden.Contains(cell))) continue;
                if (++entryChecks > 32) break;
                var oldWidth = project.Settings.Width; var oldHeight = project.Settings.Height;
                if (project.Settings.AutoSize)
                {
                    project.Settings.Width = Math.Min(512, Math.Max(project.Settings.Width, candidate.Bounds.Right + exteriorMargin));
                    project.Settings.Height = Math.Min(512, Math.Max(project.Settings.Height, candidate.Bounds.Bottom + exteriorMargin));
                }
                room.GrowthReservation = candidate.Bounds.Cells().ToList(); room.ComplexReservation = [.. room.GrowthReservation]; room.Parcel = candidate.Bounds; room.Seed = candidate.Bounds.Center;
                if (!TryReserveSpecialEntry(project, room, definitions))
                {
                    room.GrowthReservation.Clear(); room.ComplexReservation.Clear(); project.Settings.Width = oldWidth; project.Settings.Height = oldHeight;
                    continue;
                }
                chosen = candidate.Bounds; break;
            }
            if (chosen == null)
            {
                room.GrowthReservation.Clear(); room.ComplexReservation.Clear(); room.Cells.Clear(); room.Parcel = new(); room.Bounds = new(); room.HasSeed = true;
                diagnostics.Add(new(room.Id, "placement-capacity", "No protected interstice or nearby exterior slot fits this room without obstructing a dock or circulation lane. Its instance was retained for editing."));
                continue;
            }
            room.Parcel = chosen.Value; room.GrowthReservation = room.Parcel.Cells().ToList(); room.ComplexReservation = [.. room.GrowthReservation];
            room.Seed = room.Parcel.Center; room.HasSeed = true; room.GrowthSide = "exterior"; room.PlannedArea = null; room.Cells.Clear(); room.Bounds = new();
            occupied.UnionWith(room.GrowthReservation);
            if (project.Settings.AutoSize)
            {
                project.Settings.Width = Math.Min(512, Math.Max(project.Settings.Width, room.Parcel.Right + exteriorMargin));
                project.Settings.Height = Math.Min(512, Math.Max(project.Settings.Height, room.Parcel.Bottom + exteriorMargin));
            }
        }
        if (maintenanceOwner == null) { maintenanceOwner = new() { OwnerId = RequirementCompiler.MaintenanceOwner }; project.Layout.Departments.Add(maintenanceOwner); }
        if (!maintenanceOwner.Pinned && !maintenanceOwner.Locked)
        {
            maintenanceOwner.Bounds = LayoutGeometry.Bounds(project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && room.OwnerId == maintenanceOwner.OwnerId).SelectMany(room => room.GrowthReservation));
            maintenanceOwner.Anchor = maintenanceOwner.Bounds.Center;
        }
    }
    private sealed class NestedPlan
    {
        public GridBounds Bounds;
        public List<(DesignRoom Room, NestedPlan Plan, Cell Origin)> Children = [];
        public List<Cell> Own = [];
        public int FloorArea;
    }

    private static NestedPlan ComplexPlan(StationDesignProject project, DesignRoom room, Dictionary<string, RoomDefinition> definitions)
    {
        var definition = definitions[room.DefinitionPath];
        NestedPlan plan = null!;
        for (var iteration = 0; iteration < Math.Min(4096, Math.Max(1, project.Settings.GrowthIterations)); iteration++)
        {
            plan = BuildComplexPlan(project, room, definitions);
            if (definition.ComplexArea is not { } budget || plan.FloorArea >= budget.Min && plan.FloorArea <= budget.Max) return plan;
            var direction = plan.FloorArea > budget.Max ? -1 : 1;
            var choice = RoomHierarchy.Descendants(project, room, true).Where(candidate => candidate.Enabled && !candidate.Orphaned && !candidate.FloorLocked && !candidate.FloorEdited)
                .Where(candidate => direction < 0 ? Goal(candidate, definitions[candidate.DefinitionPath]) > RequirementCompiler.EffectiveMinimumArea(definitions[candidate.DefinitionPath]) : Goal(candidate, definitions[candidate.DefinitionPath]) < definitions[candidate.DefinitionPath].Area.Max)
                .Select(candidate =>
                {
                    var area = definitions[candidate.DefinitionPath].Area; var preferred = candidate.TargetAreaOverride ?? area.Target;
                    var current = Goal(candidate, definitions[candidate.DefinitionPath]);
                    var cost = area.Weight * (Math.Pow((double)(current + direction - preferred) / Math.Max(1, preferred), 2) - Math.Pow((double)(current - preferred) / Math.Max(1, preferred), 2));
                    return (Room: candidate, Cost: cost);
                }).OrderBy(item => item.Cost).ThenBy(item => item.Room.Id, StringComparer.Ordinal).FirstOrDefault();
            if (choice.Room == null) return plan;
            choice.Room.PlannedArea = Goal(choice.Room, definitions[choice.Room.DefinitionPath]) + direction;
        }
        return plan;
    }

    private static NestedPlan BuildComplexPlan(StationDesignProject project, DesignRoom room, Dictionary<string, RoomDefinition> definitions)
    {
        var definition = definitions[room.DefinitionPath];
        var children = project.Layout.Rooms.Where(child => child.Enabled && !child.Orphaned && child.ParentInstanceId == room.Id)
            .OrderBy(child => child.Id, StringComparer.Ordinal).Select(child => (Room: child, Plan: ComplexPlan(project, child, definitions))).ToList();
        if (children.Count == 0)
        {
            var bounds = RoomDimensions(project, room, definition);
            return new() { Bounds = bounds, Own = bounds.Cells().ToList(), FloorArea = Goal(room, definition) };
        }
        var blockedArea = children.Sum(child => (child.Plan.Bounds.Width + 2) * (child.Plan.Bounds.Height + 2));
        var minimumWidth = children.Max(child => child.Plan.Bounds.Width) + 4;
        var totalArea = Goal(room, definition) + blockedArea;
        NestedPlan? best = null; var bestCost = double.PositiveInfinity;
        var maximumWidth = Math.Min(512, Math.Max(minimumWidth, (int)Math.Ceiling(Math.Sqrt(totalArea) * 2)));
        for (var width = minimumWidth; width <= maximumWidth; width++)
        {
            var candidate = new NestedPlan(); var x = 2; var y = 2; var rowHeight = 0; var minimumHeight = 1;
            foreach (var child in children)
            {
                if (x > 2 && x + child.Plan.Bounds.Width + 2 > width) { x = 2; y += rowHeight + 3; rowHeight = 0; }
                candidate.Children.Add((child.Room, child.Plan, new(x, y)));
                minimumHeight = Math.Max(minimumHeight, y + child.Plan.Bounds.Height + 2);
                x += child.Plan.Bounds.Width + 3; rowHeight = Math.Max(rowHeight, child.Plan.Bounds.Height);
            }
            var height = Math.Max(minimumHeight, (int)Math.Ceiling((double)totalArea / width));
            candidate.Bounds = new(0, 0, width, height);
            var blocked = candidate.Children.SelectMany(child => new GridBounds(child.Origin.X - 1, child.Origin.Y - 1, child.Plan.Bounds.Width + 2, child.Plan.Bounds.Height + 2).Cells()).ToHashSet();
            candidate.Own = candidate.Bounds.Cells().Except(blocked).ToList();
            candidate.FloorArea = candidate.Own.Count + children.Sum(child => child.Plan.FloorArea);
            if (candidate.Own.Count < RequirementCompiler.EffectiveMinimumArea(definition) || candidate.Own.Count > definition.Area.Max || !LayoutGeometry.Connected(candidate.Own) ||
                !LayoutGeometry.ShapeFits(candidate.Bounds.Cells(), definition.ComplexShape ?? definition.Shape)) continue;
            if (definition.ComplexArea is { } budget && (candidate.FloorArea < budget.Min || candidate.FloorArea > budget.Max)) continue;
            var cost = Math.Abs(candidate.Own.Count - Goal(room, definition)) * 10 + candidate.Bounds.Area + Math.Abs((double)width / height - 1.15) * 5;
            if (cost < bestCost) { best = candidate; bestCost = cost; }
        }
        // An impossible parent remains an inspectable draft; its author bounds are never relaxed.
        if (best != null) return best;
        var fallbackWidth = children.Sum(child => child.Plan.Bounds.Width + 3) + 1;
        var fallbackHeight = children.Max(child => child.Plan.Bounds.Height) + 4;
        var fallback = new NestedPlan { Bounds = new(0, 0, fallbackWidth, fallbackHeight) }; var offset = 2;
        foreach (var child in children) { fallback.Children.Add((child.Room, child.Plan, new(offset, 2))); offset += child.Plan.Bounds.Width + 3; }
        var excluded = fallback.Children.SelectMany(child => new GridBounds(child.Origin.X - 1, child.Origin.Y - 1, child.Plan.Bounds.Width + 2, child.Plan.Bounds.Height + 2).Cells()).ToHashSet();
        fallback.Own = fallback.Bounds.Cells().Except(excluded).ToList(); fallback.FloorArea = fallback.Own.Count + children.Sum(child => child.Plan.FloorArea);
        return fallback;
    }

    private static void PlaceNestedRooms(StationDesignProject project, Dictionary<string, RoomDefinition> definitions, List<ProgramDiagnostic> diagnostics)
    {
        foreach (var room in project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && string.IsNullOrEmpty(room.ParentInstanceId)).ToList())
        {
            var plan = ComplexPlan(project, room, definitions);
            if (plan.Children.Count == 0) { room.ComplexReservation = [.. room.GrowthReservation]; continue; }
            Place(room, plan, new(room.Parcel.X, room.Parcel.Y));
        }
        void Place(DesignRoom room, NestedPlan plan, Cell origin, bool preserveAllocation = false)
        {
            var preserved = preserveAllocation || room.FloorLocked || room.FloorEdited || room.Pinned && room.HasSeed && room.ComplexReservation.Count > 0 || room.HasSeed && (room.GrowthReservation.Count > 0 || room.Cells.Count > 0) && HasFixedPhysicalDependency(project, room);
            if (!preserved || room.ComplexReservation.Count == 0)
                room.ComplexReservation = LayoutGeometry.Sorted(plan.Bounds.Cells().Select(cell => cell + origin));
            if (!preserved)
            {
                room.GrowthReservation = LayoutGeometry.Sorted(plan.Own.Select(cell => cell + origin));
                room.Parcel = LayoutGeometry.Bounds(room.ComplexReservation);
                if (plan.Children.Count > 0) { room.GrowthSide = "complex"; room.PlannedArea = room.GrowthReservation.Count; }
                if (!room.Pinned || !room.HasSeed)
                    room.Seed = room.GrowthReservation.OrderBy(cell => cell.Distance(room.Parcel.Center)).ThenBy(cell => cell.Y).ThenBy(cell => cell.X).First();
                room.HasSeed = true; room.Cells.Clear(); room.Bounds = new();
            }
            var definition = definitions[room.DefinitionPath];
            if (plan.Children.Count > 0 && (room.GrowthReservation.Count < RequirementCompiler.EffectiveMinimumArea(definition) || room.GrowthReservation.Count > definition.Area.Max))
                diagnostics.Add(new(room.Id, "nested-capacity", "The nested complex cannot provide the parent's exclusive floor and child wall/circulation space within its authored bounds."));
            if (definition.ComplexArea is { } budget && (plan.FloorArea < budget.Min || plan.FloorArea > budget.Max))
                diagnostics.Add(new(room.Id, "complex-area-capacity", "The nested complex cannot fit its exclusive subtree floor budget together with the required walls and parent circulation."));
            foreach (var child in plan.Children)
            {
                var childOrigin = origin + child.Origin;
                if (child.Room.FloorLocked || child.Room.FloorEdited || child.Room.Pinned && child.Room.HasSeed || child.Room.HasSeed && (child.Room.GrowthReservation.Count > 0 || child.Room.Cells.Count > 0) && HasFixedPhysicalDependency(project, child.Room))
                {
                    var retained = child.Room.Cells.Concat(child.Room.GrowthReservation).ToHashSet();
                    if (!retained.IsSubsetOf(room.ComplexReservation)) diagnostics.Add(new(child.Room.Id, "nested-preserved-outside", "The protected child lies outside its parent's complex; its geometry was retained for explicit repair."));
                    var halo = retained.Concat(retained.SelectMany(cell => Cell.Directions.Select(direction => cell + direction))).ToHashSet();
                    room.GrowthReservation.RemoveAll(halo.Contains);
                    continue;
                }
                Place(child.Room, child.Plan, preserved && child.Room.HasSeed ? new(child.Room.Parcel.X, child.Room.Parcel.Y) : childOrigin, preserved && child.Room.HasSeed);
            }
        }
    }

    private static void RefreshOwnerPlanning(StationDesignProject project)
    {
        foreach (var owner in project.Layout.Departments.Where(owner => !owner.Pinned && !owner.Locked))
        {
            var masks = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && room.OwnerId == owner.OwnerId).SelectMany(room => room.GrowthReservation.Concat(room.ComplexReservation));
            owner.Bounds = LayoutGeometry.Bounds(masks); owner.Anchor = owner.Bounds.Center;
        }
    }

    internal static int Goal(DesignRoom room, RoomDefinition definition) => Math.Clamp(room.PlannedArea ?? room.TargetAreaOverride ?? definition.Area.Target, RequirementCompiler.EffectiveMinimumArea(definition), definition.Area.Max);

    private static double UnitNoise(StationDesignProject project, string key) =>
        (double)LayoutGeometry.StableOrder(key, project.Settings.Seed + project.Layout.PlacementAttempt * 7919) / int.MaxValue;

    private static GridBounds RoomDimensions(StationDesignProject project, DesignRoom room, RoomDefinition definition)
    {
        var area = Goal(room, definition); var shape = definition.ComplexShape ?? definition.Shape;
        var frontage = definition.Environment.Placement == "exterior" ? 0 : definition.Exterior.MinSpaceFrontage + definition.Contents.Where(content => content.Placement == "spaceBoundary").Sum(content => content.Count.Target);
        if (project.Settings.Packing.InternalVariation <= 0 && frontage == 0) return Dimensions(area, shape);
        var variation = project.Settings.Packing.InternalVariation;
        var spread = Math.Log(Math.Min(2.8, shape.MaxAspectRatio));
        var preferred = Math.Exp(Math.Log(Math.Min(1.8, shape.MaxAspectRatio)) * (1 - variation) +
            (UnitNoise(project, room.Id + ":aspect") * 2 - 1) * spread * variation);
        var minimum = Math.Max(1, (int)Math.Floor(Math.Sqrt(area / shape.MaxAspectRatio)));
        var maximum = Math.Max(minimum, (int)Math.Ceiling(Math.Sqrt(area * shape.MaxAspectRatio)) + 1);
        var choices = Enumerable.Range(minimum, maximum - minimum + 1).Select(width => new GridBounds(0, 0, width, Math.Max(frontage, (int)Math.Ceiling((double)area / width))))
            .Where(bounds => Math.Max((double)bounds.Width / bounds.Height, (double)bounds.Height / bounds.Width) <= shape.MaxAspectRatio + 1e-9 && (double)area / bounds.Area >= shape.MinFillRatio - 1e-9)
            .Where(bounds => bounds.Height >= frontage && bounds.Area <= definition.Area.Max)
            .OrderBy(bounds => Math.Abs(Math.Log((double)bounds.Width / bounds.Height / preferred)) + (double)(bounds.Area - area) / Math.Max(1, area) * .2)
            .ThenBy(bounds => bounds.Area).ThenBy(bounds => bounds.Width).ToList();
        return choices.Count > 0 ? choices[0] : Dimensions(area, shape);
    }

    private static void VaryPlan(StationDesignProject project, OwnerPlan plan, Pair? pair, List<int> rowStarts, int spineWidth)
    {
        if (pair != null || plan.Slots.Count == 0 || project.Layout.Departments.Any(owner => owner.OwnerId == plan.OwnerId && (owner.Pinned || owner.Locked)) ||
            plan.Slots.Any(slot => HasFixedPhysicalDependency(project, slot.Room) || RoomHierarchy.Descendants(project, slot.Room, true).Any(room => room.Pinned || room.FloorLocked || room.FloorEdited))) return;
        var amount = project.Settings.Packing.SilhouetteVariation;
        if (rowStarts.Count >= 3 && spineWidth >= 3 && amount > 0 && UnitNoise(project, plan.OwnerId + ":bend") < amount)
        {
            var original = plan.Slots.ToDictionary(slot => slot, slot => slot.Shape.ToList());
            var pivot = rowStarts[1 + LayoutGeometry.StableOrder(plan.OwnerId + ":pivot", project.Settings.Seed) % (rowStarts.Count - 1)] - 1;
            var maximum = Math.Max(1, spineWidth - 2);
            var shift = (1 + LayoutGeometry.StableOrder(plan.OwnerId + ":offset", project.Settings.Seed) % maximum) *
                (UnitNoise(project, plan.OwnerId + ":sign") < .5 ? -1 : 1);
            foreach (var slot in plan.Slots)
            {
                slot.Shape = LayoutGeometry.Sorted(slot.Shape.Select(cell => cell.Y >= pivot ? new Cell(cell.X + shift, cell.Y) : cell));
                slot.Bounds = LayoutGeometry.Bounds(slot.Shape);
            }
            var definitions = LayoutGeometry.Definitions(project); var valid = true;
            foreach (var slot in plan.Slots.Where(slot => definitions[slot.Room.DefinitionPath].Role == "filler"))
            {
                var other = plan.Slots.Where(candidate => candidate != slot).SelectMany(candidate => candidate.Shape).ToHashSet();
                var floor = slot.Shape.Where(cell => !Cell.Directions.Any(direction => other.Contains(cell + direction))).ToHashSet();
                var definition = definitions[slot.Room.DefinitionPath];
                if (floor.Count < RequirementCompiler.EffectiveMinimumArea(definition) || !LayoutGeometry.Connected(floor) || !LayoutGeometry.ShapeFits(floor, definition.ComplexShape ?? definition.Shape)) { valid = false; break; }
                slot.Shape = LayoutGeometry.Sorted(floor); slot.Bounds = LayoutGeometry.Bounds(floor);
            }
            if (!valid) foreach (var slot in plan.Slots) { slot.Shape = original[slot]; slot.Bounds = LayoutGeometry.Bounds(slot.Shape); }
        }
        var bounds = LayoutGeometry.Bounds(plan.Slots.SelectMany(slot => slot.Shape));
        foreach (var slot in plan.Slots)
        {
            slot.Shape = LayoutGeometry.Sorted(slot.Shape.Select(cell => new Cell(cell.X - bounds.X, cell.Y - bounds.Y)));
            slot.Bounds = LayoutGeometry.Bounds(slot.Shape);
        }
        plan.Width = bounds.Width; plan.Height = bounds.Height;
        plan.OriginalWidth = plan.Width; plan.OriginalHeight = plan.Height;
        if (UnitNoise(project, plan.OwnerId + ":orientation") < project.Settings.Packing.InternalVariation * .8)
        {
            plan.Rotation = UnitNoise(project, plan.OwnerId + ":turn") < .5 ? 1 : 3;
            (plan.Width, plan.Height) = (plan.Height, plan.Width);
        }
    }

    private static void ApplyPlanRotations(StationDesignProject project, List<OwnerPlan> plans)
    {
        foreach (var plan in plans.Where(plan => plan.Rotation != 0))
        {
            Cell Rotate(Cell cell)
            {
                var local = cell - plan.Origin; var width = plan.OriginalWidth; var height = plan.OriginalHeight;
                for (var step = 0; step < plan.Rotation; step++) { local = new(height - 1 - local.Y, local.X); (width, height) = (height, width); }
                return local + plan.Origin;
            }
            foreach (var room in plan.Slots.SelectMany(slot => RoomHierarchy.Descendants(project, slot.Room, true)).DistinctBy(room => room.Id))
            {
                room.Cells = LayoutGeometry.Sorted(room.Cells.Select(Rotate));
                room.GrowthReservation = LayoutGeometry.Sorted(room.GrowthReservation.Select(Rotate));
                room.ComplexReservation = LayoutGeometry.Sorted(room.ComplexReservation.Select(Rotate));
                room.FrontageFloor = LayoutGeometry.Sorted(room.FrontageFloor.Select(Rotate)); room.FrontageSpace = LayoutGeometry.Sorted(room.FrontageSpace.Select(Rotate));
                room.Parcel = LayoutGeometry.Bounds(room.Parcel.Cells().Select(Rotate));
                if (room.HasSeed) room.Seed = Rotate(room.Seed);
                for (var step = 0; step < plan.Rotation; step++) room.GrowthSide = room.GrowthSide switch { "left" => "top", "top" => "right", "right" => "bottom", "bottom" => "left", _ => room.GrowthSide };
                room.Bounds = LayoutGeometry.Bounds(room.Cells);
            }
        }
    }

    private static GridBounds Dimensions(int area, ShapeRequirement shape)
    {
        var aspect = Math.Min(1.8, shape.MaxAspectRatio);
        var width = Math.Max(1, (int) Math.Ceiling(Math.Sqrt(area * aspect)));
        var height = Math.Max(1, (int) Math.Ceiling((double) area / width));
        while ((double) width / height > shape.MaxAspectRatio + 1e-9) { width--; height = (int) Math.Ceiling((double) area / width); }
        return new(0, 0, width, height);
    }

    private static StationDesignProject Finish(StationDesignProject project, List<ProgramDiagnostic> diagnostics)
    {
        project.Stage = "placement";
        project.Report = new() { Stage = "placement", Complete = false, Diagnostics = diagnostics };
        project.Tree = ProjectTree.Build(project);
        return project;
    }
}
