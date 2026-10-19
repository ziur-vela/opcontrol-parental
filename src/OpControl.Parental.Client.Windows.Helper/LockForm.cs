using System.Text.Json;

namespace OpControl.Parental.Helper;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new LockForm());
    }
}

public static class HelperPaths
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpControlParental");
    public static string StateFile => Path.Combine(DataDirectory, "state.json");
    public static string StatusFile => Path.Combine(DataDirectory, "helper-status.json");
    public static string EventsFile => Path.Combine(DataDirectory, "helper-events.log");
}

public sealed class LockForm : Form
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly Label _title;
    private readonly Label _reason;
    private readonly Label _countdown;
    private readonly Label _footer;
    private readonly System.Windows.Forms.Timer _timer;
    private DateTime _lastRead = DateTime.MinValue;

    public LockForm()
    {
        Text = "OpControl Parental";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(16, 18, 23);
        ForeColor = Color.FromArgb(233, 235, 239);
        KeyPreview = true;
        DoubleBuffered = true;

        var shield = new Label
        {
            Text = "🛡",
            Font = new Font("Segoe UI Emoji", 56f),
            ForeColor = Color.FromArgb(0, 200, 83),
            AutoSize = true,
            BackColor = Color.Transparent
        };
        _title = new Label
        {
            Text = "This device is locked",
            Font = new Font("Segoe UI", 34f, FontStyle.Bold),
            ForeColor = Color.FromArgb(233, 235, 239),
            AutoSize = true,
            BackColor = Color.Transparent
        };
        _reason = new Label
        {
            Text = "A parent has paused this device.",
            Font = new Font("Segoe UI", 14f),
            ForeColor = Color.FromArgb(160, 167, 179),
            AutoSize = true,
            BackColor = Color.Transparent
        };
        _countdown = new Label
        {
            Text = "",
            Font = new Font("Consolas", 24f, FontStyle.Bold),
            ForeColor = Color.FromArgb(232, 188, 74),
            AutoSize = true,
            BackColor = Color.Transparent
        };
        _footer = new Label
        {
            Text = "Managed by OpControl Parental",
            Font = new Font("Segoe UI", 10f),
            ForeColor = Color.FromArgb(111, 119, 132),
            AutoSize = true,
            BackColor = Color.Transparent
        };

        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Color.Transparent
        };
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
        shield.Anchor = AnchorStyles.None;
        _title.Anchor = AnchorStyles.None;
        _reason.Anchor = AnchorStyles.None;
        _countdown.Anchor = AnchorStyles.None;
        stack.Controls.Add(shield, 0, 1);
        stack.Controls.Add(_title, 0, 2);
        stack.Controls.Add(_reason, 0, 3);

        var countdownHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        _countdown.Location = new Point(0, 0);
        countdownHost.Controls.Add(_countdown);
        countdownHost.Resize += (_, _) => _countdown.Left = (countdownHost.Width - _countdown.Width) / 2;
        _countdown.LocationChanged += (_, _) => { };
        stack.Controls.Add(countdownHost, 0, 4);

        Controls.Add(stack);
        Controls.Add(_footer);
        _footer.Location = new Point(24, 24);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => { RefreshState(); WriteStatus(Visible); };
        Load += (_, _) =>
        {
            Bounds = SystemInformation.VirtualScreen;
            _footer.Location = new Point(24, Height - 40);
            LogEvent($"helper-started session={System.Diagnostics.Process.GetCurrentProcess().SessionId}");
            RefreshState();
            WriteStatus(Visible);
            _timer.Start();
        };
        Shown += (_, _) =>
        {
            Bounds = SystemInformation.VirtualScreen;
            TopMost = true;
            Activate();
        };
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

    private void RefreshState()
    {
        try
        {
            if (!File.Exists(HelperPaths.StateFile))
            {
                HideIfVisible();
                return;
            }

            var info = new FileInfo(HelperPaths.StateFile);
            if (info.LastWriteTimeUtc == _lastRead && Visible)
            {
                UpdateCountdown();
                return;
            }
            _lastRead = info.LastWriteTimeUtc;

            var json = File.ReadAllText(HelperPaths.StateFile);
            var state = JsonSerializer.Deserialize<Contracts.LockStateDto>(json, JsonOptions);
            if (state is null || !IsEffectivelyLocked(state, DateTime.UtcNow))
            {
                HideIfVisible();
                return;
            }

            _title.Text = "This device is locked";
            _reason.Text = state.Reason switch
            {
                "manual" => "A parent has locked this device from the dashboard.",
                "schedule" => "This device is inside a lock schedule.",
                "bonus" => "Bonus time is active.",
                "override" => "A parent has unlocked this device temporarily.",
                _ => "A parent has paused this device."
            };
            UpdateCountdown(state);
            ShowLock();
            WriteStatus(true);
        }
        catch
        {
        }
    }

    private static bool IsEffectivelyLocked(Contracts.LockStateDto state, DateTime nowUtc)
    {
        if (state.IsLocked) return true;
        if (state.ManualLock && state.BonusUntilUtc is { } bonus && bonus <= nowUtc) return true;
        return false;
    }

    private void WriteStatus(bool visible)
    {
        try
        {
            var status = new
            {
                sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId,
                visible,
                updatedUtc = DateTime.UtcNow
            };
            var temp = HelperPaths.StatusFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(status));
            File.Move(temp, HelperPaths.StatusFile, overwrite: true);
        }
        catch
        {
        }
    }

    private void UpdateCountdown(Contracts.LockStateDto? state = null)
    {
        state ??= ReadState();
        var now = DateTime.UtcNow;
        if (state?.BonusUntilUtc is { } bonus && bonus > now)
        {
            _countdown.Text = $"Bonus time {Format(bonus - now)}";
            _countdown.Visible = true;
        }
        else if (state?.UnlockUntilUtc is { } unlock && unlock > now)
        {
            _countdown.Text = $"Unlocked {Format(unlock - now)}";
            _countdown.Visible = true;
        }
        else
        {
            _countdown.Text = "Ask a parent to unlock this device.";
            _countdown.Visible = true;
        }
        _countdown.Left = (Width - _countdown.Width) / 2;
    }

    private static Contracts.LockStateDto? ReadState()
    {
        try
        {
            if (!File.Exists(HelperPaths.StateFile)) return null;
            return JsonSerializer.Deserialize<Contracts.LockStateDto>(File.ReadAllText(HelperPaths.StateFile), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string Format(TimeSpan span) =>
        $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";

    private void ShowLock()
    {
        if (!Visible)
        {
            Show();
            Bounds = SystemInformation.VirtualScreen;
            LogEvent("lock-window-shown");
        }
        TopMost = true;
        BringToFront();
    }

    private void HideIfVisible()
    {
        if (Visible)
        {
            Hide();
            LogEvent("lock-window-hidden");
        }
        WriteStatus(false);
    }

    private static void LogEvent(string message)
    {
        try
        {
            File.AppendAllText(HelperPaths.EventsFile, $"{DateTime.UtcNow:o} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    protected override bool ShowWithoutActivation => false;
}
