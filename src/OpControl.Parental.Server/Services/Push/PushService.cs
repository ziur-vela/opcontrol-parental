// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services.Push;

public sealed record PushSubscribeRequest(string Endpoint, PushKeys Keys, string? DeviceLabel);
public sealed record PushKeys(string P256dh, string Auth);
public sealed record PushEndpointRequest(string Endpoint);
public sealed record PushPayload(string Title, string Body, string Url, string Tag);

/// <summary>Alerts waiting to be pushed. Filled by AlertService, drained by PushDispatcher.</summary>
public sealed class PushQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(1000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });

    public void Enqueue(Guid alertId) => _channel.Writer.TryWrite(alertId);
    public ChannelReader<Guid> Reader => _channel.Reader;
}

public sealed class PushService
{
    private const string Protector = "OpControl.Parental.Vapid";
    private static readonly SemaphoreSlim KeyGate = new(1, 1);

    private readonly ParentalDbContext _db;
    private readonly IDataProtector _protector;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<PushService> _logger;

    public PushService(ParentalDbContext db, IDataProtectionProvider protection, IHttpClientFactory http, ILogger<PushService> logger)
    {
        _db = db;
        _protector = protection.CreateProtector(Protector);
        _http = http;
        _logger = logger;
    }

    /// <summary>Returns the server's VAPID public key, creating the key pair on first use.</summary>
    public async Task<string> GetPublicKeyAsync(CancellationToken ct = default) => (await GetKeysAsync(ct)).PublicKey;

    private async Task<(string PublicKey, string PrivateKey)> GetKeysAsync(CancellationToken ct)
    {
        var settings = await _db.ServerSettings.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (settings is { VapidPublicKey: not null, VapidPrivateKeyProtected: not null })
            return (settings.VapidPublicKey, _protector.Unprotect(settings.VapidPrivateKeyProtected));

        await KeyGate.WaitAsync(ct);
        try
        {
            settings = await _db.ServerSettings.FirstOrDefaultAsync(s => s.Id == 1, ct);
            if (settings is null)
            {
                settings = new ServerSettings { Id = 1 };
                _db.ServerSettings.Add(settings);
            }
            if (settings.VapidPublicKey is null || settings.VapidPrivateKeyProtected is null)
            {
                var (pub, priv) = WebPushCrypto.CreateVapidKeys();
                settings.VapidPublicKey = pub;
                settings.VapidPrivateKeyProtected = _protector.Protect(priv);
                settings.UpdatedUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                _logger.LogInformation("Created the Web Push (VAPID) key pair");
            }
            return (settings.VapidPublicKey, _protector.Unprotect(settings.VapidPrivateKeyProtected));
        }
        finally
        {
            KeyGate.Release();
        }
    }

    public async Task SubscribeAsync(Guid familyId, Guid parentId, PushSubscribeRequest request, CancellationToken ct = default)
    {
        var endpoint = (request.Endpoint ?? "").Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || endpoint.Length > 1000)
            throw new InvalidOperationException(AppText.Get("Push.Error.BadSubscription"));
        var p256dh = (request.Keys?.P256dh ?? "").Trim();
        var auth = (request.Keys?.Auth ?? "").Trim();
        try
        {
            var key = PushBase64.Decode(p256dh);
            if (key.Length != 65 || key[0] != 0x04 || PushBase64.Decode(auth).Length < 16) throw new FormatException();
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(AppText.Get("Push.Error.BadSubscription"));
        }

        var label = string.IsNullOrWhiteSpace(request.DeviceLabel) ? null : request.DeviceLabel.Trim();
        if (label is { Length: > 200 }) label = label[..200];

