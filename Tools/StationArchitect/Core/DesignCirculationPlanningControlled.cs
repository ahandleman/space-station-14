namespace Content.StationArchitect;

public static partial class DesignCirculationPlanning
{
    private static DesignCirculationPlanResult ConnectControlled(StationDesignProject project,
        Func<DesignCirculationReservation, IEnumerable<DesignCirculationReservation>>? alternatives)
    {
        var result = new DesignCirculationPlanResult();
        DesignCirculationPlanResult Fail(string path, string code, string message)
        { result.Diagnostics.Add(new(path, code, message, "warning")); return result; }
        var working = ProgramJson.Clone(project); var policy = project.Definition.Maintenance.Connectivity;
        var definitions = RoomHierarchy.Templates(project.Definition).ToDictionary(item => item.Path, item => item.Room, StringComparer.Ordinal);
        var rooms = working.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned).ToDictionary(room => room.Id, StringComparer.Ordinal);
        var publicGuides = working.Layout.CirculationReservations.Where(guide => guide.Network == "publicHall" && rooms.ContainsKey(guide.RoomId)).OrderBy(guide => guide.Id, StringComparer.Ordinal).ToList();
        var serviceGuides = working.Layout.CirculationReservations.Where(guide => guide.Network == "maintenance" && rooms.ContainsKey(guide.RoomId)).OrderBy(guide => guide.Id, StringComparer.Ordinal).ToList();
        var pw = project.Settings.PublicWidth; var mw = project.Settings.MaintenanceWidth;
        IEnumerable<Cell> Floor(DesignRoom room) => !room.FloorLocked && !room.FloorEdited && room.GrowthReservation.Count > 0 ? room.GrowthReservation : room.Cells;
        bool GuideFits(DesignCirculationReservation guide, int width) => rooms.TryGetValue(guide.RoomId, out var room) && guide.Width == width && guide.BoundaryCells.Count == width &&
            guide.BoundaryCells.Distinct().Count() == width && DirectionValid(guide.Outward) && guide.BoundaryCells.All(cell => Floor(room).Contains(cell - guide.Outward));
        foreach (var guide in publicGuides.Concat(serviceGuides))
            if (!GuideFits(guide, guide.Network == "publicHall" ? pw : mw)) return Fail(guide.RoomId, "planning-entry-geometry", "A full-width local entrance is missing its real owned standing strip.");
        var publicLegal = DesignNetworkGeometry.LegalCenters(working, "publicHall", planning: true, oppositeFloorOverride: Array.Empty<Cell>());
        var publicPossible = publicLegal.SelectMany(cell => DesignRouting.Stamp(cell, pw)).ToHashSet();
        var canvas = new GridBounds(0, 0, project.Settings.Width, project.Settings.Height);
        var waypoints = new List<Cell>();
        foreach (var desired in project.Layout.PublicWaypoints)
        {
            var waypoint = publicLegal.Where(cell => cell.Distance(desired) <= 24).OrderBy(cell => cell.Distance(desired)).ThenBy(cell => cell.Y).ThenBy(cell => cell.X).Cast<Cell?>().FirstOrDefault();
            if (waypoint == null) return Fail("layout.public", "planning-backbone", "No full-width public backbone fits near a topology waypoint.");
            waypoints.Add(waypoint.Value);
        }
        if (waypoints.Count == 0 && publicGuides.Count > 0) waypoints.Add(Anchor(publicGuides[0]));
        var backbone = new List<Cell>();
        if (waypoints.Count > 0)
        {
            if (!publicLegal.Contains(waypoints[0])) return Fail("layout.public", "planning-backbone", "The public seed has no full-width clearance.");
            backbone.Add(waypoints[0]);
            var edges = Enumerable.Range(1, waypoints.Count - 1).Select(index => (From: waypoints[index - 1], To: waypoints[index])).ToList();
            if (project.Settings.Topology == "ring" && waypoints.Count >= 3) edges.Add((waypoints[^1], waypoints[0]));
            foreach (var edge in edges)
            {
                var used = backbone.ToHashSet();
                var path = DesignRouting.Search(edge.From, cell => cell == edge.To, cell => publicLegal.Contains(cell) && (cell == edge.From || cell == edge.To || !used.Contains(cell)), canvas, [edge.To], 100000);
                if (path == null) return Fail("layout.public", "planning-backbone", "The public backbone cannot avoid reserved room, dock or entrance geometry.");
                backbone.AddRange(path.Skip(1));
            }
        }
        var publicFloor = backbone.SelectMany(cell => DesignRouting.Stamp(cell, pw)).ToHashSet();
        foreach (var guide in publicGuides.OrderBy(guide => publicFloor.Count == 0 ? 0 : Anchor(guide).Distance(LayoutGeometry.Bounds(publicFloor).Center)).ThenBy(guide => guide.Id, StringComparer.Ordinal))
        {
            DesignCirculationReservation? chosen = null; List<Cell>? chosenPath = null;
            foreach (var candidate in new[] { guide }.Concat(alternatives?.Invoke(guide) ?? []).DistinctBy(candidate => candidate.RoomId + ":" + candidate.Outward + ":" + string.Join(";", LayoutGeometry.Sorted(candidate.BoundaryCells))).Take(24))
            {
                if (candidate.Id != guide.Id || candidate.Network != "publicHall" || !GuideFits(candidate, pw) || !candidate.ApproachCells.All(publicPossible.Contains)) continue;
                var anchor = Anchor(candidate);
                var path = publicFloor.Count == 0 ? publicLegal.Contains(anchor) ? new List<Cell> { anchor } : null : DesignNetworkGeometry.PathToFloor(working, anchor, publicFloor, pw, publicLegal);
                if (path == null) continue;
                chosen = candidate; chosenPath = path; break;
            }
            if (chosen == null || chosenPath == null)
            {
                result.FailedGuideId = guide.Id; result.ConflictRoomIds = NearbyBlockers(rooms, guide.RoomId, publicFloor.ToList(), pw);
                return Fail(guide.RoomId, "planning-dual-network", "No full-width public entrance reaches its backbone while preserving protected local maintenance approaches.");
            }
            var approach = LayoutGeometry.Sorted(chosen.ApproachCells.Concat(chosenPath.SelectMany(cell => DesignRouting.Stamp(cell, pw))));
            publicFloor.UnionWith(approach);
            result.Paths.Add(new() { Id = chosen.Id, RoomId = chosen.RoomId, Network = chosen.Network, Width = chosen.Width, Outward = chosen.Outward, BoundaryCells = [.. chosen.BoundaryCells], ApproachCells = approach });
            working.Layout.CirculationReservations[working.Layout.CirculationReservations.FindIndex(existing => existing.Id == guide.Id)] = chosen;
        }
        var serviceLegal = DesignNetworkGeometry.LegalCenters(working, "maintenance", planning: true, oppositeFloorOverride: publicFloor);
        var servicePossible = serviceLegal.SelectMany(cell => DesignRouting.Stamp(cell, mw)).ToHashSet();
        // Public arteries can divide free maintenance space. A different legal face on the
        // same cluster may join an existing component without moving or enlarging any room.
        var components = new List<HashSet<Cell>>(); var componentOf = new Dictionary<Cell, int>();
        int Component(Cell anchor)
        {
            if (!serviceLegal.Contains(anchor)) return -1;
            if (componentOf.TryGetValue(anchor, out var known)) return known;
            var cells = DesignNetworkGeometry.ConnectedCenters(serviceLegal, [anchor]); var id = components.Count;
            components.Add(cells); foreach (var cell in cells) componentOf[cell] = id; return id;
        }
        var poses = new Dictionary<string, List<(DesignCirculationReservation Guide, int Component)>>(StringComparer.Ordinal);
        foreach (var guide in serviceGuides)
        {
            poses[guide.Id] = new[] { guide }.Concat(alternatives?.Invoke(guide) ?? [])
                .DistinctBy(candidate => candidate.RoomId + ":" + candidate.Outward + ":" + string.Join(";", LayoutGeometry.Sorted(candidate.BoundaryCells))).Take(32)
                .Where(candidate => candidate.Id == guide.Id && candidate.Network == "maintenance" && GuideFits(candidate, mw) && candidate.ApproachCells.All(servicePossible.Contains))
                .Select(candidate => (Guide: candidate, Component: Component(Anchor(candidate)))).Where(candidate => candidate.Component >= 0).ToList();
            if (poses[guide.Id].Count == 0) return Fail(guide.RoomId, "planning-service-connectivity", "No tried local service doorway has a full-width clear approach after the public arteries.");
        }
        var allComponents = poses.Values.SelectMany(candidates => candidates.Select(candidate => candidate.Component)).Distinct().ToList();
        var cover = allComponents.ToDictionary(id => id, id => poses.Where(pair => pair.Value.Any(candidate => candidate.Component == id)).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal));
        var bestCover = poses.Values.Select(candidates => candidates[0].Component).ToHashSet();
        long Area(IEnumerable<int> chosen) => chosen.Sum(id => (long) components[id].Count);
        foreach (var forced in new[] { -1 }.Concat(allComponents.OrderByDescending(id => cover[id].Count).ThenByDescending(id => components[id].Count).ThenBy(id => id).Take(16)))
        {
            var chosen = new HashSet<int>(); var remaining = poses.Keys.ToHashSet(StringComparer.Ordinal);
            if (forced >= 0) { chosen.Add(forced); remaining.ExceptWith(cover[forced]); }
            while (remaining.Count > 0)
            {
                var next = allComponents.OrderByDescending(id => cover[id].Count(remaining.Contains)).ThenByDescending(id => components[id].Count).ThenBy(id => id).First();
                chosen.Add(next); remaining.ExceptWith(cover[next]);
            }
            // Remove redundant small components before publishing any doorway changes.
            foreach (var redundant in chosen.OrderBy(id => components[id].Count).ThenBy(id => id).ToList())
                if (poses.Keys.All(key => chosen.Where(id => id != redundant).Any(id => cover[id].Contains(key)))) chosen.Remove(redundant);
            if (chosen.Count < bestCover.Count || chosen.Count == bestCover.Count && Area(chosen) > Area(bestCover)) bestCover = chosen;
        }
        serviceGuides = serviceGuides.Select(guide => poses[guide.Id].First(candidate => bestCover.Contains(candidate.Component)).Guide).ToList();
        foreach (var guide in serviceGuides)
            working.Layout.CirculationReservations[working.Layout.CirculationReservations.FindIndex(existing => existing.Id == guide.Id)] = guide;
        var groups = serviceGuides.GroupBy(guide => Component(Anchor(guide))).Select(group => (Centers: components[group.Key], Guides: group.ToList())).ToList();
        if (groups.Count > policy.MaxComponents) return Fail("layout.maintenance", "planning-service-components", $"The controlled maintenance plan needs {groups.Count} width-correct components; configured maximum is {policy.MaxComponents}.");
        var allService = new HashSet<Cell>();
        foreach (var group in groups)
        {
            var tree = new HashSet<Cell>();
            foreach (var guide in group.Guides)
            {
                var anchor = Anchor(guide);
                var path = tree.Count == 0 ? new List<Cell> { anchor } : DesignNetworkGeometry.PathToFloor(working, anchor, tree, mw, group.Centers.Where(center => CanStamp(tree, center, mw, project.Settings.MaintenanceMaxWidth)).ToHashSet());
                if (path == null) return Fail(guide.RoomId, "planning-service-connectivity", "A width-safe service tree cannot join its local entries.");
                var approach = guide.ApproachCells.Concat(path.SelectMany(cell => DesignRouting.Stamp(cell, mw))).ToHashSet();
                tree.UnionWith(approach);
                if (DesignRouting.HasOpenSquare(tree, project.Settings.MaintenanceMaxWidth + 1)) return Fail(guide.RoomId, "planning-service-width", "Joining service approaches would create an oversized open maintenance passage.");
                result.Paths.Add(new() { Id = guide.Id, RoomId = guide.RoomId, Network = guide.Network, Width = guide.Width, Outward = guide.Outward, BoundaryCells = [.. guide.BoundaryCells], ApproachCells = LayoutGeometry.Sorted(approach) });
            }
            var transitions = new List<List<Cell>>();
            var ownedTransitions = DesignOwnedTransitPlanning.CountOwnedTransitions(working, group.Guides, result.Paths.Where(path => path.Network == "publicHall"));
            for (var index = ownedTransitions; index < policy.MinTransitionsPerComponent; index++)
            {
                if (!policy.AllowPublicTransitions) return Fail("layout.maintenance", "planning-transition", "Controlled components currently require an allowed public transition; owned-transit access will be measured from real door geometry at circulation.");
                List<Cell>? extension = null; List<Cell>? throat = null; List<Cell>? door = null;
                var widthSafeCenters = group.Centers.Where(center => CanStamp(tree, center, mw, project.Settings.MaintenanceMaxWidth)).ToHashSet();
                foreach (var candidate in TransitionCandidates(publicFloor, group.Centers, mw).Where(candidate => transitions.All(previous => previous.All(cell => candidate.Door.All(next => cell.Distance(next) > mw * 2))))
                    .OrderBy(candidate => candidate.Anchor.Distance(LayoutGeometry.Bounds(tree).Center)).ThenBy(candidate => candidate.Anchor.Y).ThenBy(candidate => candidate.Anchor.X).Take(256))
                {
                    var path = DesignNetworkGeometry.PathToFloor(working, candidate.Anchor, tree, mw, widthSafeCenters);
                    if (path == null) continue;
                    var proposal = tree.Concat(path.SelectMany(cell => DesignRouting.Stamp(cell, mw))).Concat(candidate.Throat).ToHashSet();
                    if (DesignRouting.HasOpenSquare(proposal, project.Settings.MaintenanceMaxWidth + 1)) continue;
                    extension = path; throat = candidate.Throat; door = candidate.Door; break;
                }
                if (extension == null || throat == null || door == null) return Fail("layout.maintenance", "planning-transition", "A maintenance component has no full-width controlled entrance onto the actual public plan.");
                tree.UnionWith(extension.SelectMany(cell => DesignRouting.Stamp(cell, mw)).Concat(throat)); transitions.Add(door);
            }
            allService.UnionWith(tree);
            // Attach the complete component tree to a real entry's planning record.
            result.Paths.First(path => path.Id == group.Guides[0].Id).ApproachCells = LayoutGeometry.Sorted(tree);
        }
        if (DesignRouting.HasOpenSquare(allService, project.Settings.MaintenanceMaxWidth + 1)) return Fail("layout.maintenance", "planning-service-width", "The combined service forest creates an oversized passage.");
        if (backbone.Count > 0) result.Paths.Add(new() { Id = "planning:public-backbone", RoomId = "", Network = "publicHall", Width = pw, ApproachCells = LayoutGeometry.Sorted(backbone.SelectMany(cell => DesignRouting.Stamp(cell, pw))) });
        foreach (var path in result.Paths)
        {
            var existing = project.Layout.CirculationReservations.FirstOrDefault(existing => existing.Id == path.Id);
            if (existing == null) project.Layout.CirculationReservations.Add(path);
            else { existing.RoomId = path.RoomId; existing.Network = path.Network; existing.Width = path.Width; existing.Outward = path.Outward; existing.BoundaryCells = path.BoundaryCells; existing.ApproachCells = path.ApproachCells; }
        }
        result.Complete = true; return result;
    }

    private static bool DirectionValid(Cell cell) => Cell.Directions.Contains(cell);

    private static bool CanStamp(HashSet<Cell> existing, Cell center, int width, int maxWidth)
    {
        var added = DesignRouting.Stamp(center, width).ToHashSet(); var bounds = LayoutGeometry.Bounds(added).Expand(maxWidth);
        if (bounds.Cells().Count(existing.Contains) < (maxWidth + 1) * (maxWidth + 1) - added.Count) return true;
        return !bounds.Cells().Any(origin => Enumerable.Range(0, maxWidth + 1).All(y => Enumerable.Range(0, maxWidth + 1).All(x => existing.Contains(origin + new Cell(x, y)) || added.Contains(origin + new Cell(x, y)))));
    }

    internal sealed record TransitionCandidate(Cell Anchor, List<Cell> Door, List<Cell> Throat, Cell Normal);
    internal static IEnumerable<TransitionCandidate> TransitionCandidates(HashSet<Cell> publicFloor, HashSet<Cell> serviceCenters, int width)
    {
        var yielded = new HashSet<Cell>();
        foreach (var normal in Cell.Directions)
        foreach (var cell in publicFloor.OrderBy(cell => cell.Y).ThenBy(cell => cell.X))
        {
            var tangent = new Cell(-normal.Y, normal.X);
            var run = Enumerable.Range(0, width).Select(index => cell + new Cell(tangent.X * index, tangent.Y * index)).ToList();
            if (!run.All(publicFloor.Contains)) continue;
            var door = run.Select(cell => cell + normal).ToList(); if (door.Any(publicFloor.Contains)) continue;
            var throat = door.SelectMany(cell => Enumerable.Range(1, width).Select(depth => cell + new Cell(normal.X * depth, normal.Y * depth))).ToList();
            var anchor = LayoutGeometry.Bounds(throat).Center;
            if (serviceCenters.Contains(anchor) && yielded.Add(anchor)) yield return new(anchor, door, throat, normal);
        }
    }
}
