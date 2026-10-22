// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Hubs;
using OpControl.Parental.Server.Services;
using Xunit;

namespace OpControl.Parental.Tests;

public class Job14ChatTests
{
    [Fact]
    public void Short_and_long_messages_share_the_same_full_width_row()
    {
        var shortRow = ChatLayout.RowWidth(1000, 24);
        var longRow = ChatLayout.RowWidth(1000, 24);
        Assert.Equal(shortRow, longRow);
        Assert.Equal(962, shortRow);
        Assert.Equal((int)(962 * 0.62), ChatLayout.MaxBubbleWidth(shortRow));
    }

    [Theory]
    [InlineData(true, 1, 38f, 62f)]
    [InlineData(false, 0, 62f, 38f)]
    public void Child_is_pinned_right_and_parent_left(bool own, int column, float first, float second)
    {
        Assert.Equal(column, ChatLayout.ColumnFor(own));
        Assert.Equal(first, ChatLayout.FirstColumnPercent(own));
        Assert.Equal(second, ChatLayout.SecondColumnPercent(own));
    }

    [Fact]
    public void Narrow_threads_keep_a_usable_minimum()
    {
        Assert.Equal(ChatLayout.MinimumRowWidth, ChatLayout.RowWidth(120, 24));
        Assert.Equal(ChatLayout.MinimumBubbleWidth, ChatLayout.MaxBubbleWidth(100));
    }
}

public class Job14PreferenceTests
{
    private static ParentalDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ParentalDbContext>().UseInMemoryDatabase("job14-pref-" + Guid.NewGuid()).Options);

    [Fact]
    public async Task Enter_to_send_defaults_on_and_persists_per_parent()
    {
        await using var db = NewDb();
        var family = new Family { Name = "F" };
        var parent = new ParentUser { FamilyId = family.Id, DisplayName = "P", UserName = "p@test", Email = "p@test" };
        db.Families.Add(family);
        db.Users.Add(parent);
        await db.SaveChangesAsync();
        var service = new ParentPreferenceService(db);
        Assert.True(await service.GetEnterToSendAsync(family.Id, parent.Id));
        await service.SaveEnterToSendAsync(family.Id, parent.Id, false);
        Assert.False(await service.GetEnterToSendAsync(family.Id, parent.Id));
        Assert.False((await db.Users.FindAsync(parent.Id))!.EnterToSend);
    }
}

