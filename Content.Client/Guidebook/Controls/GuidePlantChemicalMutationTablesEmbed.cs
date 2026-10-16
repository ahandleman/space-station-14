using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Client.Guidebook.Richtext;
using Content.Shared.Botany.Components;
using Content.Shared.EntityEffects.Effects.Botany;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Prototypes;

namespace Content.Client.Guidebook.Controls;

/// <summary>
/// Follows chemical mutations' pool references, including their combined selection weights.
/// </summary>
[UsedImplicitly]
public sealed partial class GuidePlantChemicalMutationTablesEmbed : BoxContainer, IDocumentTag
{
    [Dependency] private IPrototypeManager _prototype = default!;

    public GuidePlantChemicalMutationTablesEmbed()
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
            foreach (var mutation in table.Mutations)
            {
                if (mutation.Effect is not PlantMutateChemicals effect)
                    continue;

                var pools = effect.RandomChemTables.Select(p => _prototype.Index(p)).ToArray();
                var totalWeight = pools.Sum(p => p.Fills.Sum(f => f.Weight));
                foreach (var pool in pools)
                {
                    var title = Loc.GetString("guidebook-plant-table-pool-title",
                        ("table", GuidePlantReferenceTable.Name(table.ID)), ("pool", GuidePlantReferenceTable.Name(pool.ID)));
                    var view = new GuidePlantReferenceTable(title,
                        Loc.GetString("guidebook-plant-table-pool-note", ("chance", GuidePlantReferenceTable.Percent(mutation.BaseOdds))),
                        Loc.GetString("guidebook-plant-table-weight"), Loc.GetString("guidebook-plant-table-chance"),
                        Loc.GetString("guidebook-plant-table-reagents"));
                    foreach (var fill in pool.Fills)
                    {
                        view.AddCell(GuidePlantReferenceTable.Text(fill.Weight.ToString("0.###")));
                        view.AddCell(GuidePlantReferenceTable.Text(GuidePlantReferenceTable.Percent(totalWeight > 0 ? fill.Weight / totalWeight : 0)));
                        var reagents = new BoxContainer { Orientation = LayoutOrientation.Vertical, HorizontalExpand = true };
                        foreach (var reagent in fill.Reagents.Select(r => _prototype.Index(r)).OrderBy(r => r.LocalizedName))
                            reagents.AddChild(new GuideReagentLink(reagent));
                        view.AddCell(reagents);
                    }
                    AddChild(view);
                }
            }
        }
        control = this;
        return true;
    }
}
