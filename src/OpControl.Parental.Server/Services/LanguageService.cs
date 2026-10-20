using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed class LanguageService
{
    private readonly ParentalDbContext _db;
    private readonly LockService _locks;

    public LanguageService(ParentalDbContext db, LockService locks)
    {
        _db = db;
        _locks = locks;
    }

    public async Task SetParentLanguageAsync(Guid parentId, string? language, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == parentId, ct);
        if (user is null) return;
        user.PreferredLanguage = AppCulture.Normalize(language);
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetFamilyLanguageAsync(Guid familyId, string? language, CancellationToken ct = default)
    {
        var family = await _db.Families.FirstOrDefaultAsync(f => f.Id == familyId, ct);
        if (family is null) return;
        family.DefaultLanguage = AppCulture.Normalize(language);
        await _db.SaveChangesAsync(ct);
        await PublishFamilyDevicesAsync(familyId, ct);
    }

    public async Task SetDeviceLanguageAsync(Guid familyId, Guid deviceId, string? language, CancellationToken ct = default)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId && d.FamilyId == familyId, ct);
        if (device is null) return;
        device.ClientLanguage = string.IsNullOrWhiteSpace(language) ? null : AppCulture.Normalize(language);
        await _db.SaveChangesAsync(ct);
        await _locks.PublishDeviceStateAsync(device.FamilyId, device.ChildId, ct);
    }

    private async Task PublishFamilyDevicesAsync(Guid familyId, CancellationToken ct)
    {
        var childIds = await _db.Children.AsNoTracking().Where(c => c.FamilyId == familyId).Select(c => c.Id).ToListAsync(ct);
        foreach (var childId in childIds)
            await _locks.PublishDeviceStateAsync(familyId, childId, ct);
    }
}
