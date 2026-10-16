using Content.Shared.Chemistry.Reagent;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Utility;

namespace Content.Client.Guidebook.Controls;

/// <summary>
/// A reagent-colored background with a contrasting link to its guidebook details.
/// </summary>
public sealed class GuideReagentLink : PanelContainer
{
    public GuideReagentLink(ReagentPrototype reagent)
    {
        var color = reagent.SubstanceColor;
        var textColor = 0.2126f * color.R + 0.7152f * color.G + 0.0722f * color.B > 0.5f
            ? Color.Black
            : Color.White;
        PanelOverride = new StyleBoxFlat { BackgroundColor = color };
        Margin = new Thickness(0, 1);

        var message = new FormattedMessage();
        message.PushColor(textColor);
        message.AddText(reagent.LocalizedName);
        message.Pop();
        var link = new GuidebookRichPrototypeLink
        {
            LinkedPrototype = reagent,
            TargetGuide = reagent.Group == "Drinks" ? "Drinks" : "Chemicals",
            HorizontalExpand = true,
            Margin = new Thickness(6, 2),
        };
        link.SetMessage(message);
        AddChild(link);
    }
}
