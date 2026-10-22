using System.Globalization;
using OpControl.Parental.Contracts;
using OpControl.Parental.Localization;

namespace OpControl.Parental.Helper;

internal abstract class KidView : UserControl
{
    protected ChildSummaryDto? Summary;
    public ChildSummaryDto? CachedSummary => Summary;
    private bool _rebuilding;
    private int _lastWidth;

    public static bool Scaling { get; set; }

    public event Action<string>? Navigate;

    protected int ContentWidth => Math.Max(320, (Width > 40 ? Width : 820) - 28);

    protected KidView()
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        AutoScroll = false;
        Padding = new Padding(0);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Scaling || _rebuilding || Summary is null || Width <= 0) return;
        if (Math.Abs(Width - _lastWidth) < 24) return;
        _lastWidth = Width;
        Rebuild();
    }

    public virtual void SetSummary(ChildSummaryDto summary)
    {
        Summary = summary;
        _lastWidth = Width;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Rebuild();
    }

    public virtual void UpdateLive(RuntimeLockStateDto? state, bool online) { }

    protected abstract void Rebuild();

    protected void Raise(string tab) => Navigate?.Invoke(tab);

    protected Control PageHeader(string eyebrow, string title, Control? trailing = null)
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = trailing is null ? 1 : 2,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 14)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        if (trailing is not null) header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(KidUi.Eyebrow(Loc.T(eyebrow)), 0, 0);
        header.Controls.Add(KidUi.Title(title), 0, 1);
        if (trailing is not null)
        {
            trailing.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            header.Controls.Add(trailing, 1, 1);
            header.SetRowSpan(trailing, 2);
            trailing.Anchor = AnchorStyles.Right;
        }
        return header;
    }

    protected static CardPanel Card(string eyebrow, string? title = null)
    {
        var card = new CardPanel { Dock = DockStyle.Fill, AutoScroll = true };
        var stack = KidUi.Stack();
        stack.Dock = DockStyle.Top;
        stack.AutoSize = true;
        if (!string.IsNullOrWhiteSpace(eyebrow)) stack.Controls.Add(KidUi.Eyebrow(Loc.T(eyebrow)));
        if (!string.IsNullOrWhiteSpace(title)) stack.Controls.Add(KidUi.Body(title, size: 12.5f));
        card.Controls.Add(stack);
        card.Tag = stack;
        return card;
    }

    protected static FlowLayoutPanel Stack(CardPanel card) => (FlowLayoutPanel)card.Tag!;

    protected static TableLayoutPanel PageStack() => new()
    {
        Dock = DockStyle.Top,
        ColumnCount = 1,
        RowCount = 0,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        BackColor = Color.Transparent,
        Padding = new Padding(0),
        Margin = new Padding(0)
    };

    protected static void AddPageRow(TableLayoutPanel page, Control child)
    {
        var row = page.RowCount;
        page.RowCount = row + 1;
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        child.Dock = DockStyle.Top;
        page.Controls.Add(child, 0, row);
    }

    protected static Panel ScrollHost(TableLayoutPanel content)
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent };
        scroll.Controls.Add(content);
        return scroll;
    }

    protected void RebuildBase(Control content)
    {
        _rebuilding = true;
        SuspendLayout();
        try
        {
            Controls.Clear();
            Controls.Add(content);
        }
        finally
        {
            ResumeLayout(true);
            _rebuilding = false;
        }
    }

    protected static string LocalTime(DateTime utc) =>
        utc.ToLocalTime().ToString("d MMM, HH:mm", CultureInfo.CurrentCulture);

    protected static string DayName(int day) => CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)day);

    protected static string WeekdayLabel(int day) => CultureInfo.CurrentCulture.DateTimeFormat.GetDayName((DayOfWeek)day);
}

internal sealed class HomeView : KidView
{
    private Label? _date;
    private Label? _greeting;
    private Label? _statusChip;
    private Label? _countdown;
    private Label? _used;
    private Label? _bonus;
    private RingPanel? _ring;
    private Label? _nextLockTitle;
    private Label? _nextLockDetail;
    private Label? _nextLockTime;
    private bool _online;
    private RuntimeLockStateDto? _live;

    public override void SetSummary(ChildSummaryDto summary)
    {
        Summary = summary;
        Rebuild();
        UpdateLive(_live, _online);
    }

