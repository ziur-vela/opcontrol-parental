// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed record CameraConsentStatusDto(bool Accepted, int Version, string Text);

public sealed class CameraConsentService
{
    public const int CurrentVersion = 1;
    public static string Text => AppText.Get("Consent.Text");


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
