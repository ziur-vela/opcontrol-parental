namespace OpControl.Parental.Helper;

public sealed class OverrideMenu : Form
{
    public OverrideMenu(Action<string, int?> onSelected, Action onCancel)
    {
        Text = "Parent override";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = true;
        Width = 440;
        Height = 310;
        BackColor = Color.FromArgb(25, 28, 35);
        ForeColor = Color.FromArgb(233, 235, 239);
        MaximizeBox = false;
        MinimizeBox = false;

        var title = new Label
        {
            Text = "Parent override",
            Font = new Font("Segoe UI", 18f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(24, 20)
        };
        var subtitle = new Label
        {
            Text = "Choose what happens next.",
            ForeColor = Color.FromArgb(160, 167, 179),
            AutoSize = true,
            Location = new Point(24, 56)
        };
        var unlock = MakeButton("Unlock until next lock cycle", 24, 92, 392);
        var bonus = MakeButton("Give bonus time", 24, 142, 392);
        var cancel = MakeButton("Cancel", 24, 202, 392);
        unlock.Click += (_, _) => { onSelected("unlock-until-next", null); Close(); };
        bonus.Click += (_, _) =>
        {
            using var dialog = new BonusDialog(minutes => { onSelected("bonus", minutes); Close(); });
            dialog.ShowDialog(this);
        };
        cancel.Click += (_, _) => { onCancel(); Close(); };

        Controls.AddRange([title, subtitle, unlock, bonus, cancel]);
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
        BackColor = Color.FromArgb(33, 37, 46),
        ForeColor = Color.FromArgb(233, 235, 239)
    };

    protected override bool ShowWithoutActivation => false;
}

public sealed class BonusDialog : Form
{
    private readonly NumericUpDown _custom;
    public BonusDialog(Action<int> onSelected)
    {
        Text = "Give bonus time";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        TopMost = true;
        Width = 400;
        Height = 260;
        BackColor = Color.FromArgb(25, 28, 35);
        ForeColor = Color.FromArgb(233, 235, 239);
        MaximizeBox = false;
        MinimizeBox = false;

        var title = new Label
        {
            Text = "Give bonus time",
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
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
                BackColor = Color.FromArgb(20, 80, 45),
                ForeColor = Color.FromArgb(200, 255, 220)
            };
            button.Click += (_, _) => { onSelected(minutes); Close(); };
            quick.Controls.Add(button);
        }

        var label = new Label { Text = "Custom minutes", AutoSize = true, Location = new Point(22, 130) };
        _custom = new NumericUpDown { Minimum = 1, Maximum = 1440, Value = 20, Width = 120, Location = new Point(140, 126) };
        var give = new Button
        {
            Text = "Give bonus",
            Width = 140,
            Height = 40,
            Location = new Point(22, 175),
            BackColor = Color.FromArgb(0, 200, 83),
            ForeColor = Color.FromArgb(6, 19, 10),
            FlatStyle = FlatStyle.Flat
        };
        var cancel = new Button { Text = "Cancel", Width = 110, Height = 40, Location = new Point(180, 175), FlatStyle = FlatStyle.Flat };
        give.Click += (_, _) => { onSelected((int)_custom.Value); Close(); };
        cancel.Click += (_, _) => Close();

        Controls.AddRange([title, quick, label, _custom, give, cancel]);
        Dpi.ScaleForm(this);
    }

    protected override bool ShowWithoutActivation => false;
}
