using OpControl.Parental.Localization;
namespace OpControl.Parental.Helper;

public sealed class OverrideMenu : Form
{
    public OverrideMenu(Action<string, int?> onSelected, Action onCancel)
    {
        AutoScaleMode = AutoScaleMode.None;
        Text = Loc.T("Client.Override.Title");
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = true;
        Width = 440;
        Height = 310;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        MaximizeBox = false;
        MinimizeBox = false;

        var title = new Label
        {
            Text = Loc.T("Client.Override.Title"),
            Font = Theme.Sans(18f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(24, 20)
        };
        var subtitle = new Label
        {
            Text = Loc.T("Client.Override.Subtitle"),
            ForeColor = Theme.Muted,
            AutoSize = true,
            Location = new Point(24, 56)
        };
        var unlock = MakeButton(Loc.T("Client.Override.UnlockNext"), 24, 92, 392);
        var bonus = MakeButton(Loc.T("Client.Override.Bonus"), 24, 142, 392);
        var cancel = MakeButton(Loc.T("Client.Override.Cancel"), 24, 202, 392);
        unlock.Click += (_, _) => { onSelected("unlock-until-next", null); Close(); };
        bonus.Click += (_, _) =>
        {
            using var dialog = new BonusDialog(minutes => { onSelected("bonus", minutes); Close(); });
            dialog.ShowDialog(this);
        };
        cancel.Click += (_, _) => { onCancel(); Close(); };

        Controls.AddRange([title, subtitle, unlock, bonus, cancel]);
        Theme.StyleForm(this);
        title.ForeColor = Theme.Text; subtitle.ForeColor = Theme.Muted;
        foreach (var button in new[] { unlock, bonus, cancel }) Theme.StyleButton(button);
        Dpi.ScaleForm(this);
    }

    private static Button MakeButton(string text, int x, int y, int width) => new()
    {
        Text = text,
        Width = width,
        Height = 40,
        Location = new Point(x, y),
        FlatStyle = FlatStyle.Flat,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(12, 0, 0, 0),
        BackColor = Theme.Panel2,
        ForeColor = Theme.Text
    };

    protected override bool ShowWithoutActivation => false;
}

public sealed class BonusDialog : Form
{
    private readonly NumericUpDown _custom;
    public BonusDialog(Action<int> onSelected)
    {
        AutoScaleMode = AutoScaleMode.None;
        Text = Loc.T("Client.Bonus.Title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        TopMost = true;
        Width = 400;
        Height = 260;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        MaximizeBox = false;
        MinimizeBox = false;

        var title = new Label
        {
            Text = Loc.T("Client.Bonus.Title"),
            Font = Theme.Sans(16f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(22, 20)
        };
        var quick = new FlowLayoutPanel { Location = new Point(22, 62), Width = 340, Height = 54 };
        foreach (var minutes in new[] { 15, 30, 60 })
        {
            var button = new Button
            {
                Text = $"{minutes} min",
                Width = 100,
                Height = 40,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Panel2,
                ForeColor = Theme.Value
            };
            button.Click += (_, _) => { onSelected(minutes); Close(); };
            quick.Controls.Add(button);
        }

        var label = new Label { Text = Loc.T("Client.Bonus.Custom"), AutoSize = true, Location = new Point(22, 130) };
        _custom = new NumericUpDown { Minimum = 1, Maximum = 1440, Value = 20, Width = 120, Location = new Point(230, 126) };
        var give = new Button
        {
            Text = Loc.T("Client.Bonus.Give"),
            Width = 140,
            Height = 40,
            Location = new Point(22, 175),
            BackColor = Theme.Brand,
            ForeColor = Theme.BrandDark,
            FlatStyle = FlatStyle.Flat
        };
        var cancel = new Button { Text = Loc.T("Client.Bonus.Cancel"), Width = 110, Height = 40, Location = new Point(180, 175), FlatStyle = FlatStyle.Flat };
        give.Click += (_, _) => { onSelected((int)_custom.Value); Close(); };
        cancel.Click += (_, _) => Close();

        Controls.AddRange([title, quick, label, _custom, give, cancel]);
        Theme.StyleForm(this);
        title.ForeColor = Theme.Text; label.ForeColor = Theme.Label; label.Font = Theme.Mono(9f);
        _custom.BackColor = Theme.Panel2; _custom.ForeColor = Theme.Text;
        Theme.StyleButton(give, primary: true); Theme.StyleButton(cancel);
        foreach (Control c in quick.Controls) Theme.StyleButton((Button)c);
        Dpi.ScaleForm(this);
    }

    protected override bool ShowWithoutActivation => false;
}
