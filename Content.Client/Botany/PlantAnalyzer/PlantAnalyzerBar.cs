using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Utility;
using Robust.Shared.Log;
using System.Reflection.Metadata;
using Content.Shared.Climbing.Events;

namespace Content.Client.Botany.PlantAnalyzer;


public sealed class PlantAnalyzerBarNotch
{
    //How often to generate the marker
    public float Width;
    //How tall the marker should be
    public float Height;
    //Whether to place the marker on the top or bottom.
    public bool BottomAlign;
    //How much the initial offset should be
    public float Offset;
    //How many of the marker to generate.  0 to fill the bar.
    public int Count;
    //Set default notch to white
    public Color NotchColor = new(1f, 1f, 1f, 0.25f);
    public string TooltipText;

    public PlantAnalyzerBarNotch(
        float width = 1f,
        float height = 1f,
        float offset = 0,
        bool bottomAlign = false,
        int count = 0,
        Color? notchColor = null,
        string tooltipText = "")
    {
        Width = width;
        Height = height;
        Offset = offset;
        BottomAlign = bottomAlign;
        Count = count;
        NotchColor = notchColor ?? new Color(1f, 1f, 1f, 0.25f);
        TooltipText = tooltipText;
    }
}


public sealed class PlantAnalyzerBarBar
{
    //How often to generate the marker
    public float Start;
    //How tall the marker should be
    public float End;
    //Whether to place the marker on the top or bottom.
    public Color BarColor = new(1f, 1f, 1f, 0.25f);
    public string TooltipText;

    public PlantAnalyzerBarBar(
        float start = 0f,
        float end = 1f,
        Color? barColor = null,
        string tooltipText = "")
    {
        Start = start;
        End = end;
        BarColor = barColor ?? new Color(1f, 1f, 1f, 0.25f);
        TooltipText = tooltipText;
    }
}


public sealed class PlantAnalyzerBar : Control
{

    private readonly Label _label;
    public float Value;
    public float Capacity = 100f;
    public bool Horizontal = true;
    public Color BackgroundColor = new(0.1f, 0.1f, 0.1f);
    public Color BorderColor = new(0.7f, 0.7f, 0.7f, 1f);

    public string? TooltipText = null;

    public List<PlantAnalyzerBarNotch> Notches = new();
    public List<PlantAnalyzerBarBar> Bars = new();

