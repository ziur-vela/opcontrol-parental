using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Services;
using Xunit;

namespace OpControl.Parental.Tests;

public class FoundationTests
{
    private static ParentalDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ParentalDbContext>()
            .UseInMemoryDatabase("test-" + Guid.NewGuid())
            .Options);

    private static AlertService NewAlerts(ParentalDbContext db) =>
        new(db, new DashboardEventBus(), new EphemeralDataProtectionProvider(),
            new EmailSender(db, new EphemeralDataProtectionProvider(), NullLogger<EmailSender>.Instance),
            NullLogger<AlertService>.Instance);

    private static ScheduleDto Schedule(int mask, int start, int end, string name = "Bedtime", bool enabled = true) =>
        new(Guid.NewGuid(), name, mask, start, end, enabled);

    [Fact]
    public void Hash_is_stable_and_codes_are_normalized()
    {
        Assert.Equal(SecretToken.Hash("abc"), SecretToken.Hash("abc"));
        Assert.NotEqual(SecretToken.Hash("abc"), SecretToken.Hash("abd"));
        Assert.Equal("ABCD2345", SecretToken.NormalizeCode("ab-cd 2345"));
        Assert.True(SecretToken.FixedTimeEquals("same", "same"));
        Assert.False(SecretToken.FixedTimeEquals("same", "diff"));
    }

    [Fact]
    public void Pin_hash_verifies_and_rejects_wrong_pin()
    {
        var hash = PinHasher.Hash("4821");
        Assert.True(PinHasher.Verify(hash, "4821"));
        Assert.False(PinHasher.Verify(hash, "4822"));
        Assert.False(PinHasher.Verify(null, "4821"));
        Assert.False(PinHasher.Verify("bogus", "4821"));
    }

    [Fact]
    public void Bonus_overrides_manual_lock_then_relocks()
    {
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        var during = LockRules.Evaluate(true, null, now.AddMinutes(15), [], TimeZoneInfo.Utc, now.AddMinutes(5));
        Assert.False(during.IsLocked);
        Assert.Equal("bonus", during.Reason);

        var after = LockRules.Evaluate(true, null, now.AddMinutes(15), [], TimeZoneInfo.Utc, now.AddMinutes(16));
        Assert.True(after.IsLocked);
        Assert.Equal("manual", after.Reason);
    }

    [Fact]
    public void Manual_lock_and_unlock_override_are_evaluated_in_order()
    {
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(LockRules.Evaluate(true, now.AddMinutes(10), null, [], TimeZoneInfo.Utc, now).IsLocked);
        Assert.True(LockRules.Evaluate(true, now.AddMinutes(10), null, [], TimeZoneInfo.Utc, now.AddMinutes(11)).IsLocked);
        Assert.False(LockRules.Evaluate(false, null, null, [], TimeZoneInfo.Utc, now).IsLocked);
    }

    [Fact]
    public void Overnight_schedule_spans_midnight()
    {
        var monday = new DateTime(2026, 10, 19, 23, 0, 0, DateTimeKind.Utc);
        var schedules = new[] { Schedule(1 << 1, 22 * 60, 6 * 60) };

        Assert.True(LockRules.ActiveSchedule(schedules, TimeZoneInfo.Utc, monday) is not null);
        Assert.True(LockRules.ActiveSchedule(schedules, TimeZoneInfo.Utc, monday.AddHours(6)) is not null);
        Assert.Null(LockRules.ActiveSchedule(schedules, TimeZoneInfo.Utc, monday.AddHours(13)));
    }

    [Fact]
    public void Next_lock_cycle_uses_current_window_end_then_next_start()
    {
        var mondayNight = new DateTime(2026, 10, 19, 23, 0, 0, DateTimeKind.Utc);
        var schedules = new[] { Schedule(1 << 1, 22 * 60, 6 * 60) };

        var cycle = LockRules.NextLockCycleUtc(schedules, TimeZoneInfo.Utc, mondayNight);
        Assert.Equal(new DateTime(2026, 10, 20, 6, 0, 0, DateTimeKind.Utc), cycle);

        var mondayNoon = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        var next = LockRules.NextLockCycleUtc(schedules, TimeZoneInfo.Utc, mondayNoon);
        Assert.Equal(new DateTime(2026, 10, 19, 22, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Countdown_uses_bonus_expiry_when_bonus_is_active()
    {
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        var next = LockRules.NextLockUtc(true, null, now.AddMinutes(30), [], TimeZoneInfo.Utc, now);
        Assert.Equal(now.AddMinutes(30), next);
    }

    [Fact]
    public async Task Enrollment_code_is_single_use_and_returns_a_device_credential()
    {
        await using var db = NewDb();
        var family = new Family { Name = "Test family" };
        var child = new Child { FamilyId = family.Id, Name = "Test child" };
        db.Families.Add(family);
        db.Children.Add(child);
        db.LockStates.Add(new LockState { FamilyId = family.Id, ChildId = child.Id });
        await db.SaveChangesAsync();

        var service = new EnrollmentService(db, NullLogger<EnrollmentService>.Instance);
        var (code, _) = await service.CreateCodeAsync(family.Id, child.Id, Guid.NewGuid());
        var response = await service.RedeemAsync(new EnrollRequest(code, "Test device", DevicePlatform.Windows, "10", "UTC", "Alex"));

        Assert.Equal(family.Id, (await db.Devices.SingleAsync()).FamilyId);
        Assert.Equal(child.Id, response.ChildId);
        Assert.False(string.IsNullOrWhiteSpace(response.Credential));
        Assert.Equal(SecretToken.Hash(response.Credential), (await db.Devices.SingleAsync()).CredentialHash);

        await Assert.ThrowsAsync<EnrollmentException>(() =>
            service.RedeemAsync(new EnrollRequest(code, "Second device", DevicePlatform.Windows, "10", "UTC", null)));
    }

    [Fact]
    public async Task Enrollment_code_cannot_be_created_for_another_family()
    {
        await using var db = NewDb();
        var familyA = new Family { Name = "A" };
        var familyB = new Family { Name = "B" };
        var childB = new Child { FamilyId = familyB.Id, Name = "B child" };
        db.Families.AddRange(familyA, familyB);
        db.Children.Add(childB);
        await db.SaveChangesAsync();

        var service = new EnrollmentService(db, NullLogger<EnrollmentService>.Instance);
        await Assert.ThrowsAsync<EnrollmentException>(() => service.CreateCodeAsync(familyA.Id, childB.Id, Guid.NewGuid()));
    }

    [Fact]
    public async Task Device_auth_rejects_unknown_and_accepts_valid_tokens()
    {
        await using var db = NewDb();
        var family = new Family { Name = "F" };
        var child = new Child { FamilyId = family.Id, Name = "C" };
        var token = SecretToken.NewToken();
        db.Families.Add(family);
        db.Children.Add(child);
        db.Devices.Add(new Device
        {
            FamilyId = family.Id,
            ChildId = child.Id,
            Name = "D",
            CredentialHash = SecretToken.Hash(token),
            CredentialExpiresUtc = DateTime.UtcNow.AddDays(10)
        });
        await db.SaveChangesAsync();

        var auth = new DeviceAuthService(db);
        Assert.NotNull(await auth.ValidateAsync(token, CancellationToken.None));
        Assert.Null(await auth.ValidateAsync("not-the-token", CancellationToken.None));
    }
}

public class Job3PolicyTests
{
    private static DeviceStateDto State(
        IReadOnlyList<WebsiteRuleDto>? sites = null,
        IReadOnlyList<AppRuleDto>? apps = null,
        IReadOnlyList<ItemBonusDto>? bonuses = null,
        IReadOnlyList<ScheduleDto>? schedules = null,
        IReadOnlyList<AppDailyUsageDto>? usage = null,
        DailyLimitDto? daily = null) =>
        new(Guid.NewGuid(), "Alex", "Parent One", false, null, null, null, null,
            schedules ?? [], sites ?? [], apps ?? [], bonuses ?? [], [], DateTime.UtcNow,
            usage ?? [], daily, false);

    private static WebsiteRuleDto Site(string domain, bool blocked, RuleMode mode = RuleMode.Always, Guid? scheduleId = null) =>
        new(Guid.NewGuid(), domain, blocked, mode, scheduleId, DateTime.UtcNow);

    private static AppRuleDto App(string identity, bool blocked, RuleMode mode = RuleMode.Always, Guid? scheduleId = null, int? limit = null, bool alwaysAllowed = false) =>
        new(Guid.NewGuid(), identity, identity, null, blocked, mode, scheduleId, DateTime.UtcNow, limit, alwaysAllowed);

    [Fact]
    public void Site_rule_blocks_subdomains_and_allow_rule_wins()
    {
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        var state = State([Site("youtube.com", true), Site("classroom.google.com", false)]);
        Assert.True(PolicyRules.IsWebsiteBlocked(state, "www.youtube.com", TimeZoneInfo.Utc, now));
        Assert.True(PolicyRules.IsWebsiteBlocked(state, "m.youtube.com", TimeZoneInfo.Utc, now));
        Assert.False(PolicyRules.IsWebsiteBlocked(state, "classroom.google.com", TimeZoneInfo.Utc, now));
        Assert.False(PolicyRules.IsWebsiteBlocked(state, "example.com", TimeZoneInfo.Utc, now));
    }

    [Fact]
    public void Allow_rule_overrides_categories()
    {
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        var state = State([Site("example.com", false)]);
        Assert.False(PolicyRules.IsWebsiteBlocked(state, "example.com", TimeZoneInfo.Utc, now, _ => true));
    }

    [Fact]
    public void Scheduled_site_rule_only_applies_inside_window()
    {
        var schedule = new ScheduleDto(Guid.NewGuid(), "Homework", 0x7F, 16 * 60, 18 * 60, true);
        var state = State([Site("discord.com", true, RuleMode.Scheduled, schedule.Id)], schedules: [schedule]);
        var inside = new DateTime(2026, 10, 18, 17, 0, 0, DateTimeKind.Utc);
        var outside = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(PolicyRules.IsWebsiteBlocked(state, "discord.com", TimeZoneInfo.Utc, inside));
        Assert.False(PolicyRules.IsWebsiteBlocked(state, "discord.com", TimeZoneInfo.Utc, outside));
    }

    [Fact]
    public void Item_bonus_allows_one_site_until_expiry()
    {
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        var state = State([Site("youtube.com", true)], bonuses: [new ItemBonusDto("site", "youtube.com", now.AddMinutes(15), 15)]);
        Assert.False(PolicyRules.IsWebsiteBlocked(state, "www.youtube.com", TimeZoneInfo.Utc, now.AddMinutes(5)));
        Assert.True(PolicyRules.IsWebsiteBlocked(state, "www.youtube.com", TimeZoneInfo.Utc, now.AddMinutes(16)));
    }

    [Fact]
    public void App_rules_match_identity_and_path()
    {
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        var state = State(apps: [App("store:roblox_abc", true), App(@"path:c:\games\steam.exe", true)]);
        Assert.True(PolicyRules.IsAppBlocked(state, "store:roblox_abc", null, TimeZoneInfo.Utc, now));
        Assert.False(PolicyRules.IsAppBlocked(state, "store:other_abc", null, TimeZoneInfo.Utc, now));
        Assert.True(PolicyRules.IsAppBlocked(state, "win32:steam.exe|valve", @"C:\Games\Steam.exe", TimeZoneInfo.Utc, now));
    }

    [Fact]
    public void Domain_normalization_strips_scheme_path_port_and_www()
    {
        Assert.Equal("youtube.com", RuleService.NormalizeDomain("https://www.youtube.com/watch?v=1"));
        Assert.Equal("example.com", RuleService.NormalizeDomain("example.com:443/path"));
        Assert.Throws<InvalidOperationException>(() => RuleService.NormalizeDomain("not a domain"));
    }
}


public class Job4UsageTests
{
    private static AlertService NewAlerts(ParentalDbContext db) =>
        new(db, new DashboardEventBus(), new EphemeralDataProtectionProvider(),
            new EmailSender(db, new EphemeralDataProtectionProvider(), NullLogger<EmailSender>.Instance),
            NullLogger<AlertService>.Instance);

    private static DeviceStateDto State(
        IReadOnlyList<AppRuleDto>? apps = null,
        IReadOnlyList<AppDailyUsageDto>? usage = null,
        DailyLimitDto? daily = null) =>
        new(Guid.NewGuid(), "Alex", "Parent One", false, null, null, null, null,
            [], [], apps ?? [], [], [], DateTime.UtcNow, usage ?? [], daily, false);

    private static AppRuleDto App(string identity, bool blocked, int? limit = null, bool alwaysAllowed = false) =>
        new(Guid.NewGuid(), identity, identity, null, blocked, RuleMode.Always, null, DateTime.UtcNow, limit, alwaysAllowed);

    [Fact]
    public void Daily_limit_locks_at_zero_and_bonus_still_overrides()
    {
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        var dayStart = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);
        var dayEnd = dayStart.AddDays(1);
        var daily = new DailyLimitDto(true, 120, 120 * 60, dayStart, dayEnd);

        var evaluation = LockRules.Evaluate(false, null, null, [], TimeZoneInfo.Utc, now, daily);
        Assert.True(evaluation.IsLocked);
        Assert.Equal("daily-limit", evaluation.Reason);
        Assert.Equal(dayEnd, evaluation.EndsUtc);

        var withBonus = LockRules.Evaluate(false, null, now.AddMinutes(15), [], TimeZoneInfo.Utc, now, daily);
        Assert.False(withBonus.IsLocked);
        Assert.Equal("bonus", withBonus.Reason);

        Assert.Equal(0, LockRules.DailyLimitRemainingSeconds(daily, now));
    }

    [Fact]
    public void Daily_limit_is_not_reached_after_midnight()
    {
        var dayStart = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);
        var daily = new DailyLimitDto(true, 120, 120 * 60, dayStart, dayStart.AddDays(1));
        var afterMidnight = dayStart.AddDays(1).AddMinutes(1);
        Assert.False(LockRules.DailyLimitReached(daily, afterMidnight));
        Assert.Equal(120 * 60, LockRules.DailyLimitRemainingSeconds(daily, afterMidnight));
    }

    [Fact]
    public void Per_app_limit_blocks_and_always_allowed_app_is_exempt()
    {
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        var state = State(
            [App("store:roblox_abc", false, 60, false), App("win32:school.exe|school", false, null, true)],
            [new AppDailyUsageDto("store:roblox_abc", 3600, 60, false),
             new AppDailyUsageDto("win32:school.exe|school", 7200, null, true)]);

        Assert.True(PolicyRules.IsAppBlocked(state, "store:roblox_abc", null, TimeZoneInfo.Utc, now, out var reason));
        Assert.Equal("daily-limit", reason);
        Assert.False(PolicyRules.IsAppBlocked(state, "win32:school.exe|school", @"C:\School\school.exe", TimeZoneInfo.Utc, now));
        Assert.False(PolicyRules.IsAppBlocked(state, "store:other_abc", null, TimeZoneInfo.Utc, now));
    }

    [Fact]
    public void Local_day_bounds_use_the_child_time_zone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test-8", TimeSpan.FromHours(-8), "Test", "Test");
        var now = new DateTime(2026, 10, 19, 3, 0, 0, DateTimeKind.Utc);
        var (start, end) = UsageService.LocalDayBounds(zone, now);
        Assert.Equal(new DateTime(2026, 10, 18, 8, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc), end);
    }

    [Fact]
    public async Task Usage_sync_is_idempotent_and_usage_counts_active_time_only()
    {
        await using var db = new ParentalDbContext(new DbContextOptionsBuilder<ParentalDbContext>()
            .UseInMemoryDatabase("usage-" + Guid.NewGuid()).Options);
        var family = new Family { Name = "F" };
        var child = new Child { FamilyId = family.Id, Name = "C" };
        var device = new Device { FamilyId = family.Id, ChildId = child.Id, Name = "D" };
        db.Families.Add(family);
        db.Children.Add(child);
        db.Devices.Add(device);
        db.ScreenTimeLimits.Add(new ScreenTimeLimit { FamilyId = family.Id, ChildId = child.Id, Enabled = true, MondayMinutes = 120 });
        db.AppRules.Add(new AppRule { FamilyId = family.Id, ChildId = child.Id, AppIdentity = "win32:school.exe|school", IsBlocked = false, Mode = RuleMode.Always, AlwaysAllowed = true });
        await db.SaveChangesAsync();

        var service = new UsageService(db, NewAlerts(db));
        var start = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        var interval = new UsageIntervalDto(Guid.NewGuid(), "win32:brave.exe|brave", "Brave", start, 30, true);
        var school = new UsageIntervalDto(Guid.NewGuid(), "win32:school.exe|school", "School", start, 60, true);
        var idle = new UsageIntervalDto(Guid.NewGuid(), "", "", start, 45, false);
        var request = new UsageBatchRequest([interval, school, idle], [new WebsiteUsageDto(Guid.NewGuid(), "youtube.com", start, 30)]);

        await service.SyncUsageAsync(device, request);
        await service.SyncUsageAsync(device, request);

        Assert.Equal(3, await db.UsageIntervals.CountAsync());
        Assert.Equal(1, await db.WebsiteUsages.CountAsync());
        var used = await service.UsedTodaySecondsAsync(family.Id, child.Id, TimeZoneInfo.Utc, start.AddMinutes(5));
        Assert.Equal(30, used);
        var apps = await service.AppUsageTodayAsync(family.Id, child.Id, TimeZoneInfo.Utc, start.AddMinutes(5));
        Assert.Equal(30, apps.Single(a => a.Identity == "win32:brave.exe|brave").UsedTodaySeconds);
        Assert.True(apps.Single(a => a.Identity == "win32:school.exe|school").AlwaysAllowed);
    }

    [Fact]
    public async Task Usage_sanity_rejects_future_timestamps_and_caps_wall_clock_duration()
    {
        await using var db = new ParentalDbContext(new DbContextOptionsBuilder<ParentalDbContext>()
            .UseInMemoryDatabase("sanity-" + Guid.NewGuid()).Options);
        var family = new Family { Name = "F" };
        var child = new Child { FamilyId = family.Id, Name = "C" };
        var device = new Device { FamilyId = family.Id, ChildId = child.Id, Name = "D" };
        db.Families.Add(family);
        db.Children.Add(child);
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        var alerts = NewAlerts(db);
        var service = new UsageService(db, alerts);
        var now = DateTime.UtcNow;
        var future = new UsageIntervalDto(Guid.NewGuid(), "win32:test.exe|test", "Test", now.AddHours(1), 60, true);
        var huge = new UsageIntervalDto(Guid.NewGuid(), "win32:test.exe|test", "Test", now.AddSeconds(-10), 5000, true);

        await service.SyncUsageAsync(device, new UsageBatchRequest([future, huge]));

        var stored = await db.UsageIntervals.SingleAsync();
        Assert.InRange(stored.DurationSeconds, 1, 120);
        Assert.Single(await db.ClientTamperEvents.ToListAsync());
        Assert.Equal("usage-sanity", (await db.ClientTamperEvents.SingleAsync()).Kind);
    }

    [Fact]
    public async Task Unaccounted_intervals_are_stored_once()
    {
        await using var db = new ParentalDbContext(new DbContextOptionsBuilder<ParentalDbContext>()
            .UseInMemoryDatabase("unaccounted-" + Guid.NewGuid()).Options);
        var family = new Family { Name = "F" };
        var child = new Child { FamilyId = family.Id, Name = "C" };
        var device = new Device { FamilyId = family.Id, ChildId = child.Id, Name = "D" };
        db.Families.Add(family);
        db.Children.Add(child);
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        var service = new UsageService(db, NewAlerts(db));
        var item = new UnaccountedIntervalDto(Guid.NewGuid(), DateTime.UtcNow.AddMinutes(-3), 120, "helper-down");
        await service.SyncUnaccountedAsync(device, new UnaccountedBatchRequest([item]));
        await service.SyncUnaccountedAsync(device, new UnaccountedBatchRequest([item]));

        Assert.Equal(1, await db.UnaccountedIntervals.CountAsync());
        Assert.Equal(120, await service.UnaccountedSecondsAsync(family.Id, child.Id, item.StartUtc.AddMinutes(-1), item.StartUtc.AddMinutes(5)));
    }
}


