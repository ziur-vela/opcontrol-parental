using System.Drawing;
using System.Text.Json;
using OpControl.Parental.Contracts;
using OpControl.Parental.Localization;

namespace OpControl.Parental.Helper;

/// <summary>Self-contained child messaging panel; it can later be embedded in the Luna Chat window.</summary>
public sealed class ChildMessagingPanel : UserControl
{
    private enum LocalState { Sending, Pending, Failed }
    private sealed class LocalMessage
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Text { get; init; } = "";
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
        public LocalState State { get; set; } = LocalState.Sending;
    }
    private sealed record DisplayMessage(
        Guid Id,
        MessageDirection Direction,
        string Sender,
        string Text,
        DateTime CreatedUtc,
        MessageUiStatus Status,
        string Details,
        LocalMessage? Local,
        bool RequireAck,
        bool Acknowledged);

    private readonly TableLayoutPanel _layout;
    private readonly FlowLayoutPanel _thread;
    private readonly TextBox _input;
    private readonly Label _count;
    private readonly Label _status;
    private readonly Button _send;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolTip _toolTip = new();
    private readonly List<LocalMessage> _local = [];
    private FileSystemWatcher? _watcher;
    private bool _busy;
    private bool _loaded;
    private bool _sending;
    private string? _queuedText;

    public ChildMessagingPanel()
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Padding = new Padding(4);

        _layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Color.Transparent
        };
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var heading = new Label
        {
            Text = Loc.T("Client.Messages.YourParents"),
            Font = Theme.Mono(10f, FontStyle.Bold),
            ForeColor = Theme.Label,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(2, 0, 0, 8)
        };

        _thread = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Theme.Panel,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 0, 10)
        };

        _status = new Label
        {
            Text = "",
            Font = Theme.Mono(9f),
            ForeColor = Theme.Muted,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(2, 0, 0, 4)
        };

        _input = new TextBox
        {
            Multiline = true,
            AcceptsReturn = true,
            MaxLength = 500,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            WordWrap = true
        };
        Theme.StyleInput(_input);
        _input.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                await SendAsync();
            }
        };

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            ColumnCount = 3,
            RowCount = 1,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 6, 0, 0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _count = new Label
        {
            Text = "0/500",
            Font = Theme.Mono(8.5f),
            ForeColor = Theme.Muted,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            BackColor = Color.Transparent
        };
        _send = new Button
        {
            Text = Loc.T("Client.Messages.Send"),
            AutoSize = true,
            MinimumSize = new Size(110, 40),
            Anchor = AnchorStyles.Right
        };
        Theme.StyleButton(_send, primary: true);
        _send.Click += async (_, _) => await SendAsync();

        footer.Controls.Add(_count, 0, 0);
        footer.Controls.Add(new Label { Text = "", AutoSize = true }, 1, 0);
        footer.Controls.Add(_send, 2, 0);

        _input.TextChanged += (_, _) =>
        {
            _count.Text = $"{_input.TextLength}/500";
            if (_input.TextLength > 500) _input.Text = _input.Text[..500];
        };

        _layout.Controls.Add(heading, 0, 0);
        _layout.Controls.Add(_thread, 0, 1);
        _layout.Controls.Add(_status, 0, 2);
        _layout.Controls.Add(_input, 0, 3);
        _layout.Controls.Add(footer, 0, 4);
        Controls.Add(_layout);

        _timer = new System.Windows.Forms.Timer { Interval = 30000 };
        _timer.Tick += async (_, _) => await RefreshThreadAsync(showStatus: false);
        _thread.SizeChanged += (_, _) =>
        {
            if (_loaded && IsHandleCreated && !IsDisposed)
                BeginInvoke((Action)(() => BuildFromCache()));
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _layout.RowStyles[3] = new RowStyle(SizeType.Absolute, Dpi.S(this, 96));
        _layout.PerformLayout();
    }

    public void Start()
    {
        if (!_timer.Enabled) _timer.Start();
        StartWatcher();
        _ = RefreshThreadAsync(showStatus: true);
    }

    public void Stop()
    {
        _timer.Stop();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    private void StartWatcher()
    {
        if (_watcher is not null) return;
        try
        {
            Directory.CreateDirectory(HelperPaths.MessageDirectory);
            _watcher = new FileSystemWatcher(HelperPaths.MessageDirectory, "thread.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
                SynchronizingObject = this
            };
            _watcher.Changed += (_, _) => BuildFromCache();
            _watcher.Created += (_, _) => BuildFromCache();
            _watcher.Renamed += (_, _) => BuildFromCache();
        }
        catch
        {
            _watcher = null;
        }
    }

    private async Task RefreshThreadAsync(bool showStatus)
    {
        if (_busy || _sending) return;
        _busy = true;
        try
        {
            if (!_loaded && showStatus && string.IsNullOrWhiteSpace(_status.Text))
                _status.Text = Loc.T("Client.Messages.Loading");
            var response = await PinPipeClient.GetThreadAsync(CancellationToken.None);
            var thread = response.Ok && response.Messages is not null ? response.Messages : ReadCachedThread();
            if (thread is null)
            {
                if (showStatus && !_loaded)
                {
                    _status.Text = response.Error ?? Loc.T("Client.Messages.Loading");
                    _status.ForeColor = Theme.Warn;
                }
                return;
            }
            _loaded = true;
            BuildThread(thread);
            if (showStatus && _status.Text == Loc.T("Client.Messages.Loading"))
                _status.Text = "";
        }
        catch (Exception ex)
        {
            if (showStatus && !_loaded)
            {
                _status.Text = ex.Message;
                _status.ForeColor = Theme.Warn;
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void BuildFromCache()
    {
        if (!_loaded && !File.Exists(HelperPaths.MessageThreadFile)) return;
        var thread = ReadCachedThread() ?? [];
        _loaded = true;
        BuildThread(thread);
    }

    private static List<ChildMessageDto>? ReadCachedThread()
    {
        try
        {
            if (!File.Exists(HelperPaths.MessageThreadFile)) return null;
            return JsonSerializer.Deserialize<List<ChildMessageDto>>(
                File.ReadAllText(HelperPaths.MessageThreadFile), HelperPaths.JsonOptions);
        }
        catch { return null; }
    }

    private static List<MessageOutboxEntry> ReadOutbox() =>
        MessageOutbox.Read(HelperPaths.MessageOutboxFile);

    private void BuildThread(IReadOnlyList<ChildMessageDto> messages)
    {
        var display = new List<DisplayMessage>();
        var serverChild = messages.Where(m => m.Direction == MessageDirection.ChildToParent).ToList();
        bool AlreadyOnServer(DateTime created, string text) =>
            serverChild.Any(m => string.Equals(m.Text, text, StringComparison.Ordinal)
                && Math.Abs((m.CreatedUtc - created).TotalMinutes) < 5);

        foreach (var message in messages.OrderBy(m => m.CreatedUtc))
        {
            var own = message.Direction == MessageDirection.ChildToParent;
            display.Add(new DisplayMessage(
                message.Id,
                message.Direction,
                own ? Loc.T("Client.Messages.You") : MessageDisplay.Sender(message),
                MessageDisplay.Body(message),
                message.CreatedUtc,
                MessageStatusRules.ForDisplay(message),
                MessageDetails(message),
                null,
                message.RequireAck,
                message.AcknowledgedUtc is not null));
        }

        _local.RemoveAll(l => l.State != LocalState.Failed && AlreadyOnServer(l.CreatedUtc, l.Text));
        foreach (var local in _local)
        {
            display.Add(new DisplayMessage(local.Id, MessageDirection.ChildToParent, Loc.T("Client.Messages.You"),
                local.Text, local.CreatedUtc,
                local.State == LocalState.Failed ? MessageUiStatus.Failed : MessageUiStatus.Pending,
                local.State == LocalState.Failed ? Loc.T("Client.Messages.SendFailedHint") : Loc.T("Client.Messages.PendingHint"),
                local, false, false));
        }

        var outbox = ReadOutbox();
        foreach (var pending in outbox)
        {
            if (AlreadyOnServer(pending.CreatedUtc, pending.Text)) continue;
            if (_local.Any(l => string.Equals(l.Text, pending.Text, StringComparison.Ordinal)
                && Math.Abs((l.CreatedUtc - pending.CreatedUtc).TotalMinutes) < 5)) continue;
            display.Add(new DisplayMessage(Guid.NewGuid(), MessageDirection.ChildToParent, Loc.T("Client.Messages.You"),
                pending.Text, pending.CreatedUtc, MessageUiStatus.Pending, Loc.T("Client.Messages.PendingHint"), null, false, false));
        }

        if (_queuedText is not null)
        {
            var stillPending = _local.Any(l => l.State != LocalState.Failed && string.Equals(l.Text, _queuedText, StringComparison.Ordinal))
                || outbox.Any(p => string.Equals(p.Text, _queuedText, StringComparison.Ordinal));
            if (!stillPending)
            {
                _status.Text = Loc.T("Client.Messages.Status.Sent");
                _status.ForeColor = Theme.Value;
                _queuedText = null;
            }
        }

        display.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));
        var width = Math.Max(240, _thread.ClientSize.Width - 28);
        _thread.SuspendLayout();
        try
        {
            _thread.Controls.Clear();
            if (display.Count == 0)
            {
                _thread.Controls.Add(new Label
                {
                    Text = Loc.T("Client.Messages.Empty"),
                    Font = Theme.Sans(10f),
                    ForeColor = Theme.Muted,
                    AutoSize = true,
                    Margin = new Padding(4, 6, 4, 6)
                });
                return;
            }

            foreach (var message in display)
            {
                var own = message.Direction == MessageDirection.ChildToParent;
                var bubble = new Panel
                {
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    BackColor = Theme.Panel2,
                    Padding = new Padding(10, 8, 10, 8),
                    Margin = new Padding(own ? 70 : 0, 0, own ? 0 : 70, 8),
                    MaximumSize = new Size(width, 0)
                };

                var inner = new TableLayoutPanel
                {
                    ColumnCount = 1,
                    RowCount = message.Local?.State == LocalState.Failed ? 3 : 2,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    BackColor = Color.Transparent,
                    Dock = DockStyle.Fill
                };

                var head = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    BackColor = Color.Transparent,
                    Margin = new Padding(0, 0, 0, 3)
                };
                head.Controls.Add(new Label
                {
                    Text = message.Sender,
                    Font = Theme.Mono(8.5f),
                    ForeColor = own ? Theme.Value : Theme.Label,
                    AutoSize = true,
                    BackColor = Color.Transparent,
                    Margin = new Padding(0, 0, 6, 0)
                });
                head.Controls.Add(new Label
                {
                    Text = MessageDisplay.Time(message.CreatedUtc),
                    Font = Theme.Mono(8.5f),
                    ForeColor = Theme.Muted,
                    AutoSize = true,
                    BackColor = Color.Transparent,
                    Margin = new Padding(0, 0, 8, 0)
                });

                var statusLabel = new Label
                {
                    Text = $"{MessageStatusRules.Icon(message.Status)} {StatusText(message.Status)}",
                    Font = Theme.Mono(8.5f, FontStyle.Bold),
                    ForeColor = StatusColor(message.Status),
                    AutoSize = true,
                    BackColor = Color.Transparent,
                    Cursor = Cursors.Hand,
                    Margin = new Padding(0, 0, 6, 0)
                };
                _toolTip.SetToolTip(statusLabel, message.Details + Environment.NewLine + Loc.T("Client.Messages.TapForTimes"));
                statusLabel.Click += (_, _) => MessageBox.Show(FindForm(), message.Details,
                    Loc.T("Client.Messages.StatusTitle"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                head.Controls.Add(statusLabel);

                if (message.Direction == MessageDirection.ParentToChild && message.Acknowledged)
                {
                    head.Controls.Add(new Label
                    {
                        Text = Loc.T("Client.Messages.Status.Ack"),
                        Font = Theme.Mono(8f),
                        ForeColor = Theme.Brand,
                        AutoSize = true,
                        BackColor = Color.Transparent
                    });
                }

                var body = new Label
                {
                    Text = message.Text,
                    Font = Theme.Sans(10.5f),
                    ForeColor = Theme.Text,
                    AutoSize = true,
                    MaximumSize = new Size(Math.Max(160, width - 28), 0),
                    BackColor = Color.Transparent,
                    Margin = new Padding(0)
                };
                inner.Controls.Add(head, 0, 0);
                inner.Controls.Add(body, 0, 1);

                if (message.Local?.State == LocalState.Failed)
                {
                    var retry = new Button
                    {
                        Text = Loc.T("Client.Messages.Retry"),
                        AutoSize = true,
                        MinimumSize = new Size(96, 32),
                        Margin = new Padding(0, 6, 0, 0)
                    };
                    Theme.StyleButton(retry, danger: true);
                    var local = message.Local;
                    retry.Click += async (_, _) => await RetryAsync(local);
                    inner.Controls.Add(retry, 0, 2);
                }

                bubble.Controls.Add(inner);
                _thread.Controls.Add(bubble);
            }
        }
        finally
        {
            _thread.ResumeLayout(true);
        }

        if (_thread.Controls.Count > 0)
            _thread.ScrollControlIntoView(_thread.Controls[^1]);
    }

    private static string StatusText(MessageUiStatus status) => Loc.T(status switch
    {
        MessageUiStatus.Pending => "Client.Messages.Status.Pending",
        MessageUiStatus.Failed => "Client.Messages.Status.Failed",
        MessageUiStatus.Delivered => "Client.Messages.Status.Delivered",
        MessageUiStatus.Read => "Client.Messages.Status.Read",
        _ => "Client.Messages.Status.Sent"
    });

    private static Color StatusColor(MessageUiStatus status) => status switch
    {
        MessageUiStatus.Read => Theme.Value,
        MessageUiStatus.Failed => Theme.Danger,
        MessageUiStatus.Pending => Theme.Muted,
        _ => Theme.Muted
    };

    private static string MessageDetails(ChildMessageDto message)
    {
        var lines = new List<string>
        {
            Loc.T("Client.Messages.StatusAt", Loc.T("Client.Messages.Status.Sent"), FormatTime(message.CreatedUtc))
        };
        if (message.Direction == MessageDirection.ParentToChild)
        {
            if (message.DeliveredUtc is { } delivered)
                lines.Add(Loc.T("Client.Messages.StatusAt", Loc.T("Client.Messages.Status.Delivered"), FormatTime(delivered))
                    + (string.IsNullOrWhiteSpace(message.DeliveredBy) ? "" : Loc.T("Client.Messages.By", message.DeliveredBy)));
            if (message.SeenUtc is { } seen)
                lines.Add(Loc.T("Client.Messages.StatusAt", Loc.T("Client.Messages.Status.Read"), FormatTime(seen))
                    + (string.IsNullOrWhiteSpace(message.SeenBy) ? "" : Loc.T("Client.Messages.By", message.SeenBy)));
            if (message.AcknowledgedUtc is { } ack)
                lines.Add(Loc.T("Client.Messages.StatusAt", Loc.T("Client.Messages.Status.Ack"), FormatTime(ack))
                    + (string.IsNullOrWhiteSpace(message.AcknowledgedBy) ? "" : Loc.T("Client.Messages.By", message.AcknowledgedBy)));
        }
        else
        {
            if (message.DeliveredUtc is { } delivered)
                lines.Add(Loc.T("Client.Messages.StatusAt", Loc.T("Client.Messages.Status.Delivered"), FormatTime(delivered))
                    + (string.IsNullOrWhiteSpace(message.ParentDeliveredBy) ? "" : Loc.T("Client.Messages.By", message.ParentDeliveredBy)));
            if (message.ParentReadUtc is { } read)
                lines.Add(Loc.T("Client.Messages.StatusAt", Loc.T("Client.Messages.Status.Read"), FormatTime(read))
                    + (string.IsNullOrWhiteSpace(message.ParentReadBy) ? "" : Loc.T("Client.Messages.By", message.ParentReadBy)));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatTime(DateTime utc) =>
        utc.ToLocalTime().ToString("d MMM, HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    private async Task SendAsync()
    {
        if (_sending) return;
        var text = (_input.Text ?? "").Trim();
        if (text.Length == 0)
        {
            ShowStatus(Loc.T("Client.Messages.EmptyInput"), error: true);
            HelperPaths.LogEvent("message-send-early reason=empty");
            return;
        }
        if (text.Length > 500)
        {
            ShowStatus(Loc.T("Client.Messages.TooLong"), error: true);
            HelperPaths.LogEvent($"message-send-early reason=too-long len={text.Length}");
            return;
        }

        HelperPaths.LogEvent($"message-send-click len={text.Length}");
        var local = new LocalMessage { Text = text };
        _local.Add(local);
        _input.Text = "";
        _count.Text = "0/500";
        _sending = true;
        _send.Enabled = false;
        ShowStatus(Loc.T("Client.Messages.Sending"), error: false);
        BuildFromCache();
        try
        {
            HelperPaths.LogEvent("message-send-pipe-start");
            var response = await PinPipeClient.SendMessageAsync(text, CancellationToken.None);
            HelperPaths.LogEvent($"message-send-pipe-response ok={response.Ok} queued={response.Queued} error={response.Error ?? ""}");
            if (response.Ok)
            {
                _local.Remove(local);
                ShowStatus(Loc.T("Client.Messages.Status.Sent"), error: false);
                BuildFromCache();
                await RefreshThreadAsync(showStatus: false);
            }
            else if (response.Queued)
            {
                local.State = LocalState.Pending;
                _queuedText = text;
                ShowStatus(Loc.T("Client.Messages.Queued"), error: false);
                BuildFromCache();
            }
            else
            {
                local.State = LocalState.Failed;
                ShowStatus(response.Error ?? Loc.T("Client.Messages.SendFailedHint"), error: true);
                BuildFromCache();
            }
        }
        catch (Exception ex)
        {
            local.State = LocalState.Failed;
            ShowStatus(ex.Message, error: true);
            HelperPaths.LogEvent($"message-send-exception {ex.GetType().Name}: {ex.Message}");
            BuildFromCache();
        }
        finally
        {
            _sending = false;
            _send.Enabled = true;
            HelperPaths.LogEvent("message-send-thread-refresh");
        }
    }

    private async Task RetryAsync(LocalMessage local)
    {
        _local.Remove(local);
        _input.Text = local.Text;
        _input.SelectionStart = _input.TextLength;
        await SendAsync();
    }

    private void ShowStatus(string text, bool error)
    {
        _status.Text = text;
        _status.ForeColor = error ? Theme.Danger : Theme.Value;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _toolTip.Dispose();
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }
        base.Dispose(disposing);
    }
}
