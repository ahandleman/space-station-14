namespace Content.StationArchitect;

/// <summary>Fresh physical crew shortcuts: configured passenger tubes are reciprocal; native disposal delivery is directional.</summary>
public static class DesignCrewTraversal
{
    public static Dictionary<Cell, List<Cell>> Jumps(StationDesignProject project)
    {
        return DesignTransport.CrewEdges(project).SelectMany(edge => new[] { (edge.From, edge.To), (From: edge.To, To: edge.From) })
            .Concat(DesignSystems.NativeCrewEdges(project)).GroupBy(edge => edge.From)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.To).Distinct().ToList());
    }
}
