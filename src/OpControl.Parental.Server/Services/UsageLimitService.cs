using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed class UsageLimitService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<UsageLimitService> _logger;
    private DateTime _lastOfflineSweep = DateTime.MinValue;
    private DateTime _lastEmailSweep = DateTime.MinValue;

    public UsageLimitService(IServiceScopeFactory scopes, ILogger<UsageLimitService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await EvaluateAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Usage limit evaluation failed");
            }
        }
    }

    private async Task EvaluateAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        List<(Guid Id, Guid FamilyId)> children;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ParentalDbContext>();
            children = await db.Children.AsNoTracking().Select(c => new ValueTuple<Guid, Guid>(c.Id, c.FamilyId)).ToListAsync(ct);
        }

        foreach (var child in children)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ParentalDbContext>();
            var usage = scope.ServiceProvider.GetRequiredService<UsageService>();
            var alerts = scope.ServiceProvider.GetRequiredService<AlertService>();
            var locks = scope.ServiceProvider.GetRequiredService<LockService>();

            var tzId = await db.Devices.AsNoTracking().Where(d => d.ChildId == child.Id).Select(d => d.TimeZoneId).FirstOrDefaultAsync(ct);
            var tz = TimeZoneResolver.Resolve(tzId);
            var daily = await usage.BuildDailyLimitAsync(child.FamilyId, child.Id, tz, now, ct);
            var appUsage = await usage.AppUsageTodayAsync(child.FamilyId, child.Id, tz, now, ct);

            if (daily.Enabled && daily.TodayMinutes > 0 && daily.UsedTodaySeconds >= daily.TodayMinutes * 60)
            {
                var localDay = TimeZoneInfo.ConvertTimeFromUtc(now, tz).ToString("yyyy-MM-dd");
                var added = await alerts.AddAsync(child.FamilyId, child.Id, null, "daily-limit",
                    new AlertContent("Alert.DailyLimit.Title", "Alert.DailyLimit.Detail",
                        daily.TodayMinutes.ToString(), localDay),
                    TimeSpan.FromHours(12), ct);
                if (added is not null)
                {
                    var exists = await db.OverrideEvents.AnyAsync(e => e.ChildId == child.Id
                        && e.Option == "daily-limit-reached" && e.CreatedUtc >= daily.DayStartUtc && e.CreatedUtc < daily.DayEndUtc, ct);
                    if (!exists)
                    {
                        db.OverrideEvents.Add(new OverrideEvent
                        {
                            FamilyId = child.FamilyId,
                            ChildId = child.Id,
                            Option = "daily-limit-reached",
                            Detail = localDay,
                            CreatedUtc = now
                        });
                        await db.SaveChangesAsync(ct);
                    }
                }
            }

            foreach (var app in appUsage.Where(a => a.LimitMinutes is > 0 && !a.AlwaysAllowed && a.UsedTodaySeconds >= a.LimitMinutes * 60))
            {
                await alerts.AddAsync(child.FamilyId, child.Id, null, "app-limit",
                    new AlertContent("Alert.AppLimit.Title", "Alert.AppLimit.Detail",
                        app.Identity, app.LimitMinutes?.ToString() ?? "0"),
                    TimeSpan.FromHours(12), ct);
            }

            await locks.PublishDeviceStateAsync(child.FamilyId, child.Id, ct);

            if (now - _lastOfflineSweep > TimeSpan.FromMinutes(1))
            {
                await CheckOfflineAsync(db, alerts, child.FamilyId, child.Id, now, ct);
            }

            if (now - _lastEmailSweep > TimeSpan.FromMinutes(1))
            {
                await alerts.SendPendingEmailsAsync(child.FamilyId, ct);
            }
        }

        if (now - _lastOfflineSweep > TimeSpan.FromMinutes(1)) _lastOfflineSweep = now;
        if (now - _lastEmailSweep > TimeSpan.FromMinutes(1)) _lastEmailSweep = now;
    }

    private static async Task CheckOfflineAsync(ParentalDbContext db, AlertService alerts, Guid familyId, Guid childId, DateTime now, CancellationToken ct)
    {
        var settings = await db.FamilySettings.AsNoTracking().FirstOrDefaultAsync(s => s.FamilyId == familyId, ct);
        var threshold = TimeSpan.FromMinutes(Math.Clamp(settings?.OfflineAlertMinutes ?? 120, 5, 1440));
        var devices = await db.Devices.AsNoTracking()
            .Where(d => d.FamilyId == familyId && d.ChildId == childId && !d.IsRevoked)
            .ToListAsync(ct);
        foreach (var device in devices)
        {
            if (device.LastSeenUtc is { } seen && now - seen > threshold)
            {
                await alerts.AddAsync(familyId, childId, device.Id, "device-offline",
                    new AlertContent("Alert.DeviceOffline.Title", "Alert.DeviceOffline.Detail",
                        device.Name, seen.ToString("u")),
                    TimeSpan.FromHours(6), ct);
            }
        }
    }
}
