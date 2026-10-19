using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed class DashboardEventBus
{
    public event Action<DeviceStatusDto>? DeviceStatusChanged;
    public event Action<LockStateDto>? LockStateChanged;
    public event Action<Data.Alert>? AlertChanged;

    public void PublishDeviceStatus(DeviceStatusDto status) => DeviceStatusChanged?.Invoke(status);
    public void PublishLockState(LockStateDto state) => LockStateChanged?.Invoke(state);
    public void PublishAlert(Data.Alert alert) => AlertChanged?.Invoke(alert);
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

    public async Task ConnectedAsync(Device device)
    {
        lock (_gate)
        {
            if (!_connections.TryGetValue(device.Id, out var state))
                _connections[device.Id] = state = new ConnectionState();
            state.Count++;
            state.LastSeenUtc = DateTime.UtcNow;
        }

        await UpdateLastSeenAsync(device.Id, DateTime.UtcNow);
        _events.PublishDeviceStatus(await StatusAsync(device.Id, true));
        _logger.LogInformation("Device {DeviceId} connected", device.Id);
    }

    public async Task DisconnectedAsync(Device device)
    {
        var now = DateTime.UtcNow;
        var online = false;
        lock (_gate)
        {
            if (_connections.TryGetValue(device.Id, out var state))
            {
                state.Count--;
                state.LastSeenUtc = now;
                if (state.Count > 0)
                    online = true;
                else
                    _connections.Remove(device.Id);
            }
        }

        await UpdateLastSeenAsync(device.Id, now);
        _events.PublishDeviceStatus(await StatusAsync(device.Id, online));
        _logger.LogInformation("Device {DeviceId} disconnected (online={Online})", device.Id, online);
    }

    public async Task HeartbeatAsync(Guid deviceId, DateTime utc, string? timeZoneId, string? clientVersion)
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
        if (!string.IsNullOrWhiteSpace(clientVersion) && device.ClientVersion != clientVersion)
            device.ClientVersion = clientVersion;
        await db.SaveChangesAsync();
    }

    public async Task<DeviceStatusDto> StatusAsync(Guid deviceId, bool online)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.ParentalDbContext>();
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId);
        if (device is null)
            return new DeviceStatusDto(deviceId, Guid.Empty, "", DevicePlatform.Windows, online, null, null, null, null);
        var account = await db.DeviceAccounts.AsNoTracking()
            .Where(a => a.DeviceId == deviceId)
            .Select(a => a.OsAccountName)
            .FirstOrDefaultAsync();
        return new DeviceStatusDto(device.Id, device.ChildId, device.Name, device.Platform, online, device.LastSeenUtc, device.TimeZoneId, account, device.ClientVersion);
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
