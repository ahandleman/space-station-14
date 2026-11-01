namespace Content.StationArchitect;

public static partial class DesignPlacement
{
    private sealed record PlanningCapacity(HashSet<string> Public, HashSet<string> Maintenance, HashSet<Cell> PublicCenters, bool TopologyConnected);

    private static HashSet<Cell> FloodCenters(HashSet<Cell> legal, Cell start)
    {
        if (!legal.Contains(start)) return [];
        var reached = new HashSet<Cell> { start }; var queue = new Queue<Cell>(); queue.Enqueue(start);
        while (queue.TryDequeue(out var cell)) foreach (var direction in Cell.Directions)
        {
            var next = cell + direction; if (legal.Contains(next) && reached.Add(next)) queue.Enqueue(next);
        }
        return reached;
    }

    private static HashSet<Cell> LargestCenterComponent(HashSet<Cell> legal)
    {
        var remaining = legal.ToHashSet(); var largest = new HashSet<Cell>();
        while (remaining.Count > 0)
        {
            var component = FloodCenters(remaining, remaining.First()); remaining.ExceptWith(component);
            if (component.Count > largest.Count) largest = component;
        }
        return largest;
    }

    private static PlanningCapacity MeasurePlanningCapacity(StationDesignProject project, List<RuntimeCluster> clusters, bool honorMaintenance = true)
    {
        var view = ProgramJson.Clone(project);
        var privateIds = clusters.SelectMany(cluster => cluster.Ids).ToHashSet(StringComparer.Ordinal);
        foreach (var room in view.Layout.Rooms.Where(room => !privateIds.Contains(room.Id) && !room.Pinned && !room.FloorLocked && !room.FloorEdited)) { room.GrowthReservation.Clear(); room.Cells.Clear(); }
        var delta = new Cell();
        if (CanNormalize(project, clusters))
        {
            var bounds = LayoutGeometry.Bounds(view.Layout.Rooms.SelectMany(room => room.GrowthReservation.Concat(room.Cells)).Concat(BackboneReservation(view)));
            var padding = PlanningPadding(view); delta = new(padding - bounds.X, padding - bounds.Y);
            view.Settings.Width = Math.Clamp(bounds.Width + padding * 2, 32, 512); view.Settings.Height = Math.Clamp(bounds.Height + padding * 2, 32, 512);
            foreach (var room in view.Layout.Rooms)
            {
                room.GrowthReservation = room.GrowthReservation.Select(cell => cell + delta).ToList(); room.Cells = room.Cells.Select(cell => cell + delta).ToList();
                room.ComplexReservation = room.ComplexReservation.Select(cell => cell + delta).ToList();
                room.FrontageFloor = room.FrontageFloor.Select(cell => cell + delta).ToList(); room.FrontageSpace = room.FrontageSpace.Select(cell => cell + delta).ToList();
            }
            foreach (var reservation in view.Layout.CirculationReservations)
            {
                reservation.BoundaryCells = reservation.BoundaryCells.Select(cell => cell + delta).ToList(); reservation.ApproachCells = reservation.ApproachCells.Select(cell => cell + delta).ToList();
            }
            view.Layout.PublicWaypoints = view.Layout.PublicWaypoints.Select(cell => cell + delta).ToList();
            view.Layout.ReservedSpace = view.Layout.ReservedSpace.Select(cell => cell + delta).ToList();
            for (var index = 0; index < view.Layout.Docks.Count; index++)
            {
                var dock = view.Layout.Docks[index]; var rule = DesignDockGeometry.Requirement(view, dock);
                if (rule != null && dock.BoundaryCells.Count == 1) view.Layout.Docks[index] = DesignDockGeometry.Create(dock.RoomId, rule, dock.Instance, dock.BoundaryCells[0] + delta, dock.Outward, new(0, 0, view.Settings.Width, view.Settings.Height));
            }
        }
        var publicLegal = DesignNetworkGeometry.LegalCenters(view, "publicHall", planning: true, honorOppositeReservations: honorMaintenance);
        var publicGoals = view.Layout.CirculationReservations.Where(reservation => reservation.Network == "publicHall" && reservation.RoomId.Length > 0).ToList();
        var seed = view.Layout.PublicWaypoints.FirstOrDefault();
        if (view.Layout.PublicWaypoints.Count == 0 && publicGoals.Count > 0) seed = DesignCirculationPlanning.Anchor(publicGoals[0]);
        var publicReached = FloodCenters(publicLegal, seed);
        var topology = view.Layout.PublicWaypoints.All(publicReached.Contains);
        var reachedPublic = publicGoals.Where(reservation => ValidStandingFloor(view, reservation) && publicReached.Contains(DesignCirculationPlanning.Anchor(reservation))).Select(reservation => reservation.Id).ToHashSet(StringComparer.Ordinal);
        var maintenanceLegal = DesignNetworkGeometry.LegalCenters(view, "maintenance", planning: true, additionalBlocked: BackboneReservation(view));
        var maintenanceReached = MaintenanceCandidateCenters(view, maintenanceLegal, view.Layout.CirculationReservations.Where(reservation => reservation.Network == "maintenance" && reservation.RoomId.Length > 0).ToList());
        var reachedMaintenance = view.Layout.CirculationReservations.Where(reservation => reservation.Network == "maintenance" && reservation.RoomId.Length > 0 && ValidStandingFloor(view, reservation) &&
            maintenanceReached.Contains(DesignCirculationPlanning.Anchor(reservation))).Select(reservation => reservation.Id).ToHashSet(StringComparer.Ordinal);
        return new(reachedPublic, reachedMaintenance, publicReached.Select(cell => cell - delta).ToHashSet(), topology);
    }

    private static bool ValidStandingFloor(StationDesignProject project, DesignCirculationReservation reservation)
    {
        var room = project.Layout.Rooms.FirstOrDefault(room => room.Id == reservation.RoomId); if (room == null || reservation.BoundaryCells.Count != reservation.Width) return false;
        var floor = (room.FloorLocked || room.FloorEdited ? room.Cells : room.GrowthReservation).ToHashSet();
        return reservation.BoundaryCells.All(cell => floor.Contains(cell - reservation.Outward) && !floor.Contains(cell));
    }
}
