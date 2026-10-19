using System.Reflection;
using OpControl.Parental.Contracts;

namespace OpControl.Parental.Helper;

public sealed class LockOverlayManager : IDisposable
{
    private readonly List<LockForm> _forms = [];
    public event Action? OverrideClicked;

    public bool Visible => _forms.Any(f => f.Visible);

    /// <summary>Lock form on the primary screen; override dialogs are owned by it so they stay above the overlay.</summary>
    public Form? Owner => _forms.FirstOrDefault(f => f.IsPrimary) ?? _forms.FirstOrDefault();

    private bool DialogOpen => _forms.Any(f => f.OwnedForms.Any(o => o.Visible));

    public void Update(RuntimeLockStateDto state)
    {
        if (_forms.Count == 0)
        {
            foreach (var screen in Screen.AllScreens)
            {
                var form = new LockForm(screen);
                form.OverrideClicked += () => OverrideClicked?.Invoke();
                _forms.Add(form);
            }
        }

        foreach (var form in _forms)
            form.UpdateState(state);

        if (!Visible)
        {
            foreach (var form in _forms)
                form.Show();
            HelperPaths.LogEvent("lock-window-shown");
        }

        // Setting TopMost re-runs SetWindowPos(HWND_TOPMOST) and lifts the overlay above every other
        // topmost window. While a parent dialog is open that would bury it (bug: PIN dialog vanished
        // after ~1 s), so only re-assert when no dialog is showing.
        var dialogOpen = DialogOpen;
        foreach (var form in _forms)
        {
            if (!dialogOpen) form.TopMost = true;
            if (!form.Visible) form.Show();
        }
    }

    public void Hide()
    {
        var wasVisible = Visible;
        foreach (var form in _forms)
            form.Hide();
        if (wasVisible)
            HelperPaths.LogEvent("lock-window-hidden");
    }

    public void Dispose()
    {
        foreach (var form in _forms)
            form.Dispose();
        _forms.Clear();
    }
}

public sealed class LockForm : Form
{
    private static readonly Image? Logo = LoadLogo();
    private readonly Label _reason;
    private readonly Label _end;
    public event Action? OverrideClicked;

    public bool IsPrimary { get; }

    public LockForm(Screen screen)
    {
        IsPrimary = screen.Primary;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen.Bounds;
        BackColor = Color.FromArgb(16, 18, 23);
        ForeColor = Color.FromArgb(233, 235, 239);
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
            BackColor = Color.Transparent,
            Padding = new Padding(Dpi.S(this, 48))
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 34f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 20f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var logo = new PictureBox
        {
            Image = Logo,
            SizeMode = PictureBoxSizeMode.Zoom,
            Width = Dpi.S(this, 330),
            Height = Dpi.S(this, 66),
            Anchor = AnchorStyles.None,
            BackColor = Color.Transparent
        };
        var title = new Label
        {
            Text = "This device is locked",
            Font = new Font("Segoe UI", 34f, FontStyle.Bold),
            ForeColor = Color.FromArgb(233, 235, 239),
            AutoSize = true,
            Anchor = AnchorStyles.None,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 18, 0, 6)
        };
        _reason = new Label
        {
            Text = "A parent has paused this device.",
            Font = new Font("Segoe UI", 16f),
            ForeColor = Color.FromArgb(160, 167, 179),
            AutoSize = true,
            Anchor = AnchorStyles.None,
            BackColor = Color.Transparent
        };
        _end = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 14f),
            ForeColor = Color.FromArgb(232, 188, 74),
            AutoSize = true,
            Anchor = AnchorStyles.None,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 8, 0, 0)
        };
        var overrideLink = new Button
        {
            Text = "Parent override",
            Font = new Font("Segoe UI", 13f, FontStyle.Underline),
            ForeColor = Color.FromArgb(0, 200, 83),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(Dpi.S(this, 16), Dpi.S(this, 6), Dpi.S(this, 16), Dpi.S(this, 6)),
            TextAlign = ContentAlignment.MiddleCenter,
            Anchor = AnchorStyles.None,
            BackColor = Color.FromArgb(16, 18, 23),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        overrideLink.FlatAppearance.BorderSize = 0;
        overrideLink.Click += (_, _) => OverrideClicked?.Invoke();

        var footer = new Label
        {
            Text = "Managed by OpControl Parental",
            Font = new Font("Segoe UI", 10f),
            ForeColor = Color.FromArgb(111, 119, 132),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            BackColor = Color.Transparent
        };

        layout.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent }, 0, 0);
        layout.Controls.Add(logo, 0, 1);
        layout.Controls.Add(title, 0, 2);
        layout.Controls.Add(_reason, 0, 3);
        layout.Controls.Add(_end, 0, 4);
        layout.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent }, 0, 5);
        layout.Controls.Add(overrideLink, 0, 6);
        layout.Controls.Add(footer, 0, 7);
        Controls.Add(layout);

        FormClosing += (_, e) =>
        {
            if (e.CloseReason is CloseReason.UserClosing or CloseReason.TaskManagerClosing)
                e.Cancel = true;
        };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) e.SuppressKeyPress = true;
        };
    }

    public void UpdateState(RuntimeLockStateDto state)
    {
        _reason.Text = state.Reason switch
        {
            "manual" => $"Locked by {state.ParentDisplayName ?? "a parent"}",
            "schedule" => $"Locked by {(string.IsNullOrWhiteSpace(state.ScheduleName) ? "a schedule" : state.ScheduleName)}",
            "daily-limit" => "Daily limit reached",
            _ => "This device is locked"
        };
        _end.Text = state.Reason == "daily-limit"
            ? "Screen time resets at local midnight."
            : state.EndsUtc is { } ends
                ? $"Unlocks {ends.ToLocalTime():h:mm tt}"
                : "Ask a parent to unlock this device.";
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    private static Image? LoadLogo()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("OpControl.Parental.Helper.brand.lockup-ondark-300h.png");
            if (stream is null) return null;
            using var image = Image.FromStream(stream);
            return new Bitmap(image);
        }
        catch
        {
            return null;
        }
    }
}