    private ISawmill _sawmill = Logger.GetSawmill("botany.PlantAnalyzerBar");

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
        float capacity,
        string label,
        string? tooltip = null,
        bool horizontal = true,
        bool clearNotches = true,
        bool clearBars = true)
    {
        Capacity = MathF.Max(1f, capacity);
        TooltipText = tooltip;

        _label.Text = label;
        _label.Align = horizontal ? Label.AlignMode.Left : Label.AlignMode.Center;
        if (clearNotches) Notches.Clear();
        if (clearBars) Bars.Clear();
        Horizontal = horizontal;

        InvalidateMeasure();
        InvalidateArrange();
    }

    public void AddNotch(
        float width = 1f,
        float height = 1f,
        float offset = 0,
        bool bottomAlign = false,
        int count = 0,
        Color? notchColor = null,
        string tooltipText = "")
    {
        Notches.Add(new PlantAnalyzerBarNotch(
            width,
            height,
            offset,
            bottomAlign,
            count,
            notchColor,
            tooltipText));
    }

    public void AddBar(
        float start = 0f,
        float end = 1f,
        Color? barColor = null,
        string tooltipText = "")
    {
        Bars.Add(new PlantAnalyzerBarBar(
            MathF.Max(start, 0f),
            MathF.Min(end, Capacity),
            barColor,
            tooltipText));
    }

    const float DrawDistanceTolerance = 0.1f;
    protected override void Draw(DrawingHandleScreen handle)
    {
        var box = PixelSizeBox;
        handle.DrawRect(box, BackgroundColor);

        foreach (PlantAnalyzerBarBar barData in Bars)
        {
            if (barData.End - barData.Start <= DrawDistanceTolerance) continue;
            DrawBar(handle, barData);
        }

        foreach (PlantAnalyzerBarNotch notchData in Notches)
        {
            DrawNotches(handle, notchData);
        }

        // Border.
        handle.DrawLine(new Vector2(0, 0), new Vector2(PixelWidth, 0), BorderColor);
        handle.DrawLine(new Vector2(0, PixelHeight), new Vector2(PixelWidth, PixelHeight), BorderColor);
        handle.DrawLine(new Vector2(0, 0), new Vector2(0, PixelHeight), BorderColor);
        handle.DrawLine(new Vector2(PixelWidth, 0), new Vector2(PixelWidth, PixelHeight), BorderColor);

    }


    private void DrawBar(DrawingHandleScreen handle, PlantAnalyzerBarBar barData)
    {
        if (Horizontal)
        {
            float unitWidth = PixelWidth / Capacity;
            handle.DrawRect(new UIBox2(barData.Start * unitWidth, 0, barData.End * unitWidth, PixelHeight), barData.BarColor);
        }
        else
        {
            float unitHeight = PixelHeight / Capacity;
            handle.DrawRect(new UIBox2(0, PixelHeight - barData.End * unitHeight, PixelWidth, PixelHeight - barData.Start * unitHeight), barData.BarColor);
        }
    }
    private void DrawNotches(DrawingHandleScreen handle, PlantAnalyzerBarNotch notchData)
    {
        if (Horizontal) {
            float unitWidth = PixelWidth / Capacity;
            float notchWidth = notchData.Width * unitWidth;
            int count = 0;
            for (float offset = unitWidth * notchData.Offset; offset < PixelWidth; offset += notchWidth, count++)
            {
                if (notchData.Count > 0 && count >= notchData.Count) break;
                if (notchData.BottomAlign)
                {
                    handle.DrawLine(new Vector2(offset, PixelHeight), new Vector2(offset, PixelHeight - (PixelHeight * notchData.Height)), notchData.NotchColor);
                }
                else
                {
                    handle.DrawLine(new Vector2(offset, 0), new Vector2(offset, (PixelHeight * notchData.Height)), notchData.NotchColor);
                }
            }
        }
        else
        {
            float unitHeight = PixelHeight / Capacity;
            float notchHeight = notchData.Width * unitHeight;
            int count = 0;

            for (float offset = unitHeight * notchData.Offset; offset < PixelHeight; offset += notchHeight, count++)
            {
                if (notchData.Count > 0 && count >= notchData.Count)
                    break;

                if (notchData.BottomAlign)
                {
                    handle.DrawLine(
                        new Vector2(PixelWidth, PixelHeight - offset),
                        new Vector2(PixelWidth - PixelWidth * notchData.Height, PixelHeight - offset),
                        notchData.NotchColor);
                }
                else
                {
                    handle.DrawLine(
                        new Vector2(0, PixelHeight - offset),
                        new Vector2(PixelWidth * notchData.Height, PixelHeight - offset),
                        notchData.NotchColor);
                }
            }
        }

    }

    protected override Vector2 ArrangeOverride(Vector2 finalSize)
    {
        _label.Arrange(new UIBox2(0, 0, finalSize.X, finalSize.Y));
        return finalSize;
    }

    private const float NotchTooltipRadius = 4f;

    private string? GetNotchTooltipAtMouse()
    {
        if (Capacity <= 0 || PixelWidth <= 0 || PixelHeight <= 0)
            return null;

        var globalMousePos = UserInterfaceManager.MousePositionScaled.Position;
        var mousePos = globalMousePos - GlobalPosition;

        // If the mouse is outside the bar entirely, don't show notch tooltips.
        if (mousePos.X < 0 || mousePos.X > PixelWidth || mousePos.Y < 0 || mousePos.Y > PixelHeight)
            return null;

        var unitWidth = PixelWidth / Capacity;

        // Iterate backwards so later-drawn notches win if several overlap.
        for (var i = Notches.Count - 1; i >= 0; i--)
        {
            var notchData = Notches[i];

            if (string.IsNullOrWhiteSpace(notchData.TooltipText))
                continue;

            if (TryHitNotch(mousePos, unitWidth, notchData))
                return notchData.TooltipText;
        }

        return null;
    }

    private bool TryHitNotch(Vector2 mousePos, float unitWidth, PlantAnalyzerBarNotch notchData)
    {
        var notchStep = notchData.Width * unitWidth;

        // Avoid infinite loops / invalid notch definitions.
        if (notchStep <= 0)
            return false;

        var notchHeight = MathHelper.Clamp(notchData.Height, 0f, 1f) * PixelHeight;
        var startX = notchData.Offset * unitWidth;

        var count = 0;

        for (var x = startX; x < PixelWidth; x += notchStep, count++)
        {
            if (notchData.Count > 0 && count >= notchData.Count)
                break;

            if (!IsMouseNearNotchX(mousePos.X, x))
                continue;

            return true;
        }

        return false;
    }

    private bool IsMouseNearNotchX(float mouseX, float notchX)
    {
        return MathF.Abs(mouseX - notchX) <= NotchTooltipRadius;
    }

    private Control? SupplyTooltip(Control sender)
    {
        var text = TooltipText;

        foreach (PlantAnalyzerBarNotch notchData in Notches)
        {
            if (string.IsNullOrWhiteSpace(notchData.TooltipText)) continue;
            float unitWidth = PixelWidth / Capacity;
            float markerWidth = unitWidth * notchData.Width;
            var globalMousePos = UserInterfaceManager.MousePositionScaled.Position;
            var mousePos = globalMousePos - GlobalPosition;
            float detectionRange = 5f;
            //First get rid of the offset so that we're going from the same starting position.
            var mouseMarkerRelativeOffset = mousePos.X - (notchData.Offset * unitWidth);
            //Take the relative position modulo
            float modValue = ((detectionRange + mouseMarkerRelativeOffset) % markerWidth);
            if (modValue > 0 && modValue <= 2 * detectionRange && (notchData.Count == 0 || mouseMarkerRelativeOffset / markerWidth <= notchData.Count))
            {
                text = notchData.TooltipText;
            }


        }
        // if (HasMarker && !string.IsNullOrWhiteSpace(MarkerTooltipText))
        // {
        //     var globalMousePos = UserInterfaceManager.MousePositionScaled.Position;
        //     var mousePos = globalMousePos - GlobalPosition;

        //     var markerX = PixelWidth * MathHelper.Clamp01(MarkerValue / Capacity);

        //     if (MathF.Abs(mousePos.X - markerX) <= 4f)
        //         text = MarkerTooltipText;
        // }

        if (string.IsNullOrWhiteSpace(text))
            return null;

        var msg = new FormattedMessage();
        msg.AddText(text);

        var tooltip = new Tooltip();
        tooltip.SetMessage(msg);
        return tooltip;
    }
}
