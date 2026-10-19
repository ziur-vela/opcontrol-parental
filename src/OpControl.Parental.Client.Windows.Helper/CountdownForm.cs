namespace OpControl.Parental.Helper;

public sealed class CountdownForm : Form
{
    private readonly Label _label;
    private bool _blink;

    public CountdownForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(20, 23, 29);
        Width = 390;
        Height = 62;
        Padding = new Padding(10, 4, 10, 4);
        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 12);

        _label = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Regular),
            ForeColor = Color.FromArgb(0, 200, 83),
            BackColor = Color.Transparent
        };
        Controls.Add(_label);
        Dpi.ScaleForm(this);
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 12);
        Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(43, 48, 58), 1);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        };
    }

    public void UpdateText(string text, TimeSpan remaining)
    {
        _label.Text = text;
        _blink = !_blink;
        if (remaining.TotalSeconds <= 60)
            _label.ForeColor = _blink ? Color.FromArgb(240, 101, 93) : Color.FromArgb(232, 188, 74);
        else
            _label.ForeColor = Color.FromArgb(0, 200, 83);

        // Compact pill sized to the text (was a fixed 390x62 box, oversized at 200% scaling).
        var text_ = TextRenderer.MeasureText(text, _label.Font);
        var size = new Size(text_.Width + Padding.Horizontal + Dpi.S(this, 8), text_.Height + Padding.Vertical + Dpi.S(this, 4));
        if (Size != size) Size = size;
        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(area.Right - Width - Dpi.S(this, 12), area.Bottom - Height - Dpi.S(this, 8));
        if (!Visible) Show();
    }

    public void HideBanner()
    {
        if (Visible) Hide();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000;
            cp.ExStyle |= 0x00000080;
            return cp;
        }
    }
}
