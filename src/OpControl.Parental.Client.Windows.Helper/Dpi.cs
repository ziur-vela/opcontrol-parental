namespace OpControl.Parental.Helper;

/// <summary>
/// Forms in this helper are built in code with pixel sizes designed for 96 DPI.
/// Fonts are in points and already follow the display scale, so only geometry needs scaling.
/// Without this, at 150-200% scaling text is clipped (X1 runs at 200%).
/// </summary>
internal static class Dpi
{
    public static float Factor(Control c) => c.DeviceDpi / 96f;

    public static int S(Control c, int value) => (int)Math.Round(value * Factor(c));

    public static void ScaleForm(Form form)
    {
        var f = Factor(form);
        if (Math.Abs(f - 1f) < 0.01f) return;
        form.SuspendLayout();
        form.Size = Scale(form.Size, f);
        form.Padding = Scale(form.Padding, f);
        ScaleChildren(form, f);
        form.ResumeLayout(true);
    }

    private static void ScaleChildren(Control parent, float f)
    {
        if (parent is TableLayoutPanel table)
        {
            foreach (ColumnStyle style in table.ColumnStyles)
                if (style.SizeType == SizeType.Absolute)
                    style.Width = (int)Math.Round(style.Width * f);
            foreach (RowStyle style in table.RowStyles)
                if (style.SizeType == SizeType.Absolute)
                    style.Height = (int)Math.Round(style.Height * f);
        }
        foreach (Control c in parent.Controls)
        {
            c.Location = new Point((int)Math.Round(c.Left * f), (int)Math.Round(c.Top * f));
            if (!c.AutoSize && c.Dock == DockStyle.None) c.Size = Scale(c.Size, f);
            else if (!c.AutoSize && c.Dock is DockStyle.Top or DockStyle.Bottom) c.Height = (int)Math.Round(c.Height * f);
            c.Margin = Scale(c.Margin, f);
            c.Padding = Scale(c.Padding, f);
            ScaleChildren(c, f);
        }
    }

    private static Size Scale(Size s, float f) => new((int)Math.Round(s.Width * f), (int)Math.Round(s.Height * f));

    private static Padding Scale(Padding p, float f) =>
        new((int)Math.Round(p.Left * f), (int)Math.Round(p.Top * f), (int)Math.Round(p.Right * f), (int)Math.Round(p.Bottom * f));
}