public class Job14BlockingTests
{
    private static ParentalDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ParentalDbContext>().UseInMemoryDatabase("job14-block-" + Guid.NewGuid()).Options);

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

    private static async Task<(Family Family, Child Child, Device Device)> SeedAsync(ParentalDbContext db)
    {
        var family = new Family { Name = "Blocking" };
        var child = new Child { FamilyId = family.Id, Name = "Child" };
        var device = new Device { FamilyId = family.Id, ChildId = child.Id, Name = "PC", TimeZoneId = "UTC" };
        db.Families.Add(family);
        db.Children.Add(child);
        db.Devices.Add(device);
        db.FamilySettings.Add(new FamilySettings { FamilyId = family.Id, RepeatedBlockAlertHours = 6 });
        await db.SaveChangesAsync();
        return (family, child, device);
    }

    private static VisitBatchRequest Batch(params (string Domain, bool Blocked, string? Reason, DateTime Utc)[] visits) =>
        new(visits.Select(v => new VisitEventDto(v.Domain, v.Utc, v.Blocked, v.Reason)).ToList());

    [Fact]
    public async Task Visits_are_de_duplicated_per_five_minutes_and_lookups_are_kept()
    {
        await using var db = NewDb();
        var (_, child, device) = await SeedAsync(db);
        var rules = new RuleService(db, Alerts(db));
        var now = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        await rules.SyncVisitsAsync(device, Batch(
            ("khanacademy.org", false, null, now),
            ("khanacademy.org", false, null, now.AddSeconds(30)),
            ("khanacademy.org", false, null, now.AddSeconds(90)),
            ("khanacademy.org", false, null, now.AddMinutes(6))));
        var rows = await db.VisitRecords.Where(v => v.ChildId == child.Id).OrderBy(v => v.VisitedUtc).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(3, rows[0].Lookups);
        Assert.Equal(1, rows[1].Lookups);
    }

    [Fact]
    public async Task Repeated_blocked_alerts_fire_once_per_window_and_show_the_total()
    {
        await using var db = NewDb();
        var (family, child, device) = await SeedAsync(db);
        var rules = new RuleService(db, Alerts(db));
        var now = DateTime.UtcNow.AddMinutes(-5);
        for (var i = 0; i < 6; i++)
        {
            await rules.SyncVisitsAsync(device, Batch(
                ("whatsapp.com", true, "category:social", now.AddSeconds(i * 10))));
        }
        var rows = await db.VisitRecords.Where(v => v.ChildId == child.Id).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(6, rows[0].BlockedLookups);
        var alerts = await db.Alerts.Where(a => a.FamilyId == family.Id && a.Type == "repeated-blocked-site").ToListAsync();
        Assert.Single(alerts);
        var (_, detail) = AlertText.Render(alerts[0], "en");
        Assert.Contains("5 blocked attempts", detail);
        Assert.Contains("Social media", detail);

        await rules.SyncVisitsAsync(device, Batch(("whatsapp.com", true, "category:social", now.AddMinutes(1))));
        Assert.Single(await db.Alerts.Where(a => a.Type == "repeated-blocked-site").ToListAsync());
    }

    [Fact]
    public async Task Background_domains_are_never_visits()
    {
        await using var db = NewDb();
        var (_, _, device) = await SeedAsync(db);
        var rules = new RuleService(db, Alerts(db));
        var now = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        await rules.SyncVisitsAsync(device, Batch(
            ("opcontrol.net", false, null, now),
            ("icanhazip.com", false, null, now),
            ("tacticalrmm.io", false, null, now),
            ("whatsapp.com", false, null, now)));
        Assert.Single(await db.VisitRecords.ToListAsync());
        Assert.Equal("whatsapp.com", (await db.VisitRecords.SingleAsync()).Domain);
    }

    [Fact]
    public void Categories_never_block_background_traffic()
    {
        var state = new DeviceStateDto(Guid.NewGuid(), "Child", "Parent", false, null, null, null, null,
            [], [], [], [], [], DateTime.UtcNow, [], null, false);
        var now = DateTime.UtcNow;
        Assert.Null(PolicyRules.GetWebsiteBlockReason(state, "opcontrol.net", TimeZoneInfo.Utc, now, _ => "social"));
        Assert.Null(PolicyRules.GetWebsiteBlockReason(state, "icanhazip.com", TimeZoneInfo.Utc, now, _ => "social"));
        Assert.Equal("category:social", PolicyRules.GetWebsiteBlockReason(state, "whatsapp.com", TimeZoneInfo.Utc, now, _ => "social"));
        Assert.Equal("rule:always", PolicyRules.GetWebsiteBlockReason(
            state with { WebsiteRules = [new WebsiteRuleDto(Guid.NewGuid(), "example.com", true, RuleMode.Always, null, now)] },
            "example.com", TimeZoneInfo.Utc, now));
    }
}