    public override void UpdateLive(RuntimeLockStateDto? state, bool online)
    {
        _live = state;
        _online = online;
        if (_date is not null)
            _date.Text = DateTime.Now.ToString("dddd · MMM d", CultureInfo.CurrentCulture).ToUpperInvariant();
        if (_greeting is not null && Summary is not null)
        {
            var first = Summary.ChildName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? Summary.ChildName;
            _greeting.Text = Loc.T("Client.Kid.Greeting", first);
        }
        if (_statusChip is not null)
        {
            var locked = state?.IsLocked == true;
            _statusChip.Text = (locked ? Loc.T("Client.Kid.Status.Locked") : Loc.T("Client.Kid.Status.Unlocked"))
                + " · " + (online ? Loc.T("Client.Kid.Status.Online") : Loc.T("Client.Kid.Status.Offline"));
            _statusChip.ForeColor = locked ? Theme.Danger : Theme.Value;
        }
        UpdateTimeLeft(state);
    }

    private void UpdateTimeLeft(RuntimeLockStateDto? state)
    {
        if (Summary is null || _countdown is null || _used is null || _ring is null || _bonus is null) return;
        var now = DateTime.UtcNow;
        var remainingSeconds = state?.DailyLimitRemainingSeconds;
        var dailyUsed = Summary.UsedTodaySeconds;
        var dailyLimit = Summary.ScreenTimeEnabled ? Summary.TodayLimitMinutes * 60 : 0;
        if (state?.DailyLimitMinutes is { } liveMinutes && state.DailyLimitRemainingSeconds is { } liveLeft)
        {
            dailyLimit = liveMinutes * 60;
            dailyUsed = Math.Max(0, liveMinutes * 60 - liveLeft);
        }

        TimeSpan? banner = null;
        if (state?.BonusUntilUtc is { } bonus && bonus > now)
            banner = bonus - now;
        else if (state?.NextLockUtc is { } next && next > now)
            banner = next - now;
        else if (remainingSeconds is { } left && left > 0)
            banner = TimeSpan.FromSeconds(left);

        _countdown.Text = banner is { } span ? KidUi.Clock((int)span.TotalSeconds)
            : dailyLimit > 0 && dailyUsed < dailyLimit ? KidUi.Clock(dailyLimit - dailyUsed)
            : Loc.T("Client.Kid.NoLimit");
        _used.Text = dailyLimit > 0
            ? Loc.T("Client.Kid.UsedOf", KidUi.Duration(dailyUsed), KidUi.Duration(dailyLimit))
            : Loc.T("Client.Kid.UsedNoLimit", KidUi.Duration(dailyUsed));
        _ring.Fraction = dailyLimit > 0 ? Math.Max(0, (double)(dailyLimit - dailyUsed) / dailyLimit) : 0;

        if (state?.BonusUntilUtc is { } bonusUntil && bonusUntil > now)
        {
            _bonus.Text = Loc.T("Client.Kid.BonusActive", state.BonusMinutes ?? 0,
                state.ParentDisplayName ?? Loc.T("Client.Kid.YourParents"),
                bonusUntil.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
            _bonus.Visible = true;
        }
        else if (state?.Reason == "override" && state.EndsUtc is { } unlockedUntil && unlockedUntil > now)
        {
            var when = unlockedUntil.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
            _bonus.Text = string.IsNullOrWhiteSpace(state.UnlockScheduleName)
                ? Loc.T("Client.Kid.ParentUnlock", when)
                : Loc.T("Client.Kid.ParentUnlockSchedule", when, state.UnlockScheduleName);
            _bonus.Visible = true;
        }
        else
        {
            _bonus.Visible = false;
        }
    }

    protected override void Rebuild()
    {
        if (Summary is null) return;
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 16)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var titles = KidUi.Stack();
        _date = KidUi.Eyebrow(DateTime.Now.ToString("dddd · MMM d", CultureInfo.CurrentCulture));
        _greeting = KidUi.Title("");
        _greeting.Font = Theme.Sans(27f, FontStyle.Bold);
        titles.Controls.Add(_date);
        titles.Controls.Add(_greeting);
        _statusChip = new Label
        {
            AutoSize = false,
            Width = 300,
            Height = 32,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = Theme.Mono(9f, FontStyle.Bold),
            BackColor = Theme.Panel2,
            ForeColor = Theme.Value,
            Margin = new Padding(0, 10, 0, 0),
            Anchor = AnchorStyles.Right | AnchorStyles.Top
        };
        header.Controls.Add(titles, 0, 0);
        header.Controls.Add(_statusChip, 1, 0);
        root.Controls.Add(header, 0, 0);

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            BackColor = Color.Transparent
        };
        for (var i = 0; i < 3; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3f));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 260));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var timeCard = BuildTimeCard();
        var lockCard = BuildNextLockCard();
        var usedCard = BuildMostUsedCard();
        var rulesCard = BuildRulesCard();
        var messagesCard = BuildMessagesCard();

        grid.Controls.Add(timeCard, 0, 0);
        grid.SetColumnSpan(timeCard, 2);
        grid.Controls.Add(lockCard, 2, 0);
        grid.Controls.Add(usedCard, 0, 1);
        grid.Controls.Add(rulesCard, 1, 1);
        grid.Controls.Add(messagesCard, 2, 1);
        root.Controls.Add(grid, 0, 1);

        RebuildBase(root);
        UpdateLive(_live, _online);
    }

    private CardPanel BuildTimeCard()
    {
        var card = Card("Client.Kid.TimeLeft");
        var stack = Stack(card);
        stack.Margin = new Padding(0, 6, 0, 6);
        var row = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 6, 0, 6)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _ring = new RingPanel { Size = new Size(132, 132), Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 8, 12, 0) };
        row.Controls.Add(_ring, 0, 0);

        var values = KidUi.Stack();
        _countdown = KidUi.Mono("0:00:00", 30f, true, Theme.Value);
        _countdown.Margin = new Padding(0, 2, 0, 6);
        _used = KidUi.Body("", true, 10.5f);
        _bonus = KidUi.Body("", false, 9.5f);
        _bonus.ForeColor = Theme.Warn;
        _bonus.Visible = false;
        values.Controls.Add(_countdown);
        values.Controls.Add(_used);
        values.Controls.Add(KidUi.Mono(Loc.T("Client.Kid.ResetsMidnight"), 8.5f, false, Theme.Faint));
        values.Controls.Add(_bonus);
        row.Controls.Add(values, 1, 0);
        stack.Controls.Add(row);

        var footer = KidUi.Link(Loc.T("Client.Kid.ViewSchedule"));
        footer.Click += (_, _) => Raise("schedule");
        stack.Controls.Add(footer);
        return card;
    }

    private CardPanel BuildNextLockCard()
    {
        var card = Card("Client.Kid.NextLock");
        var stack = Stack(card);
        _nextLockTitle = KidUi.Body("", false, 12.5f);
        _nextLockTime = KidUi.Mono("", 9f, true, Theme.Value);
        _nextLockDetail = KidUi.Body("", true, 9.5f);
        stack.Controls.Add(_nextLockTitle);
        stack.Controls.Add(_nextLockTime);
        stack.Controls.Add(_nextLockDetail);
        var link = KidUi.Link(Loc.T("Client.Kid.ViewSchedule"));
        link.Click += (_, _) => Raise("schedule");
        stack.Controls.Add(link);
        FillNextLock();
        return card;
    }

    private void FillNextLock()
    {
        if (Summary is null || _nextLockTitle is null || _nextLockTime is null || _nextLockDetail is null) return;
        var now = DateTime.Now;
        var today = now.DayOfWeek;
        var next = Summary.Schedules
            .Where(s => s.IsEnabled && (s.DaysMask & (1 << (int)today)) != 0 && s.StartMinutes > now.Hour * 60 + now.Minute)
            .OrderBy(s => s.StartMinutes)
            .FirstOrDefault();
        if (next is null)
        {
            _nextLockTitle.Text = Loc.T("Client.Kid.NoLocks");
            _nextLockTime.Text = "";
            _nextLockDetail.Text = "";
            return;
        }
        var start = now.Date.AddMinutes(next.StartMinutes);
        var end = next.EndMinutes >= next.StartMinutes ? now.Date.AddMinutes(next.EndMinutes) : now.Date.AddDays(1).AddMinutes(next.EndMinutes);
        _nextLockTitle.Text = next.Name;
        _nextLockTime.Text = start.ToString("t", CultureInfo.CurrentCulture);
        _nextLockDetail.Text = Loc.T("Client.Kid.NextLockUntil", end.ToString("t", CultureInfo.CurrentCulture));
    }

    private CardPanel BuildMostUsedCard()
    {
        var card = Card("Client.Kid.MostUsed");
        var stack = Stack(card);
        var apps = Summary!.AppsToday.Where(a => a.Seconds > 0).Take(4).ToList();
        if (apps.Count == 0)
        {
            stack.Controls.Add(KidUi.Body(Loc.T("Client.Kid.NoUsage"), true));
            return card;
        }
        var max = Math.Max(1, apps.Max(a => a.Seconds));
        foreach (var app in apps)
        {
            var row = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 2,
                Width = 205,
                Height = 42,
                AutoSize = false,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 0, 6)
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            row.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
            var name = new Label
            {
                Text = ShortAppName(app.DisplayName),
                AutoSize = false,
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                Height = 20,
                Font = Theme.Sans(8.5f),
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            };
            var value = KidUi.Mono(KidUi.Duration(app.Seconds), 8.5f, false, Theme.Value);
            value.Anchor = AnchorStyles.Right;
            value.Margin = new Padding(6, 1, 0, 0);
            row.Controls.Add(name, 0, 0);
            row.Controls.Add(value, 1, 0);
            var bar = new BarPanel { Fraction = (double)app.Seconds / max, Thickness = 6, Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 0) };
            row.Controls.Add(bar, 0, 1);
            row.SetColumnSpan(bar, 2);
            stack.Controls.Add(row);
        }
        return card;
    }

    private static string ShortAppName(string name)
    {
        var value = (name ?? "").Trim();
        var slash = value.LastIndexOf('\\');
        if (slash >= 0) value = value[(slash + 1)..];
        if (!value.Contains(' '))
        {
            var dot = value.IndexOf('.');
            if (dot > 0) value = value[..dot];
        }
        else
        {
            var dot = value.LastIndexOf('.');
            if (dot > 0 && value.Length - dot <= 5) value = value[..dot];
        }
        return string.IsNullOrWhiteSpace(value) ? name : value;
    }

    private CardPanel BuildRulesCard()
    {
        var card = Card("Client.Kid.MyRules");
        var stack = Stack(card);
        var items = new (string Text, string Label)[]
        {
            (Summary!.Websites.Count(w => !w.Allowed).ToString(CultureInfo.InvariantCulture), Loc.T("Client.Kid.BlockedSites")),
            (Summary.Apps.Count(a => !a.Allowed).ToString(CultureInfo.InvariantCulture), Loc.T("Client.Kid.BlockedApps")),
            (Summary.Apps.Count(a => a.DailyLimitMinutes is > 0).ToString(CultureInfo.InvariantCulture), Loc.T("Client.Kid.AppLimits")),
            ((Summary.Websites.Count(w => w.Allowed) + Summary.Apps.Count(a => a.AlwaysAllowed)).ToString(CultureInfo.InvariantCulture), Loc.T("Client.Kid.AlwaysAllowed"))
        };
        var table = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 0, 4)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        for (var i = 0; i < items.Length; i++)
        {
            var cell = KidUi.Stack();
            cell.Margin = new Padding(0, 0, 6, 5);
            cell.Controls.Add(KidUi.Mono(items[i].Text, 16f, true, Theme.Text));
            cell.Controls.Add(KidUi.Mono(items[i].Label, 7.5f, false, Theme.Muted));
            table.Controls.Add(cell, i % 2, i / 2);
        }
        stack.Controls.Add(table);
        var link = KidUi.Link(Loc.T("Client.Kid.ViewRules"));
        link.Click += (_, _) => Raise("rules");
        stack.Controls.Add(link);
        return card;
    }

    private CardPanel BuildMessagesCard()
    {
        var card = Card("Client.Kid.Messages");
        var stack = Stack(card);
        var latest = Summary!.RecentMessages.LastOrDefault(m => m.Direction == MessageDirection.ParentToChild);
        if (latest is null)
        {
            stack.Controls.Add(KidUi.Body(Loc.T("Client.Kid.NoMessages"), true));
        }
        else
        {
            stack.Controls.Add(KidUi.Mono(MessageDisplay.Sender(latest), 8.5f, true, Theme.Label));
            var body = KidUi.Body(latest.Text, size: 9.5f);
            body.MaximumSize = new Size(200, 40);
            body.AutoEllipsis = true;
            stack.Controls.Add(body);
            stack.Controls.Add(KidUi.Mono(MessageDisplay.Time(latest), 8f, false, Theme.Faint));
        }
        if (Summary.UnreadMessages > 0)
            stack.Controls.Add(KidUi.Mono(Loc.T("Client.Kid.Unread", Summary.UnreadMessages), 8.5f, true, Theme.Warn));

        var button = new Button
        {
            Text = Loc.T("Client.Kid.MessageParents"),
            AutoSize = false,
            Width = 220,
            Height = 38,
            Margin = new Padding(0, 10, 0, 0)
        };
        Theme.StyleButton(button, primary: true);
        button.Font = Theme.Mono(8.5f, FontStyle.Bold);
        button.Click += (_, _) => Raise("messages");
        stack.Controls.Add(button);
        return card;
    }
}

