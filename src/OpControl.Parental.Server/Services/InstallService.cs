using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed class InstallService
{
    private readonly ParentalDbContext _db;
    private readonly IDataProtector _protector;
    private readonly IHttpContextAccessor _http;
    private readonly IConfiguration _configuration;
    private readonly ILogger<InstallService> _logger;

    public InstallService(ParentalDbContext db, IDataProtectionProvider protection, IHttpContextAccessor http, IConfiguration configuration, ILogger<InstallService> logger)
    {
        _db = db;
        _protector = protection.CreateProtector("OpControl.Parental.InstallCode");
        _http = http;
        _configuration = configuration;
        _logger = logger;
    }

    public string BaseUrl
    {
        get
        {
            var configured = _configuration["PublicBaseUrl"];
            if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');
            var request = _http.HttpContext?.Request;
            if (request is not null && request.Host.HasValue)
                return $"{request.Scheme}://{request.Host.Value}".TrimEnd('/');
            return "https://parental.opcontrol.net";
        }
    }

    public async Task<InstallLinkCreatedDto> CreateAsync(Guid familyId, Guid childId, Guid parentId, DevicePlatform platform, CancellationToken ct = default)
    {
        if (platform != DevicePlatform.Windows)
            throw new EnrollmentException("Linux and Android installers are coming soon.");
        var child = await _db.Children.AsNoTracking().FirstOrDefaultAsync(c => c.Id == childId && c.FamilyId == familyId, ct)
            ?? throw new EnrollmentException("Child not found in this family.");
        var settings = await _db.FamilySettings.FirstOrDefaultAsync(s => s.FamilyId == familyId, ct);
        var minutes = Math.Clamp(settings?.InstallLinkMinutes ?? 1440, 5, 10080);
        var now = DateTime.UtcNow;

        var rawToken = SecretToken.NewCode(8);
        var token = $"{rawToken[..4]}-{rawToken[4..]}";
        var code = SecretToken.NewCode(10);
        var link = new InstallLink
        {
            FamilyId = familyId,
            ChildId = childId,
            Platform = platform,
            TokenHash = SecretToken.Hash(SecretToken.NormalizeCode(token)),
            CodeHash = SecretToken.Hash(SecretToken.NormalizeCode(code)),
            CodeProtected = _protector.Protect(code),
            CreatedByParentId = parentId,
            CreatedUtc = now,
            ExpiresUtc = now.AddMinutes(minutes)
        };
        _db.InstallLinks.Add(link);
        _db.EnrollmentCodes.Add(new EnrollmentCode
        {
            FamilyId = familyId,
            ChildId = childId,
            CreatedByParentId = parentId,
            CodeHash = link.CodeHash,
            CreatedUtc = link.CreatedUtc,
            ExpiresUtc = link.ExpiresUtc
        });
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Install link {LinkId} created for child {ChildId}", link.Id, childId);
        return new InstallLinkCreatedDto(link.Id, childId, child.Name, platform, token, $"{BaseUrl}/i/{token}", code, link.CreatedUtc, link.ExpiresUtc);
    }

    public async Task<List<InstallLinkDto>> ListAsync(Guid familyId, CancellationToken ct = default)
    {
        var rows = await _db.InstallLinks.AsNoTracking()
            .Where(l => l.FamilyId == familyId)
            .OrderByDescending(l => l.CreatedUtc)
            .Take(25)
            .ToListAsync(ct);
        var childIds = rows.Select(r => r.ChildId).Distinct().ToList();
        var children = await _db.Children.AsNoTracking()
            .Where(c => childIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var deviceIds = rows.Where(r => r.UsedDeviceId is not null).Select(r => r.UsedDeviceId!.Value).Distinct().ToList();
        var devices = await _db.Devices.AsNoTracking()
            .Where(d => deviceIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        return rows.Select(r => new InstallLinkDto(
            r.Id,
            r.ChildId,
            children.GetValueOrDefault(r.ChildId, ""),
            r.Platform,
            r.CreatedUtc,
            r.ExpiresUtc,
            r.UsedUtc,
            r.RevokedUtc,
            r.UsedDeviceId is { } id ? devices.GetValueOrDefault(id) : null)).ToList();
    }

    public async Task<bool> RevokeAsync(Guid familyId, Guid linkId, CancellationToken ct = default)
    {
        var link = await _db.InstallLinks.FirstOrDefaultAsync(l => l.Id == linkId && l.FamilyId == familyId, ct);
        if (link is null) return false;
        link.RevokedUtc ??= DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<InstallLink?> FindAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = SecretToken.Hash(SecretToken.NormalizeCode(token));
        return await _db.InstallLinks.AsNoTracking().FirstOrDefaultAsync(l => l.TokenHash == hash, ct);
    }

    public async Task<InstallLinkStatusDto> StatusAsync(string token, CancellationToken ct = default)
    {
        var link = await FindAsync(token, ct);
        if (link is null)
            return new InstallLinkStatusDto("invalid", null, null, DateTime.MinValue, "This install link is not valid.");
        if (link.RevokedUtc is not null)
            return new InstallLinkStatusDto("revoked", null, null, link.ExpiresUtc, "This install link was revoked. Ask the parent to create a new one.");
        if (link.ExpiresUtc <= DateTime.UtcNow && link.UsedUtc is null)
            return new InstallLinkStatusDto("expired", null, null, link.ExpiresUtc, "This install link has expired. Ask the parent to create a new one.");

        var child = await _db.Children.AsNoTracking().FirstOrDefaultAsync(c => c.Id == link.ChildId, ct);
        if (link.UsedUtc is not null)
        {
            var device = link.UsedDeviceId is { } id
                ? await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct)
                : null;
            return new InstallLinkStatusDto("connected", child?.Name, device?.Name, link.ExpiresUtc);
        }
        return new InstallLinkStatusDto("pending", child?.Name, null, link.ExpiresUtc);
    }

    public string? UnprotectCode(InstallLink link)
    {
        try { return _protector.Unprotect(link.CodeProtected); }
        catch { return null; }
    }

    public async Task MarkUsedAsync(string codeHash, Guid deviceId, CancellationToken ct = default)
    {
        var link = await _db.InstallLinks.FirstOrDefaultAsync(l => l.CodeHash == codeHash && l.UsedUtc == null, ct);
        if (link is null) return;
        link.UsedUtc = DateTime.UtcNow;
        link.UsedDeviceId = deviceId;
        await _db.SaveChangesAsync(ct);
    }
}
