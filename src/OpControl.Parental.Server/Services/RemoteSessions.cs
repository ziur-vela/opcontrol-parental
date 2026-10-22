// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Hubs;

namespace OpControl.Parental.Server.Services;

public sealed class RemoteSessionService
{
    private const string LegacyNoticeDefault = "Parent's watching you";

    private readonly ParentalDbContext _db;
    private readonly IHubContext<ParentalHub> _hub;
    private readonly RemoteSessionRegistry _registry;
    private readonly CameraConsentService _consent;
    private readonly ILogger<RemoteSessionService> _logger;

    public RemoteSessionService(ParentalDbContext db, IHubContext<ParentalHub> hub, RemoteSessionRegistry registry, CameraConsentService consent, ILogger<RemoteSessionService> logger)
    {
        _db = db;
        _hub = hub;
        _registry = registry;
        _consent = consent;
        _logger = logger;
    }

    public async Task<RemoteSessionCreatedDto> CreateAsync(Guid familyId, Guid parentId, Guid deviceId, RemoteSessionKind kind, CancellationToken ct = default)
    {
        var device = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId && d.FamilyId == familyId, ct)
            ?? throw new InvalidOperationException(AppText.Get("Error.DeviceNotFoundFamily"));
        if (kind == RemoteSessionKind.Camera && !await _consent.HasAcceptedAsync(parentId, ct))
            throw new InvalidOperationException(AppText.Get("Error.CameraConsent"));
        var now = DateTime.UtcNow;
        await EndExistingForDeviceAsync(deviceId, ct);

        var showNotice = kind == RemoteSessionKind.Screen ? device.ScreenNoticeEnabled : device.CameraNoticeEnabled;
        var familySettings = await _db.FamilySettings.AsNoTracking().FirstOrDefaultAsync(s => s.FamilyId == familyId, ct);
        var familyLanguage = await _db.Families.AsNoTracking().Where(f => f.Id == familyId).Select(f => f.DefaultLanguage).FirstOrDefaultAsync(ct);
        var language = AppCulture.Normalize(device.ClientLanguage ?? familyLanguage);
        var storedNotice = familySettings?.NoticeText?.Trim();
        var noticeText = string.IsNullOrWhiteSpace(storedNotice) || storedNotice == LegacyNoticeDefault
            ? AppText.Get("Settings.NoticeDefault", language)
            : storedNotice;
        var session = new RemoteSession
        {
            FamilyId = familyId,
            ChildId = device.ChildId,
            DeviceId = deviceId,
            RequestedByParentId = parentId,
            Kind = kind,
            Status = RemoteSessionStatus.Requested,
            CreatedUtc = now,
            NoticeShown = showNotice
        };
        _db.RemoteSessions.Add(session);
        await _db.SaveChangesAsync(ct);
        _registry.Ensure(session.Id, now.AddMinutes(10));

        await _hub.Clients.Group(DeviceGroups.Device(deviceId)).SendAsync(DeviceHubMethods.RemoteSessionRequested,
            new RemoteSessionRequest(session.Id, deviceId, device.ChildId, kind, showNotice, noticeText), ct);
        _logger.LogInformation("Remote {Kind} session {SessionId} requested for device {DeviceId} by parent {ParentId}", kind, session.Id, deviceId, parentId);
        return new RemoteSessionCreatedDto(session.Id, deviceId, device.ChildId, kind, session.CreatedUtc, now.AddMinutes(10));
    }

    public async Task EndExistingForDeviceAsync(Guid deviceId, CancellationToken ct = default)
    {
        var existing = await _db.RemoteSessions.AsNoTracking()
            .Where(s => s.DeviceId == deviceId && (s.Status == RemoteSessionStatus.Requested || s.Status == RemoteSessionStatus.Active))
            .Select(s => s.Id)
            .ToListAsync(ct);
        foreach (var id in existing)
        {
            if (_registry.Contains(id)) await _registry.StopAsync(id, "replaced", ct);
            else await EndAsync(id, "replaced", ct);
        }
    }

    public async Task StopAsync(Guid sessionId, Guid? familyId, string reason, CancellationToken ct = default)
    {
        var session = await _db.RemoteSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return;
        if (familyId is not null && session.FamilyId != familyId) return;
        await _registry.StopAsync(sessionId, reason, ct);
        await EndAsync(sessionId, reason, ct);
    }

    public async Task MarkStartedAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await _db.RemoteSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return;
        session.Status = RemoteSessionStatus.Active;
        session.StartedUtc ??= DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task EndAsync(Guid sessionId, string reason, CancellationToken ct = default)
    {
        var session = await _db.RemoteSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return;
        if (session.Status == RemoteSessionStatus.Ended) return;
        session.Status = RemoteSessionStatus.Ended;
        session.EndedUtc = DateTime.UtcNow;
        session.EndReason = reason.Length > 80 ? reason[..80] : reason;
        await _db.SaveChangesAsync(ct);
        await _hub.Clients.Group(DeviceGroups.Device(session.DeviceId))
            .SendAsync(DeviceHubMethods.RemoteSessionStopped, new RemoteSessionStopRequest(session.Id), ct);
        _logger.LogInformation("Remote session {SessionId} ended: {Reason}", sessionId, reason);
    }
}