internal sealed class ScheduleView : KidView
{
    protected override void Rebuild()
    {
        if (Summary is null) return;
        var page = PageStack();
        var header = PageHeader("Client.Kid.Schedule.Eyebrow", Loc.T("Client.Kid.Schedule.Title"));
        header.Margin = new Padding(0, 0, 0, 14);
        AddPageRow(page, header);
        var week = BuildWeekCard();
        week.Margin = new Padding(0, 0, 0, 12);
        AddPageRow(page, week);
        var locks = BuildLocksCard();
        locks.Margin = new Padding(0, 0, 0, 12);
        AddPageRow(page, locks);
        var limits = BuildAppLimitsCard();
        AddPageRow(page, limits);
        RebuildBase(ScrollHost(page));
    }

    private CardPanel BuildWeekCard()
    {
        var card = Card("Client.Kid.Schedule.Daily");
        card.AutoScroll = false;
        card.AutoSize = true;
        card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        card.Margin = new Padding(0, 0, 0, 12);
        var stack = Stack(card);
        for (var day = 0; day < 7; day++)
        {
            var minutes = Summary!.Weekdays.FirstOrDefault(w => w.Day == day)?.Minutes ?? 0;
            var isToday = (int)DateTime.Now.DayOfWeek == day;
            var row = new TableLayoutPanel
            {
                ColumnCount = 3,
                RowCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 1, 0, 1)
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            row.Controls.Add(KidUi.Mono(WeekdayLabel(day), 9f, isToday, isToday ? Theme.Value : Theme.Muted), 0, 0);
            var bar = new BarPanel
            {
                Fraction = isToday && Summary.ScreenTimeEnabled && minutes > 0
                    ? Math.Min(1, (double)Summary.UsedTodaySeconds / (minutes * 60))
                    : 0,
                BarColor = isToday ? Theme.Brand : Theme.Value,
                Thickness = 7,
                Width = 420,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(8, 5, 8, 5)
            };
            row.Controls.Add(bar, 1, 0);
            row.Controls.Add(KidUi.Mono(Summary.ScreenTimeEnabled ? $"{minutes} min" : Loc.T("Client.Kid.Schedule.Off"),
                8.5f, isToday, isToday ? Theme.Value : Theme.Muted), 2, 0);
            stack.Controls.Add(row);
        }
        stack.Controls.Add(KidUi.Divider(Loc.T("Client.Kid.Schedule.RunsOut")));
        return card;
    }

