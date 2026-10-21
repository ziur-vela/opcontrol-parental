using System.Reflection;
using OpControl.Parental.Contracts;
using OpControl.Parental.Localization;

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
    private MessagePopupForm? _messagePopup;
    private ChildMessageForm? _childMessageForm;
    private NotifyIcon? _tray;
    private ToolStripMenuItem? _trayMessageItem;
    private Icon? _trayIcon;
    private Bitmap? _trayBitmap;
    private string _trayLanguage = Loc.Language;
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
        _overlays.MessageClicked += ShowChildMessageWindow;
        _countdown = new CountdownForm();
        _ = _marshal.Handle;
        _remote = new RemoteCaptureHost(monitored);
        _remote.NoticeChanged += OnRemoteNoticeChanged;
        _remote.Start();
        CreateTrayIcon();
        _timer = new System.Windows.Forms.Timer { Interval = 500 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        HelperPaths.LogEvent($"helper-started session={System.Diagnostics.Process.GetCurrentProcess().SessionId} user={Environment.UserName}");
        Tick();
    }

    private void Tick()
    {
        var state = HelperPaths.ReadState();
        Loc.SetLanguage(state?.Language);
        UpdateRemoteNotice();
        CheckNotification();
        if (state is { IsLocked: true } && !_overlays.Visible)
            _overlays.Update(state);
        CheckMessagePopups();
        UpdateTrayLanguage();
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
            var text = Loc.T("Client.Countdown.Bonus", minutes, Format(remaining));
            _countdown.UpdateText(text, remaining);
            SetVisible(true, false, state.Reason, text);
        }
        else if (state.NextLockUtc is { } next && next > now)
        {
            var remaining = next - now;
            var text = Loc.T("Client.Countdown.LockIn", Format(remaining));
            _countdown.UpdateText(text, remaining);
            SetVisible(true, false, state.Reason, text);
        }
        else if (state.DailyLimitRemainingSeconds is { } dailyLeft && dailyLeft > 0)
        {
            var remaining = TimeSpan.FromSeconds(dailyLeft);
            var text = Loc.T("Client.Countdown.LeftToday", FormatShort(remaining));
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

    private void CreateTrayIcon()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("OpControl.Parental.Helper.brand.icon-color-128.png");
            if (stream is not null)
            {
                _trayBitmap = new Bitmap(stream);
                _trayIcon = Icon.FromHandle(_trayBitmap.GetHicon());
            }
            _trayIcon ??= SystemIcons.Shield;

            _trayMessageItem = new ToolStripMenuItem(Loc.T("Client.Messages.Open"));
            _trayMessageItem.Click += (_, _) => ShowChildMessageWindow();

            var menu = new ContextMenuStrip();
            menu.Items.Add(_trayMessageItem);
            _tray = new NotifyIcon
            {
                Icon = _trayIcon,
                Text = "Parental OpControl",
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += (_, _) => ShowChildMessageWindow();
        }
        catch (Exception ex)
        {
            HelperPaths.LogEvent($"tray-icon-failed {ex.Message}");
        }
    }

    private void UpdateTrayLanguage()
    {
        if (_trayMessageItem is null || _trayLanguage == Loc.Language) return;
        _trayLanguage = Loc.Language;
        _trayMessageItem.Text = Loc.T("Client.Messages.Open");
    }

    private void CheckMessagePopups()
    {
        if (_messagePopup is { IsDisposed: false }) return;
        try
        {
            if (!Directory.Exists(HelperPaths.MessagePopupDirectory)) return;
            var files = Directory.GetFiles(HelperPaths.MessagePopupDirectory, "*.json");
            if (files.Length == 0) return;

            ChildMessageDto? next = null;
            string? nextPath = null;
            foreach (var file in files)
            {
                ChildMessageDto? message = null;
                try
                {
                    message = System.Text.Json.JsonSerializer.Deserialize<ChildMessageDto>(
                        File.ReadAllText(file), HelperPaths.JsonOptions);
                }
                catch { }
                if (message is null)
                {
                    try { File.Delete(file); } catch { }
                    continue;
                }
                if (DateTime.UtcNow - message.CreatedUtc > TimeSpan.FromHours(24))
                {
                    try { File.Delete(file); } catch { }
                    continue;
                }
                if (next is null || message.CreatedUtc < next.CreatedUtc)
                {
                    next = message;
                    nextPath = file;
                }
            }

            if (next is null || nextPath is null) return;
            try { File.Delete(nextPath); } catch { }
            ShowMessagePopup(next);
        }
        catch (Exception ex)
        {
            HelperPaths.LogEvent($"message-popup-failed {ex.Message}");
        }
    }

    private void ShowMessagePopup(ChildMessageDto message)
    {
        try
        {
            var popup = new MessagePopupForm(message);
            popup.Seen += m => _ = ReportSeenAsync(m.Id);
            popup.Acknowledged += m => _ = ReportAcknowledgedAsync(m.Id);
            popup.ReplyClicked += OpenChildMessagesFromPopup;
            popup.FormClosed += (_, _) => _messagePopup = null;
            _messagePopup = popup;

            var owner = _overlays.Visible ? _overlays.Owner : null;
            if (owner is { IsDisposed: false }) popup.Show(owner);
            else popup.Show();
            popup.BringToFront();
            popup.Activate();
            HelperPaths.LogEvent($"message-popup-shown id={message.Id}");
        }
        catch (Exception ex)
        {
            HelperPaths.LogEvent($"message-popup-show-failed {ex.Message}");
        }
    }

    private void OpenChildMessagesFromPopup()
    {
        _messagePopup = null;
        ShowChildMessageWindow();
    }

    private void ShowChildMessageWindow()
    {
        try
        {
            if (_childMessageForm is { IsDisposed: false })
            {
                _childMessageForm.BringToFront();
                _childMessageForm.Activate();
                return;
            }

            var aboveLock = _overlays.Visible;
            var form = new ChildMessageForm(aboveLock);
            form.FormClosed += (_, _) => _childMessageForm = null;
            _childMessageForm = form;
            var owner = aboveLock ? _overlays.Owner : null;
            if (owner is { IsDisposed: false }) form.Show(owner);
            else form.Show();
            form.BringToFront();
            form.Activate();
            HelperPaths.LogEvent("message-window-opened");
        }
        catch (Exception ex)
        {
            HelperPaths.LogEvent($"message-window-failed {ex.Message}");
        }
    }

    private static async Task ReportSeenAsync(Guid messageId)
    {
        try
        {
            var response = await PinPipeClient.MarkSeenAsync(messageId, CancellationToken.None);
            if (!response.Ok) HelperPaths.LogEvent($"message-seen-failed {messageId} {response.Error}");
        }
        catch { }
    }

    private static async Task ReportAcknowledgedAsync(Guid messageId)
    {
        try
        {
            var response = await PinPipeClient.MarkAcknowledgedAsync(messageId, CancellationToken.None);
            if (!response.Ok) HelperPaths.LogEvent($"message-ack-failed {messageId} {response.Error}");
        }
        catch { }
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
            var message = Loc.T(minutes == 1 ? "Client.Notice.MinutesLeftOne" : "Client.Notice.MinutesLeftMany", minutes);
            ShowNotification(Loc.T("Client.Notice.ScreenTime"), message);
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
            MessageBox.Show(response.Error ?? Loc.T("Client.OverrideFailedBody"), Loc.T("Client.OverrideFailedTitle"),
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
            _messagePopup?.Dispose();
            _childMessageForm?.Dispose();
            if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
            _trayIcon?.Dispose();
            _trayBitmap?.Dispose();
            _usage.FlushAll();
        }
        base.Dispose(disposing);
    }
}