public sealed class RemoteSessionRegistry
{
    private sealed class Entry
    {
        public Guid Id;
        public WebSocket? Parent;
        public WebSocket? Device;
        public DateTime ExpiresUtc;
        public DateTime LastParentSeenUtc = DateTime.UtcNow;
        public TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Started;
        public int Stopped;
    }

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RemoteSessionRegistry> _logger;
    private readonly TimeSpan _heartbeatTimeout;
    private readonly TimeSpan _watchdogInterval;

    public RemoteSessionRegistry(IServiceScopeFactory scopes, ILogger<RemoteSessionRegistry> logger,
        TimeSpan? heartbeatTimeout = null, TimeSpan? watchdogInterval = null)
    {
        _scopes = scopes;
        _logger = logger;
        _heartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(30);
        _watchdogInterval = watchdogInterval ?? TimeSpan.FromSeconds(5);
    }

    public void Ensure(Guid id, DateTime expiresUtc)
    {
        var entry = _entries.GetOrAdd(id, _ => new Entry { Id = id, ExpiresUtc = expiresUtc });
        _ = Task.Run(async () =>
        {
            var delay = expiresUtc - DateTime.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay);
            await StopAsync(id, "timeout");
        });
    }

    public async Task AttachParentAsync(Guid id, WebSocket socket, CancellationToken ct)
    {
        if (!_entries.TryGetValue(id, out var entry))
        {
            await CloseAsync(socket, "Unknown or expired session.");
            return;
        }
        entry.Parent = socket;
        entry.LastParentSeenUtc = DateTime.UtcNow;
        StartIfReady(entry);
        _ = Task.Run(() => WatchdogAsync(entry));
        try
        {
            await MonitorParentAsync(entry, ct);
        }
        finally
        {
            await StopAsync(id, "parent-closed");
        }
    }

    public async Task AttachDeviceAsync(Guid id, WebSocket socket, CancellationToken ct)
    {
        if (!_entries.TryGetValue(id, out var entry))
        {
            await CloseAsync(socket, "Unknown or expired session.");
            return;
        }
        entry.Device = socket;
        StartIfReady(entry);
        try
        {
            await entry.Closed.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await StopAsync(id, "device-closed");
        }
    }

    private void StartIfReady(Entry entry)
    {
        if (entry.Parent is null || entry.Device is null) return;
        if (Interlocked.Exchange(ref entry.Started, 1) == 1) return;
        _ = Task.Run(() => RelayAsync(entry));
    }

    private async Task RelayAsync(Entry entry)
    {
        var buffer = new byte[1024 * 1024];
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<RemoteSessionService>().MarkStartedAsync(entry.Id);
            while (entry.Device is { State: WebSocketState.Open } && entry.Parent is { State: WebSocketState.Open })
            {
                var result = await entry.Device.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (entry.Parent is not { State: WebSocketState.Open } parent) break;
                if (result.MessageType == WebSocketMessageType.Text)
                    await parent.SendAsync(buffer.AsMemory(0, result.Count), WebSocketMessageType.Text, true, CancellationToken.None);
                else
                    await parent.SendAsync(buffer.AsMemory(0, result.Count), WebSocketMessageType.Binary, true, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Remote relay ended for {SessionId}", entry.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Remote relay failed for {SessionId}", entry.Id);
        }
        finally
        {
            await StopAsync(entry.Id, "relay-ended");
        }
    }

    private async Task MonitorParentAsync(Entry entry, CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (entry.Parent is { State: WebSocketState.Open } parent)
            {
                var result = await parent.ReceiveAsync(buffer, ct);
                entry.LastParentSeenUtc = DateTime.UtcNow;
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    if (text.Contains("stop", StringComparison.OrdinalIgnoreCase)) break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    public bool Contains(Guid id) => _entries.ContainsKey(id);

    private async Task WatchdogAsync(Entry entry)
    {
        while (_entries.TryGetValue(entry.Id, out var current) && ReferenceEquals(current, entry))
        {
            await Task.Delay(_watchdogInterval);
            if (DateTime.UtcNow - entry.LastParentSeenUtc > _heartbeatTimeout)
            {
                _logger.LogWarning("Remote session {SessionId} ended: no viewer heartbeat for {Timeout}", entry.Id, _heartbeatTimeout);
                await StopAsync(entry.Id, "heartbeat-timeout");
                return;
            }
        }
    }

    public async Task StopAsync(Guid id, string reason, CancellationToken ct = default)
    {
        if (!_entries.TryRemove(id, out var entry)) return;
        if (Interlocked.Exchange(ref entry.Stopped, 1) == 1) return;
        await CloseAsync(entry.Parent, reason);
        await CloseAsync(entry.Device, reason);
        entry.Closed.TrySetResult();
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<RemoteSessionService>().EndAsync(id, reason, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not finalise remote session {SessionId}", id);
        }
    }

    private static async Task CloseAsync(WebSocket? socket, string reason)
    {
        if (socket is null) return;
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
        }
        catch { }
        socket.Dispose();
    }
}
