using System.Drawing;
using OpControl.Parental.Contracts;
using OpControl.Parental.Localization;

namespace OpControl.Parental.Helper;

/// <summary>Self-contained child messaging panel; it can later be embedded in the Luna Chat window.</summary>
public sealed class ChildMessagingPanel : UserControl
{
    private readonly FlowLayoutPanel _thread;
    private readonly TextBox _input;
    private readonly Label _count;
    private readonly Label _status;
    private readonly Button _send;
    private readonly System.Windows.Forms.Timer _timer;
    private bool _busy;
    private bool _loaded;
    private bool _sending;

    public ChildMessagingPanel()
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Padding = new Padding(4);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Color.Transparent
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

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
            Height = 66,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle
        };
        Theme.StyleInput(_input);
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

        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(_thread, 0, 1);
        layout.Controls.Add(_status, 0, 2);
        layout.Controls.Add(_input, 0, 3);
        layout.Controls.Add(footer, 0, 4);
        Controls.Add(layout);

        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += async (_, _) => await RefreshThreadAsync(showStatus: false);
        _input.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && e.Control)
            {
                e.SuppressKeyPress = true;
                await SendAsync();
            }
        };
    }

    public void Start()
    {
        if (!_timer.Enabled) _timer.Start();
        _ = RefreshThreadAsync(showStatus: true);
    }

    public void Stop() => _timer.Stop();

    private async Task RefreshThreadAsync(bool showStatus)
    {
        if (_busy || _sending) return;
        _busy = true;
        try
        {
            var response = await PinPipeClient.GetThreadAsync(CancellationToken.None);
            var thread = response.Ok ? response.Messages : ReadCachedThread();
            if (thread is null)
            {
                if (showStatus && !_loaded) _status.Text = response.Error ?? Loc.T("Client.Messages.Loading");
                return;
            }
            _loaded = true;
            BuildThread(thread);
            if (showStatus) _status.Text = "";
        }
        catch
        {
            if (showStatus && !_loaded) _status.Text = Loc.T("Client.Messages.Loading");
        }
        finally
        {
            _busy = false;
        }
    }

    private static IReadOnlyList<ChildMessageDto>? ReadCachedThread()
    {
        try
        {
            if (!File.Exists(HelperPaths.MessageThreadFile)) return null;
            return System.Text.Json.JsonSerializer.Deserialize<List<ChildMessageDto>>(
                File.ReadAllText(HelperPaths.MessageThreadFile), HelperPaths.JsonOptions);
        }
        catch { return null; }
    }

    private void BuildThread(IReadOnlyList<ChildMessageDto> messages)
    {
        var width = Math.Max(240, _thread.ClientSize.Width - 28);
        _thread.SuspendLayout();
        try
        {
            _thread.Controls.Clear();
            if (messages.Count == 0)
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

            foreach (var message in messages.OrderBy(m => m.CreatedUtc))
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
                    RowCount = 2,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    BackColor = Color.Transparent,
                    Dock = DockStyle.Fill
                };
                var head = new Label
                {
                    Text = $"{MessageDisplay.Sender(message)} · {MessageDisplay.Time(message)}",
                    Font = Theme.Mono(8.5f),
                    ForeColor = own ? Theme.Value : Theme.Label,
                    AutoSize = true,
                    BackColor = Color.Transparent,
                    Margin = new Padding(0, 0, 0, 3)
                };
                var body = new Label
                {
                    Text = MessageDisplay.Body(message),
                    Font = Theme.Sans(10.5f),
                    ForeColor = Theme.Text,
                    AutoSize = true,
                    MaximumSize = new Size(Math.Max(160, width - 28), 0),
                    BackColor = Color.Transparent,
                    Margin = new Padding(0)
                };
                inner.Controls.Add(head, 0, 0);
                inner.Controls.Add(body, 0, 1);
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

    private async Task SendAsync()
    {
        if (_sending) return;
        var text = (_input.Text ?? "").Trim();
        if (text.Length == 0) return;
        if (text.Length > 500)
        {
            ShowStatus(Loc.T("Client.Messages.TooLong"), error: true);
            return;
        }

        _sending = true;
        _send.Enabled = false;
        _status.Text = Loc.T("Client.Messages.Sending");
        try
        {
            var response = await PinPipeClient.SendMessageAsync(text, CancellationToken.None);
            if (response.Ok || response.Queued)
            {
                _input.Text = "";
                ShowStatus(response.Queued ? (response.Error ?? Loc.T("Client.Messages.SendFailed")) : Loc.T("Client.Messages.Sent"), error: response.Queued);
                await RefreshThreadAsync(showStatus: false);
            }
            else
            {
                ShowStatus(response.Error ?? Loc.T("Client.Messages.SendFailed"), error: true);
            }
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, error: true);
        }
        finally
        {
            _sending = false;
            _send.Enabled = true;
        }
    }

    private void ShowStatus(string message, bool error)
    {
        _status.Text = message;
        _status.ForeColor = error ? Theme.Warn : Theme.Value;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