public class Job5BMultiTenantTests
{
    private static ParentalDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ParentalDbContext>()
            .UseInMemoryDatabase("job5b-" + Guid.NewGuid())
            .Options);

    private static AlertService Alerts(ParentalDbContext db) =>
        new(db, new DashboardEventBus(), new EphemeralDataProtectionProvider(),
            new EmailSender(db, new EphemeralDataProtectionProvider(), NullLogger<EmailSender>.Instance),
            NullLogger<AlertService>.Instance);

    private static (Family A, Child ChildA, Family B, Child ChildB) TwoFamilies()
    {
        var a = new Family { Name = "A" };
        var childA = new Child { FamilyId = a.Id, Name = "A child" };
        var b = new Family { Name = "B" };
        var childB = new Child { FamilyId = b.Id, Name = "B child" };
        return (a, childA, b, childB);
    }

    [Fact]
    public async Task Cross_family_child_mutations_are_rejected()
    {
        await using var db = NewDb();
        var (a, childA, b, childB) = TwoFamilies();
        db.Families.AddRange(a, b);
        db.Children.AddRange(childA, childB);
        db.LockStates.Add(new LockState { FamilyId = a.Id, ChildId = childA.Id });
        db.LockStates.Add(new LockState { FamilyId = b.Id, ChildId = childB.Id });
        await db.SaveChangesAsync();

        var locks = new LockService(db, null!, new DashboardEventBus(),
            new RuleService(db, Alerts(db)), new UsageService(db, Alerts(db)), Alerts(db),
            NullLogger<LockService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => locks.SetPinAsync(a.Id, childB.Id, "4821"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => locks.ClearPinAsync(a.Id, childB.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RuleService(db, Alerts(db))
            .AddItemBonusAsync(a.Id, childB.Id, Guid.NewGuid(), "site", "youtube.com", 5));
        Assert.Null((await db.LockStates.SingleAsync(x => x.ChildId == childB.Id)).OverridePinHash);
    }

    [Fact]
    public async Task Suspended_family_device_credentials_stop_working()
    {
        await using var db = NewDb();
        var (a, childA, _, _) = TwoFamilies();
        var token = SecretToken.NewToken();
        db.Families.Add(a);
        db.Children.Add(childA);
        db.Devices.Add(new Device
        {
            FamilyId = a.Id, ChildId = childA.Id, Name = "D",
            CredentialHash = SecretToken.Hash(token), CredentialExpiresUtc = DateTime.UtcNow.AddDays(30)
        });
        await db.SaveChangesAsync();

        var auth = new DeviceAuthService(db);
        Assert.NotNull(await auth.ValidateAsync(token, CancellationToken.None));

        a.Status = FamilyStatus.Suspended;
        await db.SaveChangesAsync();
        Assert.Null(await auth.ValidateAsync(token, CancellationToken.None));

        a.Status = FamilyStatus.Active;
        await db.SaveChangesAsync();
        Assert.NotNull(await auth.ValidateAsync(token, CancellationToken.None));
    }

    [Fact]
    public async Task Invitation_is_single_use_expiring_and_bound_to_kind()
    {
        await using var db = NewDb();
        var (a, childA, _, _) = TwoFamilies();
        db.Families.Add(a);
        db.Children.Add(childA);
        await db.SaveChangesAsync();

        var protection = new EphemeralDataProtectionProvider();
        var email = new EmailSender(db, protection, NullLogger<EmailSender>.Instance);
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var service = new InvitationService(db, null!, email, protection, config, NullLogger<InvitationService>.Instance);

        var created = await service.CreateCoParentAsync(a.Id, Guid.NewGuid(), null, ParentPermissions.ViewReports);
        Assert.Null(created.Email);
        Assert.False(created.EmailSent);
        Assert.NotNull(await service.FindValidAsync(created.Token));

        var invitation = await db.Invitations.SingleAsync();
        invitation.ExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        Assert.Null(await service.FindValidAsync(created.Token));
    }

    [Fact]
    public void Permission_matrix_owner_and_parent_behave_differently()
    {
        var parent = new FamilyAccess(Guid.NewGuid(), Guid.NewGuid(), "P", FamilyRole.Parent,
            ParentPermissions.ViewReports | ParentPermissions.LockUnlockBonus, false, FamilyStatus.Active);
        Assert.True(parent.Has(ParentPermissions.ViewReports));
        Assert.True(parent.Has(ParentPermissions.LockUnlockBonus));
        Assert.False(parent.Has(ParentPermissions.ManagePin));
        Assert.False(parent.Has(ParentPermissions.RemoteView));
        Assert.Throws<PermissionDeniedException>(() => parent.Require(ParentPermissions.ManageDevices));

        var owner = parent with { Role = FamilyRole.Owner, Permissions = ParentPermissions.None };
        Assert.True(owner.Has(ParentPermissions.ManageDevices));
        Assert.True(owner.Has(ParentPermissions.RemoteView));
        owner.Require(ParentPermissions.ManagePin);
    }

    [Fact]
    public async Task Camera_consent_is_one_time_and_versioned()
    {
        await using var db = NewDb();
        var family = new Family { Name = "F" };
        var parent = new ParentUser { FamilyId = family.Id, DisplayName = "P" };
        db.Families.Add(family);
        db.Users.Add(parent);
        await db.SaveChangesAsync();
        var service = new CameraConsentService(db);
        Assert.False(await service.HasAcceptedAsync(parent.Id));
        await service.AcceptAsync(family.Id, parent.Id, "127.0.0.1", "test");
        Assert.True(await service.HasAcceptedAsync(parent.Id));
        await service.AcceptAsync(family.Id, parent.Id, "127.0.0.1", "test");
        Assert.Equal(1, await db.CameraConsents.CountAsync());
    }

    [Fact]
    public void Build_info_is_visible_and_versioned()
    {
        var info = new BuildInfo();
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
        Assert.False(string.IsNullOrWhiteSpace(info.DisplayVersion));
        Assert.Contains("build", info.DisplayVersion);
        Assert.False(string.IsNullOrWhiteSpace(info.ToDto().Commit));
    }
}