public class Job14RemoteTests
{
    private static ParentalDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ParentalDbContext>().UseInMemoryDatabase("job14-remote-" + Guid.NewGuid()).Options);

    private sealed class LazyScopeFactory(Func<IServiceProvider> provider) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => provider().CreateScope();
    }

    private static async Task<(ServiceProvider Provider, RemoteSessionRegistry Registry, RemoteSessionService Service,
        ParentalDbContext Db, Family Family, Child Child, Device Device, ParentUser Parent)> BuildAsync()
    {
        var db = NewDb();
        var family = new Family { Name = "Remote" };
        var child = new Child { FamilyId = family.Id, Name = "Child" };
        var device = new Device { FamilyId = family.Id, ChildId = child.Id, Name = "PC", TimeZoneId = "UTC" };
        var parent = new ParentUser { FamilyId = family.Id, DisplayName = "P", UserName = "p@test", Email = "p@test" };
        db.Families.Add(family);
        db.Children.Add(child);
        db.Devices.Add(device);
        db.Users.Add(parent);
        await db.SaveChangesAsync();

        var holder = new ProviderHolder();
        var registry = new RemoteSessionRegistry(new LazyScopeFactory(() => holder.Provider),
            NullLogger<RemoteSessionRegistry>.Instance, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(30));
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddLogging();
        services.AddSingleton<IHubContext<ParentalHub>>(new NullHubContext());
        services.AddSingleton(registry);
        services.AddScoped<RemoteSessionService>();
        services.AddScoped<CameraConsentService>();
        var provider = services.BuildServiceProvider();
        holder.Provider = provider;
        var service = provider.CreateScope().ServiceProvider.GetRequiredService<RemoteSessionService>();
        return (provider, registry, service, db, family, child, device, parent);
    }

    [Fact]
    public async Task Stop_finalises_the_database_even_if_a_socket_close_would_hang()
    {
        var ctx = await BuildAsync();
        await using var db = ctx.Db;
        var session = new RemoteSession
        {
            FamilyId = ctx.Family.Id, ChildId = ctx.Child.Id, DeviceId = ctx.Device.Id,
            RequestedByParentId = ctx.Parent.Id, Kind = RemoteSessionKind.Screen,
            Status = RemoteSessionStatus.Requested, CreatedUtc = DateTime.UtcNow
        };
        db.RemoteSessions.Add(session);
        await db.SaveChangesAsync();
        ctx.Registry.Ensure(session.Id, DateTime.UtcNow.AddMinutes(10));
        using var socket = new HangingWebSocket();
        var attach = ctx.Registry.AttachParentAsync(session.Id, socket, CancellationToken.None);
        await Task.Delay(100);
        await ctx.Registry.StopAsync(session.Id, "parent-closed");
        var row = await db.RemoteSessions.FindAsync(session.Id);
        Assert.Equal(RemoteSessionStatus.Ended, row!.Status);
        await attach.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private sealed class HangingWebSocket : FakeWebSocket
    {
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => new TaskCompletionSource().Task;
    }

    private sealed class ProviderHolder
    {
        public IServiceProvider Provider { get; set; } = null!;
    }

    [Fact]
    public async Task New_view_request_ends_an_existing_session_for_the_device()
    {
        var ctx = await BuildAsync();
        await using var db = ctx.Db;
        var existing = new RemoteSession
        {
            FamilyId = ctx.Family.Id, ChildId = ctx.Child.Id, DeviceId = ctx.Device.Id,
            RequestedByParentId = ctx.Parent.Id, Kind = RemoteSessionKind.Screen,
            Status = RemoteSessionStatus.Active, CreatedUtc = DateTime.UtcNow
        };
        db.RemoteSessions.Add(existing);
        await db.SaveChangesAsync();

        var created = await ctx.Service.CreateAsync(ctx.Family.Id, ctx.Parent.Id, ctx.Device.Id, RemoteSessionKind.Screen);
        var rows = await db.RemoteSessions.OrderBy(s => s.CreatedUtc).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(RemoteSessionStatus.Ended, rows[0].Status);
        Assert.Equal("replaced", rows[0].EndReason);
        Assert.Equal(created.Id, rows[1].Id);
    }

    [Fact]
    public async Task Missing_viewer_heartbeat_ends_the_session()
    {
        var ctx = await BuildAsync();
        await using var db = ctx.Db;
        var session = new RemoteSession
        {
            FamilyId = ctx.Family.Id, ChildId = ctx.Child.Id, DeviceId = ctx.Device.Id,
            RequestedByParentId = ctx.Parent.Id, Kind = RemoteSessionKind.Screen,
            Status = RemoteSessionStatus.Requested, CreatedUtc = DateTime.UtcNow
        };
        db.RemoteSessions.Add(session);
        await db.SaveChangesAsync();
        ctx.Registry.Ensure(session.Id, DateTime.UtcNow.AddMinutes(10));
        using var socket = new FakeWebSocket();
        var attach = ctx.Registry.AttachParentAsync(session.Id, socket, CancellationToken.None);
        await Task.Delay(600);
        await attach.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(ctx.Registry.Contains(session.Id));
        var row = await db.RemoteSessions.FindAsync(session.Id);
        Assert.Equal(RemoteSessionStatus.Ended, row!.Status);
        Assert.Equal("heartbeat-timeout", row.EndReason);
    }

    private class FakeWebSocket : WebSocket
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() { _state = WebSocketState.Aborted; _closed.TrySetResult(); }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        { _state = WebSocketState.Closed; _closed.TrySetResult(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        { _state = WebSocketState.CloseSent; _closed.TrySetResult(); return Task.CompletedTask; }
        public override void Dispose() => Abort();
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var result = await ReceiveAsync(buffer.AsMemory(), cancellationToken);
            return new WebSocketReceiveResult(result.Count, result.MessageType, result.EndOfMessage);
        }
        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await _closed.Task.WaitAsync(cancellationToken);
            return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}

public class Job14ReviewTests
{
    private static ParentalDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ParentalDbContext>().UseInMemoryDatabase("job14-review-" + Guid.NewGuid()).Options);

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return handler(request, cancellationToken);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static async Task<(ParentalDbContext Db, Family Family, Child Child)> SeedAsync()
    {
        var db = NewDb();
        var family = new Family { Name = "Review" };
        var child = new Child { FamilyId = family.Id, Name = "Child" };
        db.Families.Add(family);
        db.Children.Add(child);
        await db.SaveChangesAsync();
        return (db, family, child);
    }

    [Fact]
    public async Task Review_reads_title_description_and_favicon_server_side()
    {
        var (db, family, child) = await SeedAsync();
        await using var _ = db;
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><head><title>Khan Academy</title><meta name=\"description\" content=\"Free lessons\"><link rel=\"icon\" href=\"/fav.png\"></head></html>", Encoding.UTF8, "text/html")
        }));
        var service = new SiteReviewService(db, new Factory(handler), new MemoryCache(new MemoryCacheOptions()), NullLogger<SiteReviewService>.Instance, (_, _) => Task.FromResult(true));
        var review = await service.GetAsync(family.Id, child.Id, "khanacademy.org");
        Assert.Equal("Khan Academy", review.Title);
        Assert.Equal("Free lessons", review.Description);
        Assert.Equal("https://khanacademy.org/fav.png", review.FaviconUrl);
        Assert.Null(review.FetchError);
        Assert.Equal("SiteReview.Purpose.KhanAcademy", review.KnownPurposeKey);
    }

    [Fact]
    public async Task Adult_list_domains_never_trigger_a_fetch_or_favicon()
    {
        var (db, family, child) = await SeedAsync();
        await using var _ = db;
        db.CategoryDomains.Add(new CategoryDomain { CategoryKey = "adult", Domain = "example-adult.test", AddedVersion = 1 });
        await db.SaveChangesAsync();
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var service = new SiteReviewService(db, new Factory(handler), new MemoryCache(new MemoryCacheOptions()), NullLogger<SiteReviewService>.Instance);
        var review = await service.GetAsync(family.Id, child.Id, "example-adult.test");
        Assert.True(review.IsAdult);
        Assert.Null(review.Title);
        Assert.Null(review.FaviconUrl);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Oversized_pages_and_redirect_loops_are_reported()
    {
        var (db, family, child) = await SeedAsync();
        await using var _ = db;
        var big = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html></html>", Encoding.UTF8, "text/html")
            };
            response.Content.Headers.ContentLength = SiteReviewService.MaxBytes + 1;
            return Task.FromResult(response);
        });
        var bigService = new SiteReviewService(db, new Factory(big), new MemoryCache(new MemoryCacheOptions()), NullLogger<SiteReviewService>.Instance, (_, _) => Task.FromResult(true));
        Assert.Equal("Website.Review.TooLarge", (await bigService.GetAsync(family.Id, child.Id, "big.test")).FetchError);

        var loop = new Handler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://loop.test/again");
            return Task.FromResult(response);
        });
        var loopService = new SiteReviewService(db, new Factory(loop), new MemoryCache(new MemoryCacheOptions()), NullLogger<SiteReviewService>.Instance, (_, _) => Task.FromResult(true));
        Assert.Equal("Website.Review.Redirects", (await loopService.GetAsync(family.Id, child.Id, "loop.test")).FetchError);
    }

    [Fact]
    public async Task Fetch_timeout_is_reported()
    {
        var (db, family, child) = await SeedAsync();
        await using var _ = db;
        var timeout = new Handler((_, _) => throw new TaskCanceledException("timeout"));
        var service = new SiteReviewService(db, new Factory(timeout), new MemoryCache(new MemoryCacheOptions()), NullLogger<SiteReviewService>.Instance, (_, _) => Task.FromResult(true));
        Assert.Equal("Website.Review.Timeout", (await service.GetAsync(family.Id, child.Id, "slow.test")).FetchError);
    }
}

