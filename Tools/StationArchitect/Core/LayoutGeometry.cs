namespace Content.StationArchitect;

/// <summary>Geometry shared by the room planners. Blank cells are space, never implicit maintenance.</summary>
public static class LayoutGeometry
{
    public static List<Cell> Sorted(IEnumerable<Cell> cells) => cells.Distinct().OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToList();

    public static GridBounds Bounds(IEnumerable<Cell> cells)
    {
        var list = cells.ToList();
        if (list.Count == 0) return new();
        var x = list.Min(cell => cell.X); var y = list.Min(cell => cell.Y);
        return new(x, y, list.Max(cell => cell.X) - x + 1, list.Max(cell => cell.Y) - y + 1);
    }

    public static bool Connected(IEnumerable<Cell> cells)
    {
        var remaining = cells.ToHashSet();
        if (remaining.Count == 0) return true;
        var queue = new Queue<Cell>(); queue.Enqueue(remaining.First()); remaining.Remove(queue.Peek());
        while (queue.TryDequeue(out var cell))
            foreach (var direction in Cell.Directions)
                if (remaining.Remove(cell + direction)) queue.Enqueue(cell + direction);
        return remaining.Count == 0;
    }

    public static int Components(IEnumerable<Cell> cells)
    {
        var remaining = cells.ToHashSet(); var count = 0;
        while (remaining.Count > 0)
        {
            count++;
            var queue = new Queue<Cell>(); queue.Enqueue(remaining.First()); remaining.Remove(queue.Peek());
            while (queue.TryDequeue(out var cell))
                foreach (var direction in Cell.Directions)
                    if (remaining.Remove(cell + direction)) queue.Enqueue(cell + direction);
        }
        return count;
    }

    public static bool ShapeFits(IEnumerable<Cell> cells, ShapeRequirement shape)
    {
        var set = cells.ToHashSet(); if (set.Count == 0) return false;
        var bounds = Bounds(set);
        return Math.Max((double) bounds.Width / bounds.Height, (double) bounds.Height / bounds.Width) <= shape.MaxAspectRatio + 1e-9 &&
               (double) set.Count / bounds.Area + 1e-9 >= shape.MinFillRatio;
    }

    public static int StableOrder(string value, int seed = 0)
    {
        unchecked
        {
            uint hash = (uint) seed ^ 2166136261;
            foreach (var character in value) hash = (hash ^ character) * 16777619;
            return (int) (hash & 0x7fffffff);
        }
    }

    public static Dictionary<string, RoomDefinition> Definitions(StationDesignProject project) =>
        RequirementCompiler.Rooms(project.Definition).ToDictionary(item => item.Path, item => item.Room, StringComparer.Ordinal);

    /// <summary>Observed complex geometry: exclusive subtree floor and real separating seams.</summary>
    public static HashSet<Cell> ComplexFloor(StationDesignProject project, DesignRoom parent, IReadOnlyDictionary<string, HashSet<Cell>>? replacements = null)
    {
        var rooms = RoomHierarchy.Descendants(project, parent, true).Where(room => room.Enabled && !room.Orphaned);
        var floor = rooms.SelectMany(room => replacements != null && replacements.TryGetValue(room.Id, out var replacement) ? replacement : (IEnumerable<Cell>)room.Cells).ToHashSet();
        var seams = floor.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Where(cell => !floor.Contains(cell) &&
            (floor.Contains(cell + new Cell(1, 0)) && floor.Contains(cell - new Cell(1, 0)) || floor.Contains(cell + new Cell(0, 1)) && floor.Contains(cell - new Cell(0, 1)))).ToList();
        floor.UnionWith(seams); return floor;
    }

    public static void RefreshBounds(StationDesignProject project)
    {
        foreach (var room in project.Layout.Rooms) room.Bounds = Bounds(room.Cells);
        foreach (var owner in project.Layout.Departments)
        {
            owner.Cells = Sorted(project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && room.OwnerId == owner.OwnerId).SelectMany(room => room.Cells));
            if (owner.Cells.Count > 0) owner.Bounds = Bounds(owner.Cells);
        }
    }
}