    private CardPanel BuildLocksCard()
    {
        var card = Card("Client.Kid.Schedule.LockedTimes");
        card.Dock = DockStyle.Top;
        card.AutoSize = true;
        card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        card.Margin = new Padding(0, 0, 0, 12);
        var stack = Stack(card);
        if (Summary!.Schedules.Count == 0)
            stack.Controls.Add(KidUi.Body(Loc.T("Client.Kid.Schedule.NoLocks"), true));
        foreach (var schedule in Summary.Schedules)
        {
            var row = KidUi.Stack();
            row.Margin = new Padding(0, 0, 0, 8);
            row.Controls.Add(KidUi.Body(schedule.Name, size: 10.5f));
            row.Controls.Add(KidUi.Mono($"{Clock(schedule.StartMinutes)}–{Clock(schedule.EndMinutes)} · {Days(schedule.DaysMask)}", 8.5f));
            stack.Controls.Add(row);
        }
        return card;
    }

    private CardPanel BuildAppLimitsCard()
    {
        var card = Card("Client.Kid.Schedule.AppLimits");
        card.Dock = DockStyle.Top;
        card.AutoSize = true;
        card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var stack = Stack(card);
        var apps = Summary!.Apps.Where(a => a.DailyLimitMinutes is > 0).ToList();
        if (apps.Count == 0)
            stack.Controls.Add(KidUi.Body(Loc.T("Client.Kid.Schedule.NoAppLimits"), true));
        foreach (var app in apps)
        {
            var row = new TableLayoutPanel
            {
                ColumnCount = 3,
                RowCount = 1,
                AutoSize = true,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 1, 0, 3)
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            row.Controls.Add(KidUi.Body(app.DisplayName, size: 9.5f), 0, 0);
            row.Controls.Add(KidUi.Mono(Loc.T("Client.Kid.Schedule.Used", KidUi.Duration(app.UsedTodaySeconds)), 8.5f), 1, 0);
            row.Controls.Add(KidUi.Mono(Loc.T("Client.Kid.Schedule.Limit", app.DailyLimitMinutes ?? 0), 8.5f, true, Theme.Value), 2, 0);
            stack.Controls.Add(row);
        }
        return card;
    }

