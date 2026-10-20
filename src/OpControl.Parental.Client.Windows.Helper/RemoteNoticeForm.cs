namespace OpControl.Parental.Helper;

/// <summary>Small OpControl-style corner notice shown while a remote-view session has notices enabled.</summary>
internal sealed class RemoteNoticeForm : Form
{
    private readonly Label _label;
    private readonly Panel _dot;

    public RemoteNoticeForm(string text)
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Theme.Panel;

        _dot = new Panel { Width = Dpi.S(this, 7), Height = Dpi.S(this, 7), BackColor = Theme.Brand };
        _label = new Label
        {
            Text = text,
            AutoSize = true,
            Font = Theme.Sans(9.5f, FontStyle.Bold),
            ForeColor = Theme.Text,
            BackColor = Color.Transparent
        };
        Controls.Add(_dot);
        Controls.Add(_label);
        LayoutNotice();
        Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Line2, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        };
        PositionTopRight();
    }

    private void LayoutNotice()
    {
        var pad = Dpi.S(this, 10);
        _label.Location = new Point(pad + Dpi.S(this, 7) + Dpi.S(this, 8), pad);
        Height = Math.Max(_label.Height + pad * 2, Dpi.S(this, 34));
        Width = _label.Right + pad;
        _dot.Location = new Point(pad, (Height - _dot.Height) / 2);
    }

    private void PositionTopRight()
    {
        var area = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(area.Right - Width - Dpi.S(this, 14), area.Top + Dpi.S(this, 14));
    }

    public void ShowNotice(string text)
    {
        if (_label.Text != text)
        {
            _label.Text = text;
            LayoutNotice();
            PositionTopRight();
        }
        if (!Visible) Show();
        TopMost = true;
    }

    public void HideNotice()
    {
        if (Visible) Hide();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT
            return cp;
        }
    }
}
