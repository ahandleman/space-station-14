namespace Content.StationArchitect;

/// <summary>Physical room-complex observations. A tree link alone never proves containment or access.</summary>
public static class DesignNestedGeometry
{
    public static bool IsAncestor(StationDesignProject project, string ancestorId, DesignRoom room)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var id = room.ParentInstanceId; id.Length > 0 && seen.Add(id);)
        {
            if (id == ancestorId) return true;
            id = project.Layout.Rooms.FirstOrDefault(candidate => candidate.Id == id)?.ParentInstanceId ?? "";
        }
        return false;
    }

    public static List<DesignRoom> Subtree(StationDesignProject project, DesignRoom room) => RoomHierarchy.Descendants(project, room, true).Where(candidate => candidate.Enabled && !candidate.Orphaned).ToList();

    public static HashSet<Cell> ComplexCells(StationDesignProject project, DesignRoom room)
    {
        var floor = Subtree(project, room).SelectMany(candidate => candidate.Cells).ToHashSet();
        var physical = project.Layout.Walls.Concat(project.Layout.Hull).Concat(project.Layout.Windows)
            .Concat(DesignMeasurements.ValidInterfaces(project).Where(port => port.Kind != "utility").SelectMany(port => port.Cells));
        return floor.Concat(physical.Where(cell => Cell.Directions.Count(direction => floor.Contains(cell + direction)) >= 2)).ToHashSet();
    }

    public static bool EntryAllowed(StationDesignProject project, DesignRoom room, DesignRoom? other, IReadOnlyDictionary<string, RoomDefinition> definitions)
    {
        if (room.ParentInstanceId.Length == 0 || definitions[room.DefinitionPath].Nesting.AllowExternalEntry) return true;
        var parent = project.Layout.Rooms.FirstOrDefault(candidate => candidate.Id == room.ParentInstanceId);
        return parent != null && other != null && (other.Id == parent.Id || IsAncestor(project, parent.Id, other));
    }

    public static HashSet<Cell> WalkingFloor(StationDesignProject project)
    {
        var blocked = project.Layout.Equipment.Where(entity => DesignSystems.BlocksCrew(project, entity)).SelectMany(entity => entity.Footprint)
            .Concat(DesignTransport.EndpointFloorCells(project)).ToHashSet();
        return project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned).SelectMany(room => room.Cells)
            .Concat(project.Layout.PublicCorridors).Concat(project.Layout.Maintenance)
            .Concat(DesignMeasurements.ValidInterfaces(project).Where(port => port.Kind == "walking" && port.Type is "doorway" or "airlock" or "dockingPort").SelectMany(port => port.Cells)).Except(blocked).ToHashSet();
    }

    public static bool Reachable(StationDesignProject project, HashSet<Cell> allowed, IEnumerable<Cell> sources, HashSet<Cell> target, bool crewTubes, IReadOnlyDictionary<Cell, List<Cell>>? crewJumps = null)
    {
        var jumps = crewTubes ? crewJumps ?? DesignCrewTraversal.Jumps(project) : new Dictionary<Cell, List<Cell>>();
        var reached = new HashSet<Cell>(); var queue = new Queue<Cell>();
        foreach (var source in sources.Where(allowed.Contains)) if (reached.Add(source)) queue.Enqueue(source);
        while (queue.TryDequeue(out var cell))
        {
            if (target.Contains(cell)) return true;
            foreach (var direction in Cell.Directions)
            { var next = cell + direction; if (allowed.Contains(next) && reached.Add(next)) queue.Enqueue(next); }
            if (jumps.TryGetValue(cell, out var exits)) foreach (var next in exits)
                if (allowed.Contains(next) && reached.Add(next)) queue.Enqueue(next);
        }
        return false;
    }

    public static IEnumerable<ProgramDiagnostic> Validate(StationDesignProject project, bool grown, bool circulated, IReadOnlyDictionary<string, RoomDefinition> definitions, bool observeAll = false, IReadOnlyDictionary<Cell, List<Cell>>? crewJumps = null)
    {
        var rooms = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && definitions.ContainsKey(room.DefinitionPath)).ToList();
        var walking = circulated ? WalkingFloor(project) : [];
        crewJumps ??= circulated ? DesignCrewTraversal.Jumps(project) : new Dictionary<Cell, List<Cell>>();
        foreach (var room in rooms)
        {
            var expectedParent = RoomHierarchy.ParentTemplatePath(room.DefinitionPath);
            if (expectedParent == null)
            {
                if (room.ParentInstanceId.Length > 0) yield return new(room.Id, "nested-parent", "A top-level room cannot identify a room parent.");
                continue;
            }
            var parent = rooms.FirstOrDefault(candidate => candidate.Id == room.ParentInstanceId && candidate.DefinitionPath == expectedParent);
            if (parent == null) { yield return new(room.Id, "nested-parent", "A nested room needs an active instance of its immediate parent template."); continue; }
            var rule = definitions[room.DefinitionPath].Nesting;
            var subtree = Subtree(project, parent); var subtreeIds = subtree.Select(candidate => candidate.Id).ToHashSet(StringComparer.Ordinal);
            if (grown && (rule.Internal || observeAll))
            {
                var reserved = parent.ComplexReservation.ToHashSet();
                if (reserved.Count == 0 || room.Cells.Any(cell => !reserved.Contains(cell)))
                    yield return new(room.Id, "nested-containment", "The child floor must lie inside its parent's explicit room-complex allocation.");
                if (rooms.Where(candidate => !subtreeIds.Contains(candidate.Id)).SelectMany(candidate => candidate.Cells).Concat(project.Layout.PublicCorridors).Concat(project.Layout.Maintenance).Any(reserved.Contains))
                    yield return new(parent.Id, "complex-intrusion", "An unrelated room or station passage occupies the internal parent complex.");
            }
            if (!circulated || room.Cells.Count == 0) continue;
            if (rule.Internal || observeAll)
            {
                var childFloor = Subtree(project, room).SelectMany(child => child.Cells).ToHashSet();
                var boundary = project.Layout.Walls.Concat(project.Layout.Hull).Concat(project.Layout.Windows)
                    .Concat(DesignMeasurements.ValidInterfaces(project).Where(port => port.Kind != "utility").SelectMany(port => port.Cells)).ToHashSet();
                if (childFloor.Any(cell => Cell.Directions.Any(direction => !childFloor.Contains(cell + direction) && !boundary.Contains(cell + direction))))
                    yield return new(room.Id, "nested-enclosure", "An internal child must have an actual closed boundary of walls, windows and controlled interfaces; a planning allocation is insufficient.");
                // Child rooms may share the suite's outer hull. Actual floor and
                // internal seams must still attach them to the physical parent complex.
                if (!Reachable(project, ComplexCells(project, parent), parent.Cells, childFloor, false))
                    yield return new(room.Id, "nested-internal-placement", "The child must belong to the actual connected parent complex; an empty planning reservation does not connect separate rooms.");
            }
            if (rule.RequireParentConnection || observeAll)
            {
                var local = subtree.SelectMany(candidate => candidate.Cells).ToHashSet();
                local.UnionWith(DesignMeasurements.ValidInterfaces(project).Where(port => port.Kind == "walking" && subtreeIds.Contains(port.From) && subtreeIds.Contains(port.To)).SelectMany(port => port.Cells));
                local.IntersectWith(walking);
                if (!Reachable(project, local, parent.Cells, room.Cells.ToHashSet(), false))
                    yield return new(room.Id, "nested-parent-connection", "The child needs actual walking access from its parent within their room complex.");
            }
            if (!rule.AllowExternalEntry || observeAll)
            {
                var outside = rooms.Where(candidate => !subtreeIds.Contains(candidate.Id)).SelectMany(candidate => candidate.Cells).Concat(project.Layout.PublicCorridors).Concat(project.Layout.Maintenance);
                if (Reachable(project, walking.Except(parent.Cells).ToHashSet(), outside, room.Cells.ToHashSet(), true, crewJumps))
                    yield return new(room.Id, "nested-approach-bypass", "An outside station approach reaches this child without traversing its parent's floor.");
                if (project.Layout.Interfaces.Any(port => port.Kind == "walking" && (port.From == room.Id && !subtreeIds.Contains(port.To) || port.To == room.Id && !subtreeIds.Contains(port.From))))
                    yield return new(room.Id, "nested-external-entry", "This internal child forbids a direct entrance from outside its parent complex.");
            }
        }
    }
}