    private static string Clock(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";

    private static string Days(int mask)
    {
        var selected = Enumerable.Range(0, 7).Where(d => (mask & (1 << d)) != 0).ToList();
        if (selected.Count == 7) return Loc.T("Client.Kid.Schedule.EveryDay");
        if (selected.SequenceEqual(new[] { 1, 2, 3, 4, 5 })) return Loc.T("Client.Kid.Schedule.Weekdays");
        if (selected.SequenceEqual(new[] { 0, 6 })) return Loc.T("Client.Kid.Schedule.Weekends");
        return string.Join(", ", selected.Select(DayName));
    }
}

internal sealed class RulesView : KidView
{
    private bool _apps;
    private Button? _websitesButton;
    private Button? _appsButton;
    private Panel? _body;

    protected override void Rebuild()
    {
        if (Summary is null) return;
        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent
        };
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        page.Controls.Add(PageHeader("Client.Kid.Rules.Eyebrow", Loc.T("Client.Kid.Rules.Title")), 0, 0);

        var switchRow = KidUi.Row();
        _websitesButton = new Button { Text = Loc.T("Client.Kid.Rules.Websites"), AutoSize = false, Width = 150, Height = 38 };
        _appsButton = new Button { Text = Loc.T("Client.Kid.Rules.Apps"), AutoSize = false, Width = 150, Height = 38 };
        Theme.StyleButton(_websitesButton, primary: !_apps);
        Theme.StyleButton(_appsButton, primary: _apps);
        _websitesButton.Click += (_, _) => { if (_apps) { _apps = false; Rebuild(); } };
        _appsButton.Click += (_, _) => { if (!_apps) { _apps = true; Rebuild(); } };
        switchRow.Controls.Add(_websitesButton);
        switchRow.Controls.Add(_appsButton);
        switchRow.Margin = new Padding(0, 0, 0, 12);
        page.Controls.Add(switchRow, 0, 1);

