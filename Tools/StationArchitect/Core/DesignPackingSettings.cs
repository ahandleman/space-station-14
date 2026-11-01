namespace Content.StationArchitect;

/// <summary>Spatial planning controls, independent of department or room identifiers.</summary>
public sealed class DesignPackingSettings
{
    public double Compactness { get; set; } = 1;
    public double InternalVariation { get; set; } = .65;
    public double SilhouetteVariation { get; set; } = .6;
    public double Interlock { get; set; } = .8;
    public double DepartmentBundling { get; set; } = .85;
    public double PreferredAspectRatio { get; set; } = 1.15;
    public int DistributedClusters { get; set; } = 3;
    public int MaxRoomsPerCluster { get; set; } = 8;
    public int StationwideSpacing { get; set; } = 8;
    public int BoundaryGrowthBudget { get; set; } = 256;
    public double MaintenanceCoverage { get; set; } = .75;
    public int MaintenanceWrapDistance { get; set; } = 2;
    public int MaintenanceConnectionsPerCluster { get; set; } = 2;
}
