using Content.Shared.EntityEffects;
using Robust.Shared.Prototypes;

namespace Content.Shared.Botany.Components;

/// <summary>
/// Defines a named plant mutation that can be shared by multiple mutation tables.
/// </summary>
[Prototype]
public sealed partial class PlantMutationPrototype : IPrototype
{
    /// <summary>
    /// The unique ID of this mutation.
    /// </summary>
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    /// The text to display to players when examining something with this mutation.
    /// </summary>
    [DataField]
    public LocId? Description;

    /// <summary>
    /// The actual EntityEffect to apply to the target
    /// </summary>
    [DataField(required: true)]
    public EntityEffect Effect;

    /// <summary>
    /// This mutation will target the harvested produce
    /// </summary>
    [DataField]
    public bool AppliesToProduce = true;

    /// <summary>
    /// This mutation will target the growing plant as soon as this mutation is applied.
    /// </summary>
    [DataField]
    public bool AppliesToPlant = true;

    /// <summary>
    /// This mutation stays on the plant and its produce. If false while AppliesToPlant is true, the effect will run when triggered.
    /// </summary>
    [DataField]
    public bool Persists = true;
}