        var existing = await _db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint, ct);
        if (existing is null)
        {
            _db.PushSubscriptions.Add(new PushSubscription
            {
                FamilyId = familyId,
                ParentId = parentId,
                Endpoint = endpoint,
                P256dh = p256dh,
                Auth = auth,
                DeviceLabel = label
            });
        }
        else
        {
            // The same browser can only belong to the parent who is signed in on it now.
            existing.FamilyId = familyId;
            existing.ParentId = parentId;
            existing.P256dh = p256dh;
            existing.Auth = auth;
            existing.DeviceLabel = label ?? existing.DeviceLabel;
            existing.FailureCount = 0;
            existing.LastError = null;
        }
        await EnsurePreferenceAsync(familyId, parentId, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UnsubscribeAsync(Guid parentId, string endpoint, CancellationToken ct = default)
    {
        var sub = await _db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint && s.ParentId == parentId, ct);
        if (sub is null) return;
        _db.PushSubscriptions.Remove(sub);
        await _db.SaveChangesAsync(ct);
    }

    public async Task RemoveSubscriptionAsync(Guid parentId, Guid subscriptionId, CancellationToken ct = default)
    {
        var sub = await _db.PushSubscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId && s.ParentId == parentId, ct);
        if (sub is null) return;
        _db.PushSubscriptions.Remove(sub);
        await _db.SaveChangesAsync(ct);
    }

    public Task<List<PushSubscription>> ListAsync(Guid parentId, CancellationToken ct = default) =>
        _db.PushSubscriptions.AsNoTracking().Where(s => s.ParentId == parentId).OrderByDescending(s => s.CreatedUtc).ToListAsync(ct);

    public async Task<PushPreference> EnsurePreferenceAsync(Guid familyId, Guid parentId, CancellationToken ct = default)
    {
        var pref = await _db.PushPreferences.FirstOrDefaultAsync(p => p.ParentId == parentId, ct);
        if (pref is not null) return pref;
        pref = new PushPreference { ParentId = parentId, FamilyId = familyId };
        _db.PushPreferences.Add(pref);
        await _db.SaveChangesAsync(ct);
        return pref;
    }

    public async Task SavePreferenceAsync(Guid familyId, Guid parentId, PushPreference values, CancellationToken ct = default)
    {
        var pref = await EnsurePreferenceAsync(familyId, parentId, ct);
        pref.Messages = values.Messages;
        pref.Tampering = values.Tampering;
        pref.Limits = values.Limits;
        pref.PinOverride = values.PinOverride;
        pref.Offline = values.Offline;
        pref.RepeatedBlock = values.RepeatedBlock;
        pref.NewApp = values.NewApp;
        pref.Other = values.Other;
        pref.RespectQuietHours = values.RespectQuietHours;
        pref.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Sends a test notification to one of the parent's subscriptions.</summary>
    public async Task<(bool Ok, string? Error)> SendTestAsync(Guid parentId, string endpoint, CancellationToken ct = default)
    {
        var sub = await _db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint && s.ParentId == parentId, ct);
        if (sub is null) return (false, AppText.Get("Push.Error.NotSubscribed"));
        var language = await LanguageForAsync(sub.ParentId, sub.FamilyId, ct);
        var payload = new PushPayload(AppText.Get("App.Name", language), AppText.Get("Push.Test.Body", language), "/settings", "test");
        var error = await SendAsync(sub, payload, urgent: true, ct);
        await _db.SaveChangesAsync(ct);
        return (error is null, error);
    }

    /// <summary>Pushes one alert to every parent in the family who wants that kind of alert.</summary>
    public async Task SendAlertAsync(Guid alertId, CancellationToken ct = default)
    {
        var alert = await _db.Alerts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == alertId, ct);
        if (alert is null) return;
        var category = Category(alert.Type);

        var subs = await _db.PushSubscriptions.Where(s => s.FamilyId == alert.FamilyId).ToListAsync(ct);
        if (subs.Count == 0) return;
        var parentIds = subs.Select(s => s.ParentId).Distinct().ToList();
        var parents = await _db.Users.AsNoTracking().Where(p => parentIds.Contains(p.Id) && p.FamilyId == alert.FamilyId).ToListAsync(ct);
        var prefs = await _db.PushPreferences.AsNoTracking().Where(p => parentIds.Contains(p.ParentId)).ToDictionaryAsync(p => p.ParentId, ct);
        var family = await _db.Families.AsNoTracking().FirstOrDefaultAsync(f => f.Id == alert.FamilyId, ct);
        var quiet = await InQuietHoursAsync(alert.FamilyId, ct);
        var urgent = category is "messages" or "tampering";

        foreach (var parent in parents)
        {
            if (!Allowed(parent, category)) continue;
            var pref = prefs.GetValueOrDefault(parent.Id) ?? new PushPreference();
            if (!Wants(pref, category)) continue;
            if (quiet && pref.RespectQuietHours && !urgent) continue;

            var language = AppCulture.Normalize(parent.PreferredLanguage ?? family?.DefaultLanguage);
            var (title, detail) = AlertText.Render(alert, language);
            var url = category == "messages" ? "/messages" : "/alerts";
            var payload = new PushPayload(Trim(title, 120), Trim(detail, 240), url, category == "messages" ? "msg-" + alert.ChildId : alert.Type + "-" + alert.Id);
            foreach (var sub in subs.Where(s => s.ParentId == parent.Id))
                await SendAsync(sub, payload, urgent, ct);
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task<string?> SendAsync(PushSubscription sub, PushPayload payload, bool urgent, CancellationToken ct)
    {
        try
        {
            var keys = await GetKeysAsync(ct);
            var subject = await SubjectAsync(ct);
            var body = WebPushCrypto.Encrypt(sub.P256dh, sub.Auth, JsonSerializer.SerializeToUtf8Bytes(new
            {
                title = payload.Title,
                body = payload.Body,
                url = payload.Url,
                tag = payload.Tag
            }));
            using var request = new HttpRequestMessage(HttpMethod.Post, sub.Endpoint) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentEncoding.Add("aes128gcm");
            request.Headers.TryAddWithoutValidation("TTL", urgent ? "86400" : "43200");
            request.Headers.TryAddWithoutValidation("Urgency", urgent ? "high" : "normal");
            request.Headers.TryAddWithoutValidation("Authorization",
                WebPushCrypto.VapidAuthorization(sub.Endpoint, subject, keys.PublicKey, keys.PrivateKey, DateTimeOffset.UtcNow));

            using var client = _http.CreateClient("webpush");
            using var response = await client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                sub.LastSuccessUtc = DateTime.UtcNow;
                sub.FailureCount = 0;
                sub.LastError = null;
                return null;
            }
            var text = Trim(await response.Content.ReadAsStringAsync(ct), 300);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                _db.PushSubscriptions.Remove(sub);
                _logger.LogInformation("Removed expired push subscription {Id} ({Status})", sub.Id, (int)response.StatusCode);
                return AppText.Get("Push.Error.Expired");
            }
            return Fail(sub, $"{(int)response.StatusCode} {text}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or ArgumentException or System.Security.Cryptography.CryptographicException)
        {
            return Fail(sub, ex.Message);
        }
    }

    private string Fail(PushSubscription sub, string error)
    {
        sub.LastErrorUtc = DateTime.UtcNow;
        sub.LastError = Trim(error, 500);
        sub.FailureCount++;
        _logger.LogWarning("Push to subscription {Id} failed ({Count}): {Error}", sub.Id, sub.FailureCount, sub.LastError);
        if (sub.FailureCount >= 10) _db.PushSubscriptions.Remove(sub);
        return sub.LastError;
    }

    private async Task<string> SubjectAsync(CancellationToken ct)
    {
        var settings = await _db.ServerSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (!string.IsNullOrWhiteSpace(settings?.FromAddress)) return "mailto:" + settings.FromAddress.Trim();
        if (Uri.TryCreate(settings?.PublicBaseUrl, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps) return url.GetLeftPart(UriPartial.Authority);
        return "mailto:admin@localhost";
    }

    private async Task<string> LanguageForAsync(Guid parentId, Guid familyId, CancellationToken ct)
    {
        var preferred = await _db.Users.AsNoTracking().Where(p => p.Id == parentId).Select(p => p.PreferredLanguage).FirstOrDefaultAsync(ct);
        var familyDefault = await _db.Families.AsNoTracking().Where(f => f.Id == familyId).Select(f => f.DefaultLanguage).FirstOrDefaultAsync(ct);
        return AppCulture.Normalize(preferred ?? familyDefault);
    }

    private async Task<bool> InQuietHoursAsync(Guid familyId, CancellationToken ct)
    {
        var pref = await _db.AlertPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.FamilyId == familyId, ct);
        if (pref is null || pref.QuietStartMinutes == pref.QuietEndMinutes) return false;
        var tzId = await _db.Devices.AsNoTracking().Where(d => d.FamilyId == familyId).Select(d => d.TimeZoneId).FirstOrDefaultAsync(ct);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneResolver.Resolve(tzId));
        var minutes = local.Hour * 60 + local.Minute;
        return pref.QuietStartMinutes < pref.QuietEndMinutes
            ? minutes >= pref.QuietStartMinutes && minutes < pref.QuietEndMinutes
            : minutes >= pref.QuietStartMinutes || minutes < pref.QuietEndMinutes;
    }

    public static string Category(string type) => type switch
    {
        "child-message" => "messages",
        "tampering" => "tampering",
        "daily-limit" or "app-limit" => "limits",
        "pin-override" => "pin",
        "device-offline" => "offline",
        "repeated-blocked-site" => "blocked",
        "new-app" => "newapp",
        _ => "other"
    };

    public static bool Wants(PushPreference p, string category) => category switch
    {
        "messages" => p.Messages,
        "tampering" => p.Tampering,
        "limits" => p.Limits,
        "pin" => p.PinOverride,
        "offline" => p.Offline,
        "blocked" => p.RepeatedBlock,
        "newapp" => p.NewApp,
        _ => p.Other
    };

    /// <summary>Owners get everything; co-parents need Messaging for messages and View reports for alerts.</summary>
    public static bool Allowed(ParentUser parent, string category)
    {
        if (parent.RoleInFamily == FamilyRole.Owner) return true;
        var needed = category == "messages" ? ParentPermissions.Messaging : ParentPermissions.ViewReports;
        return (parent.Permissions & needed) == needed;
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}

/// <summary>Sends queued alert notifications in the background so alerts are never slowed down by push delivery.</summary>
public sealed class PushDispatcher : BackgroundService
{
    private readonly PushQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PushDispatcher> _logger;

    public PushDispatcher(PushQueue queue, IServiceScopeFactory scopes, ILogger<PushDispatcher> logger)
    {
        _queue = queue;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var alertId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<PushService>().SendAlertAsync(alertId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Push for alert {AlertId} failed", alertId);
            }
        }
    }
}
