namespace Content.StationGeneration;

internal static class GridRouter
{
    private readonly record struct State(Cell Cell, int Direction);

    // Admissible heuristic: minimum per-step cost is 6. Direction belongs to the state,
    // so turn penalties do not invalidate a cheaper arrival with a different heading.
    public static List<Cell>? Find(StationGenerationConfig c, Cell start, Cell target,
        HashSet<Cell> forbidden, HashSet<Cell> existing, int width, bool reuse)
    {
        var frontier = new PriorityQueue<State, (int Cost, int Sequence)>();
        var costs = new Dictionary<State, int>();
        var parents = new Dictionary<State, State>();
        var legal = new Dictionary<Cell, bool>();
        var sequence = 0;
        var first = new State(start, 4);
        if (!Legal(start) || !Legal(target)) return null;
        frontier.Enqueue(first, (0, sequence++));
        costs[first] = 0;
        var expansions = 0;
        while (frontier.TryDequeue(out var current, out _) && expansions++ < c.Width * c.Height * 6)
        {
            if (current.Cell == target)
            {
                var path = new List<Cell> { current.Cell };
                while (parents.TryGetValue(current, out var previous))
                {
                    current = previous;
                    path.Add(current.Cell);
                }
                path.Reverse();
                return path;
            }
            for (var dir = 0; dir < 4; dir++)
            {
                var next = new State(current.Cell + Cell.Directions[dir], dir);
                if (!Legal(next.Cell)) continue;
                var step = existing.Contains(next.Cell) ? (reuse ? 6 : 15) : 10;
                var cost = costs[current] + step + (current.Direction != 4 && current.Direction != dir ? 4 : 0);
                if (costs.TryGetValue(next, out var old) && old <= cost) continue;
                costs[next] = cost;
                parents[next] = current;
                frontier.Enqueue(next, (cost + 6 * next.Cell.Distance(target), sequence++));
            }
        }
        return null;

        bool Legal(Cell p)
        {
            if (!legal.TryGetValue(p, out var value))
                legal[p] = value = StationGenerator.Brush(p, width).All(q => StationGenerator.Inside(c, q, c.HullThickness + 2) && !forbidden.Contains(q));
            return value;
        }
    }

    public static List<Cell>? FindToAny(StationGenerationConfig c, Cell start, HashSet<Cell> targets, HashSet<Cell> forbidden)
    {
        var queue = new Queue<Cell>();
        var parents = new Dictionary<Cell, Cell>();
        var visited = new HashSet<Cell> { start };
        queue.Enqueue(start);
        while (queue.TryDequeue(out var current))
        {
            if (targets.Contains(current))
            {
                var result = new List<Cell> { current };
                while (parents.TryGetValue(current, out var previous))
                {
                    current = previous;
                    result.Add(current);
                }
                result.Reverse();
                return result;
            }
            foreach (var direction in Cell.Directions)
            {
                var next = current + direction;
                if (!StationGenerator.Inside(c, next, c.HullThickness + 1) || forbidden.Contains(next) || !visited.Add(next)) continue;
                parents[next] = current;
                queue.Enqueue(next);
            }
        }
        return null;
    }

    public static int Turns(List<Cell> path)
    {
        var turns = 0;
        for (var i = 2; i < path.Count; i++)
            if (new Cell(path[i].X - path[i - 1].X, path[i].Y - path[i - 1].Y) !=
                new Cell(path[i - 1].X - path[i - 2].X, path[i - 1].Y - path[i - 2].Y)) turns++;
        return turns;
    }
}
