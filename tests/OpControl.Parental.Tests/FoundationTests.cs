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
    public void Bonus_overrides_manual_lock_then_relocks()
    {
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        var state = new LockState
        {
            ManualLock = true,
            BonusUntilUtc = now.AddMinutes(15)
        };

        var during = LockEvaluator.Evaluate(state, Array.Empty<Schedule>(), TimeZoneInfo.Utc, now.AddMinutes(5));
        Assert.False(during.IsLocked);
        Assert.Equal("bonus", during.Reason);

        var after = LockEvaluator.Evaluate(state, Array.Empty<Schedule>(), TimeZoneInfo.Utc, now.AddMinutes(16));
        Assert.True(after.IsLocked);
        Assert.Equal("manual", after.Reason);
    }

    [Fact]
    public void Manual_lock_and_unlock_override_are_evaluated_in_order()
    {
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        var state = new LockState { ManualLock = true, UnlockUntilUtc = now.AddMinutes(10) };

        Assert.False(LockEvaluator.Evaluate(state, Array.Empty<Schedule>(), TimeZoneInfo.Utc, now).IsLocked);
        Assert.True(LockEvaluator.Evaluate(state, Array.Empty<Schedule>(), TimeZoneInfo.Utc, now.AddMinutes(11)).IsLocked);

        state.ManualLock = false;
        state.UnlockUntilUtc = null;
        Assert.False(LockEvaluator.Evaluate(state, Array.Empty<Schedule>(), TimeZoneInfo.Utc, now).IsLocked);
    }

    [Fact]
    public void Overnight_schedule_spans_midnight()
    {
        var monday = new DateTime(2026, 10, 19, 23, 0, 0, DateTimeKind.Utc);
        var schedules = new List<Schedule>
        {
            new() { DaysMask = 1 << 1, StartMinutes = 22 * 60, EndMinutes = 6 * 60, IsEnabled = true }
        };

        Assert.True(LockEvaluator.IsScheduleActive(schedules, TimeZoneInfo.Utc, monday));
        Assert.True(LockEvaluator.IsScheduleActive(schedules, TimeZoneInfo.Utc, monday.AddHours(6)));
        Assert.False(LockEvaluator.IsScheduleActive(schedules, TimeZoneInfo.Utc, monday.AddHours(13)));
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
        var response = await service.RedeemAsync(new EnrollRequest(code, "Test device", DevicePlatform.Windows, "10", "UTC", "Ziur"));

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
        await Assert.ThrowsAsync<EnrollmentException>(() =>
            service.CreateCodeAsync(familyA.Id, childB.Id, Guid.NewGuid()));
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
