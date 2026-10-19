using Microsoft.AspNetCore.SignalR;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Hubs;

namespace OpControl.Parental.Server.Services;

public sealed class DashboardEventBus
{
    public event Action<DeviceStatusDto>? DeviceStatusChanged;
    public event Action<LockStateDto>? LockStateChanged;

    public void PublishDeviceStatus(DeviceStatusDto status) => DeviceStatusChanged?.Invoke(status);
    public void PublishLockState(LockStateDto state) => LockStateChanged?.Invoke(state);
}

public sealed class DevicePresenceService
{
    private sealed class ConnectionState
    {
        public int Count;
        public DateTime LastSeenUtc = DateTime.UtcNow;
    }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DashboardEventBus _events;
    private readonly ILogger<DevicePresenceService> _logger;
    private readonly Dictionary<Guid, ConnectionState> _connections = new();
    private readonly object _gate = new();

    public DevicePresenceService(IServiceScopeFactory scopeFactory, DashboardEventBus events, ILogger<DevicePresenceService> logger)
    {
        _scopeFactory = scopeFactory;
        _events = events;
        _logger = logger;
    }

    public bool IsOnline(Guid deviceId)
    {
        lock (_gate)
            return _connections.TryGetValue(deviceId, out var state) && state.Count > 0;
    }

    public async Task ConnectedAsync(Guid deviceId, Guid familyId, Guid childId, string name, DevicePlatform platform, string? timeZoneId)
    {
        lock (_gate)
        {
            if (!_connections.TryGetValue(deviceId, out var state))
                _connections[deviceId] = state = new ConnectionState();
            state.Count++;
            state.LastSeenUtc = DateTime.UtcNow;
        }

        await UpdateLastSeenAsync(deviceId, DateTime.UtcNow);
        _events.PublishDeviceStatus(new DeviceStatusDto(deviceId, childId, name, platform, true, DateTime.UtcNow, timeZoneId));
        _logger.LogInformation("Device {DeviceId} connected", deviceId);
    }

    public async Task DisconnectedAsync(Guid deviceId, Guid childId, string name, DevicePlatform platform, string? timeZoneId)
    {
        var now = DateTime.UtcNow;
        var online = false;
        lock (_gate)
        {
            if (_connections.TryGetValue(deviceId, out var state))
            {
                state.Count--;
                state.LastSeenUtc = now;
                if (state.Count > 0)
                    online = true;
                else
                    _connections.Remove(deviceId);
            }
        }

        await UpdateLastSeenAsync(deviceId, now);
        _events.PublishDeviceStatus(new DeviceStatusDto(deviceId, childId, name, platform, online, now, timeZoneId));
        _logger.LogInformation("Device {DeviceId} disconnected (online={Online})", deviceId, online);
    }

    public async Task HeartbeatAsync(Guid deviceId, DateTime utc, string? timeZoneId)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(deviceId, out var state))
                state.LastSeenUtc = DateTime.UtcNow;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.ParentalDbContext>();
        var device = await db.Devices.FindAsync(deviceId);
        if (device is null) return;
        device.LastSeenUtc = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
        if (!string.IsNullOrWhiteSpace(timeZoneId) && device.TimeZoneId != timeZoneId)
            device.TimeZoneId = timeZoneId!;
        await db.SaveChangesAsync();
    }

    private async Task UpdateLastSeenAsync(Guid deviceId, DateTime utc)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.ParentalDbContext>();
        var device = await db.Devices.FindAsync(deviceId);
        if (device is null) return;
        device.LastSeenUtc = utc;
        await db.SaveChangesAsync();
    }
}
