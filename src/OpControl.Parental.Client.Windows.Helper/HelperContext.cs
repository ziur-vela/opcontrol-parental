using OpControl.Parental.Contracts;

namespace OpControl.Parental.Helper;

public sealed class HelperContext : ApplicationContext
{
    private readonly string _monitored;
    private readonly LockOverlayManager _overlays;
    private readonly CountdownForm _countdown;
    private readonly KeyboardHook _hook = new();
    private readonly UsageSampler _usage = new();
    private readonly System.Windows.Forms.Timer _timer;
    private readonly RemoteCaptureHost _remote;
    private readonly Control _marshal = new();
    private RemoteNoticeForm? _notice;
    private PinDialog? _pinDialog;
    private NotificationForm? _notification;
    private readonly DateTime _helperStartedUtc = DateTime.UtcNow;
    private DateTime _lastNotificationUtc = DateTime.MinValue;
    private bool _lastVisible;
    private bool _hookInstalled;
    private bool _wasLocked;
    private readonly HashSet<int> _warnedThresholds = [];
    private DateTime _warningDay = DateTime.MinValue;

    public HelperContext(string monitored)
    {
        _monitored = monitored;
        _overlays = new LockOverlayManager();
        _overlays.OverrideClicked += ShowPinDialog;
        _countdown = new CountdownForm();
        _ = _marshal.Handle;
        _remote = new RemoteCaptureHost(monitored);
        _remote.NoticeChanged += OnRemoteNoticeChanged;
        _remote.Start();
        _timer = new System.Windows.Forms.Timer { Interval = 500 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        HelperPaths.LogEvent($"helper-started session={System.Diagnostics.Process.GetCurrentProcess().SessionId} user={Environment.UserName}");
        Tick();
    }

    private void Tick()
    {
        UpdateRemoteNotice();
        CheckNotification();
        var state = HelperPaths.ReadState();
        _usage.Sample(state);
        if (state is null)
        {
            _overlays.Hide();
            _countdown.HideBanner();
            _hook.Uninstall();
            HelperPaths.WriteStatus(false, false, null);
            return;
        }

        if (state.IsLocked)
        {
            _countdown.HideBanner();
            _overlays.Update(state);
            if (state.Reason == "daily-limit" && !_wasLocked)
                HelperPaths.LogEvent("daily-limit-lock");
            _wasLocked = true;
            if (!_hookInstalled && _hook.Install())
            {
                _hookInstalled = true;
                HelperPaths.LogEvent("keyboard-hook-installed");
            }
            _lastVisible = true;
            HelperPaths.WriteStatus(true, true, state.Reason);
            return;
        }

        _wasLocked = false;
        _overlays.Hide();
        _hook.Uninstall();
        if (_hookInstalled)
        {
            _hookInstalled = false;
            HelperPaths.LogEvent("keyboard-hook-removed");
        }

        var now = DateTime.UtcNow;
        CheckDailyWarnings(state);
        if (state.BonusUntilUtc is { } bonus && bonus > now)
        {
            var remaining = bonus - now;
            var minutes = state.BonusMinutes ?? 0;
            var text = $"Bonus +{minutes} min · {Format(remaining)} left";
            _countdown.UpdateText(text, remaining);
            SetVisible(true, false, state.Reason, text);
        }
        else if (state.NextLockUtc is { } next && next > now)
        {
            var remaining = next - now;
            var text = $"Lock in {Format(remaining)}";
            _countdown.UpdateText(text, remaining);
            SetVisible(true, false, state.Reason, text);
        }
        else if (state.DailyLimitRemainingSeconds is { } dailyLeft && dailyLeft > 0)
        {
            var remaining = TimeSpan.FromSeconds(dailyLeft);
            var text = $"{FormatShort(remaining)} left today";
            _countdown.UpdateText(text, remaining);
            SetVisible(true, false, state.Reason, text);
        }
        else
        {
            _countdown.HideBanner();
            SetVisible(false, false, state.Reason);
        }
    }

    private void OnRemoteNoticeChanged()
    {
        try
        {
            if (!_marshal.IsDisposed && _marshal.IsHandleCreated) _marshal.BeginInvoke((Action)UpdateRemoteNotice);
        }
        catch (ObjectDisposedException) { }
    }

    private void UpdateRemoteNotice()
    {
        if (_remote.NoticeActive && !string.IsNullOrWhiteSpace(_remote.NoticeText))
        {
            _notice ??= new RemoteNoticeForm(_remote.NoticeText!);
            _notice.ShowNotice(_remote.NoticeText!);
        }
        else
        {
            _notice?.HideNotice();
        }
    }

    private void CheckDailyWarnings(RuntimeLockStateDto state)
    {
        var today = DateTime.Today;
        if (today != _warningDay)
        {
            _warningDay = today;
            _warnedThresholds.Clear();
        }

        if (state.DailyLimitRemainingSeconds is not { } left || left <= 0 || state.BonusUntilUtc is { } bonus && bonus > DateTime.UtcNow)
            return;

        foreach (var minutes in new[] { 15, 5, 1 })
        {
            if (left > minutes * 60 || left <= (minutes - 1) * 60) continue;
            if (!_warnedThresholds.Add(minutes)) continue;
            var message = $"{minutes} minute{(minutes == 1 ? "" : "s")} of screen time left today.";
            ShowNotification("Screen time", message);
            if (state.SpokenWarnings) Speak(message);
            HelperPaths.LogEvent($"daily-warning-{minutes}");
            break;
        }
    }

    private void ShowNotification(string title, string message)
    {
        try
        {
            _notification?.Close();
            _notification = new NotificationForm(title, message);
            _notification.Show();
        }
        catch { }
    }

    private static void Speak(string text)
    {
        try
        {
            var type = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (type is null) return;
            dynamic? voice = Activator.CreateInstance(type);
            voice?.Speak(text, 0);
        }
        catch { }
    }

    private void CheckNotification()
    {
        var notification = NotificationForm.Read();
        if (notification is null) return;
        var (title, message, utc) = notification.Value;
        if (utc <= _lastNotificationUtc) return;
        _lastNotificationUtc = utc;
        if (utc < _helperStartedUtc) return;
        try
        {
            _notification?.Close();
            _notification = new NotificationForm(title, message);
            _notification.Show();
            HelperPaths.LogEvent($"notification-shown {title}: {message}");
        }
        catch { }
    }

    private void SetVisible(bool countdown, bool locked, string? reason, string? banner = null)
    {
        if (_lastVisible != countdown)
        {
            HelperPaths.LogEvent(countdown ? "countdown-shown" : "countdown-hidden");
            _lastVisible = countdown;
        }
        HelperPaths.WriteStatus(locked, countdown, reason, banner);
    }

    private static string Format(TimeSpan span) =>
        $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";

    private static string FormatShort(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{Math.Max(1, span.Minutes)}m";

    private void ShowPinDialog()
    {
        if (_pinDialog is { IsDisposed: false })
        {
            _pinDialog.BringToFront();
            _pinDialog.Activate();
            return;
        }

        var dialog = new PinDialog();
        dialog.Verified += ticket =>
        {
            dialog.Close();
            ShowOverrideMenu(ticket);
        };
        dialog.FormClosed += (_, _) => _pinDialog = null;
        _pinDialog = dialog;
        ShowOwned(dialog);
        dialog.BringToFront();
        dialog.Activate();
    }

    private void ShowOverrideMenu(string ticket)
    {
        OverrideMenu? menu = null;
        menu = new OverrideMenu(
            (option, minutes) =>
            {
                menu!.Close();
                _ = ApplyOverrideAsync(ticket, option, minutes);
            },
            () => menu?.Close());
        ShowOwned(menu);
        menu.BringToFront();
        menu.Activate();
    }

    private void ShowOwned(Form dialog)
    {
        var owner = _overlays.Visible ? _overlays.Owner : null;
        if (owner is { IsDisposed: false }) dialog.Show(owner);
        else dialog.Show();
    }

    private async Task ApplyOverrideAsync(string ticket, string option, int? minutes)
    {
        var response = await PinPipeClient.ApplyOverrideAsync(ticket, option, minutes, CancellationToken.None);
        if (!response.Ok)
        {
            MessageBox.Show(response.Error ?? "The override could not be applied.", "Parent override",
                MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1,
                MessageBoxOptions.DefaultDesktopOnly);
        }
        else
        {
            HelperPaths.LogEvent($"override-applied option={option} minutes={minutes}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _remote.Dispose();
            _notice?.Dispose();
            _marshal.Dispose();
            _hook.Dispose();
            _overlays.Dispose();
            _countdown.Dispose();
            _pinDialog?.Dispose();
            _notification?.Dispose();
            _usage.FlushAll();
        }
        base.Dispose(disposing);
    }
}
