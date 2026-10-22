// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Drawing;
using System.Text.Json;
using OpControl.Parental.Contracts;
using OpControl.Parental.Localization;

namespace OpControl.Parental.Helper;

/// <summary>Child messaging panel, embedded as the Messages tab of My OpControl.</summary>
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
    private readonly Label _legend;
    private readonly Button _send;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly System.Windows.Forms.Timer _highlightTimer;
    private readonly ToolTip _toolTip = new();
    private readonly List<LocalMessage> _local = [];
    private FileSystemWatcher? _watcher;
    private bool _busy;
    private bool _loaded;
    private bool _sending;
    private string? _queuedText;
    private DateTime _clearStatusAt = DateTime.MaxValue;
    private Guid? _highlightId;
    private DateTime _highlightUntil;
    private int _unread;

    public event Action<int>? UnreadChanged;

    public int UnreadCount => _unread;

    public ChildMessagingPanel()
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Padding = new Padding(0);

        _layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            BackColor = Color.Transparent
        };
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _thread = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Theme.Panel,
            Padding = new Padding(12),
            Margin = new Padding(0, 0, 0, 8)
        };

        _status = new Label
        {
            Text = "",
            Font = Theme.Mono(9f),
            ForeColor = Theme.Muted,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(2, 0, 0, 6)
        };

        var quick = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 6)
        };
        AddQuickReply(quick, "Client.Messages.Quick.MoreTime");
        AddQuickReply(quick, "Client.Messages.Quick.Homework");
        AddQuickReply(quick, "Client.Messages.Quick.ComeHere");

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

        var meta = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 6, 0, 0)
        };
        meta.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        meta.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _legend = new Label
        {
            Text = Loc.T("Client.Messages.Legend"),
            Font = Theme.Mono(8f),
            ForeColor = Theme.Muted,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            BackColor = Color.Transparent
        };
        _count = new Label
        {
            Text = "0/500",
            Font = Theme.Mono(8.5f),
            ForeColor = Theme.Muted,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            BackColor = Color.Transparent
        };
        meta.Controls.Add(_legend, 0, 0);
        meta.Controls.Add(_count, 1, 0);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 6, 0, 0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _send = new Button
        {
            Text = Loc.T("Client.Messages.Send"),
            AutoSize = true,
            MinimumSize = new Size(120, 40),
            Anchor = AnchorStyles.Right
        };
        Theme.StyleButton(_send, primary: true);
        _send.Click += async (_, _) => await SendAsync();
        footer.Controls.Add(new Label { Text = "", AutoSize = true }, 0, 0);
        footer.Controls.Add(_send, 1, 0);

        _input.TextChanged += (_, _) =>
        {
            _count.Text = $"{_input.TextLength}/500";
            if (_input.TextLength > 500) _input.Text = _input.Text[..500];
        };

        _layout.Controls.Add(_thread, 0, 0);
        _layout.Controls.Add(_status, 0, 1);
        _layout.Controls.Add(quick, 0, 2);
        _layout.Controls.Add(_input, 0, 3);
        _layout.Controls.Add(meta, 0, 4);
        _layout.Controls.Add(footer, 0, 5);
        Controls.Add(_layout);

        _timer = new System.Windows.Forms.Timer { Interval = 30000 };
        _timer.Tick += async (_, _) => await RefreshThreadAsync(showStatus: false);
        _statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _statusTimer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow >= _clearStatusAt)
            {
                _status.Text = "";
                _clearStatusAt = DateTime.MaxValue;
            }
        };
        _statusTimer.Start();
        _highlightTimer = new System.Windows.Forms.Timer { Interval = 1700 };
        _highlightTimer.Tick += (_, _) =>
        {
            _highlightTimer.Stop();
            _highlightId = null;
            if (_loaded && IsHandleCreated && !IsDisposed)
                BeginInvoke((Action)(() => BuildFromCache()));
        };
        _thread.SizeChanged += (_, _) =>
        {
            if (_loaded && IsHandleCreated && !IsDisposed)
                BeginInvoke((Action)(() => BuildFromCache()));
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _legend.Text = Loc.T("Client.Messages.Legend");
        _layout.PerformLayout();
    }

    private void AddQuickReply(FlowLayoutPanel row, string key)
    {
        var button = new Button
        {
            Text = Loc.T(key),
            AutoSize = true,
            Height = 34,
            Margin = new Padding(0, 0, 6, 4)
        };
        Theme.StyleButton(button);
        button.Font = Theme.Mono(8.5f);
        button.Click += (_, _) =>
        {
            _input.Text = Loc.T(key);
            _input.SelectionStart = _input.TextLength;
            _input.Focus();
        };
        row.Controls.Add(button);
    }

    public void Start()
    {
        if (!_timer.Enabled) _timer.Start();
        StartWatcher();
        BuildFromCache();
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

    public void Highlight(Guid messageId)
    {
        _highlightId = messageId;
        _highlightUntil = DateTime.UtcNow.AddSeconds(2);
        _highlightTimer.Stop();
        _highlightTimer.Start();
        BuildFromCache();
    }

    public async Task MarkSeenAsync(Guid messageId)
    {
        try
        {
            var response = await PinPipeClient.MarkSeenAsync(messageId, CancellationToken.None);
            if (!response.Ok) HelperPaths.LogEvent($"message-seen-failed {messageId} {response.Error}");
        }
        catch { }
    }

    public async Task MarkAllSeenAsync()
    {
        var thread = ReadCachedThread() ?? [];
        foreach (var message in thread.Where(m => m.Direction == MessageDirection.ParentToChild && m.SeenUtc is null))
            await MarkSeenAsync(message.Id);
        _ = RefreshThreadAsync(showStatus: false);
    }

    public static List<ChildMessageDto>? ReadCache() => ReadCachedThread();

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
                ShowStatus(Loc.T("Client.Messages.Status.Sent"), error: false, clearAfter: TimeSpan.FromSeconds(4));
                _queuedText = null;
            }
        }

        display.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));
        _unread = messages.Count(m => m.Direction == MessageDirection.ParentToChild && m.SeenUtc is null && !m.Deleted);
        UnreadChanged?.Invoke(_unread);

        var wasAtBottom = AtBottom();
        var rowWidth = ChatLayout.RowWidth(_thread.ClientSize.Width, _thread.Padding.Horizontal);
        var maxBubble = ChatLayout.MaxBubbleWidth(rowWidth);
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
                _thread.Controls.Add(BuildBubbleRow(message, rowWidth, maxBubble));
        }
        finally
        {
            _thread.ResumeLayout(true);
        }

        if (wasAtBottom)
            ScrollToBottom();
    }

    private Control BuildBubbleRow(DisplayMessage message, int rowWidth, int maxBubble)
    {
        var own = message.Direction == MessageDirection.ChildToParent;
        var row = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            Width = rowWidth,
            MinimumSize = new Size(rowWidth, 0),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(0)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, ChatLayout.FirstColumnPercent(own)));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, ChatLayout.SecondColumnPercent(own)));

        var highlight = _highlightId is { } id && id == message.Id && DateTime.UtcNow < _highlightUntil;
        var bubble = new CardPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MaximumSize = new Size(maxBubble, 0),
            Padding = new Padding(11, 9, 11, 9),
            BackColor = highlight ? Theme.Highlight : own ? Theme.OwnBubble : Theme.ParentBubble,
            BorderColor = own ? Theme.OwnBubbleBorder : Theme.ParentBubbleBorder,
            Anchor = own ? AnchorStyles.Right | AnchorStyles.Top : AnchorStyles.Left | AnchorStyles.Top,
            Margin = new Padding(0)
        };

        var inner = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 3,
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
            Margin = new Padding(0, 0, 0, 0)
        });

        if (own)
        {
            var statusLabel = new Label
            {
                Text = $"{MessageStatusRules.Icon(message.Status)} {StatusText(message.Status)}",
                Font = Theme.Mono(8.5f, FontStyle.Bold),
                ForeColor = StatusColor(message.Status),
                AutoSize = true,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Margin = new Padding(8, 0, 0, 0)
            };
            _toolTip.SetToolTip(statusLabel, message.Details + Environment.NewLine + Loc.T("Client.Messages.TapForTimes"));
            statusLabel.Click += (_, _) => MessageBox.Show(FindForm(), message.Details,
                Loc.T("Client.Messages.StatusTitle"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            head.Controls.Add(statusLabel);
        }

        var body = new Label
        {
            Text = message.Text,
            Font = Theme.Sans(10.5f),
            ForeColor = Theme.Text,
            AutoSize = true,
            MaximumSize = new Size(Math.Max(150, maxBubble - 24), 0),
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        inner.Controls.Add(head, 0, 0);
        inner.Controls.Add(body, 0, 1);

        if (!own && message.RequireAck)
        {
            var ack = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 6, 0, 0)
            };
            if (message.Acknowledged)
            {
                ack.Controls.Add(new Label
                {
                    Text = "✓ " + Loc.T("Client.Messages.Status.Ack"),
                    Font = Theme.Mono(8.5f, FontStyle.Bold),
                    ForeColor = Theme.BrandText,
                    AutoSize = true,
                    BackColor = Color.Transparent
                });
            }
            else
            {
                var button = new Button
                {
                    Text = Loc.T("Client.Messages.GotIt"),
                    AutoSize = true,
                    MinimumSize = new Size(100, 32),
                    Margin = new Padding(0)
                };
                Theme.StyleButton(button, primary: true);
                button.Font = Theme.Mono(8.5f, FontStyle.Bold);
                button.Click += async (_, _) => await AckAsync(message.Id);
                ack.Controls.Add(button);
            }
            inner.Controls.Add(ack, 0, 2);
        }
        else if (message.Local?.State == LocalState.Failed)
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
        row.Controls.Add(bubble, ChatLayout.ColumnFor(own), 0);
        return row;
    }

    private async Task AckAsync(Guid messageId)
    {
        try
        {
            var response = await PinPipeClient.MarkAcknowledgedAsync(messageId, CancellationToken.None);
            if (!response.Ok) HelperPaths.LogEvent($"message-ack-failed {messageId} {response.Error}");
        }
        catch { }
        await RefreshThreadAsync(showStatus: false);
    }

    private bool AtBottom()
    {
        try
        {
            return _thread.VerticalScroll.Value + _thread.ClientSize.Height >= _thread.VerticalScroll.Maximum - 16;
        }
        catch { return true; }
    }

    private void ScrollToBottom()
    {
        if (_thread.Controls.Count == 0) return;
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
        ShowStatus(Loc.T("Client.Messages.Sending"), error: false, clearAfter: TimeSpan.Zero);
        BuildFromCache();
        try
        {
            HelperPaths.LogEvent("message-send-pipe-start");
            var response = await PinPipeClient.SendMessageAsync(text, CancellationToken.None);
            HelperPaths.LogEvent($"message-send-pipe-response ok={response.Ok} queued={response.Queued} error={response.Error ?? ""}");
            if (response.Ok)
            {
                _local.Remove(local);
                ShowStatus(Loc.T("Client.Messages.Status.Sent"), error: false, clearAfter: TimeSpan.FromSeconds(4));
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

    private void ShowStatus(string text, bool error, TimeSpan? clearAfter = null)
    {
        _status.Text = text;
        _status.ForeColor = error ? Theme.Danger : Theme.Value;
        _clearStatusAt = clearAfter is { } span
            ? DateTime.UtcNow.Add(span)
            : error ? DateTime.MaxValue : DateTime.MaxValue;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _statusTimer.Dispose();
            _highlightTimer.Dispose();
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
