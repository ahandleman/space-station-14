using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Utility;

namespace Content.Client.Botany.PlantAnalyzer;

public sealed class PlantAnalyzerBar : Control
{
    private readonly Label _label;

    public float Value;
    public float Capacity = 100f;

    public int LargeNotchWidth = 5;
    public int SmallNotchWidth = 1;
    public bool HasMarker;
    public float MarkerValue;
    public Color MarkerColor = Color.White;
    public string? MarkerTooltipText;
    public Color BarColor = new(0.2f, 0.8f, 0.25f);
    public Color BackgroundColor = new(0.1f, 0.1f, 0.1f);
    public Color BorderColor = new(1f, 1f, 1f, 0.20f);
    public Color NotchColor = new(1f, 1f, 1f, 0.25f);

    public string? TooltipText;

    public PlantAnalyzerBar()
    {
        MouseFilter = MouseFilterMode.Pass;
        TooltipSupplier = SupplyTooltip;

        _label = new Label
        {
            ClipText = true,
            FontColorOverride = Color.White,
            Margin = new Thickness(6, 0, 6, 0),
        };

        AddChild(_label);
    }

    public void SetData(
        float value,
        float capacity,
        Color color,
        string label,
        string? tooltip = null,
        float? markerValue = null,
        string? markerTooltip = null)
    {
        Value = value;
        Capacity = MathF.Max(1f, capacity);
        BarColor = color;
        TooltipText = tooltip;

        HasMarker = markerValue != null;
        MarkerValue = markerValue ?? 0f;
        MarkerTooltipText = markerTooltip;

        _label.Text = label;

        InvalidateMeasure();
        InvalidateArrange();
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        var box = PixelSizeBox;
        handle.DrawRect(box, BackgroundColor);

        var fraction = MathHelper.Clamp01(Value / Capacity);
        var fillWidth = PixelWidth * fraction;

        if (fillWidth > 0)
        {
            handle.DrawRect(new UIBox2(0, 0, fillWidth, PixelHeight), BarColor);
        }

        if (HasMarker && MarkerValue >= 0f && MarkerValue <= Capacity)
        {
            var markerX = PixelWidth * MathHelper.Clamp01(MarkerValue / Capacity);

            // Small shadow so the white marker is readable on yellow/green/red.
            handle.DrawLine(
                new Vector2(markerX + 1, 0),
                new Vector2(markerX + 1, PixelHeight),
                Color.Black);

            handle.DrawLine(
                new Vector2(markerX, 0),
                new Vector2(markerX, PixelHeight),
                MarkerColor);
        }

        // Border.
        handle.DrawLine(new Vector2(0, 0), new Vector2(PixelWidth, 0), BorderColor);
        handle.DrawLine(new Vector2(0, PixelHeight), new Vector2(PixelWidth, PixelHeight), BorderColor);
        handle.DrawLine(new Vector2(0, 0), new Vector2(0, PixelHeight), BorderColor);
        handle.DrawLine(new Vector2(PixelWidth, 0), new Vector2(PixelWidth, PixelHeight), BorderColor);

        DrawNotches(handle, SmallNotchWidth, PixelHeight / 5);
        DrawNotches(handle, LargeNotchWidth, PixelHeight / 3);

    }

    private void DrawNotches(DrawingHandleScreen handle, int width, int height)
    {
        float unitWidth = PixelWidth / Capacity;
        float notchWidth = width * unitWidth;
        for (float offset = notchWidth; offset < PixelWidth; offset += notchWidth)
        {
            handle.DrawLine(new Vector2(offset, PixelHeight), new Vector2(offset, PixelHeight - height), NotchColor);
        }

    }

    protected override Vector2 ArrangeOverride(Vector2 finalSize)
    {
        _label.Arrange(new UIBox2(0, 0, finalSize.X, finalSize.Y));
        return finalSize;
    }

    private Control? SupplyTooltip(Control sender)
    {
        var text = TooltipText;

        if (HasMarker && !string.IsNullOrWhiteSpace(MarkerTooltipText))
        {
            var globalMousePos = UserInterfaceManager.MousePositionScaled.Position;
            var mousePos = globalMousePos - GlobalPosition;

            var markerX = PixelWidth * MathHelper.Clamp01(MarkerValue / Capacity);

            if (MathF.Abs(mousePos.X - markerX) <= 4f)
                text = MarkerTooltipText;
        }

        if (string.IsNullOrWhiteSpace(text))
            return null;

        var msg = new FormattedMessage();
        msg.AddText(text);

        var tooltip = new Tooltip();
        tooltip.SetMessage(msg);
        return tooltip;
    }
}
