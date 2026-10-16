using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Client.Guidebook.Richtext;
using Content.Shared.Botany.Components;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Prototypes;

namespace Content.Client.Guidebook.Controls;

/// <summary>
/// Lists all concrete plant prototypes, including newly added species, without a manual catalog.
/// </summary>
[UsedImplicitly]
public sealed partial class GuidePlantListEmbed : BoxContainer, IDocumentTag
{
    [Dependency] private IPrototypeManager _prototype = default!;
    [Dependency] private IComponentFactory _factory = default!;

    public GuidePlantListEmbed()
    {
        IoCManager.InjectDependencies(this);
        Orientation = LayoutOrientation.Vertical;
        HorizontalExpand = true;
    }

    public bool TryParseTag(Dictionary<string, string> args, [NotNullWhen(true)] out Control? control)
    {
        var plants = _prototype.EnumeratePrototypes<EntityPrototype>()
            .Where(p => !p.Abstract && p.HasComp<PlantDataComponent>(_factory) && p.HasComp<PlantComponent>(_factory))
            .OrderBy(p => p.TryComp<PlantDataComponent>(out var data, _factory) ? Loc.GetString(data.Name) : p.Name);

        foreach (var plant in plants)
            AddChild(new GuidePlantEmbed(plant));

        control = this;
        return true;
    }
}
