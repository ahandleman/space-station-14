namespace Content.StationArchitect;

public static partial class DesignPlacement
{
    private sealed class PackingRepairBudget { public int Remaining = 24; public int BestPaths; }
    /// <summary>
    /// Negotiate a narrow packing failure by moving a complete, unprotected nearby cluster.
    /// Every trial keeps the room program and reselects its real-width local entrances.
    /// Only a complete two-network plan commits a translation.
    /// </summary>
    private static DesignCirculationPlanResult RepairCirculationPacking(StationDesignProject project, List<OwnerPlan> plans,
        List<Pair> pairs, Dictionary<string, RoomDefinition> definitions, HashSet<string> expected, DesignCirculationPlanResult initial,
        PackingRepairBudget? search = null, int depth = 0)
    {
        search ??= new();
        if (initial.Complete || !initial.Diagnostics.Any(issue => issue.Code == "planning-dual-network") ||
            depth >= 4 || search.Remaining <= 0 ||
            project.Layout.PublicEdited || project.Layout.MaintenanceEdited || project.Layout.EnvelopeEdited ||
            project.Layout.CirculationReservations.Any(guide => guide.Edited || guide.Locked)) return initial;
        var failure = initial.Diagnostics.First(issue => issue.Code == "planning-dual-network").Path;
        var failedRoom = project.Layout.Rooms.FirstOrDefault(room => room.Id == failure);
        if (failedRoom == null) return initial;
        var groups = new List<RuntimeCluster>(); var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            var left = plans.FirstOrDefault(plan => plan.OwnerId == pair.Left && plan.Slots.Any(slot => slot.Room.DefinitionPath == pair.LeftRoom));
            var right = plans.FirstOrDefault(plan => plan.OwnerId == pair.Right && plan.Slots.Any(slot => slot.Room.DefinitionPath == pair.RightRoom));
            if (left != null && right != null && !assigned.Contains(left.Id) && !assigned.Contains(right.Id)) Add([left, right]);
        }
        foreach (var plan in plans.Where(plan => !assigned.Contains(plan.Id))) Add([plan]);
        void Add(List<OwnerPlan> parts)
        {
            var rooms = parts.SelectMany(plan => plan.Slots).SelectMany(slot => RoomHierarchy.Descendants(project, slot.Room, true)).DistinctBy(room => room.Id).ToList();
            groups.Add(new() { Plans = parts, Rooms = rooms, Ids = rooms.Select(room => room.Id).ToHashSet(StringComparer.Ordinal),
                Protected = parts.Any(plan => plan.Protected) || rooms.Any(room => room.Pinned || room.FloorLocked || room.FloorEdited || HasFixedPhysicalDependency(project, room)) ||
                    project.Layout.Departments.Any(owner => parts.Any(plan => plan.OwnerId == owner.OwnerId) && (owner.Locked || owner.Pinned)) });
            foreach (var part in parts) assigned.Add(part.Id);
        }
        var failedGuide = project.Layout.CirculationReservations.FirstOrDefault(guide => guide.Network == "publicHall" && guide.RoomId == failure);
        if (failedGuide == null) return initial;
        var nearestBackbone = project.Layout.PublicWaypoints.OrderBy(cell => cell.Distance(DesignCirculationPlanning.Anchor(failedGuide))).FirstOrDefault();
        var conflict = LayoutGeometry.Bounds(failedGuide.ApproachCells.Append(nearestBackbone)).Expand(project.Settings.PublicWidth + 1);
        var conflictCells = conflict.Cells().ToHashSet();
        var canvas = new GridBounds(2, 2, project.Settings.Width - 4, project.Settings.Height - 4);
        var backboneFloor = PlanningBackboneFloor(project); var backbone = Halo(backboneFloor); var docks = Halo(DesignDockGeometry.ReservedCells(project));
        var directLanes = new[] { false, true }.Select(horizontalFirst => Halo(OrthogonalLine(DesignCirculationPlanning.Anchor(failedGuide), nearestBackbone, horizontalFirst)
            .SelectMany(cell => DesignRouting.Stamp(cell, project.Settings.PublicWidth)))).ToList();
        var candidates = groups.Where(group => !group.Protected && !group.Ids.Contains(failure) &&
                !project.Layout.Docks.Any(dock => group.Ids.Contains(dock.RoomId)) && group.Floor().Overlaps(conflictCells))
            .OrderByDescending(group => initial.ConflictRoomIds.Any(group.Ids.Contains))
            .ThenBy(group => group.Floor().Count).ThenBy(group => group.Bounds().Center.Distance(failedRoom.Seed)).Take(6).ToList();
        PackingRepairTrace($"depth={depth} failure={failure} witness={string.Join(',', initial.ConflictRoomIds)} candidates={string.Join(',', candidates.Select(group => group.Plans[0].Id))}");
        foreach (var group in candidates)
        {
            var savedGuides = project.Layout.CirculationReservations.ToList();
            var foreign = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && !group.Ids.Contains(room.Id))
                .SelectMany(room => room.GrowthReservation.Concat(room.ComplexReservation).Concat(room.Cells)).ToHashSet();
            var foreignHalo = Halo(foreign);
            var fixedGuideHalo = Halo(project.Layout.CirculationReservations.Where(guide => guide.Edited || guide.Locked).SelectMany(guide => guide.BoundaryCells.Concat(guide.ApproachCells)));
            var floor = group.Floor(); var bounds = group.Bounds();
            var ownedSpace = group.Rooms.SelectMany(room => room.FrontageSpace).ToHashSet();
            var foreignSpace = Halo(project.Layout.Rooms.Where(room => !group.Ids.Contains(room.Id)).SelectMany(room => room.FrontageSpace));
            var choices = new List<Cell>();
            var extent = LayoutGeometry.Bounds(foreign).Expand(8);
            var positions = new HashSet<Cell>();
            var xAlignments = new HashSet<int>(); var yAlignments = new HashSet<int>();
            foreach (var neighbor in project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && !group.Ids.Contains(room.Id)))
            {
                var box = LayoutGeometry.Bounds(neighbor.GrowthReservation.Concat(neighbor.Cells)); if (box.Area == 0) continue;
                xAlignments.UnionWith([box.X, box.Right - bounds.Width, box.Right + project.Settings.RoomGap, box.X - project.Settings.RoomGap - bounds.Width]);
                yAlignments.UnionWith([box.Y, box.Bottom - bounds.Height, box.Bottom + project.Settings.RoomGap, box.Y - project.Settings.RoomGap - bounds.Height]);
            }
            foreach (var x in xAlignments) foreach (var y in yAlignments) positions.Add(new(x, y));
            for (var dy = -8; dy <= 8; dy++) for (var dx = -8; dx <= 8; dx++) positions.Add(new(bounds.X + dx, bounds.Y + dy));
            for (var y = Math.Max(canvas.Y, extent.Y); y < Math.Min(canvas.Bottom, extent.Bottom) - bounds.Height; y += 2)
            for (var x = Math.Max(canvas.X, extent.X); x < Math.Min(canvas.Right, extent.Right) - bounds.Width; x += 2) positions.Add(new(x, y));
            foreach (var position in positions)
            {
                var delta = position - new Cell(bounds.X, bounds.Y);
                if (delta.Distance(new()) > 36 || delta == new Cell()) continue;
                var moved = floor.Select(cell => cell + delta).ToHashSet();
                var movedSpace = ownedSpace.Select(cell => cell + delta).ToHashSet();
                if (moved.Any(cell => !canvas.Contains(cell)) || moved.Overlaps(foreignHalo) ||
                    moved.Overlaps(fixedGuideHalo) || moved.Overlaps(backbone) || moved.Overlaps(docks) || moved.Overlaps(foreignSpace) ||
                    movedSpace.Overlaps(foreignHalo) || movedSpace.Overlaps(fixedGuideHalo) || movedSpace.Overlaps(backbone) ||
                    project.Layout.Docks.Any(dock => DesignDockGeometry.PlanningIntersects(project, dock, moved, true))) continue;
                if (!OwnerShapesSurvive(project, group, definitions, delta)) continue;
                choices.Add(delta);
            }
            var plateauDistance = 0;
            foreach (var delta in choices.OrderByDescending(delta => directLanes.Any(lane => !floor.Any(cell => lane.Contains(cell + delta))))
                .ThenBy(delta => delta.Distance(new())).ThenBy(delta => LayoutGeometry.StableOrder(group.Plans[0].Id + delta, project.Settings.Seed)))
            {
                if (delta.Distance(new()) < plateauDistance) continue;
                var madeProgress = false;
                PackingRepairTrace($"trial depth={depth} cluster={group.Plans[0].Id} delta={delta}");
                var moved = floor.Select(cell => cell + delta).ToHashSet();
                var movedSpaceHalo = Halo(ownedSpace.Select(cell => cell + delta));
                var affectedIds = savedGuides.Where(guide => !guide.Edited && !guide.Locked &&
                    (group.Ids.Contains(guide.RoomId) || moved.Overlaps(Halo(guide.ApproachCells.Concat(guide.BoundaryCells))) || movedSpaceHalo.Overlaps(guide.ApproachCells.Concat(guide.BoundaryCells))))
                    .Select(guide => guide.Id).ToHashSet(StringComparer.Ordinal);
                var affectedGroups = groups.Where(candidate => candidate == group || savedGuides.Any(guide => affectedIds.Contains(guide.Id) && candidate.Ids.Contains(guide.RoomId))).ToList();
                // An adjacent automatic doorway is negotiable; a protected room or door is not.
                if (affectedGroups.Any(candidate => candidate.Protected)) { PackingRepairTrace("rejected protected adjacent allocation"); continue; }
                project.Layout.CirculationReservations.RemoveAll(guide => affectedIds.Contains(guide.Id));
                MoveCluster(project, group, delta);
                var publicLegal = DesignNetworkGeometry.LegalCenters(project, "publicHall", planning: true);
                var publicReached = FloodCenters(publicLegal, project.Layout.PublicWaypoints.FirstOrDefault());
                var maintenanceLegal = DesignNetworkGeometry.LegalCenters(project, "maintenance", planning: true, additionalBlocked: backboneFloor);
                var maintenanceReached = MaintenanceCandidateCenters(project, maintenanceLegal, project.Layout.CirculationReservations.Where(guide => guide.Network == "maintenance" && guide.RoomId.Length > 0).ToList());
                foreach (var affected in affectedGroups)
                {
                    var protectedBeforeTrial = affected.Protected;
                    ReserveClusterApproaches(project, affected, definitions, publicReached, maintenanceReached);
                    affected.Protected = protectedBeforeTrial;
                }
                var actual = project.Layout.CirculationReservations.Select(guide => guide.Id).ToHashSet(StringComparer.Ordinal);
                if (expected.All(actual.Contains))
                {
                    foreach (var poses in RepairPoseTrials(project, plans, pairs, definitions, affectedIds, group.Ids, directLanes))
                    {
                        foreach (var pose in poses)
                        {
                            var index = project.Layout.CirculationReservations.FindIndex(guide => guide.Id == pose.Id);
                            if (index >= 0) project.Layout.CirculationReservations[index] = pose;
                        }
                        PackingRepairTrace("poses " + string.Join(';', poses.Select(pose => pose.RoomId + ":" + pose.Outward + ":" + pose.BoundaryCells[0])));
                        if (!EntriesIndependentlyConnected(project, backboneFloor)) { PackingRepairTrace("independent entry connectivity rejected"); continue; }
                        if (search.Remaining-- <= 0) break;
                        var trial = DesignCirculationPlanning.Connect(project, expected, guide => AlternativeApproaches(project, plans, pairs, definitions, guide));
                        PackingRepairTrace($"result complete={trial.Complete} paths={trial.Paths.Count} failure={trial.FailedGuideId} diagnostics={string.Join(';', trial.Diagnostics.Select(issue => issue.Code + ':' + issue.Path))}");
                        SavePrivatePackingWitness(project, trial, search);
                        if (trial.Complete) { RefreshOwnerPlanning(project); return trial; }
                        if (trial.Paths.Count > initial.Paths.Count)
                        {
                            madeProgress = true;
                            var combined = RepairCirculationPacking(project, plans, pairs, definitions, expected, trial, search, depth + 1);
                            if (combined.Complete) { RefreshOwnerPlanning(project); return combined; }
                        }
                    }
                }
                else PackingRepairTrace("missing guides " + string.Join(',', expected.Where(id => !actual.Contains(id))));
                project.Layout.CirculationReservations.RemoveAll(guide => affectedIds.Contains(guide.Id));
                MoveCluster(project, group, new(-delta.X, -delta.Y));
                project.Layout.CirculationReservations = [.. savedGuides];
                if (!madeProgress) plateauDistance = delta.Distance(new()) + project.Settings.PublicWidth + project.Settings.MaintenanceWidth;
                if (search.Remaining <= 0) return initial;
            }
        }
        return initial;
    }

    private static void PackingRepairTrace(string message)
    {
        if (Environment.GetEnvironmentVariable("STATION_ARCHITECT_QA_LABEL") is { Length: > 0 }) Console.WriteLine("Packing repair: " + message);
    }

    private static void SavePrivatePackingWitness(StationDesignProject project, DesignCirculationPlanResult trial, PackingRepairBudget search)
    {
        if (trial.Paths.Count <= search.BestPaths) return;
        search.BestPaths = trial.Paths.Count;
        if (Environment.GetEnvironmentVariable("STATION_ARCHITECT_QA_OUTPUT") is not { Length: > 0 } directory) return;
        var label = Environment.GetEnvironmentVariable("STATION_ARCHITECT_QA_LABEL") ?? "qa";
        Directory.CreateDirectory(directory);
        var prefix = Path.Combine(directory, "architect-dense-" + label + "-best-private");
        File.WriteAllText(prefix + ".station.json", System.Text.Json.JsonSerializer.Serialize(project, ProgramJson.Options));
        File.WriteAllText(prefix + "-plan.json", System.Text.Json.JsonSerializer.Serialize(trial, ProgramJson.Options));
    }

    private static void SaveFinalPlanningWitness(StationDesignProject project, DesignCirculationPlanResult result)
    {
        if (Environment.GetEnvironmentVariable("STATION_ARCHITECT_QA_OUTPUT") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        var label = Environment.GetEnvironmentVariable("STATION_ARCHITECT_QA_LABEL") ?? "qa";
        File.WriteAllText(Path.Combine(directory, "architect-dense-" + label + "-planning-detail.json"), System.Text.Json.JsonSerializer.Serialize(result, ProgramJson.Options));
    }

    private static IEnumerable<List<DesignCirculationReservation>> RepairPoseTrials(StationDesignProject project, List<OwnerPlan> plans,
        List<Pair> pairs, Dictionary<string, RoomDefinition> definitions, HashSet<string> affected, HashSet<string> movedRooms, List<HashSet<Cell>> lanes)
    {
        var neighboring = project.Layout.CirculationReservations.Where(guide => affected.Contains(guide.Id) && guide.Network == "publicHall" && !movedRooms.Contains(guide.RoomId)).ToList();
        if (neighboring.Count == 0) return [new List<DesignCirculationReservation>()];
        var choices = neighboring.Select(guide => new[] { guide }.Concat(AlternativeApproaches(project, plans, pairs, definitions, guide))
            .OrderBy(candidate => lanes.Min(lane => candidate.ApproachCells.Min(cell => lane.Min(cell.Distance))))
            .ThenBy(candidate => candidate.ApproachCells.Count).DistinctBy(candidate => (candidate.RoomId, candidate.Outward)).Take(4).ToList()).ToList();
        var preferred = choices.Select(options => options[0]).ToList();
        var trials = new List<List<DesignCirculationReservation>> { preferred };
        for (var index = 0; index < choices.Count && trials.Count < 8; index++)
        foreach (var alternative in choices[index].Skip(1))
        {
            var trial = preferred.ToList(); trial[index] = alternative; trials.Add(trial);
            if (trials.Count == 8) break;
        }
        return trials;
    }

    private static bool EntriesIndependentlyConnected(StationDesignProject project, HashSet<Cell> backbone)
    {
        foreach (var network in new[] { "publicHall", "maintenance" })
        {
            var guides = project.Layout.CirculationReservations.Where(guide => guide.Network == network && guide.RoomId.Length > 0).ToList();
            if (guides.Count == 0) continue;
            var legal = DesignNetworkGeometry.LegalCenters(project, network, planning: true, additionalBlocked: network == "maintenance" ? backbone : null);
            var seed = network == "publicHall" && project.Layout.PublicWaypoints.Count > 0 ? project.Layout.PublicWaypoints[0] : DesignCirculationPlanning.Anchor(guides[0]);
            var reached = FloodCenters(legal, seed);
            if (network == "maintenance" && project.Definition.Maintenance.Connectivity.Policy == "controlled")
            {
                if (guides.Any(guide => !legal.Contains(DesignCirculationPlanning.Anchor(guide)))) return false;
                continue;
            }
            if (guides.Any(guide => !reached.Contains(DesignCirculationPlanning.Anchor(guide)))) return false;
            if (network == "publicHall" && project.Layout.PublicWaypoints.Any(waypoint => !reached.Contains(waypoint))) return false;
        }
        return true;
    }

    private static IEnumerable<Cell> OrthogonalLine(Cell from, Cell to, bool horizontalFirst)
    {
        yield return from;
        for (var pass = 0; pass < 2; pass++)
        {
            var horizontal = horizontalFirst == (pass == 0);
            while (horizontal ? from.X != to.X : from.Y != to.Y)
            {
                from += horizontal ? new Cell(Math.Sign(to.X - from.X), 0) : new Cell(0, Math.Sign(to.Y - from.Y));
                yield return from;
            }
        }
    }

    private static HashSet<Cell> PlanningBackboneFloor(StationDesignProject project)
    {
        var waypoints = project.Layout.PublicWaypoints;
        var centers = new HashSet<Cell>(waypoints);
        for (var index = 1; index < waypoints.Count; index++) centers.UnionWith(OrthogonalLine(waypoints[index - 1], waypoints[index], true));
        if (project.Settings.Topology == "ring" && waypoints.Count >= 3) centers.UnionWith(OrthogonalLine(waypoints[^1], waypoints[0], true));
        return centers.SelectMany(cell => DesignRouting.Stamp(cell, project.Settings.PublicWidth)).ToHashSet();
    }

    private static bool OwnerShapesSurvive(StationDesignProject project, RuntimeCluster group, Dictionary<string, RoomDefinition> definitions, Cell delta)
    {
        foreach (var ownerId in group.Rooms.Select(room => room.OwnerId).Distinct(StringComparer.Ordinal))
        {
            var definition = project.Definition.Departments.FirstOrDefault(owner => owner.Id == ownerId);
            if (definition == null) continue;
            var rooms = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && room.OwnerId == ownerId && definitions[room.DefinitionPath].Environment.Placement != "exterior").ToList();
            var mask = rooms.SelectMany(room => room.GrowthReservation.Concat(room.Cells).Select(cell => group.Ids.Contains(room.Id) ? cell + delta : cell));
            var bounds = LayoutGeometry.Bounds(mask); if (bounds.Area == 0) continue;
            var area = rooms.Sum(room => Goal(room, definitions[room.DefinitionPath]));
            var aspect = Math.Max((double)bounds.Width / bounds.Height, (double)bounds.Height / bounds.Width);
            if (aspect > definition.Shape.MaxAspectRatio || (double)area / bounds.Area < definition.Shape.MinFillRatio) return false;
        }
        return true;
    }
}
