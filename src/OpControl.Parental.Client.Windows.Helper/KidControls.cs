using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace OpControl.Parental.Helper;

internal sealed class CardPanel : Panel
{
    public Color BorderColor = Theme.Line;
    public int CornerRadius = 0;

    public CardPanel()
    {
        DoubleBuffered = true;
        BackColor = Theme.Panel;
        Padding = new Padding(16);
        Margin = new Padding(0);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(BorderColor);
        if (CornerRadius <= 0)
        {
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
        else
        {
            using var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawPath(pen, path);
        }
    }

    private static GraphicsPath Rounded(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class BarPanel : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Fraction { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BarColor { get; set; } = Theme.Value;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color TrackColor { get; set; } = Theme.Panel2;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Thickness { get; set; } = 8;

    public BarPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        Height = 8;
        Margin = new Padding(0, 4, 0, 4);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var y = (Height - Thickness) / 2;
        using var track = new SolidBrush(TrackColor);
        e.Graphics.FillRectangle(track, 0, y, Width, Thickness);
        var fraction = Math.Clamp(Fraction, 0, 1);
        if (fraction <= 0) return;
        using var fill = new SolidBrush(BarColor);
        e.Graphics.FillRectangle(fill, 0, y, (int)Math.Round(Width * fraction), Thickness);
    }
}

internal sealed class RingPanel : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Fraction { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color RingColor { get; set; } = Theme.Value;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color TrackColor { get; set; } = Theme.Line;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Thickness { get; set; } = 10;

    public RingPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        Size = new Size(124, 124);
        Margin = new Padding(0);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var inset = Thickness / 2 + 1;
        var rect = new Rectangle(inset, inset, Width - inset * 2 - 1, Height - inset * 2 - 1);
        using var track = new Pen(TrackColor, Thickness);
        e.Graphics.DrawEllipse(track, rect);
        var fraction = Math.Clamp(Fraction, 0, 1);
        if (fraction <= 0) return;
        using var ring = new Pen(RingColor, Thickness) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
        e.Graphics.DrawArc(ring, rect, -90f, (float)(360 * fraction));
    }
}

internal sealed class TagLabel : Label
{
    private TagLabel(string text, Color color, Color background)
    {
        Text = text;
        AutoSize = true;
        Font = Theme.Mono(8.5f, FontStyle.Bold);
        ForeColor = color;
        BackColor = background;
        Padding = new Padding(6, 2, 6, 2);
        Margin = new Padding(0, 0, 6, 4);
    }

    public static TagLabel Danger(string text, bool light) =>
        new(text, Theme.Danger, light ? Color.FromArgb(250, 236, 234) : Color.FromArgb(45, 27, 24));

    public static TagLabel Warn(string text, bool light) =>
        new(text, Theme.Warn, light ? Color.FromArgb(250, 244, 226) : Color.FromArgb(43, 37, 23));

    public static TagLabel Value(string text, bool light) =>
        new(text, Theme.Value, light ? Color.FromArgb(232, 245, 240) : Color.FromArgb(18, 44, 38));
}

internal static class KidUi
{
    public static Label Eyebrow(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        Font = Theme.Mono(9f, FontStyle.Bold),
        ForeColor = Theme.Label,
        AutoSize = true,
        BackColor = Color.Transparent,
        Margin = new Padding(0, 0, 0, 6)
    };

    public static Label Title(string text) => new()
    {
        Text = text,
        Font = Theme.Sans(23f, FontStyle.Bold),
        ForeColor = Theme.Text,
        AutoSize = true,
        BackColor = Color.Transparent,
        Margin = new Padding(0, 0, 0, 4)
    };

    public static Label Body(string text, bool muted = false, float size = 10.5f) => new()
    {
        Text = text,
        Font = Theme.Sans(size),
        ForeColor = muted ? Theme.Muted : Theme.Text,
        AutoSize = true,
        BackColor = Color.Transparent,
        Margin = new Padding(0, 0, 0, 4)
    };

    public static Label Mono(string text, float size = 9.5f, bool bold = false, Color? color = null) => new()
    {
        Text = text,
        Font = Theme.Mono(size, bold ? FontStyle.Bold : FontStyle.Regular),
        ForeColor = color ?? Theme.Muted,
        AutoSize = true,
        BackColor = Color.Transparent,
        Margin = new Padding(0, 0, 0, 4)
    };

    public static Label Link(string text) => new()
    {
        Text = text,
        Font = Theme.Mono(9f, FontStyle.Bold),
        ForeColor = Theme.Value,
        AutoSize = true,
        BackColor = Color.Transparent,
        Cursor = Cursors.Hand,
        Margin = new Padding(0, 6, 0, 0)
    };

    public static Label Divider(string text) => new()
    {
        Text = text,
        Font = Theme.Mono(8.5f),
        ForeColor = Theme.Faint,
        AutoSize = true,
        BackColor = Color.Transparent,
        Margin = new Padding(0, 4, 0, 2)
    };

    public static string Duration(int seconds)
    {
        if (seconds <= 0) return "0m";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{Math.Max(1, span.Minutes)}m";
    }

    public static string Clock(int seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}";
    }

    public static FlowLayoutPanel Row(int gap = 8)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        return row;
    }

    public static FlowLayoutPanel Stack(int gap = 4)
    {
        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        return stack;
    }
}
