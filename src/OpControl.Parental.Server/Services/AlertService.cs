using System.Net;
using System.Net.Mail;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed class AlertService
{
    private readonly ParentalDbContext _db;
    private readonly DashboardEventBus _events;
    private readonly IDataProtector _protector;
    private readonly EmailSender _serverEmail;
    private readonly ILogger<AlertService> _logger;

    public AlertService(ParentalDbContext db, DashboardEventBus events, IDataProtectionProvider protection,
        EmailSender serverEmail, ILogger<AlertService> logger)
    {
        _db = db;
        _events = events;
        _protector = protection.CreateProtector("OpControl.Parental.Smtp");
        _serverEmail = serverEmail;
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

        var preferences = await EnsurePreferenceAsync(familyId, ct);
        if (IsMuted(preferences, type))
        {
            alert.EmailedUtc = now;
            alert.EmailLastError = "muted";
        }
        else
        {
            alert.NextEmailAttemptUtc = await NextEmailDueAsync(preferences, now, ct);
        }

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

    public async Task SaveSettingsAsync(Guid familyId, bool emailsEnabled, string? host, int port, bool useSsl, string? username, string? password, string? from, int offlineMinutes, int? installLinkMinutes = null, CancellationToken ct = default)
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
        if (installLinkMinutes is { } linkMinutes)
            settings.InstallLinkMinutes = Math.Clamp(linkMinutes, 5, 10080);
        settings.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public string? UnprotectPassword(FamilySettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SmtpPasswordProtected)) return null;
        try { return _protector.Unprotect(settings.SmtpPasswordProtected); }
        catch { return null; }
    }

    public async Task<AlertPreference> EnsurePreferenceAsync(Guid familyId, CancellationToken ct = default)
    {
        var preference = await _db.AlertPreferences.FirstOrDefaultAsync(p => p.FamilyId == familyId, ct);
        if (preference is not null) return preference;
        preference = new AlertPreference { FamilyId = familyId };
        _db.AlertPreferences.Add(preference);
        await _db.SaveChangesAsync(ct);
        return preference;
    }

    public async Task SavePreferenceAsync(Guid familyId, AlertPreferencesDto dto, CancellationToken ct = default)
    {
        var preference = await EnsurePreferenceAsync(familyId, ct);
        preference.MutedNewApp = dto.MutedNewApp;
        preference.MutedRepeatedBlock = dto.MutedRepeatedBlock;
        preference.MutedPinOverride = dto.MutedPinOverride;
        preference.MutedLimit = dto.MutedLimit;
        preference.MutedOffline = dto.MutedOffline;
        preference.MutedTampering = dto.MutedTampering;
        preference.QuietStartMinutes = Math.Clamp(dto.QuietStartMinutes, 0, 1439);
        preference.QuietEndMinutes = Math.Clamp(dto.QuietEndMinutes, 0, 1439);
        preference.DailyDigest = dto.DailyDigest;
        preference.DigestHour = Math.Clamp(dto.DigestHour, 0, 23);
        preference.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public static AlertPreferencesDto ToDto(AlertPreference p) =>
        new(p.MutedNewApp, p.MutedRepeatedBlock, p.MutedPinOverride, p.MutedLimit, p.MutedOffline, p.MutedTampering,
            p.QuietStartMinutes, p.QuietEndMinutes, p.DailyDigest, p.DigestHour);

    public static bool IsMuted(AlertPreference p, string type) => type switch
    {
        "new-app" => p.MutedNewApp,
        "repeated-blocked-site" => p.MutedRepeatedBlock,
        "pin-override" => p.MutedPinOverride,
        "daily-limit" or "app-limit" => p.MutedLimit,
        "device-offline" => p.MutedOffline,
        "tampering" => p.MutedTampering,
        _ => false
    };

    public async Task<int> SendPendingEmailsAsync(Guid familyId, CancellationToken ct = default)
    {
        var settings = await _db.FamilySettings.AsNoTracking().FirstOrDefaultAsync(s => s.FamilyId == familyId, ct);
        if (settings is null || !settings.AlertEmailsEnabled)
        {
            var unsent = await _db.Alerts.Where(a => a.FamilyId == familyId && a.EmailedUtc == null).ToListAsync(ct);
            foreach (var alert in unsent) alert.EmailedUtc = DateTime.UtcNow;
            if (unsent.Count > 0) await _db.SaveChangesAsync(ct);
            return 0;
        }

        var now = DateTime.UtcNow;
        var due = await _db.Alerts
            .Where(a => a.FamilyId == familyId && a.EmailedUtc == null && (a.NextEmailAttemptUtc == null || a.NextEmailAttemptUtc <= now))
            .OrderBy(a => a.CreatedUtc)
            .Take(50)
            .ToListAsync(ct);
        if (due.Count == 0) return 0;

        var preferences = await EnsurePreferenceAsync(familyId, ct);
        var recipients = await _db.Users.AsNoTracking()
            .Where(u => u.FamilyId == familyId && u.Email != null && u.Email != "")
            .Select(u => u.Email!)
            .ToListAsync(ct);
        if (recipients.Count == 0)
        {
            foreach (var alert in due) alert.EmailedUtc = now;
            await _db.SaveChangesAsync(ct);
            return 0;
        }

        if (preferences.DailyDigest)
        {
            var sentDigest = await SendDigestAsync(familyId, settings, due, recipients, ct);
            await _db.SaveChangesAsync(ct);
            return sentDigest;
        }

        var sent = 0;
        foreach (var alert in due)
        {
            var (ok, error) = await SendOneAsync(settings, alert, recipients, ct);
            if (ok)
            {
                alert.EmailedUtc = DateTime.UtcNow;
                alert.EmailLastError = null;
                sent++;
            }
            else
            {
                ScheduleRetry(alert, error);
            }
        }
        await _db.SaveChangesAsync(ct);
        return sent;
    }

    private async Task<int> SendDigestAsync(Guid familyId, FamilySettings settings, List<Alert> alerts, List<string> recipients, CancellationToken ct)
    {
        var body = new System.Text.StringBuilder();
        body.AppendLine($"{alerts.Count} alert{(alerts.Count == 1 ? "" : "s")} for your family:");
        body.AppendLine();
        foreach (var alert in alerts)
            body.AppendLine($"• {alert.Title}\n  {alert.Detail}\n  {alert.CreatedUtc:u}");
        var (ok, error) = await SendMessageAsync(settings, recipients, "Parental OpControl daily digest", body.ToString(), ct);
        if (!ok)
        {
            foreach (var alert in alerts) ScheduleRetry(alert, error);
            return 0;
        }
        var now = DateTime.UtcNow;
        foreach (var alert in alerts)
        {
            alert.EmailedUtc = now;
            alert.EmailLastError = null;
        }
        return alerts.Count;
    }

    private async Task<(bool Ok, string? Error)> SendOneAsync(FamilySettings settings, Alert alert, List<string> recipients, CancellationToken ct)
    {
        var body = $"{alert.Title}\n\n{alert.Detail}\n\n{alert.CreatedUtc:u}";
        return await SendMessageAsync(settings, recipients, alert.Title, body, ct);
    }

    private async Task<(bool Ok, string? Error)> SendMessageAsync(FamilySettings settings, List<string> recipients, string subject, string body, CancellationToken ct)
    {
        var server = await _serverEmail.EnsureSettingsAsync(ct);
        if (_serverEmail.IsConfigured(server))
        {
            string? lastError = null;
            foreach (var recipient in recipients)
            {
                var (sent, error) = await _serverEmail.SendAsync(recipient, subject, body, ct);
                if (!sent) lastError = error;
            }
            return (lastError is null, lastError);
        }

        if (string.IsNullOrWhiteSpace(settings.SmtpHost) || string.IsNullOrWhiteSpace(settings.FromAddress))
            return (false, "No SMTP server is configured.");

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(settings.FromAddress),
                Subject = subject,
                Body = body,
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
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Alert email could not be sent");
            return (false, ex.Message);
        }
    }

    private static void ScheduleRetry(Alert alert, string? error)
    {
        alert.EmailAttempts++;
        alert.EmailLastError = error is { Length: > 500 } ? error[..500] : error;
        if (alert.EmailAttempts >= 6)
        {
            alert.EmailedUtc = DateTime.UtcNow;
            return;
        }
        var minutes = Math.Min(60, (int)Math.Pow(2, alert.EmailAttempts));
        alert.NextEmailAttemptUtc = DateTime.UtcNow.AddMinutes(minutes);
    }

    private async Task<DateTime?> NextEmailDueAsync(AlertPreference preference, DateTime nowUtc, CancellationToken ct)
    {
        var tzId = await _db.Devices.AsNoTracking()
            .Where(d => d.FamilyId == preference.FamilyId)
            .Select(d => d.TimeZoneId)
            .FirstOrDefaultAsync(ct);
        var tz = TimeZoneResolver.Resolve(tzId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var minutes = local.Hour * 60 + local.Minute;

        var quiet = preference.QuietStartMinutes != preference.QuietEndMinutes
            && (preference.QuietStartMinutes < preference.QuietEndMinutes
                ? minutes >= preference.QuietStartMinutes && minutes < preference.QuietEndMinutes
                : minutes >= preference.QuietStartMinutes || minutes < preference.QuietEndMinutes);
        if (quiet)
        {
            var localEnd = local.Date.AddMinutes(preference.QuietEndMinutes);
            if (preference.QuietStartMinutes > preference.QuietEndMinutes && minutes >= preference.QuietStartMinutes)
                localEnd = localEnd.AddDays(1);
            if (localEnd <= local) localEnd = localEnd.AddDays(1);
            return UsageService.ToUtc(DateTime.SpecifyKind(localEnd, DateTimeKind.Unspecified), tz);
        }

        if (!preference.DailyDigest) return null;

        var nextDigest = local.Date.AddHours(preference.DigestHour);
        if (nextDigest <= local) nextDigest = nextDigest.AddDays(1);
        return UsageService.ToUtc(DateTime.SpecifyKind(nextDigest, DateTimeKind.Unspecified), tz);
    }
}
