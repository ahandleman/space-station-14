using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Utility;

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


public sealed class PlantAnalyzerBar : Control
{

    private readonly Label _label;

    public float Value;
    public float Capacity = 100f;
    public Color BarColor = new(0.2f, 0.8f, 0.25f);
    public Color BackgroundColor = new(0.1f, 0.1f, 0.1f);
    public Color BorderColor = new(1f, 1f, 1f, 0.20f);

    public string? TooltipText = null;

    public List<PlantAnalyzerBarNotch> Notches = new();

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
        string? tooltip = null)
    {
        Value = value;
        Capacity = MathF.Max(1f, capacity);
        BarColor = color;
        TooltipText = tooltip;

        _label.Text = label;

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

        // Border.
        handle.DrawLine(new Vector2(0, 0), new Vector2(PixelWidth, 0), BorderColor);
        handle.DrawLine(new Vector2(0, PixelHeight), new Vector2(PixelWidth, PixelHeight), BorderColor);
        handle.DrawLine(new Vector2(0, 0), new Vector2(0, PixelHeight), BorderColor);
        handle.DrawLine(new Vector2(PixelWidth, 0), new Vector2(PixelWidth, PixelHeight), BorderColor);

        foreach (PlantAnalyzerBarNotch notchData in Notches)
        {
            DrawNotches(handle, notchData);
        }
    }

    private void DrawNotches(DrawingHandleScreen handle, PlantAnalyzerBarNotch notchData)
    {
        float unitWidth = PixelWidth / Capacity;
        float notchWidth = notchData.Width * unitWidth;
        int count = 0;
        for (float offset = notchData.Offset; offset < PixelWidth; offset += notchWidth, count++)
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

    protected override Vector2 ArrangeOverride(Vector2 finalSize)
    {
        _label.Arrange(new UIBox2(0, 0, finalSize.X, finalSize.Y));
        return finalSize;
    }

    private Control? SupplyTooltip(Control sender)
    {
        var text = TooltipText;


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