public class Job14ExclusionTests
{
    [Theory]
    [InlineData("win32:dwagent.exe|dwservice", "DWAgent")]
    [InlineData("win32:tacticalagent.exe|amidaware", "Tactical RMM Agent")]
    [InlineData("win32:meshagent.exe|meshcentral", "Mesh Agent")]
    [InlineData("win32:tailscale-ipn.exe|tailscale", "Tailscale")]
    [InlineData("win32:tailscaled.exe|tailscale", "Tailscale")]
    public void Remote_support_agents_never_show_in_most_used(string identity, string display) =>
        Assert.True(AppUsageExclusions.IsExcluded(identity, display));
}

internal sealed class NullHubContext : IHubContext<ParentalHub>
{
    public IHubClients Clients { get; } = new NullClients();
    public IGroupManager Groups { get; } = new NullGroups();
}

internal sealed class NullClients : IHubClients
{
    private static readonly IClientProxy Proxy = new NullProxy();
    public IClientProxy All => Proxy;
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
    public IClientProxy Client(string connectionId) => Proxy;
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
    public IClientProxy Group(string groupName) => Proxy;
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
    public IClientProxy User(string userId) => Proxy;
    public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
}

internal sealed class NullProxy : IClientProxy
{
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class NullGroups : IGroupManager
{
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
