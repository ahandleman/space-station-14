using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Client.Guidebook.Richtext;
using Content.Shared.Botany.Components;
using Content.Shared.EntityEffects;
using Content.Shared.EntityEffects.Effects;
using Content.Shared.EntityEffects.Effects.Botany;
using Content.Shared.EntityEffects.Effects.Botany.PlantAttributes;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client.Guidebook.Controls;

/// <summary>
/// Displays all plant mutation tables, or a single table selected by the Table argument.
/// </summary>
[UsedImplicitly]
public sealed partial class GuidePlantMutationTablesEmbed : BoxContainer, IDocumentTag
{
    [Dependency] private IPrototypeManager _prototype = default!;
    [Dependency] private IEntitySystemManager _systems = default!;

    public GuidePlantMutationTablesEmbed()
    {
        IoCManager.InjectDependencies(this);
        Orientation = LayoutOrientation.Vertical;
        HorizontalExpand = true;
    }

    public bool TryParseTag(Dictionary<string, string> args, [NotNullWhen(true)] out Control? control)
    {
        control = null;
        if (args.TryGetValue("Table", out var id) && !_prototype.HasIndex<RandomPlantMutationListPrototype>(id))
            return false;

        foreach (var table in _prototype.EnumeratePrototypes<RandomPlantMutationListPrototype>()
                     .Where(p => id == null || p.ID == id).OrderBy(p => GuidePlantReferenceTable.Name(p.ID)))
        {
            var view = new GuidePlantReferenceTable(GuidePlantReferenceTable.Name(table.ID),
                Loc.GetString(table.IgnoreMutationMod ? "guidebook-plant-table-ignores-modifier" : "guidebook-plant-table-uses-modifier"),
                Loc.GetString("guidebook-plant-table-mutation"), Loc.GetString("guidebook-plant-table-base-chance"),
                Loc.GetString("guidebook-plant-table-effect"));
            foreach (var mutation in table.Mutations)
            {
                view.AddCell(GuidePlantReferenceTable.Text(FormattedMessage.EscapeText(GuidePlantReferenceTable.Name(mutation.Name))));
                view.AddCell(GuidePlantReferenceTable.Text(GuidePlantReferenceTable.Percent(mutation.BaseOdds)));
                view.AddCell(GuidePlantReferenceTable.Text(Describe(mutation.Effect)));
            }
            AddChild(view);
        }
        control = this;
        return true;
    }

    private string Describe(EntityEffect effect)
    {
        return effect switch
        {
            PlantChangeStat stat => Loc.GetString("guidebook-plant-table-stat-change",
                ("min", stat.ApplyRange.Min), ("max", stat.ApplyRange.Max),
                ("up", Describe(stat.Up)), ("down", Describe(stat.Down))),
            PlantMutateChemicals => Loc.GetString("guidebook-plant-table-chemical-change"),
            PlantMutateSpeciesChange => Loc.GetString("guidebook-plant-table-species-change"),
            PlantMutateHarvest => Loc.GetString("guidebook-plant-table-harvest-change"),
            Slipify => Loc.GetString("guidebook-plant-table-slippery-effect"),
            _ => effect.EntityEffectGuidebookText(_prototype, _systems)
                ?? FormattedMessage.EscapeText(GuidePlantReferenceTable.Name(effect.GetType().Name)),
        };
    }
}
