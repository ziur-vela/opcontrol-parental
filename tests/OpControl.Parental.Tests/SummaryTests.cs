// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Services;
using Xunit;

namespace OpControl.Parental.Tests;

public class SummaryTests
{
    private static ParentalDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ParentalDbContext>()
            .UseInMemoryDatabase("summary-" + Guid.NewGuid())
            .Options);

    private sealed record Seed(Family Family, Child Child, Child Sibling, ParentUser Parent, Device Device);

    private static async Task<Seed> SeedAsync(ParentalDbContext db, string name)
    {
        var family = new Family { Name = name, DefaultLanguage = "en" };
        var child = new Child { FamilyId = family.Id, Name = name + " child" };
        var sibling = new Child { FamilyId = family.Id, Name = name + " sibling" };
        var parent = new ParentUser
        {
            FamilyId = family.Id,
            DisplayName = name + " parent",
            UserName = name.ToLowerInvariant() + "@example.test",
            Email = name.ToLowerInvariant() + "@example.test",
            Permissions = ParentPermissions.All
        };
        var device = new Device { FamilyId = family.Id, ChildId = child.Id, Name = name + " PC", TimeZoneId = "UTC" };
        db.Families.Add(family);
        db.Children.AddRange(child, sibling);
        db.Users.Add(parent);
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return new Seed(family, child, sibling, parent, device);
    }

    [Fact]
    public async Task Summary_is_scoped_to_the_devices_own_family_and_child()
    {
        await using var db = NewDb();
        var alpha = await SeedAsync(db, "Alpha");
        var beta = await SeedAsync(db, "Beta");

        db.WebsiteRules.AddRange(
            new WebsiteRule { FamilyId = alpha.Family.Id, ChildId = alpha.Child.Id, Domain = "alpha-own.test" },
            new WebsiteRule { FamilyId = beta.Family.Id, ChildId = beta.Child.Id, Domain = "beta-secret.test" },
            new WebsiteRule { FamilyId = alpha.Family.Id, ChildId = alpha.Sibling.Id, Domain = "alpha-sibling.test" });
        db.Schedules.AddRange(
            new Schedule { FamilyId = alpha.Family.Id, ChildId = alpha.Child.Id, Name = "Bedtime", DaysMask = 1 },
            new Schedule { FamilyId = alpha.Family.Id, ChildId = alpha.Sibling.Id, Name = "Sibling time", DaysMask = 1 });
        await db.SaveChangesAsync();

        var summary = await new ChildSummaryService(db).BuildAsync(alpha.Device);

        Assert.Equal(alpha.Child.Id, summary.ChildId);
        Assert.Equal("Alpha PC", summary.DeviceName);
        Assert.Contains(summary.Websites, w => w.Domain == "alpha-own.test");
        Assert.DoesNotContain(summary.Websites, w => w.Domain is "beta-secret.test" or "alpha-sibling.test");
        Assert.Contains(summary.Schedules, s => s.Name == "Bedtime");
        Assert.DoesNotContain(summary.Schedules, s => s.Name == "Sibling time");
    }

    [Fact]
    public async Task Summary_does_not_expose_pin_alerts_emails_permissions_remote_or_domains()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, "Private");
        db.LockStates.Add(new LockState
        {
            FamilyId = seed.Family.Id,
            ChildId = seed.Child.Id,
            OverridePinHash = "super-secret-pin-hash"
        });
        db.FamilySettings.Add(new FamilySettings
        {
            FamilyId = seed.Family.Id,
            SmtpHost = "smtp.secret.test",
            SmtpUsername = "smtp-user",
            FromAddress = "private-sender@example.test"
        });
        db.AlertPreferences.Add(new AlertPreference { FamilyId = seed.Family.Id, QuietStartMinutes = 123 });
        db.RemoteSessions.Add(new RemoteSession
        {
            FamilyId = seed.Family.Id,
            ChildId = seed.Child.Id,
            DeviceId = seed.Device.Id,
            Kind = RemoteSessionKind.Camera,
            Status = RemoteSessionStatus.Active
        });
        db.Categories.Add(new Category
        {
            Key = "social",
            Name = "Social",
            Version = 1,
            Domains = [new CategoryDomain { CategoryKey = "social", Domain = "hidden-category-domain.test", AddedVersion = 1 }]
        });
        db.ChildCategories.Add(new ChildCategory
        {
            FamilyId = seed.Family.Id,
            ChildId = seed.Child.Id,
            CategoryKey = "social",
            Enabled = true
        });
        await db.SaveChangesAsync();

        var summary = await new ChildSummaryService(db).BuildAsync(seed.Device);
        Assert.Contains(summary.Categories, c => c.Key == "social" && c.Name == "Social");

        var json = JsonSerializer.Serialize(summary);
        Assert.DoesNotContain("super-secret-pin-hash", json);
        Assert.DoesNotContain("smtp.secret.test", json);
        Assert.DoesNotContain("private-sender@example.test", json);
        Assert.DoesNotContain("smtp-user", json);
        Assert.DoesNotContain("hidden-category-domain.test", json);
        Assert.DoesNotContain("QuietStartMinutes", json);
        Assert.DoesNotContain("RemoteSession", json);
        Assert.DoesNotContain("Permissions", json);
    }

    [Fact]
    public async Task Summary_includes_usage_messages_and_history_for_the_child_only()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, "Usage");
        var now = DateTime.UtcNow;
        db.UsageIntervals.Add(new UsageInterval
        {
            FamilyId = seed.Family.Id,
            ChildId = seed.Child.Id,
            DeviceId = seed.Device.Id,
            AppIdentity = "app:game",
            AppDisplayName = "Game",
            StartUtc = now.AddMinutes(-30),
            DurationSeconds = 600,
            Active = true
        });
        db.VisitRecords.Add(new VisitRecord
        {
            FamilyId = seed.Family.Id,
            ChildId = seed.Child.Id,
            DeviceId = seed.Device.Id,
            Domain = "visited.test",
            VisitedUtc = now.AddMinutes(-5)
        });
        db.OverrideEvents.Add(new OverrideEvent
        {
            FamilyId = seed.Family.Id,
            ChildId = seed.Child.Id,
            Option = "dashboard-bonus",
            Minutes = 15,
            CreatedUtc = now.AddMinutes(-10)
        });
        db.ChildMessages.Add(new ChildMessage
        {
            FamilyId = seed.Family.Id,
            ChildId = seed.Child.Id,
            Direction = MessageDirection.ParentToChild,
            SenderParentId = seed.Parent.Id,
            Text = "hello",
            CreatedUtc = now.AddMinutes(-2)
        });
        await db.SaveChangesAsync();

        var summary = await new ChildSummaryService(db).BuildAsync(seed.Device);

        Assert.True(summary.UsedTodaySeconds >= 600);
        Assert.Contains(summary.Visits, v => v.Domain == "visited.test");
        Assert.Contains(summary.History, h => h.Kind == "bonus" && h.Minutes == 15);
        Assert.Contains(summary.RecentMessages, m => m.Text == "hello");
        Assert.Equal(1, summary.UnreadMessages);
    }

    [Fact]
    public async Task Summary_hides_shell_and_own_apps_but_keeps_file_explorer()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, "Usage");
        var now = DateTime.UtcNow;
        void Add(string identity, string display, int seconds) => db.UsageIntervals.Add(new UsageInterval
        {
            FamilyId = seed.Family.Id,
            ChildId = seed.Child.Id,
            DeviceId = seed.Device.Id,
            AppIdentity = identity,
            AppDisplayName = display,
            StartUtc = now.AddMinutes(-10),
            DurationSeconds = seconds,
            Active = true
        });

        Add("win32:notepad.exe|microsoftcorporation", "Notepad", 300);
        Add("win32:explorer.exe|microsoftcorporation", "File Explorer", 240);
        Add("win32:explorer.exe|microsoftcorporation", "Windows Explorer", 3600);
        Add("win32:shellexperiencehost.exe|microsoftcorporation", "Windows Shell Experience Host", 600);
        Add("win32:opcontrol.parental.helper.exe|opcontrolitsolutions", "OpControl Parental Helper", 900);
        Add("store:microsoft.windows.startmenuexperiencehost_cw5n1h2txyewy", "Start", 120);
        await db.SaveChangesAsync();

        var summary = await new ChildSummaryService(db).BuildAsync(seed.Device);
        var apps = summary.AppsToday.Select(a => a.DisplayName).ToList();

        Assert.Contains("Notepad", apps);
        Assert.Contains("File Explorer", apps);
        Assert.DoesNotContain("Windows Explorer", apps);
        Assert.DoesNotContain(apps, name => name.Contains("Shell", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(apps, name => name.Contains("OpControl", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Start", apps);
    }

    [Fact]
    public void Cache_returns_null_when_missing_or_damaged_and_round_trips_when_present()
    {
        var path = Path.Combine(Path.GetTempPath(), "ocp-summary-" + Guid.NewGuid() + ".json");
        Assert.Null(ChildSummaryCache.Read(path));

        var summary = new ChildSummaryDto(Guid.NewGuid(), "Emma", "X1", DateTime.UtcNow,
            true, 120, 60, 120, [], [], [], [], [], [], [], [], [], [], 0);
        ChildSummaryCache.Write(path, summary);
        var loaded = ChildSummaryCache.Read(path);
        Assert.NotNull(loaded);
        Assert.Equal("Emma", loaded!.ChildName);
        Assert.Equal(120, loaded.TodayLimitMinutes);

        File.WriteAllText(path, "{not json");
        Assert.Null(ChildSummaryCache.Read(path));
        File.Delete(path);
    }
}
