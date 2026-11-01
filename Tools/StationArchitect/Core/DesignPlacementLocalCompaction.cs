namespace Content.StationArchitect;

public static partial class DesignPlacement
{
    private static void CompactLocalPlans(StationDesignProject project, List<RuntimeCluster> clusters,
        Dictionary<string, RoomDefinition> definitions)
    {
        if (project.Settings.Packing.Interlock <= 0 || project.Layout.PublicEdited || project.Layout.MaintenanceEdited ||
            project.Layout.Interfaces.Any(port => port.Edited) || project.Layout.Routes.Any(route => route.Edited || route.Locked)) return;
        var edge = Math.Max(project.Settings.PublicWidth, project.Settings.MaintenanceWidth) + 2;
        var canvas = new GridBounds(edge, edge, project.Settings.Width - 2 * edge, project.Settings.Height - 2 * edge);
        var backbone = BackboneReservation(project);
        var center = project.Layout.PublicWaypoints.Count > 0 ? LayoutGeometry.Bounds(project.Layout.PublicWaypoints).Center : LayoutGeometry.Bounds(clusters.SelectMany(cluster => cluster.Floor())).Center;
        var ownerAreas = clusters.SelectMany(cluster => cluster.Rooms).GroupBy(room => room.OwnerId).ToDictionary(group => group.Key,
            group => group.Sum(room => Goal(room, definitions[room.DefinitionPath])), StringComparer.Ordinal);
        var clearance = Math.Max(1, project.Settings.RoomGap + (int)Math.Round((1 - project.Settings.Packing.Compactness) * project.Settings.DepartmentGap));
        var capacity = MeasurePlanningCapacity(project, clusters);
        for (var pass = 0; pass < Math.Clamp((int)Math.Ceiling(project.Settings.Packing.Interlock * 6), 1, 6); pass++)
        {
            var changed = false;
            foreach (var cluster in clusters.Where(cluster => !cluster.Protected).OrderByDescending(cluster => cluster.Bounds().Center.Distance(center)))
            {
                var floor = cluster.Floor(); var bounds = cluster.Bounds();
                var others = clusters.Where(other => other != cluster).SelectMany(other => other.Floor()).ToHashSet();
                var otherHalo = others.ToHashSet(); for (var layer = 0; layer < clearance; layer++) otherHalo = Halo(otherHalo);
                var foreignGuides = project.Layout.CirculationReservations.Where(guide => !cluster.Ids.Contains(guide.RoomId)).ToList();
                var guideHalo = Halo(foreignGuides.SelectMany(guide => guide.ApproachCells.Concat(guide.BoundaryCells)));
                var ownGuides = project.Layout.CirculationReservations.Where(guide => cluster.Ids.Contains(guide.RoomId)).ToList();
                var foreignSpace = Halo(project.Layout.Rooms.Where(room => !cluster.Ids.Contains(room.Id)).SelectMany(room => room.FrontageSpace));
                var ownSpace = cluster.Rooms.SelectMany(room => room.FrontageSpace).ToHashSet();
                var foreignDocks = project.Layout.Docks.Where(dock => !cluster.Ids.Contains(dock.RoomId)).ToList();
                var foreignDockHalo = Halo(foreignDocks.SelectMany(DesignDockGeometry.ReservedCells));
                var candidates = new HashSet<Cell>();
                for (var distance = 1; distance <= 48; distance++)
                {
                    var dx = Math.Sign(center.X - bounds.Center.X) * distance; var dy = Math.Sign(center.Y - bounds.Center.Y) * distance;
                    candidates.Add(new(dx, 0)); candidates.Add(new(0, dy)); candidates.Add(new(dx, dy));
                }
                foreach (var neighbor in clusters.Where(other => other != cluster))
                {
                    var box = neighbor.Bounds();
                    foreach (var x in new[] { box.X, box.Right - bounds.Width })
                    {
                        candidates.Add(new(x - bounds.X, box.Bottom + clearance - bounds.Y));
                        candidates.Add(new(x - bounds.X, box.Y - clearance - bounds.Bottom));
                    }
                    foreach (var y in new[] { box.Y, box.Bottom - bounds.Height })
                    {
                        candidates.Add(new(box.Right + clearance - bounds.X, y - bounds.Y));
                        candidates.Add(new(box.X - clearance - bounds.Right, y - bounds.Y));
                    }
                }
                var current = PackingScore(project, clusters, center, ownerAreas);
                var ranked = new List<(Cell Delta, double Score)>();
                foreach (var delta in candidates.Where(delta => delta != new Cell()).OrderBy(delta => delta.Distance(new())).Take(384))
                {
                    var moved = floor.Select(cell => cell + delta).ToHashSet();
                    if (moved.Any(cell => !canvas.Contains(cell)) || moved.Overlaps(otherHalo) || moved.Overlaps(guideHalo) || moved.Overlaps(backbone) ||
                        moved.Overlaps(foreignSpace) || moved.Overlaps(foreignDockHalo) || foreignDocks.Any(dock => DesignDockGeometry.PlanningIntersects(project, dock, moved, true))) continue;
                    var approaches = ownGuides.SelectMany(guide => guide.ApproachCells).Select(cell => cell + delta).ToHashSet();
                    if (approaches.Overlaps(Halo(others)) || approaches.Overlaps(foreignSpace) || approaches.Overlaps(foreignDockHalo)) continue;
                    if (ownGuides.Any(guide => foreignGuides.Where(other => other.Network != guide.Network).Any(other =>
                        Halo(other.ApproachCells).Overlaps(guide.ApproachCells.Select(cell => cell + delta))))) continue;
                    if (ownGuides.Where(guide => guide.Network == "maintenance").SelectMany(guide => guide.ApproachCells).Select(cell => cell + delta).Any(backbone.Contains)) continue;
                    var space = ownSpace.Select(cell => cell + delta).ToHashSet();
                    if (space.Overlaps(Halo(others)) || space.Overlaps(guideHalo) || space.Overlaps(backbone) ||
                        space.Overlaps(project.Layout.Docks.Where(dock => !cluster.Ids.Contains(dock.RoomId)).SelectMany(dock => dock.BoundaryCells))) continue;
                    var docks = TranslatedDocks(project, cluster, delta);
                    if (docks == null || docks.Any(dock => DesignDockGeometry.PlanningIntersects(project, dock, others, true) ||
                        DesignDockGeometry.PlanningIntersects(project, dock, backbone) || foreignDocks.Any(other => DesignDockGeometry.PlanningOverlap(project, dock, other)))) continue;
                    if (docks.SelectMany(DesignDockGeometry.ReservedCells).Any(cell => otherHalo.Contains(cell) || guideHalo.Contains(cell))) continue;
                    var score = PackingScore(project, clusters, center, ownerAreas, cluster, delta);
                    if (score < current - 1e-6) ranked.Add((delta, score));
                }
                foreach (var candidate in ranked.OrderBy(candidate => candidate.Score).Take(3))
                {
                    if (!OwnerShapesSurvive(project, cluster, definitions, candidate.Delta)) continue;
                    MoveCluster(project, cluster, candidate.Delta);
                    var next = MeasurePlanningCapacity(project, clusters);
                    if (next.TopologyConnected && next.Public.IsSupersetOf(capacity.Public) && next.Maintenance.IsSupersetOf(capacity.Maintenance))
                    { capacity = next; changed = true; break; }
                    MoveCluster(project, cluster, new(-candidate.Delta.X, -candidate.Delta.Y));
                }
            }
            if (!changed) break;
        }
        if (CanNormalize(project, clusters))
        {
            var bounds = LayoutGeometry.Bounds(clusters.SelectMany(cluster => cluster.Floor()).Concat(backbone).Concat(project.Layout.ReservedSpace));
            var padding = PlanningPadding(project); var delta = new Cell(padding - bounds.X, padding - bounds.Y);
            foreach (var cluster in clusters) MoveCluster(project, cluster, delta);
            project.Layout.PublicWaypoints = project.Layout.PublicWaypoints.Select(cell => cell + delta).ToList();
            project.Settings.Width = Math.Clamp(bounds.Width + padding * 2, 32, 512); project.Settings.Height = Math.Clamp(bounds.Height + padding * 2, 32, 512);
            RefreshGeneratedDocks(project);
        }
    }
}
