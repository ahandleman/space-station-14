using Content.Shared.Botany.Components;
using Content.Shared.Botany.Systems;
using Robust.Shared.Prototypes;

namespace Content.Shared.EntityEffects.Effects.Botany.PlantAttributes;

/// <summary>
/// Entity effect that adds severity to a plant's mutation table for its next mutation check.
/// </summary>
/// <inheritdoc cref="EntityEffectSystem{T,TEffect}"/>
public sealed partial class PlantMutateEntityEffectSystem : EntityEffectSystem<PlantComponent, PlantMutate>
{
    [Dependency] private PlantMutationSystem _mutation = default!;

    private EntityQuery<PlantHolderComponent> _plantHolderQuery;

    public override void Initialize()
    {
        base.Initialize();
        _plantHolderQuery = GetEntityQuery<PlantHolderComponent>();
    }

    protected override void Effect(Entity<PlantComponent> entity, ref EntityEffectEvent<PlantMutate> args)
    {
        if (!_plantHolderQuery.TryComp(entity.Owner, out var plantHolder) || plantHolder.Dead)
            return;

        var severity = args.Effect.IgnoreMutationMod
            ? args.Effect.Amount
            : args.Effect.Amount * plantHolder.MutationMod;

        _mutation.AccumulateMutationSeverity(entity.Owner, severity, args.Effect.MutationTable);
    }
}

/// <inheritdoc cref="EntityEffect"/>
public sealed partial class PlantMutate : BasePlantAdjustAttribute<PlantMutate>
{
    public override string GuidebookAttributeName { get; set; } = "plant-attribute-mutation-level";

    /// <summary>
    /// The table of mutations this chemical can cause.
    /// </summary>
    [DataField(required: true)]
    public ProtoId<RandomPlantMutationListPrototype> MutationTable;

    /// <summary>
    /// Whether this chemical's mutation amount is unaffected by the plant's mutation modifier.
    /// </summary>
    [DataField]
    public bool IgnoreMutationMod;
}
