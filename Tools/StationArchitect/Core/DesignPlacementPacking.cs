namespace Content.StationArchitect;

public static partial class DesignPlacement
{
    private sealed class RuntimeCluster
    {
        public List<OwnerPlan> Plans = [];
        public List<DesignRoom> Rooms = [];
        public HashSet<string> Ids = new(StringComparer.Ordinal);
        public bool Protected;
        private HashSet<Cell>? _floor;
        private GridBounds? _bounds;
        private Dictionary<string, GridBounds>? _ownerBounds;
        public HashSet<Cell> Floor() => _floor ??= Rooms.SelectMany(room => room.GrowthReservation.Concat(room.ComplexReservation).Concat(room.Cells)).ToHashSet();
        public GridBounds Bounds() { _bounds ??= LayoutGeometry.Bounds(Floor()); return _bounds.Value; }
        public Dictionary<string, GridBounds> OwnerBounds() => _ownerBounds ??= Rooms.GroupBy(room => room.OwnerId).ToDictionary(group => group.Key, group => LayoutGeometry.Bounds(group.SelectMany(room => room.GrowthReservation.Concat(room.ComplexReservation).Concat(room.Cells))), StringComparer.Ordinal);
        public void Invalidate() { _floor = null; _bounds = null; _ownerBounds = null; }
    }

    private static Cell Outward(string side) => side switch { "left" => new(-1, 0), "right" => new(1, 0), "top" => new(0, -1), "bottom" => new(0, 1), _ => new() };
    private static int PlanningPadding(StationDesignProject project) => Math.Max(project.Settings.EnvelopePadding + 2,
        Math.Max(project.Settings.PublicWidth, project.Settings.MaintenanceWidth) + 2);
    private static HashSet<Cell> Halo(IEnumerable<Cell> cells) => cells.SelectMany(cell => Cell.Directions.Select(direction => cell + direction).Append(cell)).ToHashSet();

    private static bool HasFixedPhysicalDependency(StationDesignProject project, DesignRoom root)
    {
        var rooms = RoomHierarchy.Descendants(project, root, true).ToList();
        var ids = rooms.Select(room => room.Id).ToHashSet(StringComparer.Ordinal);
        var mask = rooms.SelectMany(room => room.GrowthReservation.Concat(room.ComplexReservation).Concat(room.Cells)).ToHashSet();
        var protectedSystemDevices = project.Layout.SystemDevices.Where(device => device.Edited || device.Locked).Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
        protectedSystemDevices.UnionWith(project.Layout.SystemControls.Where(control => control.Edited || control.Locked).SelectMany(control => control.DeviceIds.Append(control.ControllerDeviceId)));
        protectedSystemDevices.UnionWith(project.Layout.SystemNetworks.Where(network => network.Edited || network.Locked || network.Segments.Any(segment => segment.Edited || segment.Locked))
            .SelectMany(network => network.Terminals.Select(terminal => terminal.DeviceId)));
        var protectedSystemEntities = project.Layout.SystemDevices.Where(device => protectedSystemDevices.Contains(device.Id)).Select(device => device.EntityId).ToHashSet(StringComparer.Ordinal);
        var support = Halo(mask);
        return project.Layout.Equipment.Any(entity => entity.Pinned && ids.Contains(entity.RoomId)) ||
            project.Layout.Equipment.Any(entity => protectedSystemEntities.Contains(entity.Id) && ids.Contains(entity.RoomId)) ||
            project.Layout.SystemNetworks.Any(network => network.Segments.Any(segment => (network.Edited || network.Locked || segment.Edited || segment.Locked) && support.Contains(segment.Position))) ||
            project.Layout.Docks.Any(dock => (dock.Edited || dock.Locked) && ids.Contains(dock.RoomId)) ||
            project.Layout.CirculationReservations.Any(reservation => (reservation.Edited || reservation.Locked) && ids.Contains(reservation.RoomId)) ||
            project.Layout.Interfaces.Any(port => port.Edited && (ids.Contains(port.From) || ids.Contains(port.To))) ||
            project.Layout.Routes.Any(route => (route.Edited || route.Locked) && (ids.Contains(route.From) || ids.Contains(route.To) || route.Cells.Any(mask.Contains))) ||
            project.Layout.TransportNetworks.Any(network => (network.Edited || network.Locked) && (network.Terminals.Any(terminal => ids.Contains(terminal.RoomId)) || network.Segments.Any(segment => mask.Contains(segment.Position))));
    }

