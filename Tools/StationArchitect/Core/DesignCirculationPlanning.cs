namespace Content.StationArchitect;

public sealed class DesignCirculationPlanResult
{
    public bool Complete { get; set; }
    public List<ProgramDiagnostic> Diagnostics { get; set; } = [];
    public List<DesignCirculationReservation> Paths { get; set; } = [];
    public string FailedGuideId { get; set; } = "";
    /// <summary>Nearby allocation overlaps with a direct planning corridor, ranked for bounded relocation trials; not proof of an unavoidable cut.</summary>
    public List<string> ConflictRoomIds { get; set; } = [];
}

/// <summary>Planning-only connected arteries for both passage networks. No built floor is implied.</summary>
public static partial class DesignCirculationPlanning
{
    public static DesignCirculationPlanResult Connect(StationDesignProject project, IEnumerable<string>? expectedGuideIds = null,
        Func<DesignCirculationReservation, IEnumerable<DesignCirculationReservation>>? alternatives = null)
    {
        var result = new DesignCirculationPlanResult();
        void Warn(string path, string code, string message) => result.Diagnostics.Add(new(path, code, message, "warning"));
        if (expectedGuideIds != null)
        {
            var actualIds = project.Layout.CirculationReservations.Select(guide => guide.Id).ToHashSet(StringComparer.Ordinal);
            var missing = expectedGuideIds.Distinct(StringComparer.Ordinal).Where(id => !actualIds.Contains(id)).Order(StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            { Warn("layout", "planning-entry-missing", "Expected local circulation guides could not be placed: " + string.Join(", ", missing) + ". They were not counted as a completed plan."); return result; }
        }
        if (project.Layout.PublicEdited || project.Layout.MaintenanceEdited || project.Layout.Routes.Any(route => route.Edited || route.Locked) || project.Layout.CirculationReservations.Any(reservation => reservation.Edited || reservation.Locked))
        { Warn("layout", "planning-protected-circulation", "Protected passage geometry was preserved; connected automatic planning arteries were not substituted for it."); return result; }
        if (project.Definition.Maintenance.Connectivity.Policy == "controlled") return ConnectControlled(project, alternatives);
        var working = ProgramJson.Clone(project);
        var rooms = working.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned).ToDictionary(room => room.Id, StringComparer.Ordinal);
        var publicGuides = working.Layout.CirculationReservations.Where(guide => guide.Network == "publicHall" && rooms.ContainsKey(guide.RoomId)).OrderBy(guide => guide.Id, StringComparer.Ordinal).ToList();
        var serviceGuides = working.Layout.CirculationReservations.Where(guide => guide.Network == "maintenance" && rooms.ContainsKey(guide.RoomId)).OrderBy(guide => guide.Id, StringComparer.Ordinal).ToList();
        var publicWidth = project.Settings.PublicWidth; var serviceWidth = project.Settings.MaintenanceWidth;
        var publicLegal = DesignNetworkGeometry.LegalCenters(working, "publicHall", planning: true, oppositeFloorOverride: Array.Empty<Cell>());
        var publicPossibleFloor = publicLegal.SelectMany(cell => DesignRouting.Stamp(cell, publicWidth)).ToHashSet();
        var canvas = new GridBounds(0, 0, project.Settings.Width, project.Settings.Height);
        var anchors = new Dictionary<string, Cell>(StringComparer.Ordinal);
        foreach (var guide in publicGuides.Concat(serviceGuides))
        {
            var expectedWidth = guide.Network == "publicHall" ? publicWidth : serviceWidth;
            var ownFloor = !rooms[guide.RoomId].FloorLocked && !rooms[guide.RoomId].FloorEdited && rooms[guide.RoomId].GrowthReservation.Count > 0 ? rooms[guide.RoomId].GrowthReservation.ToHashSet() : rooms[guide.RoomId].Cells.ToHashSet();
            if (guide.Width != expectedWidth || guide.BoundaryCells.Count != expectedWidth || guide.BoundaryCells.Distinct().Count() != expectedWidth || DesignDockGeometry.Direction(guide.Outward).Length == 0 || !guide.BoundaryCells.All(cell => ownFloor.Contains(cell - guide.Outward)))
            { Warn(guide.RoomId, "planning-entry-geometry", "A local circulation guide no longer has its full-width owned boundary strip. Rebuild its planning allocation before connecting arteries."); return result; }
            anchors[guide.Id] = Anchor(guide);
            if (guide.Network == "publicHall" && !guide.ApproachCells.All(publicPossibleFloor.Contains))
            { Warn(guide.RoomId, "planning-entry-geometry", "The complete local public approach intersects another physical allocation or protected clearance."); return result; }
        }
        var backbone = new List<Cell>(); var publicFloor = new HashSet<Cell>();
        var waypoints = new List<Cell>();
        foreach (var waypoint in project.Layout.PublicWaypoints)
        {
            var legal = publicLegal.Where(cell => cell.Distance(waypoint) <= 24).OrderBy(cell => cell.Distance(waypoint)).ThenBy(cell => cell.Y).ThenBy(cell => cell.X).Cast<Cell?>().FirstOrDefault();
            if (legal == null) { Warn("layout.public", "planning-backbone", "No full-width planning backbone fits near a topology waypoint."); return result; }
            waypoints.Add(legal.Value);
        }
        if (waypoints.Count == 0 && publicGuides.Count > 0) waypoints.Add(anchors[publicGuides[0].Id]);
        if (waypoints.Count > 0)
        {
            if (!publicLegal.Contains(waypoints[0])) { Warn("layout.public", "planning-backbone", "The public planning seed is not clear at the configured width."); return result; }
            backbone.Add(waypoints[0]);
            var edges = Enumerable.Range(1, waypoints.Count - 1).Select(index => (From: waypoints[index - 1], To: waypoints[index])).ToList();
            if (project.Settings.Topology == "ring" && waypoints.Count >= 3) edges.Add((waypoints[^1], waypoints[0]));
            foreach (var edge in edges)
            {
                var previous = backbone.ToHashSet();
                bool Allowed(Cell cell) => publicLegal.Contains(cell) && (cell == edge.From || cell == edge.To || !previous.Contains(cell));
                var path = DesignRouting.Search(edge.From, cell => cell == edge.To, Allowed, canvas, [edge.To], 100000);
                if (path == null) { Warn("layout.public", "planning-backbone", "A simple full-width public planning backbone cannot pass the reserved station geometry."); return result; }
                backbone.AddRange(path.Skip(1));
            }
            if (project.Settings.Topology == "ring" && waypoints.Count >= 3 && (backbone[0] != backbone[^1] || backbone.Take(backbone.Count - 1).Distinct().Count() != backbone.Count - 1))
            { Warn("layout.public", "planning-backbone", "The planning backbone would backtrack instead of closing a simple ring."); return result; }
            publicFloor.UnionWith(backbone.SelectMany(cell => DesignRouting.Stamp(cell, publicWidth)));
        }
        var serviceLegal = DesignNetworkGeometry.LegalCenters(working, "maintenance", planning: true, oppositeFloorOverride: publicFloor);
        var commonService = CommonComponent(serviceLegal, serviceGuides, anchors);
        if (commonService.Count != serviceGuides.Count)
        {
            var missing = serviceGuides.Where(guide => !commonService.Contains(guide.Id)).Select(guide => guide.Id);
            Warn("layout.maintenance", "planning-service-connectivity", "These service guides are not in one full-width planning component before public arteries: " + string.Join(", ", missing) + ". Preserve or widen the connecting gap.");
            return result;
        }
        var mainService = serviceGuides.Count == 0 ? (Cell?) null : anchors[serviceGuides[0].Id];
        var backboneFloor = publicFloor.ToList();
        foreach (var guide in publicGuides.OrderBy(guide => publicFloor.Count == 0 ? 0 : anchors[guide.Id].Distance(DesignMeasurements.Bounds(publicFloor).Center)).ThenBy(guide => guide.Id, StringComparer.Ordinal))
        {
            List<Cell>? accepted = null; DesignCirculationReservation? selected = null;
            var candidates = new[] { guide }.Concat(alternatives?.Invoke(guide) ?? [])
                .DistinctBy(candidate => candidate.RoomId + ":" + candidate.Outward + ":" + string.Join(";", LayoutGeometry.Sorted(candidate.BoundaryCells))).Take(24);
            foreach (var candidate in candidates)
            {
                if (candidate.Id != guide.Id || candidate.Network != "publicHall" || candidate.Width != publicWidth || !rooms.TryGetValue(candidate.RoomId, out var candidateRoom)) continue;
                var candidateFloor = !candidateRoom.FloorLocked && !candidateRoom.FloorEdited && candidateRoom.GrowthReservation.Count > 0 ? candidateRoom.GrowthReservation.ToHashSet() : candidateRoom.Cells.ToHashSet();
                if (candidate.BoundaryCells.Count != publicWidth || candidate.BoundaryCells.Distinct().Count() != publicWidth || DesignDockGeometry.Direction(candidate.Outward).Length == 0 ||
                    !candidate.BoundaryCells.All(cell => candidateFloor.Contains(cell - candidate.Outward)) || !candidate.ApproachCells.All(publicPossibleFloor.Contains)) continue;
                var anchor = Anchor(candidate);
                if (!publicLegal.Contains(anchor)) continue;
                var guideIndex = working.Layout.CirculationReservations.FindIndex(existing => existing.Id == guide.Id);
                working.Layout.CirculationReservations[guideIndex] = candidate;
                var candidateServiceLegal = DesignNetworkGeometry.LegalCenters(working, "maintenance", planning: true, oppositeFloorOverride: publicFloor);
                var beforeConnected = mainService == null ? [] : DesignNetworkGeometry.ConnectedCenters(candidateServiceLegal, [mainService.Value]);
                if (serviceGuides.Any(service => !beforeConnected.Contains(anchors[service.Id]))) continue;
                var serviceEscapes = new HashSet<Cell>();
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    var legal = DesignNetworkGeometry.WithoutOppositeFloor(publicLegal, serviceEscapes, publicWidth);
                    var path = publicFloor.Count == 0 ? new List<Cell> { anchor } : DesignNetworkGeometry.PathToFloor(working, anchor, publicFloor, publicWidth, legal);
                    if (path == null) break;
                    var proposal = publicFloor.Concat(path.SelectMany(cell => DesignRouting.Stamp(cell, publicWidth))).Concat(candidate.ApproachCells).ToHashSet();
                    var nextServiceLegal = DesignNetworkGeometry.LegalCenters(working, "maintenance", planning: true, oppositeFloorOverride: proposal);
                    var connected = mainService == null ? [] : DesignNetworkGeometry.ConnectedCenters(nextServiceLegal, [mainService.Value]);
                    var lost = serviceGuides.Where(service => commonService.Contains(service.Id) && !connected.Contains(anchors[service.Id])).ToList();
                    if (lost.Count == 0) { accepted = path; selected = candidate; publicFloor = proposal; serviceLegal = nextServiceLegal; break; }
                    if (mainService == null) break;
                    var escape = DesignRouting.Search(anchors[lost[0].Id], cell => cell == mainService.Value, candidateServiceLegal.Contains, canvas, [mainService.Value], 100000);
                    if (escape == null) break;
                    serviceEscapes.UnionWith(escape.SelectMany(cell => DesignRouting.Stamp(cell, serviceWidth)));
                }
                if (selected != null) break;
            }
            if (accepted == null || selected == null)
            {
                result.FailedGuideId = guide.Id;
                result.ConflictRoomIds = NearbyBlockers(rooms, guide.RoomId, backboneFloor, publicWidth);
                Warn(guide.RoomId, "planning-dual-network", "No tried full-width public entry/artery preserves every previously connected service guide. Expand the critical gap or reposition its entry; no partial artery was published.");
                return result;
            }
            result.Paths.Add(new()
            {
                Id = selected.Id, RoomId = selected.RoomId, Network = selected.Network, Width = selected.Width, Outward = selected.Outward,
                BoundaryCells = [.. selected.BoundaryCells], ApproachCells = LayoutGeometry.Sorted(selected.ApproachCells.Concat(accepted.SelectMany(cell => DesignRouting.Stamp(cell, publicWidth))))
            });
        }
        // Keep the surviving service capacity physically available through later room growth and
        // window-pocket selection. These paths are reservations, not fabricated built passages.
        var serviceFloor = new HashSet<Cell>();
        var servicePossibleFloor = serviceLegal.SelectMany(cell => DesignRouting.Stamp(cell, serviceWidth)).ToHashSet();
        foreach (var guide in serviceGuides.OrderBy(guide => mainService == null ? 0 : anchors[guide.Id].Distance(mainService.Value)).ThenBy(guide => guide.Id, StringComparer.Ordinal))
        {
            if (!guide.ApproachCells.All(servicePossibleFloor.Contains))
            { Warn(guide.RoomId, "planning-entry-geometry", "The complete local service approach intersects another physical allocation or protected clearance; neither network's partial planning paths were published."); return result; }
            var anchor = anchors[guide.Id];
            var path = serviceFloor.Count == 0 ? new List<Cell> { anchor } : DesignNetworkGeometry.PathToFloor(project, anchor, serviceFloor, serviceWidth, serviceLegal);
            if (!serviceLegal.Contains(anchor) || path == null)
            { Warn(guide.RoomId, "planning-service-connectivity", "The final public plan leaves no full-width service artery to this guide; neither network's partial planning paths were published."); return result; }
            var approach = guide.ApproachCells.Concat(path.SelectMany(cell => DesignRouting.Stamp(cell, serviceWidth))).ToHashSet();
            serviceFloor.UnionWith(approach);
            result.Paths.Add(new()
            {
                Id = guide.Id, RoomId = guide.RoomId, Network = guide.Network, Width = guide.Width, Outward = guide.Outward,
                BoundaryCells = [.. guide.BoundaryCells], ApproachCells = LayoutGeometry.Sorted(approach)
            });
        }
        if (backboneFloor.Count > 0) result.Paths.Add(new() { Id = "planning:public-backbone", RoomId = "", Network = "publicHall", Width = publicWidth, ApproachCells = LayoutGeometry.Sorted(backboneFloor) });
        foreach (var path in result.Paths)
        {
            var existing = project.Layout.CirculationReservations.FirstOrDefault(guide => guide.Id == path.Id);
            if (existing == null) project.Layout.CirculationReservations.Add(path);
            else
            {
                existing.RoomId = path.RoomId; existing.Network = path.Network; existing.Width = path.Width;
                existing.Outward = path.Outward; existing.BoundaryCells = [.. path.BoundaryCells]; existing.ApproachCells = [.. path.ApproachCells];
            }
        }
        result.Complete = true;
        return result;
    }

    public static Cell Anchor(DesignCirculationReservation guide) => DesignMeasurements.Bounds(guide.BoundaryCells.SelectMany(cell => Enumerable.Range(1, guide.Width).Select(depth => cell + new Cell(guide.Outward.X * depth, guide.Outward.Y * depth)))).Center;

    private static List<string> NearbyBlockers(Dictionary<string, DesignRoom> rooms, string sourceId, List<Cell> backbone, int width)
    {
        if (!rooms.TryGetValue(sourceId, out var source) || backbone.Count == 0) return [];
        static IEnumerable<Cell> Floor(DesignRoom room) => !room.FloorLocked && !room.FloorEdited && room.GrowthReservation.Count > 0 ? room.GrowthReservation : room.Cells;
        var sourceFloor = Floor(source).ToList();
        if (sourceFloor.Count == 0) return [];
        var start = LayoutGeometry.Bounds(sourceFloor).Center;
        var end = backbone.OrderBy(cell => cell.Distance(start)).ThenBy(cell => cell.Y).ThenBy(cell => cell.X).First();
        var centers = Enumerable.Range(Math.Min(start.X, end.X), Math.Abs(start.X - end.X) + 1).SelectMany(x => new[] { new Cell(x, start.Y), new Cell(x, end.Y) })
            .Concat(Enumerable.Range(Math.Min(start.Y, end.Y), Math.Abs(start.Y - end.Y) + 1).SelectMany(y => new[] { new Cell(start.X, y), new Cell(end.X, y) }));
        var corridor = centers.SelectMany(cell => DesignRouting.Stamp(cell, width)).ToHashSet();
        return rooms.Values.Where(room => room.Id != sourceId).Select(room => (room.Id, Count: DesignNetworkGeometry.Halo(Floor(room)).Count(corridor.Contains)))
            .Where(entry => entry.Count > 0).OrderByDescending(entry => entry.Count).ThenBy(entry => entry.Id, StringComparer.Ordinal).Select(entry => entry.Id).ToList();
    }

    private static HashSet<string> CommonComponent(HashSet<Cell> legal, List<DesignCirculationReservation> guides, Dictionary<string, Cell> anchors)
    {
        var best = new HashSet<string>(StringComparer.Ordinal); var seen = new HashSet<Cell>();
        foreach (var guide in guides)
        {
            var anchor = anchors[guide.Id];
            if (!legal.Contains(anchor) || seen.Contains(anchor)) continue;
            var connected = DesignNetworkGeometry.ConnectedCenters(legal, [anchor]); seen.UnionWith(connected);
            var members = guides.Where(candidate => connected.Contains(anchors[candidate.Id])).Select(candidate => candidate.Id).ToHashSet(StringComparer.Ordinal);
            if (members.Count > best.Count) best = members;
        }
        return best;
    }
}