        _body = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        var bodyStack = PageStack();
        BuildBody(bodyStack);
        _body.Controls.Add(ScrollHost(bodyStack));
        page.Controls.Add(_body, 0, 2);
        RebuildBase(page);
    }

    private void BuildBody(TableLayoutPanel stack)
    {
        if (Summary is null) return;
        if (!_apps)
        {
            var categories = Card("Client.Kid.Rules.Categories");
            categories.Dock = DockStyle.Top;
            categories.AutoSize = true;
            categories.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            categories.Margin = new Padding(0, 0, 0, 12);
            var catStack = Stack(categories);
            if (Summary.Categories.Count == 0)
                catStack.Controls.Add(KidUi.Body(Loc.T("Client.Kid.Rules.None"), true));
            foreach (var category in Summary.Categories)
                catStack.Controls.Add(KidUi.Body(LocalizeCategory(category), size: 10f));
            AddPageRow(stack, categories);

            var allowed = Card("Client.Kid.Rules.AlwaysAllowedSites");
            allowed.Dock = DockStyle.Top;
            allowed.AutoSize = true;
            allowed.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            allowed.Margin = new Padding(0, 0, 0, 12);
            var allowStack = Stack(allowed);
            var allowedSites = Summary.Websites.Where(w => w.Allowed).ToList();
            if (allowedSites.Count == 0)
                allowStack.Controls.Add(KidUi.Body(Loc.T("Client.Kid.Rules.None"), true));
            foreach (var site in allowedSites)
                allowStack.Controls.Add(KidUi.Mono(site.Domain, 9f, false, Theme.Value));
            AddPageRow(stack, allowed);

            var blocked = Card("Client.Kid.Rules.BlockedSites");
            blocked.Dock = DockStyle.Top;
            blocked.AutoSize = true;
            blocked.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var blockedStack = Stack(blocked);
            var blockedSites = Summary.Websites.Where(w => !w.Allowed).ToList();
            if (blockedSites.Count == 0)
                blockedStack.Controls.Add(KidUi.Body(Loc.T("Client.Kid.Rules.None"), true));
            foreach (var site in blockedSites)
            {
                var row = KidUi.Row();
                row.Margin = new Padding(0, 0, 0, 6);
                row.Controls.Add(KidUi.Mono(site.Domain, 9f, false, Theme.Text));
                row.Controls.Add(site.Always
                    ? TagLabel.Danger(Loc.T("Client.Kid.Rules.Always"), Theme.IsLight)
                    : TagLabel.Warn(site.ScheduleName ?? Loc.T("Client.Kid.Rules.Scheduled"), Theme.IsLight));
                blockedStack.Controls.Add(row);
            }
            AddPageRow(stack, blocked);
        }
        else
        {
            var blocked = Card("Client.Kid.Rules.BlockedApps");
            blocked.Dock = DockStyle.Top;
            blocked.AutoSize = true;
            blocked.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            blocked.Margin = new Padding(0, 0, 0, 12);
            var blockedStack = Stack(blocked);
            var blockedApps = Summary.Apps.Where(a => !a.Allowed).ToList();
            if (blockedApps.Count == 0)
                blockedStack.Controls.Add(KidUi.Body(Loc.T("Client.Kid.Rules.None"), true));
            foreach (var app in blockedApps)
            {
                var row = KidUi.Row();
                row.Margin = new Padding(0, 0, 0, 6);
                var name = KidUi.Body(app.DisplayName, size: 9.5f);
                name.MaximumSize = new Size(420, 0);
                row.Controls.Add(name);
                row.Controls.Add((app.ScheduleName is null && app.AlwaysAllowed)
                    ? TagLabel.Value(Loc.T("Client.Kid.Rules.AlwaysAllowed"), Theme.IsLight)
                    : app.ScheduleName is null
                        ? TagLabel.Danger(Loc.T("Client.Kid.Rules.Always"), Theme.IsLight)
                        : TagLabel.Warn(app.ScheduleName, Theme.IsLight));
                blockedStack.Controls.Add(row);
            }
            AddPageRow(stack, blocked);

            var allowed = Card("Client.Kid.Rules.AlwaysAllowedApps");
            allowed.Dock = DockStyle.Top;
            allowed.AutoSize = true;
            allowed.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var allowedStack = Stack(allowed);
            var allowedApps = Summary.Apps.Where(a => a.AlwaysAllowed).ToList();
            if (allowedApps.Count == 0)
                allowedStack.Controls.Add(KidUi.Body(Loc.T("Client.Kid.Rules.None"), true));
            foreach (var app in allowedApps)
                allowedStack.Controls.Add(KidUi.Body(app.DisplayName, size: 10f));
            allowedStack.Controls.Add(KidUi.Divider(Loc.T("Client.Kid.Rules.AlwaysAllowedNote")));
            AddPageRow(stack, allowed);
        }

        var footer = KidUi.Link(Loc.T("Client.Kid.Rules.AskParents"));
        footer.Margin = new Padding(0, 8, 0, 0);
        footer.Click += (_, _) => Raise("messages");
        stack.Controls.Add(footer);
    }