    private static void PlanDenseLayout(StationDesignProject project, List<OwnerPlan> plans, List<Pair> pairs,
        Dictionary<string, RoomDefinition> definitions, int gap, List<ProgramDiagnostic> diagnostics)
    {
        var fixedRoutes = project.Layout.Routes.Where(route => route.Edited || route.Locked).ToList();
        var fixedTransport = project.Layout.TransportNetworks.Where(network => network.Edited || network.Locked).ToList();
        var fixedPorts = project.Layout.Interfaces.Where(port => port.Edited).ToList();
        var fixedApproaches = project.Layout.CirculationReservations.Where(reservation => reservation.Edited || reservation.Locked).ToList();
        var fixedSystemNetworks = project.Layout.SystemNetworks.Where(network => network.Edited || network.Locked || network.Segments.Any(segment => segment.Edited || segment.Locked)).ToList();
        var fixedNetwork = project.Layout.PublicEdited || project.Layout.MaintenanceEdited || fixedRoutes.Count > 0 || fixedTransport.Count > 0 || fixedPorts.Count > 0 || fixedApproaches.Count > 0 ||
            fixedSystemNetworks.Count > 0 || project.Layout.SystemDevices.Any(device => device.Edited || device.Locked) || project.Layout.SystemControls.Any(control => control.Edited || control.Locked);
        var fixedCells = (project.Layout.PublicEdited ? project.Layout.PublicCorridors : Enumerable.Empty<Cell>())
            .Concat(project.Layout.MaintenanceEdited ? project.Layout.Maintenance : Enumerable.Empty<Cell>())
            .Concat(fixedRoutes.SelectMany(route => route.Cells)).Concat(fixedPorts.SelectMany(port => port.Cells.Concat([port.FromApproach, port.ToApproach])))
            .Concat(fixedApproaches.SelectMany(reservation => reservation.BoundaryCells.Concat(reservation.ApproachCells)))
            .Concat(fixedSystemNetworks.SelectMany(network => network.Segments.Select(segment => segment.Position)))
            .Concat(fixedTransport.SelectMany(network => network.Segments.Select(segment => segment.Position).Concat(network.Terminals.SelectMany(terminal => terminal.Footprint)))).ToHashSet();
        var fixedHalo = Halo(fixedCells);
        var clusters = new List<RuntimeCluster>(); var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            var left = plans.FirstOrDefault(plan => plan.OwnerId == pair.Left && plan.Slots.Any(slot => slot.Room.DefinitionPath == pair.LeftRoom));
            var right = plans.FirstOrDefault(plan => plan.OwnerId == pair.Right && plan.Slots.Any(slot => slot.Room.DefinitionPath == pair.RightRoom));
            if (left == null || right == null || assigned.Contains(left.Id) || assigned.Contains(right.Id)) continue;
            Add([left, right]);
        }
        foreach (var plan in plans.Where(plan => !assigned.Contains(plan.Id))) Add([plan]);
        void Add(List<OwnerPlan> parts)
        {
            var rooms = parts.SelectMany(plan => plan.Slots).SelectMany(slot => RoomHierarchy.Descendants(project, slot.Room, true)).DistinctBy(room => room.Id).ToList();
            var ids = rooms.Select(room => room.Id).ToHashSet(StringComparer.Ordinal);
            clusters.Add(new() { Plans = parts, Rooms = rooms, Ids = ids, Protected = parts.Any(plan => plan.Protected) || rooms.Any(room => room.Pinned || room.FloorLocked || room.FloorEdited) ||
                parts.Any(plan => plan.Slots.Any(slot => HasFixedPhysicalDependency(project, slot.Room))) });
            foreach (var part in parts) assigned.Add(part.Id);
        }
        var backbone = BackboneReservation(project);
        project.Layout.CirculationReservations.RemoveAll(reservation => !reservation.Edited && !reservation.Locked);
        project.Layout.Docks.RemoveAll(dock => !dock.Edited && !dock.Locked && !project.Layout.Equipment.Any(entity => entity.Id == dock.EntityId && entity.Pinned) &&
            !project.Layout.Rooms.Any(room => room.Id == dock.RoomId && (room.FloorLocked || room.FloorEdited || room.Pinned)));
        foreach (var room in clusters.SelectMany(cluster => cluster.Rooms).Where(room => definitions[room.DefinitionPath].Exterior.Docks.Count > 0).ToList())
        foreach (var rule in definitions[room.DefinitionPath].Exterior.Docks)
        {
            var content = definitions[room.DefinitionPath].Contents.FirstOrDefault(content => content.Id == rule.ContentId);
            if (content == null) continue;
            for (var instance = 1; instance <= content.Count.Target; instance++)
            {
                if (project.Layout.Docks.Any(dock => dock.RoomId == room.Id && dock.RequirementId == rule.Id && dock.Instance == instance)) continue;
                var cluster = clusters.First(cluster => cluster.Ids.Contains(room.Id));
                DesignDock? Pick()
                {
                    var view = PlanningView(project);
                    var planned = view.Layout.Rooms.Where(candidate => candidate.Enabled && !candidate.Orphaned).SelectMany(candidate => candidate.GrowthReservation.Concat(candidate.Cells)).ToHashSet();
                    return DesignDockGeometry.Candidates(view, room, rule, instance).FirstOrDefault(dock => !DesignDockGeometry.ReservedCells(dock).Overlaps(backbone) && !DesignDockGeometry.ReservedCells(dock).Overlaps(fixedHalo) &&
                        !DesignDockGeometry.PlanningIntersects(view, dock, planned, true) && !view.Layout.Docks.Any(other => DesignDockGeometry.PlanningOverlap(view, dock, other)));
                }
                var dock = Pick();
                if (dock == null && !cluster.Protected)
                {
                    var own = cluster.Floor(); var ownBounds = LayoutGeometry.Bounds(own);
                    var others = clusters.Where(other => other != cluster).SelectMany(other => other.Floor()).ToHashSet(); var otherHalo = Halo(others);
                    var all = LayoutGeometry.Bounds(others); var center = all.Center;
                    var moves = new List<Cell>
                    {
                        new(center.X - ownBounds.Center.X, all.Y - gap - ownBounds.Bottom),
                        new(center.X - ownBounds.Center.X, all.Bottom + gap - ownBounds.Y),
                        new(all.X - gap - ownBounds.Right, center.Y - ownBounds.Center.Y),
                        new(all.Right + gap - ownBounds.X, center.Y - ownBounds.Center.Y)
                    };
                    foreach (var normal in Cell.Directions)
                        for (var distance = 2; distance <= 64; distance += 2) moves.Add(new(normal.X * distance, normal.Y * distance));
                    foreach (var delta in moves.Distinct().OrderBy(delta => delta.Distance(new())).ThenBy(delta => LayoutGeometry.StableOrder(room.Id + delta, project.Settings.Seed)))
                    {
                        var moved = own.Select(cell => cell + delta).ToHashSet();
                        var extent = LayoutGeometry.Bounds(moved.Concat(others));
                        var canExpand = project.Settings.AutoSize && !fixedNetwork && !project.Layout.EnvelopeEdited && !clusters.Any(other => other.Protected);
                        if ((!canExpand && moved.Any(cell => cell.X < 4 || cell.Y < 4 || cell.X >= project.Settings.Width - 4 || cell.Y >= project.Settings.Height - 4)) ||
                            extent.Width + 2 * project.Settings.EnvelopePadding > 512 || extent.Height + 2 * project.Settings.EnvelopePadding > 512 || moved.Overlaps(otherHalo) || moved.Overlaps(backbone) || moved.Overlaps(fixedHalo) || moved.Overlaps(Halo(DesignDockGeometry.ReservedCells(project)))) continue;
                        if (project.Layout.Docks.Where(other => !cluster.Ids.Contains(other.RoomId)).Any(other => DesignDockGeometry.PlanningIntersects(project, other, moved, true))) continue;
                        MoveCluster(project, cluster, delta); dock = Pick();
                        if (dock != null) break;
                        MoveCluster(project, cluster, new(-delta.X, -delta.Y));
                    }
                }
                if (dock == null) diagnostics.Add(new(room.Id, "dock-placement", "No declared shuttle berth and full-width straight approach fits this room's exposed boundary. The room and docking requirements were retained for editing.", instance > content.Count.Min ? "warning" : "error"));
                else project.Layout.Docks.Add(dock);
            }
        }
        var privateBounds = LayoutGeometry.Bounds(clusters.SelectMany(cluster => cluster.Floor()));
        if (project.Settings.AutoSize && !fixedNetwork && !project.Layout.EnvelopeEdited && !clusters.Any(cluster => cluster.Protected))
        {
            var padding = PlanningPadding(project);
            var delta = new Cell(Math.Max(0, padding - privateBounds.X), Math.Max(0, padding - privateBounds.Y));
            if (delta != new Cell())
            {
                foreach (var cluster in clusters) MoveCluster(project, cluster, delta);
                project.Layout.PublicWaypoints = project.Layout.PublicWaypoints.Select(cell => cell + delta).ToList();
            }
            privateBounds = LayoutGeometry.Bounds(clusters.SelectMany(cluster => cluster.Floor()));
            project.Settings.Width = Math.Min(512, Math.Max(project.Settings.Width, privateBounds.Right + padding));
            project.Settings.Height = Math.Min(512, Math.Max(project.Settings.Height, privateBounds.Bottom + padding));
            RefreshGeneratedDocks(project); backbone = BackboneReservation(project);
        }
        foreach (var cluster in clusters) ReserveClusterApproaches(project, cluster, definitions);
        var initialCapacity = MeasurePlanningCapacity(project, clusters, honorMaintenance: false);
        var missingPublic = project.Layout.CirculationReservations.Where(reservation => reservation.Network == "publicHall" && !reservation.Edited && !reservation.Locked && !initialCapacity.Public.Contains(reservation.Id)).Select(reservation => reservation.Id).ToHashSet(StringComparer.Ordinal);
        project.Layout.CirculationReservations.RemoveAll(reservation => reservation.Network == "maintenance" && !reservation.Edited && !reservation.Locked || missingPublic.Contains(reservation.Id));
        foreach (var cluster in clusters) ReserveClusterApproaches(project, cluster, definitions, publicCenters: initialCapacity.PublicCenters, networkOnly: "publicHall");
        var maintenanceCenters = MaintenanceCandidateCenters(project, DesignNetworkGeometry.LegalCenters(project, "maintenance", planning: true, additionalBlocked: BackboneReservation(project)), []);
        foreach (var cluster in clusters) ReserveClusterApproaches(project, cluster, definitions, maintenanceCenters: maintenanceCenters, networkOnly: "maintenance");
        foreach (var plan in plans.Where(plan => plan.Slots.Any(slot => definitions[slot.Room.DefinitionPath].Access.AllowPublicEntry)))
            if (!project.Layout.CirculationReservations.Any(reservation => reservation.Network == "publicHall" && plan.Slots.Any(slot => slot.Room.Id == reservation.RoomId)))
                diagnostics.Add(new(plan.Id, "public-entry-plan", "No connected full-width public entrance was found for this eligible cluster; its allocation remains unverified.", "warning"));
        RoomGrowth.ReservePlanningFrontages(project);
        if (project.Definition.Maintenance.Connectivity.Policy == "controlled")
        {
            CompactLocalPlans(project, clusters, definitions);
            // A neighboring cluster may have moved off a previously obstructed
            // facade. Reconsider omitted automatic entrances against the final
            // masks before placing special rooms; an old omission is not a pin.
            var finalPublicLegal = DesignNetworkGeometry.LegalCenters(project, "publicHall", planning: true);
            var finalPublicSource = project.Layout.PublicWaypoints.FirstOrDefault();
            var finalPublicCenters = FloodCenters(finalPublicLegal, finalPublicSource);
            foreach (var cluster in clusters) ReserveClusterApproaches(project, cluster, definitions, publicCenters: finalPublicCenters, networkOnly: "publicHall");
            var finalMaintenanceLegal = DesignNetworkGeometry.LegalCenters(project, "maintenance", planning: true, additionalBlocked: BackboneReservation(project));
            foreach (var cluster in clusters) ReserveClusterApproaches(project, cluster, definitions, maintenanceCenters: finalMaintenanceLegal, networkOnly: "maintenance");
            diagnostics.RemoveAll(issue => issue.Code == "public-entry-plan" && plans.Any(plan => plan.Id == issue.Path &&
                project.Layout.CirculationReservations.Any(guide => guide.Network == "publicHall" && plan.Slots.Any(slot => slot.Room.Id == guide.RoomId))));
            return;
        }
        // Establish joint feasibility before optimizing compactness. Once published, these
        // world-space passages and doorways are hard obstacles for every packing move.
        var proof = DesignCirculationPlanning.Connect(project, ExpectedApproachIds(project, plans, definitions),
            guide => AlternativeApproaches(project, plans, pairs, definitions, guide));
        if (!proof.Complete)
        {
            PackingRepairTrace("initial constructive proof: " + string.Join(';', proof.Diagnostics.Select(issue => issue.Code + ':' + issue.Path)));
            return;
        }
        CompactWithFixedCirculation(project, clusters, definitions);
    }

    private static StationDesignProject PlanningView(StationDesignProject project)
    {
        var view = ProgramJson.Clone(project);
        view.Layout.Walls.Clear(); view.Layout.Hull.Clear(); view.Layout.Windows.Clear();
        if (!view.Layout.PublicEdited) view.Layout.PublicCorridors.Clear();
        if (!view.Layout.MaintenanceEdited) view.Layout.Maintenance.Clear();
        view.Layout.Equipment.RemoveAll(entity => !entity.Pinned);
        return view;
    }

    private static void ReserveClusterApproaches(StationDesignProject project, RuntimeCluster cluster, Dictionary<string, RoomDefinition> definitions,
        HashSet<Cell>? publicCenters = null, HashSet<Cell>? maintenanceCenters = null, string? networkOnly = null)
    {
        foreach (var plan in cluster.Plans)
        foreach (var network in new[] { "publicHall", "maintenance" })
        {
            if (networkOnly != null && network != networkOnly) continue;
            if (network == "publicHall" && plan.OwnerId == RequirementCompiler.MaintenanceOwner) continue;
            var eligible = plan.Slots.Select(slot => slot.Room).Where(room => network == "publicHall" ? definitions[room.DefinitionPath].Access.AllowPublicEntry : definitions[room.DefinitionPath].Access.AllowMaintenanceEntry).ToList();
            if (eligible.Count == 0) continue;
            if (project.Layout.CirculationReservations.Any(reservation => reservation.Network == network && eligible.Any(room => room.Id == reservation.RoomId))) continue;
            var chosen = ClusterApproachCandidates(project, cluster, plan, network, definitions)
                .FirstOrDefault(reservation => network == "publicHall" ? publicCenters == null || publicCenters.Contains(DesignCirculationPlanning.Anchor(reservation)) :
                    maintenanceCenters == null || maintenanceCenters.Contains(DesignCirculationPlanning.Anchor(reservation)));
            if (chosen != null) project.Layout.CirculationReservations.Add(chosen);
            else cluster.Protected = true;
        }
    }

    // The same local geometry rules serve initial packing and transactional artery retries.
    // Returning candidates does not move a room, change its permissions, or publish a route.
    private static IEnumerable<DesignCirculationReservation> ClusterApproachCandidates(StationDesignProject project, RuntimeCluster cluster,
        OwnerPlan plan, string network, Dictionary<string, RoomDefinition> definitions)
    {
        var all = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned).SelectMany(room => room.GrowthReservation.Concat(room.ComplexReservation).Concat(room.Cells)).ToHashSet();
        var structural = Halo(all); var bounds = cluster.Bounds();
        var dockSpace = Halo(DesignDockGeometry.ReservedCells(project));
        dockSpace.UnionWith(Halo(project.Layout.ReservedSpace));
        var canvas = new GridBounds(0, 0, project.Settings.Width, project.Settings.Height);
        var width = network == "publicHall" ? project.Settings.PublicWidth : project.Settings.MaintenanceWidth;
        var competing = Halo(project.Layout.CirculationReservations.Where(reservation => reservation.Network != network).SelectMany(reservation => reservation.BoundaryCells.Concat(reservation.ApproachCells)));
        if (network == "maintenance") competing.UnionWith(BackboneReservation(project));
        var choices = new List<(DesignCirculationReservation Reservation, double Score)>();
        foreach (var room in plan.Slots.Select(slot => slot.Room).Where(room => network == "publicHall" ? definitions[room.DefinitionPath].Access.AllowPublicEntry : definitions[room.DefinitionPath].Access.AllowMaintenanceEntry))
        {
            var floor = room.FloorLocked || room.FloorEdited ? room.Cells.ToHashSet() : room.GrowthReservation.ToHashSet();
            foreach (var normal in Cell.Directions)
            {
                var tangent = new Cell(-normal.Y, normal.X);
                foreach (var edge in floor)
                {
                    var door = Enumerable.Range(0, width).Select(index => edge + new Cell(tangent.X * index, tangent.Y * index) + normal).ToList();
                    if (!door.All(cell => floor.Contains(cell - normal) && !all.Contains(cell))) continue;
                    var distance = normal.X > 0 ? bounds.Right - edge.X : normal.X < 0 ? edge.X - bounds.X + 1 : normal.Y > 0 ? bounds.Bottom - edge.Y : edge.Y - bounds.Y + 1;
                    var length = Math.Max(width, distance + 1);
                    var throat = Enumerable.Range(1, length).SelectMany(depth => door.Select(cell => cell + new Cell(normal.X * depth, normal.Y * depth))).ToHashSet();
                    if (throat.Any(cell => !canvas.Contains(cell) || structural.Contains(cell) || dockSpace.Contains(cell) || competing.Contains(cell)) || door.Any(dockSpace.Contains)) continue;
                    var reservation = new DesignCirculationReservation { Id = "approach:" + plan.Id + ":" + network, RoomId = room.Id, Network = network, BoundaryCells = door, ApproachCells = LayoutGeometry.Sorted(throat), Outward = normal, Width = width };
                    var anchor = DesignCirculationPlanning.Anchor(reservation);
                    var facing = project.Layout.PublicWaypoints.Count == 0 ? (normal == Outward(room.GrowthSide) ? -4 : 0) :
                        network == "publicHall" ? project.Layout.PublicWaypoints.Min(anchor.Distance) * .75 :
                        -anchor.Distance(LayoutGeometry.Bounds(project.Layout.PublicWaypoints).Center) * .3;
                    var score = length * 2 + (definitions[room.DefinitionPath].Role == "filler" ? -10 : 0) + facing + UnitNoise(project, reservation.Id + edge) * .25;
                    choices.Add((reservation, score));
                }
            }
        }
        return choices.OrderBy(choice => choice.Score).ThenBy(choice => choice.Reservation.BoundaryCells[0].Y).ThenBy(choice => choice.Reservation.BoundaryCells[0].X).Select(choice => choice.Reservation);
    }

    private static IEnumerable<DesignCirculationReservation> AlternativeApproaches(StationDesignProject project, List<OwnerPlan> plans,
        List<Pair> pairs, Dictionary<string, RoomDefinition> definitions, DesignCirculationReservation guide)
    {
        if (guide.Edited || guide.Locked) return [];
        var plan = plans.FirstOrDefault(plan => guide.Id == "approach:" + plan.Id + ":" + guide.Network);
        if (plan == null && guide.Network == "maintenance")
        {
            var special = project.Layout.Rooms.FirstOrDefault(room => room.Id == guide.RoomId && guide.Id == "approach:special:" + room.Id + ":maintenance");
            if (special != null)
            {
                plan = new OwnerPlan(special.OwnerId) { Id = "special:" + special.Id };
                plan.Slots.Add(new(special, special.Parcel, special.GrowthSide));
            }
        }
        if (plan == null) return [];
        var parts = new List<OwnerPlan> { plan };
        foreach (var pair in pairs)
        {
            var left = plans.FirstOrDefault(candidate => candidate.OwnerId == pair.Left && candidate.Slots.Any(slot => slot.Room.DefinitionPath == pair.LeftRoom));
            var right = plans.FirstOrDefault(candidate => candidate.OwnerId == pair.Right && candidate.Slots.Any(slot => slot.Room.DefinitionPath == pair.RightRoom));
            if (left == plan && right != null) parts.Add(right);
            if (right == plan && left != null) parts.Add(left);
        }
        var rooms = parts.SelectMany(part => part.Slots).SelectMany(slot => RoomHierarchy.Descendants(project, slot.Room, true)).DistinctBy(room => room.Id).ToList();
        var cluster = new RuntimeCluster { Plans = parts, Rooms = rooms, Ids = rooms.Select(room => room.Id).ToHashSet(StringComparer.Ordinal) };
        var candidates = ClusterApproachCandidates(project, cluster, plan, guide.Network, definitions)
            .Where(candidate => candidate.Outward != guide.Outward || !candidate.BoundaryCells.SequenceEqual(guide.BoundaryCells)).ToList();
        // Spend a bounded solver's first trials on distinct room faces before trying
        // several nearly identical doorway positions along the same facade.
        return candidates.DistinctBy(candidate => (candidate.RoomId, candidate.Outward)).Concat(candidates)
            .DistinctBy(candidate => (candidate.RoomId, candidate.Outward, candidate.BoundaryCells[0])).Take(32).ToList();
    }

    private static HashSet<string> ExpectedApproachIds(StationDesignProject project, List<OwnerPlan> plans, Dictionary<string, RoomDefinition> definitions)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in plans)
        foreach (var network in new[] { "publicHall", "maintenance" })
        {
            if (network == "publicHall" && plan.OwnerId == RequirementCompiler.MaintenanceOwner) continue;
            var eligible = plan.Slots.Select(slot => slot.Room).Where(room => network == "publicHall" ? definitions[room.DefinitionPath].Access.AllowPublicEntry : definitions[room.DefinitionPath].Access.AllowMaintenanceEntry).Select(room => room.Id).ToHashSet(StringComparer.Ordinal);
            if (eligible.Count == 0) continue;
            var retained = project.Layout.CirculationReservations.FirstOrDefault(reservation => (reservation.Edited || reservation.Locked) && reservation.Network == network && eligible.Contains(reservation.RoomId));
            expected.Add(retained?.Id ?? "approach:" + plan.Id + ":" + network);
        }
        return expected;
    }

    private static HashSet<string> ReserveSpecialApproaches(StationDesignProject project, Dictionary<string, RoomDefinition> definitions)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal);
        var legal = DesignNetworkGeometry.LegalCenters(project, "maintenance", planning: true, additionalBlocked: BackboneReservation(project));
        var common = MaintenanceCandidateCenters(project, legal, project.Layout.CirculationReservations.Where(reservation => reservation.Network == "maintenance" && reservation.RoomId.Length > 0).ToList());
        foreach (var room in project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && string.IsNullOrEmpty(room.ParentInstanceId) &&
            (room.OwnerId == RequirementCompiler.MaintenanceOwner || definitions[room.DefinitionPath].Environment.Placement == "exterior")))
        {
            var plan = new OwnerPlan(room.OwnerId) { Id = "special:" + room.Id };
            plan.Slots.Add(new(room, room.Parcel, room.GrowthSide));
            expected.UnionWith(ExpectedApproachIds(project, [plan], definitions));
            if (room.GrowthReservation.Count > 0) ReserveClusterApproaches(project, new RuntimeCluster { Plans = [plan], Rooms = [room], Ids = new(StringComparer.Ordinal) { room.Id } }, definitions, maintenanceCenters: common);
        }
        return expected;
    }

    private static void ClearGeneratedSpecialGeometry(StationDesignProject project, Dictionary<string, RoomDefinition> definitions)
    {
        var maintenanceOwnerFixed = project.Layout.Departments.Any(owner => owner.OwnerId == RequirementCompiler.MaintenanceOwner && (owner.Pinned || owner.Locked));
        foreach (var room in project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && string.IsNullOrEmpty(room.ParentInstanceId) &&
            (room.OwnerId == RequirementCompiler.MaintenanceOwner || definitions[room.DefinitionPath].Environment.Placement == "exterior")))
        {
            if (room.Pinned || room.FloorLocked || room.FloorEdited || HasFixedPhysicalDependency(project, room) || room.OwnerId == RequirementCompiler.MaintenanceOwner && maintenanceOwnerFixed) continue;
            room.Cells.Clear(); room.GrowthReservation.Clear(); room.ComplexReservation.Clear();
            room.FrontageFloor.Clear(); room.FrontageSpace.Clear();
        }
    }

    private static bool TryReserveSpecialEntry(StationDesignProject project, DesignRoom room, Dictionary<string, RoomDefinition> definitions)
    {
        var frontageErrors = RoomGrowth.ReservePlanningFrontages(project);
        if (frontageErrors.Any(issue => issue.Path == room.Id && issue.Severity == "error"))
        {
            room.FrontageFloor.Clear(); room.FrontageSpace.Clear();
            project.Layout.ReservedSpace = LayoutGeometry.Sorted(project.Layout.Rooms.SelectMany(candidate => candidate.FrontageSpace));
            return false;
        }
        var legal = DesignNetworkGeometry.LegalCenters(project, "maintenance", planning: true, additionalBlocked: BackboneReservation(project));
        var previous = project.Layout.CirculationReservations.Where(reservation => reservation.Network == "maintenance" && reservation.RoomId.Length > 0).ToList();
        var reached = MaintenanceCandidateCenters(project, legal, previous);
        if (previous.Any(reservation => !reached.Contains(DesignCirculationPlanning.Anchor(reservation)))) return false;
        var plan = new OwnerPlan(room.OwnerId) { Id = "special:" + room.Id }; plan.Slots.Add(new(room, room.Parcel, room.GrowthSide));
        var cluster = new RuntimeCluster { Plans = [plan], Rooms = [room], Ids = new(StringComparer.Ordinal) { room.Id } };
        var oldIds = project.Layout.CirculationReservations.Select(reservation => reservation.Id).ToHashSet(StringComparer.Ordinal);
        ReserveClusterApproaches(project, cluster, definitions, maintenanceCenters: reached);
        var needsService = definitions[room.DefinitionPath].Access.AllowMaintenanceEntry;
        var hasService = project.Layout.CirculationReservations.Any(reservation => reservation.RoomId == room.Id && reservation.Network == "maintenance");
        var publicLegal = DesignNetworkGeometry.LegalCenters(project, "publicHall", planning: true);
        var publicGuides = project.Layout.CirculationReservations.Where(reservation => reservation.Network == "publicHall" && reservation.RoomId.Length > 0).ToList();
        var seed = project.Layout.PublicWaypoints.Count > 0 ? project.Layout.PublicWaypoints[0] : publicGuides.Count > 0 ? DesignCirculationPlanning.Anchor(publicGuides[0]) : new Cell();
        var publicReached = FloodCenters(publicLegal, seed);
        var preservesPublic = publicGuides.All(reservation => publicReached.Contains(DesignCirculationPlanning.Anchor(reservation)));
        if ((!needsService || hasService) && preservesPublic) return true;
        project.Layout.CirculationReservations.RemoveAll(reservation => !oldIds.Contains(reservation.Id) && !reservation.Edited && !reservation.Locked);
        room.FrontageFloor.Clear(); room.FrontageSpace.Clear();
        project.Layout.ReservedSpace = LayoutGeometry.Sorted(project.Layout.Rooms.SelectMany(candidate => candidate.FrontageSpace));
        return false;
    }

    private static HashSet<Cell> BackboneReservation(StationDesignProject project)
    {
        if (project.Layout.PublicWaypoints.Count == 0) return [];
        var bounds = LayoutGeometry.Bounds(project.Layout.PublicWaypoints).Expand(project.Settings.PublicWidth / 2 + 2);
        return bounds.Cells().ToHashSet();
    }

    // Candidate components are not a claim of connectivity. In controlled mode the
    // circulation planner must prove the explicit transitions for the resulting forest.
    private static HashSet<Cell> MaintenanceCandidateCenters(StationDesignProject project, HashSet<Cell> legal,
        List<DesignCirculationReservation> existing)
    {
        if (project.Definition.Maintenance.Connectivity.Policy == "continuous")
            return existing.Count == 0 ? LargestCenterComponent(legal) : FloodCenters(legal, DesignCirculationPlanning.Anchor(existing[0]));
        if (existing.Count == 0) return legal;
        var candidates = new HashSet<Cell>();
        foreach (var guide in existing)
        {
            var anchor = DesignCirculationPlanning.Anchor(guide);
            if (!candidates.Contains(anchor)) candidates.UnionWith(FloodCenters(legal, anchor));
        }
        return candidates;
    }

    private static double PackingScore(StationDesignProject project, List<RuntimeCluster> clusters, Cell center, Dictionary<string, int> ownerAreas, RuntimeCluster? moving = null, Cell delta = default)
    {
        GridBounds Shift(GridBounds bounds, RuntimeCluster cluster) => cluster == moving ? new(bounds.X + delta.X, bounds.Y + delta.Y, bounds.Width, bounds.Height) : bounds;
        var boxes = clusters.Select(cluster => Shift(cluster.Bounds(), cluster)).ToList();
        if (project.Layout.PublicWaypoints.Count > 0)
        {
            var guide = LayoutGeometry.Bounds(project.Layout.PublicWaypoints).Expand(project.Settings.PublicWidth / 2 + 2);
            if (project.Settings.Topology == "spine")
            {
                var minX = boxes.Min(box => box.X); var maxX = boxes.Max(box => box.Right);
                guide = new(minX, guide.Y, maxX - minX, guide.Height);
            }
            boxes.Add(guide);
        }
        var left = boxes.Min(box => box.X); var top = boxes.Min(box => box.Y);
        var width = boxes.Max(box => box.Right) - left; var height = boxes.Max(box => box.Bottom) - top;
        var extent = width * height;
        var score = extent * (1 + Math.Abs(Math.Log((double)width / height / project.Settings.Packing.PreferredAspectRatio)) * 1.1) + boxes.Sum(box => Math.Pow(box.Center.Distance(center), 2)) * .025;
        foreach (var owner in project.Definition.Departments)
        {
            var parts = clusters.Where(cluster => cluster.OwnerBounds().ContainsKey(owner.Id)).Select(cluster => Shift(cluster.OwnerBounds()[owner.Id], cluster)).ToList(); if (parts.Count == 0) continue;
            var x = parts.Min(part => part.X); var y = parts.Min(part => part.Y);
            var box = new GridBounds(x, y, parts.Max(part => part.Right) - x, parts.Max(part => part.Bottom) - y);
            var aspect = Math.Max((double)box.Width / box.Height, (double)box.Height / box.Width);
            var area = ownerAreas.GetValueOrDefault(owner.Id);
            var fill = (double)area / box.Area;
            score += (Math.Pow(Math.Max(0, aspect / owner.Shape.MaxAspectRatio - 1), 2) + Math.Pow(Math.Max(0, 1 - fill / owner.Shape.MinFillRatio), 2)) * 1e8;
            // Distributed suites may share the station without losing their owner's
            // preferred proximity. Keep that pressure active during exact-mask
            // compaction, rather than only during the initial ordering.
            if (parts.Count > 1 && owner.Rooms.Any(room => room.Placement.Policy == "distributed"))
                score += project.Settings.Packing.DepartmentBundling * parts.Sum(part => part.Center.Distance(box.Center)) * 40;
        }
        return score;
    }

    private static List<DesignDock>? TranslatedDocks(StationDesignProject project, RuntimeCluster cluster, Cell delta)
    {
        var result = new List<DesignDock>();
        foreach (var dock in project.Layout.Docks.Where(dock => cluster.Ids.Contains(dock.RoomId)))
        {
            var rule = DesignDockGeometry.Requirement(project, dock);
            if (rule == null || dock.BoundaryCells.Count != 1 || !DesignDockGeometry.TryCreate(dock.RoomId, rule, dock.Instance, dock.BoundaryCells[0] + delta, dock.Outward, new(0, 0, project.Settings.Width, project.Settings.Height), out var moved)) return null;
            result.Add(moved!);
        }
        return result;
    }

    private static void MoveCluster(StationDesignProject project, RuntimeCluster cluster, Cell delta, bool moveApproaches = true)
    {
        var docks = TranslatedDocks(project, cluster, delta);
        foreach (var room in cluster.Rooms)
        {
            room.Cells = LayoutGeometry.Sorted(room.Cells.Select(cell => cell + delta));
            room.GrowthReservation = LayoutGeometry.Sorted(room.GrowthReservation.Select(cell => cell + delta));
            room.ComplexReservation = LayoutGeometry.Sorted(room.ComplexReservation.Select(cell => cell + delta));
            room.FrontageFloor = LayoutGeometry.Sorted(room.FrontageFloor.Select(cell => cell + delta));
            room.FrontageSpace = LayoutGeometry.Sorted(room.FrontageSpace.Select(cell => cell + delta));
            room.Parcel = new(room.Parcel.X + delta.X, room.Parcel.Y + delta.Y, room.Parcel.Width, room.Parcel.Height);
            if (room.HasSeed) room.Seed += delta;
            room.Bounds = LayoutGeometry.Bounds(room.Cells);
        }
        foreach (var plan in cluster.Plans) plan.Origin += delta;
        cluster.Invalidate();
        project.Layout.ReservedSpace = LayoutGeometry.Sorted(project.Layout.Rooms.SelectMany(room => room.FrontageSpace));
        foreach (var reservation in project.Layout.CirculationReservations.Where(reservation => moveApproaches && cluster.Ids.Contains(reservation.RoomId)))
        { reservation.BoundaryCells = LayoutGeometry.Sorted(reservation.BoundaryCells.Select(cell => cell + delta)); reservation.ApproachCells = LayoutGeometry.Sorted(reservation.ApproachCells.Select(cell => cell + delta)); }
        if (docks != null)
        {
            project.Layout.Docks.RemoveAll(dock => cluster.Ids.Contains(dock.RoomId)); project.Layout.Docks.AddRange(docks);
        }
    }

    private static void RefreshGeneratedDocks(StationDesignProject project)
    {
        for (var index = 0; index < project.Layout.Docks.Count; index++)
        {
            var dock = project.Layout.Docks[index]; if (dock.Edited || dock.Locked) continue;
            try { project.Layout.Docks[index] = DesignDockGeometry.Expected(project, dock); } catch (ArgumentException) { }
        }
    }

    private static HashSet<Cell> DockSpaceForCanvas(StationDesignProject project, GridBounds canvas)
    {
        var result = DesignDockGeometry.ReservedCells(project);
        foreach (var dock in project.Layout.Docks)
        {
            var rule = DesignDockGeometry.Requirement(project, dock);
            if (rule != null && dock.BoundaryCells.Count == 1 && DesignDockGeometry.TryCreate(dock.RoomId, rule, dock.Instance, dock.BoundaryCells[0], dock.Outward, canvas, out var expected)) result.UnionWith(DesignDockGeometry.ReservedCells(expected!));
        }
        return result;
    }

    private static bool CanNormalize(StationDesignProject project, List<RuntimeCluster> clusters) =>
        project.Settings.AutoSize && !project.Layout.EnvelopeEdited && !clusters.Any(cluster => cluster.Protected) && !project.Layout.PublicEdited && !project.Layout.MaintenanceEdited &&
        !project.Layout.Rooms.Any(room => room.Pinned || room.FloorLocked || room.FloorEdited) && !project.Layout.Departments.Any(owner => owner.Pinned || owner.Locked) &&
        !project.Layout.Interfaces.Any(port => port.Edited) && !project.Layout.Routes.Any(route => route.Edited || route.Locked) &&
        !project.Layout.CirculationReservations.Any(port => port.Edited || port.Locked) && !project.Layout.Docks.Any(dock => dock.Edited || dock.Locked) &&
        !project.Layout.Equipment.Any(entity => entity.Pinned) && !project.Layout.Utilities.Any(utility => utility.Edited) && !project.Layout.TransportNetworks.Any(network => network.Edited || network.Locked) &&
        !project.Layout.SystemDevices.Any(device => device.Edited || device.Locked) && !project.Layout.SystemControls.Any(control => control.Edited || control.Locked) &&
        !project.Layout.SystemNetworks.Any(network => network.Edited || network.Locked || network.Segments.Any(segment => segment.Edited || segment.Locked));
}
