namespace Content.StationArchitect;

/// <summary>Separates pressurized internal maintenance from exterior catwalk floor using actual full-width boundary airlocks.</summary>
public static class MaintenanceAirlockGates
{
    public static void Apply(StationDesignProject project, List<ProgramDiagnostic> diagnostics)
    {
        var floor = project.Layout.Maintenance.ToHashSet();
        var envelope = project.Layout.Envelope.ToHashSet();
        var existing = project.Layout.Interfaces.Where(port => port.Kind != "utility").SelectMany(port => port.Cells).ToHashSet();
        var groups = Crossings(floor, envelope).GroupBy(edge => (edge.Direction, Line: edge.Direction.X != 0 ? edge.Cell.X : edge.Cell.Y))
            .OrderBy(group => group.Key.Line).ThenBy(group => group.Key.Direction.Y).ThenBy(group => group.Key.Direction.X).ToList();
        foreach (var group in groups)
        {
            var direction = group.Key.Direction;
            var ordered = group.Select(edge => edge.Cell).Distinct().OrderBy(cell => direction.X != 0 ? cell.Y : cell.X).ToList();
            var start = 0;
            while (start < ordered.Count)
            {
                var end = start + 1;
                while (end < ordered.Count && ordered[end].Distance(ordered[end - 1]) == 1) end++;
                var cells = ordered.GetRange(start, end - start); start = end;
                if (cells.Count < project.Settings.MaintenanceWidth || cells.Count > project.Settings.MaintenanceMaxWidth) continue;
                if (cells.Any(existing.Contains) || !cells.All(cell => floor.Contains(cell) && envelope.Contains(cell) && floor.Contains(cell - direction) && envelope.Contains(cell - direction) && floor.Contains(cell + direction) && !envelope.Contains(cell + direction))) continue;
                var id = "maintenance-airlock:" + cells[0].X + "," + cells[0].Y + ":" + direction.X + "," + direction.Y;
                project.Layout.Interfaces.Add(new()
                {
                    Id = id, From = "$maintenance", To = "$exterior-maintenance", Kind = "walking", Type = "airlock", Access = "maintenance",
                    Width = cells.Count, Cells = cells, FromApproach = cells[0] - direction, ToApproach = cells[0] + direction
                });
                floor.ExceptWith(cells); existing.UnionWith(cells);
                project.Layout.Walls.RemoveAll(cells.Contains); project.Layout.Hull.RemoveAll(cells.Contains); project.Layout.Windows.RemoveAll(cells.Contains);
            }
        }
        project.Layout.Maintenance = floor.OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToList();
        var uncapped = Crossings(floor, envelope).ToList();
        if (uncapped.Count > 0)
            diagnostics.Add(new("layout.maintenance", "exterior-maintenance-gate", $"{uncapped.Count} interior/exterior maintenance floor edges remain open. A straight full-width airlock run could not be placed without changing the edited floor; adjust the crossing geometry."));
    }

    private static IEnumerable<(Cell Cell, Cell Direction)> Crossings(HashSet<Cell> floor, HashSet<Cell> envelope)
    {
        foreach (var cell in floor.Where(envelope.Contains).OrderBy(cell => cell.Y).ThenBy(cell => cell.X))
        foreach (var direction in Cell.Directions)
            if (floor.Contains(cell + direction) && !envelope.Contains(cell + direction)) yield return (cell, direction);
    }
}
