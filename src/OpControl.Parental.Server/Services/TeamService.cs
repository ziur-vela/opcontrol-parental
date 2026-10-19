using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed record ParentDto(Guid Id, string DisplayName, string Email, FamilyRole Role, ParentPermissions Permissions, bool IsServerAdmin, DateTime CreatedUtc);

public sealed class TeamService
{
    private readonly ParentalDbContext _db;
    private readonly ILogger<TeamService> _logger;

    public TeamService(ParentalDbContext db, ILogger<TeamService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<List<ParentDto>> ListAsync(Guid familyId, CancellationToken ct = default) =>
        await _db.Users.AsNoTracking()
            .Where(u => u.FamilyId == familyId)
            .OrderBy(u => u.RoleInFamily == FamilyRole.Owner ? 0 : 1)
            .ThenBy(u => u.DisplayName)
            .Select(u => new ParentDto(u.Id, u.DisplayName, u.Email ?? "", u.RoleInFamily,
                u.RoleInFamily == FamilyRole.Owner ? ParentPermissions.All : u.Permissions, u.IsServerAdmin, u.CreatedUtc))
            .ToListAsync(ct);

    public async Task SetPermissionsAsync(Guid familyId, Guid parentId, ParentPermissions permissions, CancellationToken ct = default)
    {
        var parent = await _db.Users.FirstOrDefaultAsync(u => u.Id == parentId && u.FamilyId == familyId, ct)
            ?? throw new InvalidOperationException("Parent not found in this family.");
        if (parent.RoleInFamily == FamilyRole.Owner)
            throw new InvalidOperationException("The owner always has every permission.");
        parent.Permissions = (permissions & ParentPermissions.All);
        parent.SecurityStamp = Guid.NewGuid().ToString();
        await _db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(Guid familyId, Guid parentId, Guid actorId, CancellationToken ct = default)
    {
        if (parentId == actorId)
            throw new InvalidOperationException("You cannot remove your own account.");
        var parent = await _db.Users.FirstOrDefaultAsync(u => u.Id == parentId && u.FamilyId == familyId, ct)
            ?? throw new InvalidOperationException("Parent not found in this family.");
        if (parent.RoleInFamily == FamilyRole.Owner)
            throw new InvalidOperationException("The family owner cannot be removed.");

        await _db.UserRoles.Where(x => x.UserId == parentId).ExecuteDeleteAsync(ct);
        await _db.UserClaims.Where(x => x.UserId == parentId).ExecuteDeleteAsync(ct);
        await _db.UserLogins.Where(x => x.UserId == parentId).ExecuteDeleteAsync(ct);
        await _db.UserTokens.Where(x => x.UserId == parentId).ExecuteDeleteAsync(ct);
        _db.Users.Remove(parent);
        await _db.SaveChangesAsync(ct);
        _logger.LogWarning("Parent {ParentId} removed from family {FamilyId} by {ActorId}", parentId, familyId, actorId);
    }
}
