// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Hubs;

namespace OpControl.Parental.Server.Services;

public sealed class MessageRateLimitException(int retryAfterSeconds)
    : Exception(AppText.Get("Error.Messages.RateLimited", Math.Max(1, (int)Math.Ceiling(retryAfterSeconds / 60.0))))
{
    public int RetryAfterSeconds { get; } = Math.Max(1, retryAfterSeconds);
}

public sealed class MessageService
{
    public static readonly IReadOnlySet<string> PresetKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dinner", "wrap-up", "come-find-me" };

    public const int MaxTextLength = 500;
    public const int RateLimitCount = 10;
    public static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan PopupWindow = TimeSpan.FromHours(24);

    private readonly ParentalDbContext _db;
    private readonly IHubContext<ParentalHub> _hub;
    private readonly DashboardEventBus _events;
    private readonly AlertService _alerts;
    private readonly EmailSender _email;
    private readonly EmailRenderer _renderer;
    private readonly ILogger<MessageService> _logger;

    public MessageService(ParentalDbContext db, IHubContext<ParentalHub> hub, DashboardEventBus events,
        AlertService alerts, EmailSender email, EmailRenderer renderer, ILogger<MessageService> logger)
    {
        _db = db;
        _hub = hub;
        _events = events;
        _alerts = alerts;
        _email = email;
        _renderer = renderer;
        _logger = logger;
    }

    public async Task<ChildMessageDto> SendFromParentAsync(Guid familyId, Guid parentId, Guid childId,
        ParentMessageRequest request, CancellationToken ct = default)
    {
        var child = await _db.Children.AsNoTracking().FirstOrDefaultAsync(c => c.Id == childId && c.FamilyId == familyId, ct)
            ?? throw new InvalidOperationException(AppText.Get("Error.ChildNotFound"));
        var parent = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == parentId && u.FamilyId == familyId, ct)
            ?? throw new InvalidOperationException(AppText.Get("Error.MissingParent"));

        Guid? deviceId = null;
        if (request.DeviceId is { } requestedDevice)
        {
            var exists = await _db.Devices.AsNoTracking()
                .AnyAsync(d => d.Id == requestedDevice && d.FamilyId == familyId && d.ChildId == childId && !d.IsRevoked, ct);
            if (!exists) throw new InvalidOperationException(AppText.Get("Error.DeviceNotFoundFamily"));
            deviceId = requestedDevice;
        }

        var preset = NormalizePreset(request.Preset);
        var text = NormalizeText(request.Text, preset);
        var message = new ChildMessage
        {
            FamilyId = familyId,
            ChildId = childId,
            DeviceId = deviceId,
            Direction = MessageDirection.ParentToChild,
            SenderParentId = parentId,
            Text = text,
            Preset = preset,
            SpeakAloud = request.SpeakAloud,
            RequireAck = request.RequireAck,
            CreatedUtc = DateTime.UtcNow
        };
        _db.ChildMessages.Add(message);
        await _db.SaveChangesAsync(ct);

