namespace Content.StationArchitect;

/// <summary>Shared planning-profile geometry. Authored profiles are not proof of runtime prototype collider correctness.</summary>
public static class DesignEquipmentGeometry
{
    public static List<Cell> ExpectedFootprint(DesignSettings settings, string prototype, Cell position, int rotation)
    {
        if (!TryExpectedFootprint(settings, prototype, position, rotation, out var cells))
            throw new ArgumentException("Equipment profile dimensions, offsets or cardinal rotation are invalid.");
        return cells;
    }

    public static bool TryExpectedFootprint(DesignSettings settings, string prototype, Cell position, int rotation, out List<Cell> cells)
    {
        cells = [];
        if (settings?.PrototypeFootprints == null || prototype == null || rotation % 90 != 0) return false;
        var profile = settings.PrototypeFootprints.GetValueOrDefault(prototype) ?? new EntityFootprintSize();
        if (profile.Width is < 1 or > 16 || profile.Height is < 1 or > 16 || profile.OffsetX is < -16 or > 16 || profile.OffsetY is < -16 or > 16) return false;
        var quarter = ((rotation / 90) % 4 + 4) % 4;
        for (var y = 0; y < profile.Height; y++) for (var x = 0; x < profile.Width; x++)
        {
            var local = new Cell(profile.OffsetX + x, profile.OffsetY + y);
            var rotated = quarter switch { 1 => new Cell(-local.Y, local.X), 2 => new Cell(-local.X, -local.Y), 3 => new Cell(local.Y, -local.X), _ => local };
            var absoluteX = (long) position.X + rotated.X; var absoluteY = (long) position.Y + rotated.Y;
            if (Math.Abs(absoluteX) > 4096 || Math.Abs(absoluteY) > 4096) { cells = []; return false; }
            cells.Add(new((int) absoluteX, (int) absoluteY));
        }
        cells = cells.OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToList();
        return true;
    }
}
