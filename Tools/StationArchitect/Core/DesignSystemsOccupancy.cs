namespace Content.StationArchitect;

public static partial class DesignSystems
{
    public static bool BlocksCrew(StationDesignProject project, DesignEntity entity) => entity.Placement == "floor" &&
        project.Definition.Systems.Profiles.FirstOrDefault(profile => profile.Prototype == entity.Prototype)?.BlocksCrew != false;

    public static string OccupancyLayer(StationDesignProject project, DesignEntity entity)
    {
        var layer = project.Definition.Systems.Profiles.FirstOrDefault(profile => profile.Prototype == entity.Prototype)?.OccupancyLayer;
        return layer is null or "auto" ? entity.Placement == "floor" ? "floor" : "boundary" : layer;
    }

    public static bool CanShareCell(StationDesignProject project, DesignEntity first, DesignEntity second) =>
        OccupancyLayer(project, first) != OccupancyLayer(project, second) && !(BlocksCrew(project, first) && BlocksCrew(project, second));

    public static int MinimumWorkArea(StationDesignProject project, DesignEntity entity, int authoredMinimum) =>
        BlocksCrew(project, entity) ? Math.Max(authoredMinimum, entity.Footprint.Count) : authoredMinimum;
}
