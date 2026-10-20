namespace OpControl.Parental.Helper;

public sealed class PinDialog : Form
{
    private readonly TextBox _pin;
    private readonly Label _status;
    private readonly Button _ok;
    public event Action<string>? Verified;

    public PinDialog()
    {
        AutoScaleMode = AutoScaleMode.None;
        Text = "Parent override";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = true;
        Width = 390;
        Height = 240;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        MaximizeBox = false;
        MinimizeBox = false;

        var title = new Label
        {
            Text = "Parent override PIN",
            Font = Theme.Sans(16f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(22, 20)
        };
        _pin = new TextBox
        {
            UseSystemPasswordChar = true,
            MaxLength = 12,
            Width = 330,
            Font = Theme.Mono(18f),
            Location = new Point(22, 62),
            TextAlign = HorizontalAlignment.Center
        };
        _status = new Label
        {
            Text = "",
            ForeColor = Theme.Danger,
            AutoSize = false,
            Width = 330,
            Height = 38,
            Location = new Point(22, 104)
        };
        _ok = new Button
        {
            Text = "Unlock options",
            Width = 150,
            Height = 38,
            Location = new Point(22, 150),
            BackColor = Theme.Brand,
            ForeColor = Theme.BrandDark,
            FlatStyle = FlatStyle.Flat
        };
        var cancel = new Button
        {
            Text = "Cancel",
            Width = 120,
            Height = 38,
            Location = new Point(190, 150),
            FlatStyle = FlatStyle.Flat
        };
        Theme.StyleForm(this);
        title.ForeColor = Theme.Text;
        _pin.BackColor = Theme.Panel2; _pin.ForeColor = Theme.Text;
        Theme.StyleButton(_ok, primary: true);
        Theme.StyleButton(cancel);
        _ok.Click += async (_, _) => await VerifyAsync();
        cancel.Click += (_, _) => Close();
        AcceptButton = _ok;
        CancelButton = cancel;

        Controls.AddRange([title, _pin, _status, _ok, cancel]);
        Dpi.ScaleForm(this);
        Shown += (_, _) => _pin.Focus();
        KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await VerifyAsync();
            }
        };
    }

    private async Task VerifyAsync()
    {
        if (string.IsNullOrWhiteSpace(_pin.Text)) return;
        _ok.Enabled = false;
        _status.ForeColor = Theme.Muted;
        _status.Text = "Checking…";
        var response = await PinPipeClient.VerifyPinAsync(_pin.Text, CancellationToken.None);
        if (response.Ok && !string.IsNullOrWhiteSpace(response.Ticket))
        {
            Verified?.Invoke(response.Ticket);
            Close();
            return;
        }

        _status.ForeColor = Theme.Danger;
        var attempts = response.AttemptsRemaining is { } remaining ? $" ({remaining} attempts left)" : "";
        _status.Text = response.LockedOut
            ? $"Too many attempts. Try again in {response.RemainingSeconds ?? 60} s."
            : $"{response.Error ?? "Incorrect PIN."}{attempts}";
        _pin.SelectAll();
        _pin.Focus();
        _ok.Enabled = true;
    }

    protected override bool ShowWithoutActivation => false;
}
