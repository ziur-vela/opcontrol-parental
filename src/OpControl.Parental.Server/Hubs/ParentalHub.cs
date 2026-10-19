using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Auth;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Services;

namespace OpControl.Parental.Server.Hubs;

[Authorize(AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName)]
public sealed class ParentalHub : Hub
{
    private readonly ParentalDbContext _db;
    private readonly DevicePresenceService _presence;
    private readonly LockService _locks;
    private readonly ILogger<ParentalHub> _logger;

    public ParentalHub(ParentalDbContext db, DevicePresenceService presence, LockService locks, ILogger<ParentalHub> logger)
    {
        _db = db;
        _presence = presence;
        _locks = locks;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var device = await CurrentDeviceAsync();
        if (device is null)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, DeviceGroups.Device(device.Id));
        await Groups.AddToGroupAsync(Context.ConnectionId, DeviceGroups.Child(device.ChildId));
        await _presence.ConnectedAsync(device);

        var state = await _locks.GetDeviceStateAsync(device.FamilyId, device.ChildId);
        await Clients.Caller.SendAsync(DeviceHubMethods.DeviceStateChanged, state);

        _logger.LogInformation("Device {DeviceId} ({DeviceName}) connected on {ConnectionId}", device.Id, device.Name, Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var device = await CurrentDeviceAsync();
        if (device is not null)
        {
            await _presence.DisconnectedAsync(device);
            _logger.LogInformation("Device {DeviceId} disconnected from {ConnectionId}", device.Id, Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public Task Heartbeat(DateTime utc, string? timeZoneId, string? clientVersion)
    {
        var value = Context.User?.FindFirstValue(ParentalClaims.DeviceId);
        return Guid.TryParse(value, out var deviceId)
            ? _presence.HeartbeatAsync(deviceId, utc, timeZoneId, clientVersion)
            : Task.CompletedTask;
    }

    private async Task<Device?> CurrentDeviceAsync()
    {
        var value = Context.User?.FindFirstValue(ParentalClaims.DeviceId);
        if (!Guid.TryParse(value, out var deviceId))
            return null;
        var device = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId && !d.IsRevoked);
        if (device is null) return null;
        var active = await _db.Families.AsNoTracking().AnyAsync(f => f.Id == device.FamilyId && f.Status == FamilyStatus.Active);
        return active ? device : null;
    }
}
