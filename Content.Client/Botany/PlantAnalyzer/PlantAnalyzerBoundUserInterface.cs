using Content.Shared.Botany.PlantAnalyzer;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client.Botany.PlantAnalyzer;

[UsedImplicitly]
public sealed class PlantAnalyzerBoundUserInterface : BoundUserInterface
{
    [ViewVariables]
    private PlantAnalyzerWindow? _window;

    public PlantAnalyzerBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<PlantAnalyzerWindow>();
        _window.Title = Loc.GetString("plant-analyzer-window-title");
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        if (_window == null)
            return;

        if (message is not PlantAnalyzerScannedPlantMessage msg)
            return;

        _window.Populate(msg);
    }
}
