namespace Content.StationArchitect;

/// <summary>Shared full-width corridor capacity checks for packing and runtime routing.</summary>
public static class DesignNetworkGeometry
{
    /// <summary>Automatic passage floors leave one cell for their enclosing wall/hull inside the canvas.</summary>
    public static GridBounds PassageBounds(StationDesignProject project) => new(1, 1, project.Settings.Width - 2, project.Settings.Height - 2);

    public static HashSet<Cell> LegalCenters(StationDesignProject project, string network, bool planning = false,
        IEnumerable<Cell>? oppositeFloorOverride = null, bool honorOppositeReservations = true, IEnumerable<Cell>? additionalBlocked = null)
    {
        var isPublic = network is "publicHall" or "$public";
        if (!isPublic && network is not ("maintenance" or "$maintenance")) throw new ArgumentException("Use publicHall or maintenance for corridor capacity.");
        var width = isPublic ? project.Settings.PublicWidth : project.Settings.MaintenanceWidth;
        IEnumerable<Cell> Floor(DesignRoom room) => planning && !room.FloorLocked && !room.FloorEdited && room.GrowthReservation.Count > 0 ? room.GrowthReservation : room.Cells;
        var occupied = project.Layout.Rooms.Where(room => room.Enabled || room.Cells.Count > 0).SelectMany(Floor).ToHashSet();
        var blocked = Halo(occupied);
        blocked.UnionWith(Halo(project.Layout.ReservedSpace.Concat(DesignDockGeometry.ReservedCells(project))));
        var opposite = isPublic ? "maintenance" : "publicHall";
        blocked.UnionWith(Halo(project.Layout.CirculationReservations.Where(reservation => reservation.Network == opposite && (honorOppositeReservations || reservation.Edited || reservation.Locked))
            .SelectMany(reservation => reservation.BoundaryCells.Concat(reservation.ApproachCells))));
        var otherFloor = oppositeFloorOverride ?? (isPublic ? planning && !project.Layout.MaintenanceEdited ? [] : project.Layout.Maintenance : planning && !project.Layout.PublicEdited ? [] : project.Layout.PublicCorridors);
        blocked.UnionWith(Halo(otherFloor));
        if (additionalBlocked != null) blocked.UnionWith(Halo(additionalBlocked));
        var canvas = new GridBounds(0, 0, project.Settings.Width, project.Settings.Height);
        var envelope = project.Layout.Envelope.ToHashSet();
        var passageBounds = PassageBounds(project);
        return canvas.Cells().Where(center => DesignRouting.Stamp(center, width).All(cell => passageBounds.Contains(cell) && !blocked.Contains(cell) && (!isPublic || !project.Layout.EnvelopeEdited || envelope.Contains(cell)))).ToHashSet();
    }

    public static HashSet<Cell> ReachableFromFloor(HashSet<Cell> legalCenters, IEnumerable<Cell> sourceFloor, int width)
    {
        var source = sourceFloor.ToHashSet();
        var reached = new HashSet<Cell>(); var pending = new Queue<Cell>();
        foreach (var seed in source.SelectMany(cell => DesignRouting.Stamp(cell, width + 1)).Distinct())
            if (legalCenters.Contains(seed) && DesignRouting.Stamp(seed, width).Any(source.Contains) && reached.Add(seed)) pending.Enqueue(seed);
        while (pending.TryDequeue(out var cell)) foreach (var direction in Cell.Directions)
        {
            var next = cell + direction;
            if (legalCenters.Contains(next) && reached.Add(next)) pending.Enqueue(next);
        }
        return reached;
    }

    public static HashSet<Cell> WithoutOppositeFloor(HashSet<Cell> legalCenters, IEnumerable<Cell> oppositeFloor, int width)
    {
        var offsets = DesignRouting.Stamp(new(), width);
        var blockedCenters = Halo(oppositeFloor).SelectMany(cell => offsets.Select(offset => cell - offset)).ToHashSet();
        return legalCenters.Except(blockedCenters).ToHashSet();
    }

    public static List<Cell>? PathToFloor(StationDesignProject project, Cell start, IEnumerable<Cell> targetFloor, int width, HashSet<Cell> legalCenters, int budget = 100000)
    {
        if (!legalCenters.Contains(start)) return null;
        var target = targetFloor.ToHashSet();
        return DesignRouting.Search(start, cell => DesignRouting.Stamp(cell, width).Any(target.Contains), legalCenters.Contains, new(0, 0, project.Settings.Width, project.Settings.Height), target, budget);
    }

    public static HashSet<Cell> ConnectedCenters(HashSet<Cell> legalCenters, IEnumerable<Cell> seeds)
    {
        var reached = seeds.Where(legalCenters.Contains).ToHashSet(); var pending = new Queue<Cell>(reached);
        while (pending.TryDequeue(out var cell)) foreach (var direction in Cell.Directions)
        {
            var next = cell + direction;
            if (legalCenters.Contains(next) && reached.Add(next)) pending.Enqueue(next);
        }
        return reached;
    }

    public static HashSet<Cell> Halo(IEnumerable<Cell> cells) => cells.SelectMany(cell => Cell.Directions.Select(direction => cell + direction).Append(cell)).ToHashSet();
}
