using Content.Server.Botany.Components;
using Content.Shared.Botany;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Item.ItemToggle.Components;
using Content.Shared.Popups;
using Content.Shared.PowerCell;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Timing;
using Content.Shared.Botany.PlantAnalyzer;
using Content.Shared.FixedPoint;

namespace Content.Server.Botany.Systems;

public sealed class PlantAnalyzerSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly ItemToggleSystem _toggle = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly PowerCellSystem _cell = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<PlantAnalyzerComponent, AfterInteractEvent>(OnAfterInteract);
        SubscribeLocalEvent<PlantAnalyzerComponent, PlantAnalyzerDoAfterEvent>(OnDoAfter);
        SubscribeLocalEvent<PlantAnalyzerComponent, EntGotInsertedIntoContainerMessage>(OnInsertedIntoContainer);
        SubscribeLocalEvent<PlantAnalyzerComponent, ItemToggledEvent>(OnToggled);
        SubscribeLocalEvent<PlantAnalyzerComponent, DroppedEvent>(OnDropped);
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<PlantAnalyzerComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var component, out var xform))
        {
            if (component.NextUpdate > _timing.CurTime)
                continue;

            if (component.ScannedEntity is not { } plant)
                continue;

            if (Deleted(plant) || !HasComp<PlantHolderComponent>(plant))
            {
                StopAnalyzingEntity((uid, component), plant);
                continue;
            }

            component.NextUpdate = _timing.CurTime + component.UpdateInterval;

            // Null range means infinite range.
            if (component.MaxScanRange != null)
            {
                var plantCoords = Transform(plant).Coordinates;

                if (!_transform.InRange(plantCoords, xform.Coordinates, component.MaxScanRange.Value))
                {
                    PauseAnalyzingEntity((uid, component), plant);
                    continue;
                }
            }

            component.IsAnalyzerActive = true;
            UpdateScannedPlant(uid, plant, true);
        }
    }

    private void OnAfterInteract(Entity<PlantAnalyzerComponent> ent, ref AfterInteractEvent args)
    {
        if (args.Target is not { } target || !args.CanReach)
            return;

        if (!TryComp<PlantHolderComponent>(target, out var plantHolder) || plantHolder.Seed == null)
        {
            _popup.PopupEntity(
                Loc.GetString("plant-analyzer-popup-no-plant"),
                args.User,
                args.User,
                PopupType.Small);

            return;
        }

        // Keep this only if your prototype uses PowerCellDraw / power-cell behavior.
        if (!_cell.HasDrawCharge(ent.Owner, user: args.User))
            return;

        var doAfterArgs = new DoAfterArgs(
            EntityManager,
            args.User,
            ent.Comp.ScanDelay,
            new PlantAnalyzerDoAfterEvent(),
            ent.Owner,
            target: target,
            used: ent.Owner)
        {
            NeedHand = true,
            BreakOnMove = true,
        };

        if (!_doAfter.TryStartDoAfter(doAfterArgs))
            return;

        args.Handled = true;
    }

    private void OnDoAfter(Entity<PlantAnalyzerComponent> ent, ref PlantAnalyzerDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || args.Target is not { } target)
            return;

        if (!_cell.HasDrawCharge(ent.Owner, user: args.User))
            return;

        if (!TryComp<PlantHolderComponent>(target, out var plantHolder) || plantHolder.Seed == null)
            return;

        OpenUserInterface(args.User, ent.Owner);
        BeginAnalyzingEntity(ent, target);

        args.Handled = true;
    }

    private void OnInsertedIntoContainer(Entity<PlantAnalyzerComponent> ent, ref EntGotInsertedIntoContainerMessage args)
    {
        if (ent.Comp.ScannedEntity is not null)
            _toggle.TryDeactivate(ent.Owner);
    }

    private void OnToggled(Entity<PlantAnalyzerComponent> ent, ref ItemToggledEvent args)
    {
        if (!args.Activated && ent.Comp.ScannedEntity is { } plant)
            StopAnalyzingEntity(ent, plant);
    }

    private void OnDropped(Entity<PlantAnalyzerComponent> ent, ref DroppedEvent args)
    {
        if (ent.Comp.ScannedEntity is not null)
            _toggle.TryDeactivate(ent.Owner);
    }

    private void OpenUserInterface(EntityUid user, EntityUid analyzer)
    {
        if (!_ui.HasUi(analyzer, PlantAnalyzerUiKey.Key))
            return;

        _ui.OpenUi(analyzer, PlantAnalyzerUiKey.Key, user);
    }

    private void BeginAnalyzingEntity(Entity<PlantAnalyzerComponent> analyzer, EntityUid plant)
    {
        analyzer.Comp.ScannedEntity = plant;
        analyzer.Comp.IsAnalyzerActive = true;

        _toggle.TryActivate(analyzer.Owner);
        UpdateScannedPlant(analyzer.Owner, plant, true);
    }

    private void StopAnalyzingEntity(Entity<PlantAnalyzerComponent> analyzer, EntityUid plant)
    {
        analyzer.Comp.ScannedEntity = null;
        analyzer.Comp.IsAnalyzerActive = false;

        _toggle.TryDeactivate(analyzer.Owner);
        UpdateScannedPlant(analyzer.Owner, plant, false);
    }

    private void PauseAnalyzingEntity(Entity<PlantAnalyzerComponent> analyzer, EntityUid plant)
    {
        if (!analyzer.Comp.IsAnalyzerActive)
            return;

        analyzer.Comp.IsAnalyzerActive = false;
        UpdateScannedPlant(analyzer.Owner, plant, false);
    }

    private void UpdateScannedPlant(EntityUid analyzer, EntityUid plant, bool scanMode)
    {
        if (!_ui.HasUi(analyzer, PlantAnalyzerUiKey.Key))
            return;

        var state = GetPlantAnalyzerUiState(plant);
        state.ScanMode = scanMode;

        _ui.ServerSendUiMessage(
            analyzer,
            PlantAnalyzerUiKey.Key,
            new PlantAnalyzerScannedPlantMessage(state));
    }

    public PlantAnalyzerUiState GetPlantAnalyzerUiState(EntityUid? target)
    {
        if (target == null || !TryComp<PlantHolderComponent>(target.Value, out var holder))
            return new PlantAnalyzerUiState();

        var state = new PlantAnalyzerUiState
        {
            HasPlant = holder.Seed != null,

            WaterLevel = holder.WaterLevel,
            NutritionLevel = holder.NutritionLevel,
            PestLevel = holder.PestLevel,
            WeedLevel = holder.WeedLevel,
            Toxins = holder.Toxins,

            Age = holder.Age,
            Dead = holder.Dead,
            Harvest = holder.Harvest,
            Health = holder.Health,

            YieldMod = holder.YieldMod,
            MutationMod = holder.MutationMod,
            MutationLevel = holder.MutationLevel,

            ImproperHeat = holder.ImproperHeat,
            ImproperPressure = holder.ImproperPressure,
            ImproperLight = holder.ImproperLight,
            MissingGas = holder.MissingGas,
        };

        var seed = holder.Seed;
        if (seed == null)
            return state;

        foreach (var chemical in seed.Chemicals)
        {
            var reagentId = chemical.Key;
            var quantity = chemical.Value;

            var current = quantity.Min;

            if (quantity.PotencyDivisor != 0)
                current += seed.Potency / quantity.PotencyDivisor;

            current = FixedPoint2.Min(current, quantity.Max);

            state.Chemicals.Add(new PlantAnalyzerChemicalEntry
            {
                Reagent = reagentId,
                Min = quantity.Min,
                Max = quantity.Max,
                PotencyDivisor = quantity.PotencyDivisor,
                CurrentAmount = current,
            });
        }
        state.PlantName = Loc.GetString(seed.DisplayName);

        state.Endurance = seed.Endurance;
        state.Yield = seed.Yield;
        state.Lifespan = seed.Lifespan;
        state.Maturation = seed.Maturation;
        state.Production = seed.Production;
        state.Potency = seed.Potency;
        state.PlantSpriteRsi = seed.PlantRsi.ToString();
        state.PlantSpriteState = GetPlantSpriteState(holder);
        state.GrowthStage = GetCurrentGrowthStage(holder);
        //Shitcode but unless I want to shove the enum into shared and refactor all that, this will work
        state.MultiHarvest = seed.HarvestRepeat != HarvestType.NoRepeat;
        state.AutoHarvest = seed.HarvestRepeat == HarvestType.SelfHarvest;

        state.LastProduce = holder.LastProduce;

        state.NutrientConsumption = seed.NutrientConsumption;
        state.WaterConsumption = seed.WaterConsumption;

        state.IdealHeat = seed.IdealHeat;
        state.HeatTolerance = seed.HeatTolerance;
        state.IdealLight = seed.IdealLight;
        state.LightTolerance = seed.LightTolerance;
        state.LowPressureTolerance = seed.LowPressureTolerance;
        state.HighPressureTolerance = seed.HighPressureTolerance;
        state.ToxinsTolerance = seed.ToxinsTolerance;
        state.PestTolerance = seed.PestTolerance;
        state.WeedTolerance = seed.WeedTolerance;

        state.Seedless = seed.Seedless;
        state.Ligneous = seed.Ligneous;
        state.TurnIntoKudzu = seed.TurnIntoKudzu;
        state.CanScream = seed.CanScream;
        state.Viable = seed.Viable;

        foreach (var mutation in seed.Mutations)
            state.Mutations.Add(mutation.Name);

        return state;
    }
    private static int GetCurrentGrowthStage(PlantHolderComponent holder)
    {
        if (holder.Seed == null)
            return 0;

        return Math.Max(1, (int) (Math.Min(holder.Age, holder.Seed.Maturation) * holder.Seed.GrowthStages / holder.Seed.Maturation));
    }

    private static string GetPlantSpriteState(PlantHolderComponent holder)
    {
        var seed = holder.Seed;

        if (seed == null)
            return string.Empty;

        if (holder.Dead)
            return "dead";

        if (holder.Harvest)
            return "harvest";

        if (holder.Age < seed.Maturation)
            return $"stage-{GetCurrentGrowthStage(holder)}";

        return $"stage-{seed.GrowthStages}";
    }
}
