using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Hubs;

namespace OpControl.Parental.Server.Services;

public sealed class UpdateService
{
    private readonly ParentalDbContext _db;
    private readonly ReleaseCatalog _catalog;
    private readonly IHubContext<ParentalHub> _hub;
    private readonly AlertService _alerts;
    private readonly DashboardEventBus _events;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(ParentalDbContext db, ReleaseCatalog catalog, IHubContext<ParentalHub> hub, AlertService alerts, DashboardEventBus events, ILogger<UpdateService> logger)
    {
        _db = db;
        _catalog = catalog;
        _hub = hub;
        _alerts = alerts;
        _events = events;
        _logger = logger;
    }

    public UpdateInfoDto? BuildInfo(Device device)
    {
        var current = _catalog.Current();
        if (current?.MsiPath is null) return null;
        var installed = ReleaseCatalog.ParseVersion(device.ClientVersion ?? "0.0.0");
        var available = ReleaseCatalog.ParseVersion(current.Version);
        var requested = device.UpdateTargetVersion is not null;
        if (!requested && available <= installed) return null;
        return new UpdateInfoDto(
            current.Version,
            device.ClientVersion,
            current.Sha256 ?? "",
            current.SizeBytes,
            $"/api/device/update/download?version={Uri.EscapeDataString(current.Version)}",
            current.PublishedUtc,
            _catalog.Previous()?.Version);
    }

    public async Task<DeviceUpdateEvent> RequestAsync(Guid familyId, Guid deviceId, Guid? parentId, CancellationToken ct = default)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId && d.FamilyId == familyId && !d.IsRevoked, ct)
            ?? throw new InvalidOperationException("Device not found in this family.");
        var current = _catalog.Current() ?? throw new InvalidOperationException("No client release is published on the server yet.");
        var now = DateTime.UtcNow;
        device.UpdateRequestedUtc = now;
        device.UpdateTargetVersion = current.Version;
        device.UpdateStatus = "requested";
        device.UpdateStatusUtc = now;
        var updateEvent = new DeviceUpdateEvent
        {
            FamilyId = familyId,
            ChildId = device.ChildId,
            DeviceId = device.Id,
            FromVersion = device.ClientVersion ?? "unknown",
            ToVersion = current.Version,
            Status = "requested",
            Detail = parentId is null ? "Requested by server" : "Requested from the dashboard",
            CreatedUtc = now
        };
        _db.DeviceUpdateEvents.Add(updateEvent);
        await _db.SaveChangesAsync(ct);

        var info = new UpdateInfoDto(
            current.Version,
            device.ClientVersion,
            current.Sha256 ?? "",
            current.SizeBytes,
            $"/api/device/update/download?version={Uri.EscapeDataString(current.Version)}",
            current.PublishedUtc,
            _catalog.Previous()?.Version);
        await _hub.Clients.Group(DeviceGroups.Device(device.Id)).SendAsync(DeviceHubMethods.UpdateRequested, info, ct);
        _events.PublishDeviceStatus(await StatusAsync(device.Id, false, ct));
        _logger.LogInformation("Update {Version} requested for device {DeviceId}", current.Version, device.Id);
        return updateEvent;
    }

    public async Task<DeviceUpdateEvent> RecordHealthAsync(Device device, DeviceUpdateHealthRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var status = (request.Status ?? "").Trim().ToLowerInvariant();
        if (status is not ("healthy" or "failed" or "rolledback" or "downloading" or "installing" or "verifying"))
            status = "unknown";
        if (status.Length > 40) status = status[..40];

        var from = device.ClientVersion ?? "unknown";
        if (status == "healthy" && !string.IsNullOrWhiteSpace(request.Version))
        {
            device.ClientVersion = request.Version.Length > 40 ? request.Version[..40] : request.Version;
        }
        if (request.PreviousVersion is { Length: > 0 and <= 40 })
            device.PreviousClientVersion = request.PreviousVersion;
        device.UpdateStatus = status;
        device.UpdateStatusUtc = now;
        device.LastUpdateCheckUtc = now;
        if (status is "healthy" or "rolledback" or "failed")
        {
            device.UpdateRequestedUtc = null;
            device.UpdateTargetVersion = null;
        }

        var updateEvent = new DeviceUpdateEvent
        {
            FamilyId = device.FamilyId,
            ChildId = device.ChildId,
            DeviceId = device.Id,
            FromVersion = from,
            ToVersion = string.IsNullOrWhiteSpace(request.Version) ? device.ClientVersion ?? "unknown" : request.Version,
            Status = status,
            Detail = request.Detail is { Length: > 500 } ? request.Detail[..500] : request.Detail,
            CreatedUtc = now
        };
        _db.DeviceUpdateEvents.Add(updateEvent);
        await _db.SaveChangesAsync(ct);

        if (status is "failed" or "rolledback")
        {
            await _alerts.AddAsync(device.FamilyId, device.ChildId, device.Id, "update-" + status,
                status == "rolledback" ? "Client update rolled back" : "Client update failed",
                $"{device.Name}: {(string.IsNullOrWhiteSpace(request.Detail) ? "the client did not report healthy" : request.Detail)}",
                TimeSpan.FromHours(1), ct);
        }
        _events.PublishDeviceStatus(await StatusAsync(device.Id, false, ct));
        return updateEvent;
    }

    public async Task<DeviceStatusDto> StatusAsync(Guid deviceId, bool online, CancellationToken ct)
    {
        var device = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null) return new DeviceStatusDto(deviceId, Guid.Empty, "", DevicePlatform.Windows, online, null, null, null, null);
        var account = await _db.DeviceAccounts.AsNoTracking()
            .Where(a => a.DeviceId == deviceId)
            .Select(a => a.OsAccountName)
            .FirstOrDefaultAsync(ct);
        return new DeviceStatusDto(device.Id, device.ChildId, device.Name, device.Platform, online, device.LastSeenUtc, device.TimeZoneId, account, device.ClientVersion);
    }
}
