namespace Content.StationArchitect;

public static partial class DesignPlacement
{
    private static List<(Cell Anchor, Cell Outward)> PlanningPublicFaces(StationDesignProject project, List<OwnerPlan> group,
        Dictionary<string, RoomDefinition> definitions)
    {
        var masks = new List<(DesignRoom Room, HashSet<Cell> Floor)>(); var offset = 0;
        foreach (var plan in group)
        {
            Cell Transform(Cell cell)
            {
                var width = plan.OriginalWidth; var height = plan.OriginalHeight;
                for (var step = 0; step < plan.Rotation; step++) { cell = new(height - 1 - cell.Y, cell.X); (width, height) = (height, width); }
                return cell + new Cell(offset, 0);
            }
            foreach (var slot in plan.Slots) masks.Add((slot.Room, slot.Shape.Select(Transform).ToHashSet()));
            offset += plan.Width + 1;
        }
        var all = masks.SelectMany(mask => mask.Floor).ToHashSet(); var structure = Halo(all); var bounds = LayoutGeometry.Bounds(all);
        var widthRequired = project.Settings.PublicWidth; var result = new List<(Cell, Cell)>();
        foreach (var (room, floor) in masks.Where(mask => definitions[mask.Room.DefinitionPath].Access.AllowPublicEntry))
        foreach (var normal in Cell.Directions)
        {
            var tangent = new Cell(-normal.Y, normal.X);
            foreach (var edge in floor)
            {
                var boundary = Enumerable.Range(0, widthRequired).Select(index => edge + normal + new Cell(tangent.X * index, tangent.Y * index)).ToList();
                if (!boundary.All(cell => floor.Contains(cell - normal) && !all.Contains(cell))) continue;
                var distance = normal.X > 0 ? bounds.Right - edge.X : normal.X < 0 ? edge.X - bounds.X + 1 : normal.Y > 0 ? bounds.Bottom - edge.Y : edge.Y - bounds.Y + 1;
                var approach = Enumerable.Range(1, Math.Max(widthRequired, distance + 1)).SelectMany(depth => boundary.Select(cell => cell + new Cell(normal.X * depth, normal.Y * depth))).ToList();
                if (approach.Any(structure.Contains)) continue;
                result.Add((LayoutGeometry.Bounds(boundary.SelectMany(cell => Enumerable.Range(1, widthRequired).Select(depth => cell + new Cell(normal.X * depth, normal.Y * depth)))).Center, normal));
            }
        }
        return result;
    }

    private static double PublicFacingCost(List<(Cell Anchor, Cell Outward)> faces, Cell origin, Cell core, int scale)
    {
        if (faces.Count == 0) return 0;
        var toward = faces.Where(face =>
        {
            var delta = core - (face.Anchor + origin);
            return delta.X * face.Outward.X + delta.Y * face.Outward.Y > 0;
        }).ToList();
        var distance = (toward.Count > 0 ? toward : faces).Min(face => (face.Anchor + origin).Distance(core));
        return distance * 15 + (toward.Count == 0 ? Math.Max(1, scale) * 4 : 0);
    }
}
