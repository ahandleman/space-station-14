using Robust.Shared.Prototypes;

namespace Content.Shared.Botany.Components;

/// <summary>
/// Random weighting dataset for solutions, able to specify reagents quantity.
/// </summary>
[Prototype]
public sealed partial class RandomPlantMutationListPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    /// Mutation prototype IDs and their odds per point of mutation severity.
    /// </summary>
    [DataField(required: true)]
    public Dictionary<ProtoId<PlantMutationPrototype>, float> Mutations = [];
}
