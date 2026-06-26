using System.Threading;
using Content.Server.Botany.Systems;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;



namespace Content.Server.Botany.Components;

/// <summary>
/// After scanning, retrieves the target Uid to use with its related UI.
/// </summary>
/// <remarks>
/// Requires <c>ItemToggleComponent</c>.
/// </remarks>
[RegisterComponent, AutoGenerateComponentPause]
[Access(typeof(PlantAnalyzerSystem))]

public sealed partial class PlantAnalyzerComponent : Component
{
    /// <summary>
    /// When should the next update be sent for the plant
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    [AutoPausedField]
    public TimeSpan NextUpdate = TimeSpan.Zero;

    /// <summary>
    /// The delay between plant data updates
    /// </summary>
    [DataField]
    public TimeSpan UpdateInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// If the last state of the plant analyzer was active (e.g. they are in range of the plant).
    /// </summary>
    [DataField]
    public bool IsAnalyzerActive = false;

    /// <summary>
    /// How long it takes to scan a plant.
    /// </summary>
    [DataField]
    public TimeSpan ScanDelay = TimeSpan.FromSeconds(0.8);

    /// <summary>
    /// Which entity has been scanned, for continuous updates
    /// </summary>
    [DataField]
    public EntityUid? ScannedEntity;

    /// <summary>
    /// The maximum range in tiles at which the analyzer can receive continuous updates, a value of null will be infinite range
    /// </summary>
    [DataField]
    public float? MaxScanRange = 5f;
}

