using System.Net;
using System.Net.Mail;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed class AlertService
{
    private readonly ParentalDbContext _db;
    private readonly DashboardEventBus _events;
    private readonly IDataProtector _protector;
    private readonly ILogger<AlertService> _logger;

    public AlertService(ParentalDbContext db, DashboardEventBus events, IDataProtectionProvider protection, ILogger<AlertService> logger)
    {
        _db = db;
        _events = events;
        _protector = protection.CreateProtector("OpControl.Parental.Smtp");
        _logger = logger;
    }

    public async Task<Alert?> AddAsync(Guid familyId, Guid childId, Guid? deviceId, string type, string title, string detail, TimeSpan? dedupeWindow = null, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (dedupeWindow is { } window)
        {
            var since = now - window;
            var exists = await _db.Alerts.AsNoTracking()
                .AnyAsync(a => a.FamilyId == familyId && a.ChildId == childId && a.Type == type && a.Detail == detail && a.CreatedUtc >= since, ct);
            if (exists) return null;
        }

        var alert = new Alert
        {
            FamilyId = familyId,
            ChildId = childId,
            DeviceId = deviceId,
            Type = type,
            Title = title.Length > 200 ? title[..200] : title,
            Detail = detail.Length > 500 ? detail[..500] : detail,
            CreatedUtc = now
        };
        _db.Alerts.Add(alert);
        await _db.SaveChangesAsync(ct);
        _events.PublishAlert(alert);
        _logger.LogInformation("Alert {Type} for child {ChildId}: {Detail}", type, childId, detail);
        return alert;
    }

    public async Task<List<Alert>> GetAlertsAsync(Guid familyId, Guid childId, int take = 100, bool unreadOnly = false, CancellationToken ct = default)
    {
        var query = _db.Alerts.AsNoTracking().Where(a => a.FamilyId == familyId && a.ChildId == childId);
        if (unreadOnly) query = query.Where(a => a.ReadUtc == null);
        return await query.OrderByDescending(a => a.CreatedUtc).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
    }

    public Task<int> UnreadCountAsync(Guid familyId, Guid childId, CancellationToken ct = default) =>
        _db.Alerts.AsNoTracking().CountAsync(a => a.FamilyId == familyId && a.ChildId == childId && a.ReadUtc == null, ct);

    public async Task MarkReadAsync(Guid familyId, Guid childId, Guid alertId, CancellationToken ct = default)
    {
        var alert = await _db.Alerts.FirstOrDefaultAsync(a => a.Id == alertId && a.FamilyId == familyId && a.ChildId == childId, ct);
        if (alert is null || alert.ReadUtc is not null) return;
        alert.ReadUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _events.PublishAlert(alert);
    }

    public async Task MarkAllReadAsync(Guid familyId, Guid childId, CancellationToken ct = default)
    {
        var alerts = await _db.Alerts.Where(a => a.FamilyId == familyId && a.ChildId == childId && a.ReadUtc == null).ToListAsync(ct);
        if (alerts.Count == 0) return;
        foreach (var alert in alerts) alert.ReadUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _events.PublishAlert(alerts[0]);
    }

    public async Task<FamilySettings> EnsureSettingsAsync(Guid familyId, CancellationToken ct = default)
    {
        var settings = await _db.FamilySettings.FirstOrDefaultAsync(s => s.FamilyId == familyId, ct);
        if (settings is not null) return settings;
        settings = new FamilySettings { FamilyId = familyId };
        _db.FamilySettings.Add(settings);
        await _db.SaveChangesAsync(ct);
        return settings;
    }

    public async Task SaveSettingsAsync(Guid familyId, bool emailsEnabled, string? host, int port, bool useSsl, string? username, string? password, string? from, int offlineMinutes, CancellationToken ct = default)
    {
        var settings = await EnsureSettingsAsync(familyId, ct);
        settings.AlertEmailsEnabled = emailsEnabled;
        settings.SmtpHost = string.IsNullOrWhiteSpace(host) ? null : host.Trim();
        settings.SmtpPort = Math.Clamp(port, 1, 65535);
        settings.SmtpUseSsl = useSsl;
        settings.SmtpUsername = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        if (!string.IsNullOrWhiteSpace(password))
            settings.SmtpPasswordProtected = _protector.Protect(password);
        settings.FromAddress = string.IsNullOrWhiteSpace(from) ? null : from.Trim();
        settings.OfflineAlertMinutes = Math.Clamp(offlineMinutes, 5, 1440);
        settings.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public string? UnprotectPassword(FamilySettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SmtpPasswordProtected)) return null;
        try { return _protector.Unprotect(settings.SmtpPasswordProtected); }
        catch { return null; }
    }

    public async Task<int> SendPendingEmailsAsync(Guid familyId, CancellationToken ct = default)
    {
        var settings = await _db.FamilySettings.AsNoTracking().FirstOrDefaultAsync(s => s.FamilyId == familyId, ct);
        var pending = await _db.Alerts
            .Where(a => a.FamilyId == familyId && a.EmailedUtc == null)
            .OrderBy(a => a.CreatedUtc)
            .Take(20)
            .ToListAsync(ct);
        if (pending.Count == 0) return 0;

        if (settings is null || !settings.AlertEmailsEnabled || string.IsNullOrWhiteSpace(settings.SmtpHost) || string.IsNullOrWhiteSpace(settings.FromAddress))
        {
            foreach (var alert in pending) alert.EmailedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return 0;
        }

        var recipients = await _db.Users.AsNoTracking()
            .Where(u => u.FamilyId == familyId && u.Email != null && u.Email != "")
            .Select(u => u.Email!)
            .ToListAsync(ct);
        if (recipients.Count == 0)
        {
            foreach (var alert in pending) alert.EmailedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return 0;
        }

        var sent = 0;
        foreach (var alert in pending)
        {
            try
            {
                using var message = new MailMessage
                {
                    From = new MailAddress(settings.FromAddress),
                    Subject = alert.Title,
                    Body = $"{alert.Title}\n\n{alert.Detail}\n\n{alert.CreatedUtc:u}",
                    IsBodyHtml = false
                };
                foreach (var address in recipients) message.To.Add(address);

                using var client = new SmtpClient(settings.SmtpHost, settings.SmtpPort)
                {
                    EnableSsl = settings.SmtpUseSsl,
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    UseDefaultCredentials = false
                };
                var password = UnprotectPassword(settings);
                if (!string.IsNullOrWhiteSpace(settings.SmtpUsername))
                    client.Credentials = new NetworkCredential(settings.SmtpUsername, password ?? "");
                await client.SendMailAsync(message, ct);
                alert.EmailedUtc = DateTime.UtcNow;
                sent++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Alert email {AlertId} could not be sent", alert.Id);
                alert.EmailedUtc = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync(ct);
        return sent;
    }
}