    private static string LocalizeCategory(SummaryCategoryDto category)
    {
        var key = "Client.Kid.Category." + category.Key;
        var text = Loc.T(key);
        return text == key ? category.Name : text;
    }
}

internal sealed class ActivityView : KidView
{
    private bool _week;

    protected override void Rebuild()
    {
        if (Summary is null) return;
        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent
        };
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        page.Controls.Add(PageHeader("Client.Kid.Activity.Eyebrow", Loc.T("Client.Kid.Activity.Title")), 0, 0);

        var switchRow = KidUi.Row();
        var today = new Button { Text = Loc.T("Client.Kid.Activity.Today"), AutoSize = false, Width = 150, Height = 38 };
        var week = new Button { Text = Loc.T("Client.Kid.Activity.Week"), AutoSize = false, Width = 150, Height = 38 };
        Theme.StyleButton(today, primary: !_week);
        Theme.StyleButton(week, primary: _week);
        today.Click += (_, _) => { if (_week) { _week = false; Rebuild(); } };
        week.Click += (_, _) => { if (!_week) { _week = true; Rebuild(); } };
        switchRow.Controls.Add(today);
        switchRow.Controls.Add(week);
        switchRow.Margin = new Padding(0, 0, 0, 12);
        page.Controls.Add(switchRow, 0, 1);

