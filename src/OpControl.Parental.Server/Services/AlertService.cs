// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

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
    private readonly EmailRenderer _renderer;
    private readonly ILogger<AlertService> _logger;

    public AlertService(ParentalDbContext db, DashboardEventBus events, IDataProtectionProvider protection,
        EmailSender serverEmail, EmailRenderer renderer, ILogger<AlertService> logger)
    {
        _db = db;
        _events = events;
        _protector = protection.CreateProtector("OpControl.Parental.Smtp");
        _serverEmail = serverEmail;
        _renderer = renderer;
        _logger = logger;
    }

    public async Task<Alert?> AddAsync(Guid familyId, Guid childId, Guid? deviceId, string type, AlertContent content, TimeSpan? dedupeWindow = null, CancellationToken ct = default, bool immediateEmail = false)
    {
        var now = DateTime.UtcNow;
        var argsJson = AlertText.SerializeArgs(content.Args);
        var title = AppText.Get(content.TitleKey, AppCulture.English, content.Args);
        var detail = string.IsNullOrWhiteSpace(content.DetailKey) ? "" : AppText.Get(content.DetailKey, AppCulture.English, content.Args);
        if (dedupeWindow is { } window)
        {
            var since = now - window;
            var exists = await _db.Alerts.AsNoTracking()
                .AnyAsync(a => a.FamilyId == familyId && a.ChildId == childId && a.Type == type
                    && ((a.ArgsJson != null && a.ArgsJson == argsJson) || (a.ArgsJson == null && a.Detail == detail))
                    && a.CreatedUtc >= since, ct);
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
            TitleKey = content.TitleKey,
            DetailKey = string.IsNullOrWhiteSpace(content.DetailKey) ? null : content.DetailKey,
            ArgsJson = argsJson,
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
            alert.NextEmailAttemptUtc = immediateEmail ? now : await NextEmailDueAsync(preferences, now, ct);
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

    public async Task SaveNoticeTextAsync(Guid familyId, string? text, CancellationToken ct = default)
    {
        var settings = await EnsureSettingsAsync(familyId, ct);
        var value = string.IsNullOrWhiteSpace(text) ? "Parent's watching you" : text.Trim();
        settings.NoticeText = value.Length > 80 ? value[..80] : value;
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
        preference.MutedChildMessage = dto.MutedChildMessage;
        preference.QuietStartMinutes = Math.Clamp(dto.QuietStartMinutes, 0, 1439);
        preference.QuietEndMinutes = Math.Clamp(dto.QuietEndMinutes, 0, 1439);
        preference.DailyDigest = dto.DailyDigest;
        preference.DigestHour = Math.Clamp(dto.DigestHour, 0, 23);
        preference.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public static AlertPreferencesDto ToDto(AlertPreference p) =>
        new(p.MutedNewApp, p.MutedRepeatedBlock, p.MutedPinOverride, p.MutedLimit, p.MutedOffline, p.MutedTampering, p.MutedChildMessage,
            p.QuietStartMinutes, p.QuietEndMinutes, p.DailyDigest, p.DigestHour);

    public static bool IsMuted(AlertPreference p, string type) => type switch
    {
        "new-app" => p.MutedNewApp,
        "repeated-blocked-site" => p.MutedRepeatedBlock,
        "pin-override" => p.MutedPinOverride,
        "daily-limit" or "app-limit" => p.MutedLimit,
        "device-offline" => p.MutedOffline,
        "tampering" => p.MutedTampering,
        "child-message" => p.MutedChildMessage,
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
        var recipientRows = await _db.Users.AsNoTracking()
            .Where(u => u.FamilyId == familyId && u.Email != null && u.Email != "")
            .Select(u => new { u.Email, u.PreferredLanguage })
            .ToListAsync(ct);
        var recipients = recipientRows.Select(r => r.Email!).ToList();
        if (recipients.Count == 0)
        {
            foreach (var alert in due) alert.EmailedUtc = now;
            await _db.SaveChangesAsync(ct);
            return 0;
        }

        var familyLanguage = await FamilyLanguageAsync(familyId, ct);
        var languageFor = (string? email) => AppCulture.Normalize(
            recipientRows.FirstOrDefault(r => string.Equals(r.Email, email, StringComparison.OrdinalIgnoreCase))?.PreferredLanguage
            ?? familyLanguage);

        if (preferences.DailyDigest)
        {
            var sentDigest = await SendDigestAsync(familyId, settings, due, recipients, languageFor, ct);
            await _db.SaveChangesAsync(ct);
            return sentDigest;
        }

        var sent = 0;
        foreach (var alert in due)
        {
            var (ok, error) = await SendOneAsync(settings, alert, recipients, languageFor, ct);
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

    private async Task<int> SendDigestAsync(Guid familyId, FamilySettings settings, List<Alert> alerts, List<string> recipients,
        Func<string?, string> languageFor, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddHours(-24);
        var messageCounts = await _db.ChildMessages.AsNoTracking()
            .Where(m => m.FamilyId == familyId && m.Direction == MessageDirection.ChildToParent
                && m.DeletedUtc == null && m.CreatedUtc >= since)
            .GroupBy(m => m.ChildId)
            .Select(g => new { ChildId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ChildId, x => x.Count, ct);
        var childIds = alerts.Select(a => a.ChildId).Concat(messageCounts.Keys).Distinct().ToList();
        var children = await _db.Children.AsNoTracking()
            .Where(c => childIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name })
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var deviceIds = alerts.Where(a => a.DeviceId is not null).Select(a => a.DeviceId!.Value).Distinct().ToList();
        var devices = await _db.Devices.AsNoTracking()
            .Where(d => deviceIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Name, d.TimeZoneId })
            .ToDictionaryAsync(d => d.Id, ct);
        var familyTimeZone = await _db.Devices.AsNoTracking()
            .Where(d => d.FamilyId == familyId)
            .Select(d => d.TimeZoneId)
            .FirstOrDefaultAsync(ct);

        string? lastError = null;
        foreach (var recipient in recipients)
        {
            var language = languageFor(recipient);
            var groups = new List<EmailDigestChild>();
            var byChild = alerts.GroupBy(a => a.ChildId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var childId in byChild.Keys.Concat(messageCounts.Keys).Distinct()
                .OrderBy(id => children.GetValueOrDefault(id, "")))
            {
                var items = new List<EmailAlertEntry>();
                if (messageCounts.TryGetValue(childId, out var messageCount) && messageCount > 0)
                {
                    items.Add(new EmailAlertEntry(
                        AppText.Get(messageCount == 1 ? "Email.Digest.MessageCountOne" : "Email.Digest.MessageCountMany", language, messageCount.ToString(AppCulture.Culture(language))),
                        "", ""));
                }
                if (byChild.TryGetValue(childId, out var childAlerts))
                {
                    items.AddRange(childAlerts.OrderByDescending(a => a.CreatedUtc)
                        .Select(a =>
                        {
                            var (title, detail) = AlertText.Render(a, language);
                            var timeZone = a.DeviceId is { } deviceId && devices.TryGetValue(deviceId, out var device)
                                ? device.TimeZoneId
                                : familyTimeZone;
                            return new EmailAlertEntry(title, detail, FormatTime(a.CreatedUtc, timeZone, language));
                        }));
                }
                var top = items.Take(5).ToList();
                groups.Add(new EmailDigestChild(
                    children.GetValueOrDefault(childId, ""),
                    items.Count,
                    top,
                    Math.Max(0, items.Count - top.Count)));
            }

            var total = groups.Sum(g => g.Count);
            var rendered = _renderer.RenderDigest(language, total, groups, _renderer.AlertsUrl);
            var (ok, error) = await SendMessageAsync(settings, rendered, [recipient], ct);
            if (!ok) lastError = error;
        }
        if (lastError is not null)
        {
            foreach (var alert in alerts) ScheduleRetry(alert, lastError);
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

    private async Task<(bool Ok, string? Error)> SendOneAsync(FamilySettings settings, Alert alert, List<string> recipients,
        Func<string?, string> languageFor, CancellationToken ct)
    {
        var childName = await _db.Children.AsNoTracking()
            .Where(c => c.Id == alert.ChildId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(ct) ?? "";
        string? deviceName = null;
        string? deviceTimeZone = null;
        if (alert.DeviceId is { } deviceId)
        {
            var device = await _db.Devices.AsNoTracking()
                .Where(d => d.Id == deviceId)
                .Select(d => new { d.Name, d.TimeZoneId })
                .FirstOrDefaultAsync(ct);
            deviceName = device?.Name;
            deviceTimeZone = device?.TimeZoneId;
        }

        string? lastError = null;
        var sentAny = false;
        foreach (var recipient in recipients)
        {
            var language = languageFor(recipient);
            var (title, detail) = AlertText.Render(alert, language);
            var time = FormatTime(alert.CreatedUtc, deviceTimeZone, language);
            var rendered = _renderer.RenderAlert(language, title, detail, childName, deviceName, time, _renderer.AlertsUrl);
            var (ok, error) = await SendMessageAsync(settings, rendered, [recipient], ct);
            if (ok) sentAny = true; else lastError = error;
        }
        return (sentAny || lastError is null, lastError);
    }

    private async Task<string> FamilyLanguageAsync(Guid familyId, CancellationToken ct) =>
        AppCulture.Normalize(await _db.Families.AsNoTracking().Where(f => f.Id == familyId).Select(f => f.DefaultLanguage).FirstOrDefaultAsync(ct));

    private async Task<(bool Ok, string? Error)> SendMessageAsync(FamilySettings settings, RenderedEmail rendered, List<string> recipients, CancellationToken ct)
    {
        string? lastError = null;
        foreach (var recipient in recipients)
        {
            var (sent, error) = await _serverEmail.SendAsync(new EmailOutgoing(recipient, rendered), settings, ct);
            if (!sent) lastError = error;
        }
        return (lastError is null, lastError);
    }

    private static string FormatTime(DateTime utc, string? timeZoneId, string language)
    {
        var timeZone = TimeZoneResolver.Resolve(timeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone);
        return local.ToString("d MMM yyyy, HH:mm", AppCulture.Culture(language));
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
