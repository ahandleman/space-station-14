// Content.Shared/Botany/PlantAnalyzer/PlantAnalyzerDoAfterEvent.cs

using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Robust.Shared.Serialization;
namespace Content.Shared.Botany.PlantAnalyzer;

[Serializable, NetSerializable]
public sealed partial class PlantAnalyzerDoAfterEvent : SimpleDoAfterEvent
{
}

[Serializable, NetSerializable]
public enum PlantAnalyzerUiKey : byte
{
    Key
}

[Serializable, NetSerializable]
public sealed class PlantAnalyzerChemicalEntry
{
    public string Reagent = string.Empty;
    public FixedPoint2 Min;
    public FixedPoint2 Max;
    public FixedPoint2 PotencyDivisor;
    public FixedPoint2 CurrentAmount;
}

[Serializable, NetSerializable]
public sealed class PlantAnalyzerScannedPlantMessage : BoundUserInterfaceMessage
{
    public readonly PlantAnalyzerUiState State;
    public List<PlantAnalyzerChemicalEntry> Chemicals = new();
    public PlantAnalyzerScannedPlantMessage(PlantAnalyzerUiState state)
    {
        State = state;
    }
}

[Serializable, NetSerializable]
public sealed class PlantAnalyzerUiState : BoundUserInterfaceState
{
    public bool ScanMode;
    public bool HasPlant;

    public string PlantName = string.Empty;
    public string PlantSpriteRsi = string.Empty;
    public string PlantSpriteState = string.Empty;
    public int GrowthStage;
    public float WaterLevel;
    public float NutritionLevel;
    public float PestLevel;
    public float WeedLevel;
    public float Toxins;
    public int Age;
    public bool Dead;
    public bool Harvest;
    public float Health;

    public bool MultiHarvest;
    public bool AutoHarvest;

    public int LastProduce;
    public int YieldMod;
    public float MutationMod;
    public float MutationLevel;

    public bool ImproperHeat;
    public bool ImproperPressure;
    public bool ImproperLight;
    public int MissingGas;

    public float Endurance;
    public int Yield;
    public float Lifespan;
    public float Maturation;
    public float Production;
    public float Potency;

    public float NutrientConsumption;
    public float WaterConsumption;

    public float IdealHeat;
    public float HeatTolerance;
    public float IdealLight;
    public float LightTolerance;
    public float LowPressureTolerance;
    public float HighPressureTolerance;
    public float ToxinsTolerance;
    public float PestTolerance;
    public float WeedTolerance;

    public bool Seedless;
    public bool Ligneous;
    public bool TurnIntoKudzu;
    public bool CanScream;
    public bool Viable = true;

    public List<string> Mutations = new();

    public List<PlantAnalyzerChemicalEntry> Chemicals = new();

}
