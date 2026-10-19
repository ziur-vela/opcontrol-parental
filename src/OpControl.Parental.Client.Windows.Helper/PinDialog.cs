namespace OpControl.Parental.Helper;

public sealed class PinDialog : Form
{
    private readonly TextBox _pin;
    private readonly Label _status;
    private readonly Button _ok;
    public event Action<string>? Verified;

    public PinDialog()
    {
        Text = "Parent override";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = true;
        Width = 390;
        Height = 240;
        BackColor = Color.FromArgb(25, 28, 35);
        ForeColor = Color.FromArgb(233, 235, 239);
        MaximizeBox = false;
        MinimizeBox = false;

        var title = new Label
        {
            Text = "Parent override PIN",
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(22, 20)
        };
        _pin = new TextBox
        {
            UseSystemPasswordChar = true,
            MaxLength = 12,
            Width = 330,
            Font = new Font("Segoe UI", 18f),
            Location = new Point(22, 62),
            TextAlign = HorizontalAlignment.Center
        };
        _status = new Label
        {
            Text = "",
            ForeColor = Color.FromArgb(240, 101, 93),
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
            BackColor = Color.FromArgb(0, 200, 83),
            ForeColor = Color.FromArgb(6, 19, 10),
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
        _ok.Click += async (_, _) => await VerifyAsync();
        cancel.Click += (_, _) => Close();
        AcceptButton = _ok;
        CancelButton = cancel;

        Controls.AddRange([title, _pin, _status, _ok, cancel]);
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
        _status.ForeColor = Color.FromArgb(160, 167, 179);
        _status.Text = "Checking…";
        var response = await PinPipeClient.VerifyPinAsync(_pin.Text, CancellationToken.None);
        if (response.Ok && !string.IsNullOrWhiteSpace(response.Ticket))
        {
            Verified?.Invoke(response.Ticket);
            Close();
            return;
        }

        _status.ForeColor = Color.FromArgb(240, 101, 93);
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