        var stack = PageStack();
        var apps = BuildAppsCard();
        apps.Margin = new Padding(0, 0, 0, 12);
        AddPageRow(stack, apps);
        var sites = BuildSitesCard();
        sites.Margin = new Padding(0, 0, 0, 12);
        AddPageRow(stack, sites);
        var history = BuildHistoryCard();
        AddPageRow(stack, history);
        page.Controls.Add(ScrollHost(stack), 0, 2);
        RebuildBase(page);
    }

    private CardPanel BuildAppsCard()
    {
        var summary = Summary!;
        var total = _week ? summary.UsedWeekSeconds : summary.UsedTodaySeconds;
        var card = Card("Client.Kid.Activity.Apps", Loc.T("Client.Kid.Activity.Total", KidUi.Duration(total)));
        card.Dock = DockStyle.Top;
        card.AutoSize = true;
        card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        card.Margin = new Padding(0, 0, 0, 12);
        var stack = Stack(card);
        var apps = (_week ? summary.AppsWeek : summary.AppsToday).Take(10).ToList();
        if (apps.Count == 0)
        {
            stack.Controls.Add(KidUi.Body(Loc.T("Client.Kid.Activity.None"), true));
            return card;
        }
        var max = Math.Max(1, apps.Max(a => a.Seconds));
        foreach (var app in apps)
        {
            var row = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 0, 8)
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            row.Controls.Add(KidUi.Body(app.DisplayName, size: 9.5f), 0, 0);
            var value = KidUi.Mono(KidUi.Duration(app.Seconds), 8.5f, false, Theme.Value);
            value.Anchor = AnchorStyles.Right;
            row.Controls.Add(value, 1, 0);
            var bar = new BarPanel
            {
                Fraction = (double)app.Seconds / max,
                Thickness = 6,
                Width = 700,
                Anchor = AnchorStyles.Left | AnchorStyles.Right
            };
            row.Controls.Add(bar, 0, 1);
            row.SetColumnSpan(bar, 2);
            stack.Controls.Add(row);
        }
        return card;
    }

    private CardPanel BuildSitesCard()
    {
        var card = Card("Client.Kid.Activity.Websites");
        card.Dock = DockStyle.Top;
        card.Height = 300;
        card.Margin = new Padding(0, 0, 0, 12);
        var stack = Stack(card);
        var visits = Summary!.Visits.AsEnumerable();
        if (!_week)
        {
            var start = DateTime.Today;
            visits = visits.Where(v => v.VisitedUtc.ToLocalTime() >= start);
        }
        var list = visits.Take(80).ToList();
        if (list.Count == 0)
        {
            stack.Controls.Add(KidUi.Body(Loc.T("Client.Kid.Activity.None"), true));
            return card;
        }
        foreach (var visit in list)
        {
            var row = KidUi.Row();
            row.Margin = new Padding(0, 0, 0, 5);
            row.Controls.Add(KidUi.Mono(visit.VisitedUtc.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture), 8.5f, false, Theme.Faint));
            var domain = KidUi.Body(visit.Domain, size: 9.5f);
            domain.MaximumSize = new Size(430, 0);
            row.Controls.Add(domain);
            if (visit.Blocked)
                row.Controls.Add(TagLabel.Danger(Loc.T("Client.Kid.Activity.Blocked"), Theme.IsLight));
            stack.Controls.Add(row);
        }
        return card;
    }

    private CardPanel BuildHistoryCard()
    {
        var card = Card("Client.Kid.Activity.History");
        card.Dock = DockStyle.Top;
        card.AutoSize = true;
        card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var stack = Stack(card);
        var cutoff = _week ? DateTime.Today.AddDays(-6) : DateTime.Today;
        var rows = Summary!.History.Where(h => h.CreatedUtc.ToLocalTime() >= cutoff).Take(40).ToList();
        if (rows.Count == 0)
        {
            stack.Controls.Add(KidUi.Body(Loc.T("Client.Kid.Activity.None"), true));
            return card;
        }
        foreach (var item in rows)
        {
            var row = KidUi.Row();
            row.Margin = new Padding(0, 0, 0, 6);
            var square = new Panel
            {
                Width = 10,
                Height = 10,
                BackColor = item.Kind switch
                {
                    "locked" => Theme.Danger,
                    "unlocked" => Theme.Brand,
                    _ => Theme.Warn
                },
                Margin = new Padding(0, 5, 8, 0)
            };
            row.Controls.Add(square);
            row.Controls.Add(KidUi.Body(HistoryText(item), size: 9.5f));
            row.Controls.Add(KidUi.Mono(item.CreatedUtc.ToLocalTime().ToString("d MMM, HH:mm", CultureInfo.CurrentCulture), 8f, false, Theme.Faint));
            stack.Controls.Add(row);
        }
        return card;
    }

    private static string HistoryText(SummaryHistoryDto item) => item.Kind switch
    {
        "locked" => Loc.T("Client.Kid.Activity.Locked"),
        "unlocked" => Loc.T("Client.Kid.Activity.Unlocked"),
        _ => item.Minutes is { } minutes ? Loc.T("Client.Kid.Activity.BonusMinutes", minutes) : Loc.T("Client.Kid.Activity.Bonus")
    };
}
