// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

/// <summary>Per-parent dashboard preferences stored on the server.</summary>
public sealed class ParentPreferenceService
{
    private readonly ParentalDbContext _db;

    public ParentPreferenceService(ParentalDbContext db) => _db = db;

    public async Task<bool> GetEnterToSendAsync(Guid familyId, Guid parentId, CancellationToken ct = default)
    {
        var value = await _db.Users.AsNoTracking()
            .Where(u => u.Id == parentId && u.FamilyId == familyId)
            .Select(u => (bool?)u.EnterToSend)
            .FirstOrDefaultAsync(ct);
        return value ?? true;
    }

    public async Task SaveEnterToSendAsync(Guid familyId, Guid parentId, bool enabled, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == parentId && u.FamilyId == familyId, ct);
        if (user is null) return;
        if (user.EnterToSend == enabled) return;
        user.EnterToSend = enabled;
        await _db.SaveChangesAsync(ct);
    }
}
