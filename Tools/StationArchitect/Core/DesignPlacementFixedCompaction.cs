namespace Content.StationArchitect;

public static partial class DesignPlacement
{
    /// <summary>
    /// Interlock room silhouettes around an already connected pair of passage networks.
    /// Path masks, wall clearance and doorway standing strips stay at fixed coordinates;
    /// translations that would invalidate any part of that constructive proof are rejected.
    /// </summary>
    private static void CompactWithFixedCirculation(StationDesignProject project, List<RuntimeCluster> clusters,
        Dictionary<string, RoomDefinition> definitions)
    {
        if (project.Settings.Packing.Interlock <= 0 || project.Layout.PublicEdited || project.Layout.MaintenanceEdited) return;
        var guides = project.Layout.CirculationReservations;
        var forbidden = Halo(guides.SelectMany(guide => guide.ApproachCells));
        forbidden.UnionWith(guides.SelectMany(guide => guide.BoundaryCells));
        forbidden.UnionWith(Halo(DesignDockGeometry.ReservedCells(project)));
        forbidden.UnionWith(Halo(project.Layout.ReservedSpace));
        var canvas = new GridBounds(2, 2, project.Settings.Width - 4, project.Settings.Height - 4);
        var center = LayoutGeometry.Bounds(project.Layout.PublicWaypoints).Center;
        var ownerAreas = clusters.SelectMany(cluster => cluster.Rooms).GroupBy(room => room.OwnerId)
            .ToDictionary(group => group.Key, group => group.Sum(room => Goal(room, definitions[room.DefinitionPath])), StringComparer.Ordinal);
        var clearance = Math.Max(1, project.Settings.RoomGap + (int)Math.Round((1 - project.Settings.Packing.Compactness) * project.Settings.DepartmentGap));
        for (var pass = 0; pass < Math.Clamp((int)Math.Ceiling(project.Settings.Packing.Interlock * 4), 1, 4); pass++)
        {
            var changed = false;
            foreach (var cluster in clusters.Where(cluster => !cluster.Protected && !project.Layout.Docks.Any(dock => cluster.Ids.Contains(dock.RoomId)))
                .OrderByDescending(cluster => cluster.Bounds().Center.Distance(center)))
            {
                var other = clusters.Where(candidate => candidate != cluster).SelectMany(candidate => candidate.Floor()).ToHashSet();
                for (var layer = 0; layer < clearance; layer++) other = Halo(other);
                var bounds = cluster.Bounds(); var floor = cluster.Floor(); var candidates = new HashSet<Cell>();
                for (var distance = 1; distance <= 16; distance++)
                {
                    var dx = Math.Sign(center.X - bounds.Center.X) * distance; var dy = Math.Sign(center.Y - bounds.Center.Y) * distance;
                    candidates.Add(new(dx, 0)); candidates.Add(new(0, dy)); candidates.Add(new(dx, dy));
                }
                foreach (var neighbor in clusters.Where(candidate => candidate != cluster))
                {
                    var box = neighbor.Bounds();
                    candidates.Add(new(box.Right + clearance - bounds.X, box.Y - bounds.Y));
                    candidates.Add(new(box.X - clearance - bounds.Right, box.Y - bounds.Y));
                    candidates.Add(new(box.X - bounds.X, box.Bottom + clearance - bounds.Y));
                    candidates.Add(new(box.X - bounds.X, box.Y - clearance - bounds.Bottom));
                }
                var current = PackingScore(project, clusters, center, ownerAreas); var bestScore = current; var best = new Cell();
                foreach (var delta in candidates.Where(delta => delta != new Cell()).OrderBy(delta => delta.Distance(new())).Take(192))
                {
                    var moved = floor.Select(cell => cell + delta).ToHashSet();
                    if (moved.Any(cell => !canvas.Contains(cell)) || moved.Overlaps(other) || moved.Overlaps(forbidden) ||
                        project.Layout.Docks.Any(dock => DesignDockGeometry.PlanningIntersects(project, dock, moved, true))) continue;
                    // The unchanged endpoint must still belong to the same individual room.
                    if (guides.Where(guide => cluster.Ids.Contains(guide.RoomId)).Any(guide =>
                        !cluster.Rooms.Single(room => room.Id == guide.RoomId).GrowthReservation.ToHashSet()
                            .IsSupersetOf(guide.BoundaryCells.Select(cell => cell - guide.Outward - delta)))) continue;
                    if (cluster.Rooms.Any(room => !room.GrowthReservation.ToHashSet().IsSupersetOf(room.FrontageFloor.Select(cell => cell - delta)))) continue;
                    var score = PackingScore(project, clusters, center, ownerAreas, cluster, delta);
                    if (score >= bestScore - 1e-6 || !OwnerShapesSurvive(project, cluster, definitions, delta)) continue;
                    best = delta; bestScore = score;
                }
                if (best == new Cell()) continue;
                MoveCluster(project, cluster, best, moveApproaches: false); changed = true;
            }
            if (!changed) break;
        }
    }
}
