// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Services;
using Xunit;

namespace OpControl.Parental.Tests;

public class Job142Tests
{
    private static ParentalDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ParentalDbContext>().UseInMemoryDatabase("job142-" + Guid.NewGuid()).Options);

    private static EmailRenderer Renderer() =>
        new(Options.Create(new EmailOptions { LogoUrl = "https://parental.opcontrol.net/brand/email-lockup.png" }),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PublicBaseUrl"] = "https://parental.opcontrol.net"
            }).Build());

    private static AlertService Alerts(ParentalDbContext db) =>
        new(db, new DashboardEventBus(), new EphemeralDataProtectionProvider(),
            new EmailSender(db, new EphemeralDataProtectionProvider(), Renderer(), Options.Create(new EmailOptions()), NullLogger<EmailSender>.Instance),
            Renderer(), NullLogger<AlertService>.Instance);

    private static RuleService Rules(ParentalDbContext db) => new(db, Alerts(db));

    private static async Task<(Family Family, Child Child, Device Device)> SeedAsync(ParentalDbContext db)
    {
        var family = new Family { Name = "Job142" };
        var child = new Child { FamilyId = family.Id, Name = "Emma" };
        var device = new Device { FamilyId = family.Id, ChildId = child.Id, Name = "Yoga", TimeZoneId = "UTC" };
        db.Families.Add(family);
        db.Children.Add(child);
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return (family, child, device);
    }

    [Fact]
    public async Task Allow_exception_overrides_an_enabled_category_and_its_subdomains()
    {
        await using var db = NewDb();
        var (family, child, _) = await SeedAsync(db);
        var rules = Rules(db);
        await rules.UpsertWebsiteRuleAsync(family.Id, child.Id, null, "whatsapp.com", blocked: false, RuleMode.Always, null);
        var policy = await rules.GetDevicePolicyAsync(family.Id, child.Id);
        var state = new DeviceStateDto(child.Id, child.Name, null, false, null, null, null, null,
            [], policy.Sites, [], [], policy.Categories, DateTime.UtcNow, [], null, false);

        var now = DateTime.UtcNow;
        Assert.Null(PolicyRules.GetWebsiteBlockReason(state, "whatsapp.com", TimeZoneInfo.Utc, now, _ => "social"));
        Assert.Null(PolicyRules.GetWebsiteBlockReason(state, "web.whatsapp.com", TimeZoneInfo.Utc, now, _ => "social"));
        Assert.Equal("category:social", PolicyRules.GetWebsiteBlockReason(state, "instagram.com", TimeZoneInfo.Utc, now, _ => "social"));
    }

    [Fact]
    public async Task Allow_replaces_a_block_rule_and_records_an_allowed_history_row()
    {
        await using var db = NewDb();
        var (family, child, _) = await SeedAsync(db);
        var rules = Rules(db);
        await rules.UpsertWebsiteRuleAsync(family.Id, child.Id, null, "whatsapp.com", blocked: true, RuleMode.Always, null);
        await rules.UpsertWebsiteRuleAsync(family.Id, child.Id, null, "whatsapp.com", blocked: false, RuleMode.Always, null);

        var rows = await db.WebsiteRules.Where(r => r.ChildId == child.Id).ToListAsync();
        Assert.Single(rows);
        Assert.False(rows[0].IsBlocked);
        var history = await rules.GetHistoryAsync(family.Id, child.Id);
        Assert.Equal("Allowed", history[0].Action);
    }

    [Fact]
    public void Unblock_is_only_offered_when_there_is_a_rule()
    {
        Assert.False(RuleAvailability.CanUnblock(Guid.Empty));
        Assert.False(RuleAvailability.CanUnblock((Guid?)null));
        Assert.True(RuleAvailability.CanUnblock(Guid.NewGuid()));
    }

    [Fact]
    public async Task Add_box_allow_accepts_a_pasted_url_and_creates_an_always_allow_rule()
    {
        await using var db = NewDb();
        var (family, child, _) = await SeedAsync(db);
        var rules = Rules(db);
        await rules.UpsertWebsiteRuleAsync(family.Id, child.Id, null,
            "HTTPS://WWW.WhatsApp.com/some/path", blocked: false, RuleMode.Always, null);
        var rule = await db.WebsiteRules.SingleAsync();
        Assert.Equal("whatsapp.com", rule.Domain);
        Assert.False(rule.IsBlocked);
        Assert.Equal(RuleMode.Always, rule.Mode);
    }

    [Fact]
    public void Related_service_domains_are_offered_but_never_added_automatically()
    {
        var service = KnownSites.ServiceFor("web.whatsapp.com");
        Assert.NotNull(service);
        Assert.Equal("whatsapp", service!.Key);
        Assert.Equal("SiteReview.Service.WhatsApp", service.LabelKey);

        var related = KnownSites.RelatedDomains("whatsapp.com");
        Assert.Contains("whatsapp.net", related);
        Assert.DoesNotContain("whatsapp.com", related);

        var youtube = KnownSites.RelatedDomains("youtube.com");
        Assert.Contains("youtu.be", youtube);
        Assert.Contains("googlevideo.com", youtube);
        Assert.Contains("ytimg.com", youtube);
        Assert.DoesNotContain("youtube.com", youtube);
    }

    [Fact]
    public void Device_text_strips_nul_and_control_characters_and_keeps_valid_pairs()
    {
        Assert.Equal("ABC", DeviceText.Clean("A\0B\u0001C", 255));
        Assert.Equal("a\tb", DeviceText.Clean("  a\tb  ", 255));
        Assert.Null(DeviceText.CleanOptional("\0\u0002 ", 255));
        Assert.True(DeviceText.HasControlCharacters("bad\0value"));
        Assert.True(DeviceText.HasControlCharacters("\ud800lonely"));
        Assert.False(DeviceText.HasControlCharacters("ok \ud83d\ude00"));
        Assert.Equal("ok \ud83d\ude00", DeviceText.Clean("ok \ud83d\ude00", 255));
        Assert.Equal(10, DeviceText.Clean("0123456789abcdef", 10).Length);
    }

    [Fact]
    public async Task Installed_apps_with_nul_bytes_are_sanitized_and_all_rows_are_saved()
    {
        await using var db = NewDb();
        var (_, child, device) = await SeedAsync(db);
        var rules = Rules(db);
        var request = new AppInventoryRequest(
        [
            new AppInventoryItemDto("win32:bad.exe|pub\0lisher", "Bad\0App", "Pub\0lisher", "C:\\x\0y\\bad.exe", "win32", "1\0.0"),
            new AppInventoryItemDto("win32:good.exe|pub", "Good App", "Publisher", "C:\\good\\good.exe", "win32", "2.0"),
            new AppInventoryItemDto(new string('a', 300) + ".exe", "Long", null, null, "win32", null)
        ], DateTime.UtcNow);

        await rules.SyncAppInventoryAsync(device, request);

        var rows = await db.InstalledApps.Where(a => a.ChildId == child.Id).ToListAsync();
        Assert.Equal(3, rows.Count);
        var bad = rows.Single(r => r.DisplayName.Contains("Bad", StringComparison.Ordinal));
        Assert.Equal("BadApp", bad.DisplayName);
        Assert.Equal("Publisher", bad.Publisher);
        Assert.Equal("C:\\xy\\bad.exe", bad.Path);
        Assert.Equal("1.0", bad.Version);
        Assert.DoesNotContain('\0', bad.Identity);
        Assert.All(rows, r => Assert.DoesNotContain('\0', r.DisplayName));
        Assert.All(rows, r => Assert.True(r.Identity.Length <= 255));
    }

    [Fact]
    public async Task Visits_and_tamper_text_are_sanitized_before_saving()
    {
        await using var db = NewDb();
        var (_, child, device) = await SeedAsync(db);
        var rules = Rules(db);
        var now = DateTime.UtcNow;
        await rules.SyncVisitsAsync(device, new VisitBatchRequest(
        [
            new VisitEventDto("whats\0app.com", now, true, "category:\0social")
        ]));
        var visit = await db.VisitRecords.SingleAsync(v => v.ChildId == child.Id);
        Assert.Equal("whatsapp.com", visit.Domain);
        Assert.Equal("category:social", visit.BlockReason);

        await rules.SyncTamperEventsAsync(device, new TamperEventRequest(
        [
            new TamperEventDto("service-\0stopped", "de\0tail", now)
        ]));
        var tamper = await db.ClientTamperEvents.SingleAsync(t => t.ChildId == child.Id);
        Assert.Equal("service-stopped", tamper.Kind);
        Assert.Equal("detail", tamper.Detail);
    }
}
