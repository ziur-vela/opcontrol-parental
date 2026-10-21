using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using OpControl.Parental.Contracts;
using OpControl.Parental.Localization;

namespace OpControl.Parental.Helper;

public enum MyOpControlTab
{
    Home,
    Schedule,
    Rules,
    Activity,
    Messages
}

/// <summary>Read-only child window: what the parents set and how the child is doing.</summary>
public sealed class MyOpControlForm : Form
{
    private const int WmNcHitTest = 0x84;
    private const int WmNcLButtonDown = 0xA1;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private bool _messagesOnly;
    private readonly HomeView _home;
    private readonly ScheduleView _schedule;
    private readonly RulesView _rules;
    private readonly ActivityView _activity;
    private readonly ChildMessagingPanel _messages;
    private readonly Panel _content = new();
    private readonly Label _childLabel = new();
    private readonly Label _deviceLabel = new();
    private readonly Label _updatedLabel = new();
    private readonly Panel _notice = new();
    private readonly Label _noticeLabel = new();
    private readonly Panel _noData = new();
    private readonly TableLayoutPanel _messagesHost = new();
    private readonly Dictionary<MyOpControlTab, NavItem> _nav = new();
    private readonly System.Windows.Forms.Timer _liveTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _cacheTimer = new() { Interval = 3000 };
    private DateTime _summaryStamp = DateTime.MinValue;
    private bool _hasSummary;
    private bool _scaled;
    private MyOpControlTab _activeTab = MyOpControlTab.Home;
    private bool _online;

    public event Action<int>? UnreadChanged;

