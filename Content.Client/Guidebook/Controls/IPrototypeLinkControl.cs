using Content.Shared.Guidebook;
using Robust.Shared.Prototypes;

namespace Content.Client.Guidebook.Controls;

/// <summary>
///    Interface for controls which represent a Prototype
///    These can be linked to from a IPrototypeLinkControl
/// </summary>
public interface IPrototypeRepresentationControl
{
    // The prototype that this control represents
    public IPrototype? RepresentedPrototype { get; }
}

/// <summary>
///    Interface for controls which can be clicked to navigate
///    to a specified prototype representation, optionally on another guide page.
/// </summary>
public interface IPrototypeLinkControl
{
    // This control is a link to the specified prototype
    public IPrototype? LinkedPrototype { get; }

    /// <summary>
    /// Page to open when the linked prototype is not represented on the current page.
    /// </summary>
    public ProtoId<GuideEntryPrototype>? TargetGuide => null;

    // Initially the link will not be enabled,
    // the owner can enable the link once there is a valid target
    // for the Prototype link.
    public void EnablePrototypeLink();
}
