using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed record CameraConsentStatusDto(bool Accepted, int Version, string Text);

public sealed class CameraConsentService
{
    public const int CurrentVersion = 1;
    public const string Text = """
CAMERA SUPERVISION ACKNOWLEDGEMENT (v1)

I confirm that I am the parent or legal guardian of the child associated with this device, or that I have the family owner's express permission to view this camera.

I will use this camera view only for lawful parental supervision of that child. I will not use it to monitor any other person, and I will not use it in any place where a person has a reasonable expectation of privacy, including bedrooms, bathrooms, changing areas or similar private spaces. I will not record, share, publish or use the feed for harassment, stalking, intimidation or any unlawful purpose.

I understand that camera access is logged with my account, device and time, and that misuse can result in loss of access and legal action. I will comply with all applicable privacy, consent and monitoring laws in my jurisdiction. This notice is not legal advice; I am responsible for confirming that my use is lawful.

By selecting "I agree", I confirm that I have read and accept these conditions.
""";

    private readonly ParentalDbContext _db;

    public CameraConsentService(ParentalDbContext db) => _db = db;

    public async Task<bool> HasAcceptedAsync(Guid parentId, CancellationToken ct = default) =>
        await _db.CameraConsents.AsNoTracking().AnyAsync(c => c.ParentId == parentId && c.Version == CurrentVersion, ct);

    public async Task<CameraConsentStatusDto> StatusAsync(Guid parentId, CancellationToken ct = default) =>
        new(await HasAcceptedAsync(parentId, ct), CurrentVersion, Text);

    public async Task AcceptAsync(Guid familyId, Guid parentId, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        if (await HasAcceptedAsync(parentId, ct)) return;
        _db.CameraConsents.Add(new CameraConsent
        {
            FamilyId = familyId,
            ParentId = parentId,
            Version = CurrentVersion,
            AcceptedUtc = DateTime.UtcNow,
            IpAddress = ipAddress is { Length: > 64 } ? ipAddress[..64] : ipAddress,
            UserAgent = userAgent is { Length: > 512 } ? userAgent[..512] : userAgent
        });
        await _db.SaveChangesAsync(ct);
    }
}
