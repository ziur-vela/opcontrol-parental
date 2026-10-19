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
        var response = await service.RedeemAsync(new EnrollRequest(code, "Test device", DevicePlatform.Windows, "10", "UTC", "Emma"));

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
        IReadOnlyList<ScheduleDto>? schedules = null) =>
        new(Guid.NewGuid(), "Emma", "Hendry", false, null, null, null, null,
            schedules ?? [], sites ?? [], apps ?? [], bonuses ?? [], [], DateTime.UtcNow);

    private static WebsiteRuleDto Site(string domain, bool blocked, RuleMode mode = RuleMode.Always, Guid? scheduleId = null) =>
        new(Guid.NewGuid(), domain, blocked, mode, scheduleId, DateTime.UtcNow);

    private static AppRuleDto App(string identity, bool blocked, RuleMode mode = RuleMode.Always, Guid? scheduleId = null) =>
        new(Guid.NewGuid(), identity, identity, null, blocked, mode, scheduleId, DateTime.UtcNow);

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