        var dto = ToDto(message, parent.DisplayName, child.Name, null);
        await PushToDevicesAsync(familyId, childId, deviceId, dto, ct);
        _events.PublishMessage(dto);
        _logger.LogInformation("Parent message {MessageId} sent to child {ChildId} (device {DeviceId})", message.Id, childId, deviceId);
        return dto;
    }

    public async Task<ChildMessageDto> SendFromChildAsync(Device device, DeviceMessageRequest request, CancellationToken ct = default)
    {
        var text = NormalizeText(request.Text, null);
        var now = DateTime.UtcNow;
        var since = now - RateLimitWindow;
        var recent = await _db.ChildMessages.AsNoTracking()
            .Where(m => m.FamilyId == device.FamilyId && m.ChildId == device.ChildId
                && m.Direction == MessageDirection.ChildToParent && m.CreatedUtc >= since)
            .OrderBy(m => m.CreatedUtc)
            .Select(m => m.CreatedUtc)
            .ToListAsync(ct);
        if (recent.Count >= RateLimitCount)
        {
            var retry = (int)Math.Ceiling((recent[^RateLimitCount] + RateLimitWindow - now).TotalSeconds);
            throw new MessageRateLimitException(retry);
        }

        var childName = await _db.Children.AsNoTracking().Where(c => c.Id == device.ChildId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";
        var message = new ChildMessage
        {
            FamilyId = device.FamilyId,
            ChildId = device.ChildId,
            DeviceId = device.Id,
            Direction = MessageDirection.ChildToParent,
            SenderParentId = null,
            Text = text,
            CreatedUtc = now
        };
        _db.ChildMessages.Add(message);
        await _db.SaveChangesAsync(ct);

        var dto = ToDto(message, null, childName, null);
        _events.PublishMessage(dto);
        var alert = await _alerts.AddAsync(device.FamilyId, device.ChildId, device.Id, "child-message",
            new AlertContent("Alert.ChildMessage.Title", "Alert.ChildMessage.Detail", childName, text),
            immediateEmail: true, ct: ct);
        await TrySendMessageEmailAsync(device, message, childName, alert, ct);
        _logger.LogInformation("Child message {MessageId} sent from device {DeviceId}", message.Id, device.Id);
        return dto;
    }

    public async Task<List<ChildMessageDto>> GetThreadAsync(Guid familyId, Guid childId, int take = 300, CancellationToken ct = default)
    {
        var child = await _db.Children.AsNoTracking().FirstOrDefaultAsync(c => c.Id == childId && c.FamilyId == familyId, ct)
            ?? throw new InvalidOperationException(AppText.Get("Error.ChildNotFound"));
        var messages = await _db.ChildMessages.AsNoTracking()
            .Where(m => m.FamilyId == familyId && m.ChildId == childId && m.DeletedUtc == null)
            .OrderByDescending(m => m.CreatedUtc)
            .Take(Math.Clamp(take, 1, 1000))
            .ToListAsync(ct);
        messages.Reverse();
        var names = await LoadNamesAsync(messages, ct);
        return messages.Select(m => ToDto(m, null, child.Name, names)).ToList();
    }

    public async Task<List<ChildMessageDto>> GetThreadForDeviceAsync(Device device, int take = 300, CancellationToken ct = default)
    {
        var childName = await _db.Children.AsNoTracking().Where(c => c.Id == device.ChildId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";
        var messages = await _db.ChildMessages.AsNoTracking()
            .Where(m => m.FamilyId == device.FamilyId && m.ChildId == device.ChildId && m.DeletedUtc == null)
            .OrderByDescending(m => m.CreatedUtc)
            .Take(Math.Clamp(take, 1, 1000))
            .ToListAsync(ct);
        messages.Reverse();
        var names = await LoadNamesAsync(messages, ct);
        return messages.Select(m => ToDto(m, null, childName, names)).ToList();
    }

    public async Task<List<ChildMessageDto>> GetPendingForDeviceAsync(Device device, int take = 100, CancellationToken ct = default)
    {
        var childName = await _db.Children.AsNoTracking().Where(c => c.Id == device.ChildId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";
        var messages = await _db.ChildMessages.AsNoTracking()
            .Where(m => m.FamilyId == device.FamilyId && m.ChildId == device.ChildId
                && m.Direction == MessageDirection.ParentToChild && m.DeliveredUtc == null && m.DeletedUtc == null
                && (m.DeviceId == null || m.DeviceId == device.Id))
            .OrderBy(m => m.CreatedUtc)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct);
        var names = await LoadNamesAsync(messages, ct);
        return messages.Select(m => ToDto(m, null, childName, names)).ToList();
    }

    public async Task<ChildMessageDto> MarkDeliveredAsync(Device device, Guid messageId, CancellationToken ct = default)
    {
        var message = await FindForDeviceAsync(device, messageId, ct);
        if (message.DeliveredUtc is null)
        {
            message.DeliveredUtc = DateTime.UtcNow;
            message.DeliveredByDeviceId = device.Id;
            await _db.SaveChangesAsync(ct);
            await PublishStatusAsync(message, ct);
        }
        return await ToDtoAsync(message, ct);
    }

    public async Task<ChildMessageDto> MarkSeenAsync(Device device, Guid messageId, CancellationToken ct = default)
    {
        var message = await FindForDeviceAsync(device, messageId, ct);
        var changed = false;
        if (message.DeliveredUtc is null)
        {
            message.DeliveredUtc = DateTime.UtcNow;
            message.DeliveredByDeviceId = device.Id;
            changed = true;
        }
        if (message.SeenUtc is null)
        {
            message.SeenUtc = DateTime.UtcNow;
            message.SeenByDeviceId = device.Id;
            changed = true;
        }
        if (changed)
        {
            await _db.SaveChangesAsync(ct);
            await PublishStatusAsync(message, ct);
        }
        return await ToDtoAsync(message, ct);
    }

    public async Task<ChildMessageDto> MarkAcknowledgedAsync(Device device, Guid messageId, CancellationToken ct = default)
    {
        var message = await FindForDeviceAsync(device, messageId, ct);
        var now = DateTime.UtcNow;
        var changed = false;
        if (message.DeliveredUtc is null)
        {
            message.DeliveredUtc = now;
            message.DeliveredByDeviceId = device.Id;
            changed = true;
        }
        if (message.SeenUtc is null)
        {
            message.SeenUtc = now;
            message.SeenByDeviceId = device.Id;
            changed = true;
        }
        if (message.AcknowledgedUtc is null)
        {
            message.AcknowledgedUtc = now;
            message.AcknowledgedByDeviceId = device.Id;
            changed = true;
        }
        if (changed)
        {
            await _db.SaveChangesAsync(ct);
            await PublishStatusAsync(message, ct);
        }
        return await ToDtoAsync(message, ct);
    }

    public async Task<int> MarkParentDeliveredAsync(Guid familyId, Guid childId, Guid? parentId, CancellationToken ct = default)
    {
        await EnsureChildAsync(familyId, childId, ct);
        var pending = await _db.ChildMessages
            .Where(m => m.FamilyId == familyId && m.ChildId == childId
                && m.Direction == MessageDirection.ChildToParent && m.DeliveredUtc == null && m.DeletedUtc == null)
            .ToListAsync(ct);
        if (pending.Count == 0) return 0;
        var now = DateTime.UtcNow;
        foreach (var message in pending)
        {
            message.DeliveredUtc = now;
            message.ParentDeliveredByParentId ??= parentId;
        }
        await _db.SaveChangesAsync(ct);
        await PublishStatusBatchAsync(pending, ct);
        return pending.Count;
    }

    public async Task<int> MarkParentReadAsync(Guid familyId, Guid childId, Guid? parentId, CancellationToken ct = default)
    {
        await EnsureChildAsync(familyId, childId, ct);
        var unread = await _db.ChildMessages
            .Where(m => m.FamilyId == familyId && m.ChildId == childId
                && m.Direction == MessageDirection.ChildToParent && m.ParentReadUtc == null && m.DeletedUtc == null)
            .ToListAsync(ct);
        if (unread.Count == 0) return 0;
        var now = DateTime.UtcNow;
        foreach (var message in unread)
        {
            message.DeliveredUtc ??= now;
            message.ParentDeliveredByParentId ??= parentId;
            message.ParentReadUtc = now;
            message.ParentReadByParentId ??= parentId;
        }
        await _db.SaveChangesAsync(ct);
        await PublishStatusBatchAsync(unread, ct);
        return unread.Count;
    }

    public async Task<int> UnreadCountAsync(Guid familyId, CancellationToken ct = default) =>
        await _db.ChildMessages.AsNoTracking()
            .CountAsync(m => m.FamilyId == familyId && m.Direction == MessageDirection.ChildToParent
                && m.ParentReadUtc == null && m.DeletedUtc == null, ct);

    public async Task DeleteAsync(Guid familyId, Guid messageId, CancellationToken ct = default)
    {
        var message = await _db.ChildMessages.FirstOrDefaultAsync(m => m.Id == messageId && m.FamilyId == familyId, ct)
            ?? throw new InvalidOperationException(AppText.Get("Error.MessageNotFound"));
        if (message.DeletedUtc is not null) return;
        message.DeletedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await PublishStatusAsync(message, ct);
    }

    public async Task ClearThreadAsync(Guid familyId, Guid childId, CancellationToken ct = default)
    {
        var messages = await _db.ChildMessages
            .Where(m => m.FamilyId == familyId && m.ChildId == childId && m.DeletedUtc == null)
            .ToListAsync(ct);
        if (messages.Count == 0) return;
        var now = DateTime.UtcNow;
        foreach (var message in messages) message.DeletedUtc = now;
        await _db.SaveChangesAsync(ct);
        foreach (var message in messages)
            await PublishStatusAsync(message, ct);
    }

    private async Task EnsureChildAsync(Guid familyId, Guid childId, CancellationToken ct)
    {
        if (!await _db.Children.AsNoTracking().AnyAsync(c => c.Id == childId && c.FamilyId == familyId, ct))
            throw new InvalidOperationException(AppText.Get("Error.ChildNotFound"));
    }

    private async Task<ChildMessage> FindForDeviceAsync(Device device, Guid messageId, CancellationToken ct)
    {
        var message = await _db.ChildMessages.FirstOrDefaultAsync(m => m.Id == messageId
            && m.FamilyId == device.FamilyId && m.ChildId == device.ChildId && m.DeletedUtc == null, ct)
            ?? throw new InvalidOperationException(AppText.Get("Error.MessageNotFound"));
        if (message.Direction != MessageDirection.ParentToChild)
            throw new InvalidOperationException(AppText.Get("Error.MessageNotFound"));
        if (message.DeviceId is { } target && target != device.Id)
            throw new InvalidOperationException(AppText.Get("Error.MessageNotForDevice"));
        return message;
    }

    private async Task PublishStatusAsync(ChildMessage message, CancellationToken ct)
    {
        var childName = await _db.Children.AsNoTracking().Where(c => c.Id == message.ChildId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";
        var names = await LoadNamesAsync([message], ct);
        var dto = ToDto(message, null, childName, names);
        _events.PublishMessage(dto);
        await _hub.Clients.Group(DeviceGroups.Child(message.ChildId)).SendAsync(DeviceHubMethods.MessageStatusChanged, dto, ct);
    }

    private async Task PublishStatusBatchAsync(IReadOnlyList<ChildMessage> messages, CancellationToken ct)
    {
        if (messages.Count == 0) return;
        var childName = await _db.Children.AsNoTracking().Where(c => c.Id == messages[0].ChildId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";
        var names = await LoadNamesAsync(messages, ct);
        foreach (var message in messages)
        {
            var dto = ToDto(message, null, childName, names);
            _events.PublishMessage(dto);
            await _hub.Clients.Group(DeviceGroups.Child(message.ChildId)).SendAsync(DeviceHubMethods.MessageStatusChanged, dto, ct);
        }
    }

    private async Task PushToDevicesAsync(Guid familyId, Guid childId, Guid? deviceId, ChildMessageDto dto, CancellationToken ct)
    {
        var group = deviceId is { } target ? DeviceGroups.Device(target) : DeviceGroups.Child(childId);
        await _hub.Clients.Group(group).SendAsync(DeviceHubMethods.MessageReceived, dto, ct);
    }

    private async Task<ChildMessageDto> ToDtoAsync(ChildMessage message, CancellationToken ct)
    {
        var childName = await _db.Children.AsNoTracking().Where(c => c.Id == message.ChildId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";
        var names = await LoadNamesAsync([message], ct);
        var parentName = message.SenderParentId is { } parentId ? names.Parents.GetValueOrDefault(parentId) : null;
        return ToDto(message, parentName, childName, names);
    }

    private sealed record NameLookup(IReadOnlyDictionary<Guid, string> Parents, IReadOnlyDictionary<Guid, string> Devices);

    private async Task<NameLookup> LoadNamesAsync(IEnumerable<ChildMessage> messages, CancellationToken ct)
    {
        var parentIds = messages
            .SelectMany(m => new[] { m.SenderParentId, m.ParentDeliveredByParentId, m.ParentReadByParentId })
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var deviceIds = messages
            .SelectMany(m => new[] { m.DeliveredByDeviceId, m.SeenByDeviceId, m.AcknowledgedByDeviceId })
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var parents = parentIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Users.AsNoTracking().Where(u => parentIds.Contains(u.Id))
                .Select(u => new { u.Id, u.DisplayName })
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var devices = deviceIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Devices.AsNoTracking().Where(d => deviceIds.Contains(d.Id))
                .Select(d => new { d.Id, d.Name })
                .ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        return new NameLookup(parents, devices);
    }


    private async Task TrySendMessageEmailAsync(Device device, ChildMessage message, string childName, Alert? alert, CancellationToken ct)
    {
        if (alert is null) return;
        var settings = await _db.FamilySettings.AsNoTracking().FirstOrDefaultAsync(s => s.FamilyId == device.FamilyId, ct);
        if (settings is null || !settings.AlertEmailsEnabled || string.Equals(alert.EmailLastError, "muted", StringComparison.Ordinal))
            return;

        var recipients = await _db.Users.AsNoTracking()
            .Where(u => u.FamilyId == device.FamilyId && u.Email != null && u.Email != "")
            .Select(u => new { u.Email, u.PreferredLanguage })
            .ToListAsync(ct);
        if (recipients.Count == 0) return;

        var familyLanguage = AppCulture.Normalize(await _db.Families.AsNoTracking()
            .Where(f => f.Id == device.FamilyId).Select(f => f.DefaultLanguage).FirstOrDefaultAsync(ct));
        var timeZoneId = device.TimeZoneId;
        var sentAny = false;
        var failed = false;
        foreach (var recipient in recipients)
        {
            var language = AppCulture.Normalize(recipient.PreferredLanguage ?? familyLanguage);
            var timeZone = TimeZoneResolver.Resolve(timeZoneId);
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(message.CreatedUtc, DateTimeKind.Utc), timeZone);
            var time = local.ToString("d MMM yyyy, HH:mm", AppCulture.Culture(language));
            var rendered = _renderer.RenderMessage(language, childName, message.Text, time, _renderer.MessagesUrl);
            var (sent, error) = await _email.SendAsync(new EmailOutgoing(recipient.Email!, rendered), settings, ct);
            if (sent) sentAny = true; else failed = failed || error is not null;
        }

        if (sentAny)
        {
            alert.EmailedUtc = DateTime.UtcNow;
            alert.EmailLastError = null;
            await _db.SaveChangesAsync(ct);
        }
        else if (failed)
        {
            alert.EmailLastError = AppText.Get("Email.Error.MessageFailed");
            await _db.SaveChangesAsync(ct);
        }
    }

    private static string NormalizeText(string? text, string? preset)
    {
        var value = DeviceText.Clean(text, MaxTextLength);
        if (value.Length == 0 && preset is not null)
        {
            var key = preset switch
            {
                "dinner" => "Dinner",
                "wrap-up" => "WrapUp",
                "come-find-me" => "ComeFindMe",
                _ => null
            };
            if (key is not null) value = AppText.Get("Messages.Preset." + key);
        }
        if (value.Length == 0)
            throw new InvalidOperationException(AppText.Get("Error.Messages.Empty"));
        return value;
    }

    private static string? NormalizePreset(string? preset)
    {
        if (string.IsNullOrWhiteSpace(preset)) return null;
        var value = preset.Trim().ToLowerInvariant();
        return PresetKeys.Contains(value) ? value : null;
    }

    private static ChildMessageDto ToDto(ChildMessage message, string? parentName, string childName, NameLookup? names)
    {
        var status = message.Direction == MessageDirection.ChildToParent
            ? message.ParentReadUtc is not null ? MessageStatus.Seen
                : message.DeliveredUtc is not null ? MessageStatus.Delivered : MessageStatus.Sent
            : message.AcknowledgedUtc is not null ? MessageStatus.Acknowledged
                : message.SeenUtc is not null ? MessageStatus.Seen
                : message.DeliveredUtc is not null ? MessageStatus.Delivered
                : MessageStatus.Sent;
        var sender = message.Direction == MessageDirection.ParentToChild
            ? (string.IsNullOrWhiteSpace(parentName) ? AppText.Get("Common.ParentFallback") : parentName!)
            : childName;
        return new ChildMessageDto(
            message.Id, message.ChildId, message.DeviceId, message.Direction, message.SenderParentId,
            sender, message.Text, message.Preset, message.SpeakAloud, message.RequireAck,
            message.CreatedUtc, message.DeliveredUtc, message.SeenUtc, message.AcknowledgedUtc,
            message.ParentReadUtc, status, message.DeletedUtc is not null,
            names is not null && message.DeliveredByDeviceId is { } deliveredId ? names.Devices.GetValueOrDefault(deliveredId) : null,
            names is not null && message.SeenByDeviceId is { } seenId ? names.Devices.GetValueOrDefault(seenId) : null,
            names is not null && message.AcknowledgedByDeviceId is { } ackId ? names.Devices.GetValueOrDefault(ackId) : null,
            names is not null && message.ParentDeliveredByParentId is { } parentDeliveredId ? names.Parents.GetValueOrDefault(parentDeliveredId) : null,
            names is not null && message.ParentReadByParentId is { } parentReadId ? names.Parents.GetValueOrDefault(parentReadId) : null);
    }
}
