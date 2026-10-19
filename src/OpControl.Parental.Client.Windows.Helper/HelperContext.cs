using OpControl.Parental.Contracts;

namespace OpControl.Parental.Helper;

public sealed class HelperContext : ApplicationContext
{
    private readonly string _monitored;
    private readonly LockOverlayManager _overlays;
    private readonly CountdownForm _countdown;
    private readonly KeyboardHook _hook = new();
    private readonly System.Windows.Forms.Timer _timer;
    private PinDialog? _pinDialog;
    private NotificationForm? _notification;
    private readonly DateTime _helperStartedUtc = DateTime.UtcNow;
    private DateTime _lastNotificationUtc = DateTime.MinValue;
    private bool _lastVisible;
    private bool _hookInstalled;

    public HelperContext(string monitored)
    {
        _monitored = monitored;
        _overlays = new LockOverlayManager();
        _overlays.OverrideClicked += ShowPinDialog;
        _countdown = new CountdownForm();
        _timer = new System.Windows.Forms.Timer { Interval = 500 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        HelperPaths.LogEvent($"helper-started session={System.Diagnostics.Process.GetCurrentProcess().SessionId} user={Environment.UserName}");
        Tick();
    }

    private void Tick()
    {
        CheckNotification();
        var state = HelperPaths.ReadState();
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
            if (!_hookInstalled && _hook.Install())
            {
                _hookInstalled = true;
                HelperPaths.LogEvent("keyboard-hook-installed");
            }
            _lastVisible = true;
            HelperPaths.WriteStatus(true, true, state.Reason);
            return;
        }

        _overlays.Hide();
        _hook.Uninstall();
        if (_hookInstalled)
        {
            _hookInstalled = false;
            HelperPaths.LogEvent("keyboard-hook-removed");
        }

        var now = DateTime.UtcNow;
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
        else
        {
            _countdown.HideBanner();
            SetVisible(false, false, state.Reason);
        }
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
        dialog.Show();
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
        menu.Show();
        menu.BringToFront();
        menu.Activate();
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
            _hook.Dispose();
            _overlays.Dispose();
            _countdown.Dispose();
            _pinDialog?.Dispose();
            _notification?.Dispose();
        }
        base.Dispose(disposing);
    }
}
