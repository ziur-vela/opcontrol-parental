using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed class DeviceAuthService
{
    private readonly ParentalDbContext _db;

    public DeviceAuthService(ParentalDbContext db) => _db = db;

    public async Task<Device?> ValidateAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 512)
            return null;

        var hash = SecretToken.Hash(token);
        var now = DateTime.UtcNow;
        var device = await _db.Devices
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.CredentialHash == hash
                && !d.IsRevoked
                && d.CredentialExpiresUtc != null
                && d.CredentialExpiresUtc > now, ct);
        if (device is null) return null;
        var active = await _db.Families.AsNoTracking()
            .AnyAsync(f => f.Id == device.FamilyId && f.Status == FamilyStatus.Active, ct);
        return active ? device : null;
    }

    public async Task<(Device Device, string Token)> RefreshAsync(Guid deviceId, CancellationToken ct)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId && !d.IsRevoked, ct)
            ?? throw new InvalidOperationException("Device not found.");
        var active = await _db.Families.AnyAsync(f => f.Id == device.FamilyId && f.Status == FamilyStatus.Active, ct);
        if (!active) throw new InvalidOperationException("This family is suspended.");

        var token = SecretToken.NewToken();
        device.CredentialHash = SecretToken.Hash(token);
        device.CredentialIssuedUtc = DateTime.UtcNow;
        device.CredentialExpiresUtc = DateTime.UtcNow.AddDays(365);
        await _db.SaveChangesAsync(ct);
        return (device, token);
    }
}