    public MyOpControlForm(bool aboveLock, MyOpControlTab tab = MyOpControlTab.Home, bool messagesOnly = false,
        string? themeOverride = null, string? languageOverride = null)
    {
        _messagesOnly = messagesOnly;
        if (!string.IsNullOrWhiteSpace(languageOverride)) Loc.SetLanguage(languageOverride);
        Theme.Apply(string.Equals(themeOverride, "light", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrWhiteSpace(themeOverride) && IsLightTheme()));

        _home = new HomeView();
        _schedule = new ScheduleView();
        _rules = new RulesView();
        _activity = new ActivityView();
        _messages = new ChildMessagingPanel();

        Text = "My OpControl";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        TopMost = aboveLock;
        MaximizeBox = false;
        MinimizeBox = true;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        KeyPreview = true;
        Size = new Size(1040, 700);

        BuildShell();
        LoadSummary(force: true);
        ShowTab(messagesOnly ? MyOpControlTab.Messages : tab);

        _liveTimer.Tick += (_, _) => UpdateLive();
        _liveTimer.Start();
        _cacheTimer.Tick += (_, _) => CheckCache();
        _cacheTimer.Start();
        _messages.UnreadChanged += count =>
        {
            UpdateBadge(count);
            UnreadChanged?.Invoke(count);
        };
        _messages.Start();

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
        };
    }

    public MyOpControlTab CurrentTab => _activeTab;

    public bool IsMessagesVisible =>
        Visible && WindowState != FormWindowState.Minimized
        && _activeTab == MyOpControlTab.Messages && WindowVisibility.IsOnScreen(this);

    public bool IsOpenVisible =>
        Visible && WindowState != FormWindowState.Minimized && WindowVisibility.IsOnScreen(this);

    public void ShowTab(MyOpControlTab tab)
    {
        if (_messagesOnly) tab = MyOpControlTab.Messages;
        _activeTab = tab;
        foreach (var pair in _nav)
        {
            pair.Value.SetActive(pair.Key == tab);
            pair.Value.Visible = !_messagesOnly || pair.Key == MyOpControlTab.Messages;
        }
        ShowContent();
        if (tab == MyOpControlTab.Messages)
        {
            HideNotice();
            _ = _messages.MarkAllSeenAsync();
        }
    }

    public void SetMessagesOnly(bool messagesOnly)
    {
        _messagesOnly = messagesOnly;
        foreach (var pair in _nav)
            pair.Value.Visible = !_messagesOnly || pair.Key == MyOpControlTab.Messages;
        if (_messagesOnly) ShowTab(MyOpControlTab.Messages);
    }

    public void RestoreAndShow()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        BringToFront();
        Activate();
    }

    public void NotifyNewMessage(ChildMessageDto message)
    {
        if (message.Deleted) return;
        if (message.SpeakAloud)
            Speech.Speak(MessageDisplay.Body(message), Loc.Language);

        if (IsMessagesVisible)
        {
            HideNotice();
            _messages.Highlight(message.Id);
            _ = _messages.MarkSeenAsync(message.Id);
        }
        else
        {
            _noticeLabel.Text = Loc.T("Client.Kid.NewMessageFrom", MessageDisplay.Sender(message));
            _notice.Visible = true;
            _notice.BringToFront();
        }
    }

    private void BuildShell()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Bg
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildBody(), 0, 1);
        Controls.Add(root);
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel };
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Line);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Padding = new Padding(16, 0, 8, 0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var brand = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        var icon = new PictureBox
        {
            Image = LoadShield(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Width = 26,
            Height = 26,
            Margin = new Padding(0, 0, 9, 0),
            BackColor = Color.Transparent
        };
        brand.Controls.Add(icon);
        var title = new Label
        {
            Text = "My OpControl",
            Font = Theme.Sans(15f, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 0),
            BackColor = Color.Transparent
        };
        brand.Controls.Add(title);

        var right = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        right.Controls.Add(new Label
        {
            Text = Loc.T("Client.Kid.ViewOnly"),
            Font = Theme.Mono(8.5f),
            ForeColor = Theme.Muted,
            AutoSize = true,
            Margin = new Padding(0, 7, 12, 0),
            BackColor = Color.Transparent
        });
        var minimize = HeaderButton("–");
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        var close = HeaderButton("×");
        close.Click += (_, _) => Close();
        right.Controls.Add(minimize);
        right.Controls.Add(close);

        layout.Controls.Add(brand, 0, 0);
        layout.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent }, 1, 0);
        layout.Controls.Add(right, 2, 0);
        header.Controls.Add(layout);

        void Drag(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, WmNcLButtonDown, HtCaption, IntPtr.Zero);
        }
        header.MouseDown += (_, e) => Drag(e);
        title.MouseDown += (_, e) => Drag(e);
        brand.MouseDown += (_, e) => Drag(e);
        return header;
    }

    private static Button HeaderButton(string text)
    {
        var button = new Button
        {
            Text = text,
            Width = 40,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = Theme.Muted,
            Font = Theme.Sans(12f),
            Margin = new Padding(2, 0, 0, 0),
            TabStop = false,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Theme.Panel2;
        button.Cursor = Cursors.Hand;
        return button;
    }

    private Control BuildBody()
    {
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Bg
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 188));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        body.Controls.Add(BuildSidebar(), 0, 0);

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Bg
        };
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _notice.Visible = false;
        _notice.Height = 38;
        _notice.Dock = DockStyle.Top;
        _notice.BackColor = Theme.Panel2;
        _notice.Cursor = Cursors.Hand;
        _notice.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Brand);
            e.Graphics.DrawLine(pen, 0, _notice.Height - 1, _notice.Width, _notice.Height - 1);
        };
        _noticeLabel.Dock = DockStyle.Fill;
        _noticeLabel.TextAlign = ContentAlignment.MiddleLeft;
        _noticeLabel.Padding = new Padding(24, 0, 0, 0);
        _noticeLabel.Font = Theme.Sans(10f);
        _noticeLabel.ForeColor = Theme.Text;
        _noticeLabel.Cursor = Cursors.Hand;
        _notice.Controls.Add(_noticeLabel);
        _notice.Click += (_, _) => ShowTab(MyOpControlTab.Messages);
        _noticeLabel.Click += (_, _) => ShowTab(MyOpControlTab.Messages);
        main.Controls.Add(_notice, 0, 0);

        _content.Dock = DockStyle.Fill;
        _content.BackColor = Theme.Bg;
        _content.Padding = new Padding(24, 18, 24, 16);
        main.Controls.Add(_content, 0, 1);
        body.Controls.Add(main, 1, 0);

        _noData.BackColor = Theme.Bg;
        var noDataLabel = new Label
        {
            Text = Loc.T("Client.Kid.NoData"),
            Font = Theme.Sans(11f),
            ForeColor = Theme.Muted,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };
        _noData.Controls.Add(noDataLabel);

        BuildMessagesHost();
        _home.Navigate += tab => ShowTab(ParseTab(tab));
        _schedule.Navigate += tab => ShowTab(ParseTab(tab));
        _rules.Navigate += tab => ShowTab(ParseTab(tab));
        _activity.Navigate += tab => ShowTab(ParseTab(tab));
        return body;
    }

    private static MyOpControlTab ParseTab(string value) => value switch
    {
        "schedule" => MyOpControlTab.Schedule,
        "rules" => MyOpControlTab.Rules,
        "activity" => MyOpControlTab.Activity,
        "messages" => MyOpControlTab.Messages,
        _ => MyOpControlTab.Home
    };

    private Control BuildSidebar()
    {
        var sidebar = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel };
        sidebar.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Line);
            e.Graphics.DrawLine(pen, sidebar.Width - 1, 0, sidebar.Width - 1, sidebar.Height);
        };
        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = false,
            BackColor = Color.Transparent,
            Padding = new Padding(12, 16, 12, 12)
        };
        AddNav(stack, MyOpControlTab.Home, Loc.T("Client.Kid.Nav.Home"), MyOpControlTab.Home);
        AddNav(stack, MyOpControlTab.Schedule, Loc.T("Client.Kid.Nav.Schedule"), MyOpControlTab.Schedule);
        AddNav(stack, MyOpControlTab.Rules, Loc.T("Client.Kid.Nav.Rules"), MyOpControlTab.Rules);
        AddNav(stack, MyOpControlTab.Activity, Loc.T("Client.Kid.Nav.Activity"), MyOpControlTab.Activity);
        AddNav(stack, MyOpControlTab.Messages, Loc.T("Client.Kid.Nav.Messages"), MyOpControlTab.Messages);
        sidebar.Controls.Add(stack);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 98, BackColor = Color.Transparent };
        _childLabel.Text = "";
        _childLabel.Font = Theme.Sans(10.5f, FontStyle.Bold);
        _childLabel.ForeColor = Theme.Text;
        _childLabel.AutoSize = false;
        _childLabel.Dock = DockStyle.Top;
        _childLabel.Height = 24;
        _childLabel.BackColor = Color.Transparent;
        _deviceLabel.Font = Theme.Mono(8f);
        _deviceLabel.ForeColor = Theme.Muted;
        _deviceLabel.AutoSize = false;
        _deviceLabel.Dock = DockStyle.Top;
        _deviceLabel.Height = 20;
        _deviceLabel.BackColor = Color.Transparent;
        _updatedLabel.Font = Theme.Mono(8f);
        _updatedLabel.ForeColor = Theme.Faint;
        _updatedLabel.AutoSize = false;
        _updatedLabel.Dock = DockStyle.Top;
        _updatedLabel.Height = 20;
        _updatedLabel.BackColor = Color.Transparent;
        var inner = new Panel { Dock = DockStyle.Bottom, Height = 76, Padding = new Padding(4, 0, 4, 0), BackColor = Color.Transparent };
        inner.Controls.Add(_updatedLabel);
        inner.Controls.Add(_deviceLabel);
        inner.Controls.Add(_childLabel);
        bottom.Controls.Add(inner);
        sidebar.Controls.Add(bottom);
        bottom.BringToFront();
        return sidebar;
    }

    private void AddNav(FlowLayoutPanel stack, MyOpControlTab tab, string text, MyOpControlTab target)
    {
        if (_messagesOnly && target != MyOpControlTab.Messages) return;
        var item = new NavItem(text, () => ShowTab(target));
        item.Width = 164;
        item.Height = 44;
        item.Margin = new Padding(0, 0, 0, 4);
        _nav[tab] = item;
        stack.Controls.Add(item);
    }

    private void BuildMessagesHost()
    {
        _messagesHost.Dock = DockStyle.Fill;
        _messagesHost.ColumnCount = 1;
        _messagesHost.RowCount = 2;
        _messagesHost.BackColor = Color.Transparent;
        _messagesHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _messagesHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        var header = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 10)
        };
        header.Controls.Add(KidUi.Eyebrow(Loc.T("Client.Kid.Nav.Messages")));
        var title = KidUi.Title(Loc.T("Client.Messages.YourParents"));
        header.Controls.Add(title);
        _messagesHost.Controls.Add(header, 0, 0);
        _messages.Dock = DockStyle.Fill;
        _messagesHost.Controls.Add(_messages, 0, 1);
    }

    private void ShowContent()
    {
        _content.SuspendLayout();
        try
        {
            _content.Controls.Clear();
            if (_activeTab == MyOpControlTab.Messages)
            {
                _content.Controls.Add(_messagesHost);
            }
            else if (!_hasSummary)
            {
                _content.Controls.Add(_noData);
            }
            else
            {
                _content.Controls.Add(_activeTab switch
                {
                    MyOpControlTab.Schedule => _schedule,
                    MyOpControlTab.Rules => _rules,
                    MyOpControlTab.Activity => _activity,
                    _ => _home
                });
            }
        }
        finally
        {
            _content.ResumeLayout(true);
        }
    }

    private void UpdateLive()
    {
        var state = HelperPaths.ReadState();
        var online = HelperPaths.ReadOnline();
        if (online != _online) _online = online;
        _home.UpdateLive(state, online);
    }

    private void CheckCache()
    {
        try
        {
            var exists = File.Exists(HelperPaths.SummaryFile);
            var stamp = exists ? File.GetLastWriteTimeUtc(HelperPaths.SummaryFile) : DateTime.MinValue;
            if (stamp != _summaryStamp) LoadSummary(force: false);
        }
        catch { }
    }

    private void LoadSummary(bool force)
    {
        if (!force && _hasSummary && DateTime.UtcNow - _lastLoadUtc < TimeSpan.FromSeconds(2)) return;
        var summary = HelperPaths.ReadSummary();
        _lastLoadUtc = DateTime.UtcNow;
        try
        {
            _summaryStamp = File.Exists(HelperPaths.SummaryFile)
                ? File.GetLastWriteTimeUtc(HelperPaths.SummaryFile)
                : DateTime.MinValue;
        }
        catch { _summaryStamp = DateTime.MinValue; }

        if (summary is null)
        {
            _hasSummary = false;
            _childLabel.Text = "";
            _deviceLabel.Text = Environment.MachineName;
            _updatedLabel.Text = Loc.T("Client.Kid.UpdatedNever");
            if (_activeTab != MyOpControlTab.Messages) ShowContent();
            return;
        }

        _hasSummary = true;
        _childLabel.Text = summary.ChildName;
        _deviceLabel.Text = summary.DeviceName;
        _updatedLabel.Text = Loc.T("Client.Kid.Updated", summary.GeneratedUtc.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture));
        _home.SetSummary(summary);
        _schedule.SetSummary(summary);
        _rules.SetSummary(summary);
        _activity.SetSummary(summary);
        if (_activeTab != MyOpControlTab.Messages) ShowContent();
    }

    private DateTime _lastLoadUtc = DateTime.MinValue;

    private void UpdateBadge(int count)
    {
        if (_nav.TryGetValue(MyOpControlTab.Messages, out var item)) item.SetUnread(count);
    }

    private void HideNotice()
    {
        _notice.Visible = false;
    }

    private static Image? LoadShield()
    {
        try
        {
            var name = Theme.IsLight
                ? "OpControl.Parental.Helper.brand.icon-color-128.png"
                : "OpControl.Parental.Helper.brand.icon-ondark-64.png";
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream is null) return null;
            using var image = Image.FromStream(stream);
            return new Bitmap(image);
        }
        catch { return null; }
    }

    private static bool IsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_scaled) return;
        _scaled = true;
        Dpi.ScaleForm(this);
        MinimumSize = new Size(Dpi.S(this, 820), Dpi.S(this, 600));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmNcHitTest)
        {
            var screen = new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF));
            var point = PointToClient(screen);
            var edge = Dpi.S(this, 6);
            var left = point.X <= edge;
            var right = point.X >= ClientSize.Width - edge;
            var top = point.Y <= edge;
            var bottom = point.Y >= ClientSize.Height - edge;
            var hit = left && top ? HtTopLeft
                : right && top ? HtTopRight
                : left && bottom ? HtBottomLeft
                : right && bottom ? HtBottomRight
                : left ? HtLeft
                : right ? HtRight
                : top ? HtTop
                : bottom ? HtBottom
                : 0;
            if (hit != 0)
            {
                m.Result = (IntPtr)hit;
                return;
            }
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _liveTimer.Dispose();
            _cacheTimer.Dispose();
            _messages.Stop();
        }
        base.Dispose(disposing);
    }

    private sealed class NavItem : Panel
    {
        private readonly Button _button;
        private readonly Panel _bar;
        private readonly Label _badge;

        public NavItem(string text, Action click)
        {
            Height = 44;
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            _bar = new Panel { Dock = DockStyle.Left, Width = 3, BackColor = Theme.Brand, Visible = false };
            _button = new Button
            {
                Text = text,
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 8, 0),
                BackColor = Color.Transparent,
                ForeColor = Theme.Muted,
                Font = Theme.Mono(9.5f),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
                TabStop = false
            };
            _button.FlatAppearance.BorderSize = 0;
            _button.FlatAppearance.MouseOverBackColor = Theme.Panel2;
            _badge = new Label
            {
                AutoSize = false,
                Width = 24,
                Height = 18,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Theme.Danger,
                ForeColor = Color.White,
                Font = Theme.Mono(8f, FontStyle.Bold),
                Visible = false
            };
            _button.Click += (_, _) => click();
            Click += (_, _) => click();
            Controls.Add(_button);
            Controls.Add(_bar);
            Controls.Add(_badge);
            _bar.BringToFront();
            _badge.BringToFront();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _badge.Left = Width - _badge.Width - 10;
            _badge.Top = (Height - _badge.Height) / 2;
        }

        public void SetActive(bool active)
        {
            _bar.Visible = active;
            _button.BackColor = active ? Theme.Panel2 : Color.Transparent;
            _button.ForeColor = active ? Theme.Text : Theme.Muted;
            _button.Font = Theme.Mono(9.5f, active ? FontStyle.Bold : FontStyle.Regular);
        }

        public void SetUnread(int count)
        {
            _badge.Text = count > 99 ? "99+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _badge.Visible = count > 0;
        }
    }
}
