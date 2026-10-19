using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Auth;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed class PermissionDeniedException(string message) : Exception(message);

public sealed record FamilyAccess(
    Guid ParentId,
    Guid FamilyId,
    string DisplayName,
    FamilyRole Role,
    ParentPermissions Permissions,
    bool IsServerAdmin,
    FamilyStatus Status)
{
    public bool IsOwner => Role == FamilyRole.Owner;

    public bool Has(ParentPermissions permission) =>
        IsOwner || (Permissions & permission) == permission;

    public void Require(ParentPermissions permission)
    {
        if (!Has(permission))
            throw new PermissionDeniedException($"This action needs the {permission} permission.");
    }
}

public sealed class FamilyScope
{
    private readonly IHttpContextAccessor _accessor;
    private readonly ParentalDbContext _db;
    private FamilyAccess? _access;

    public FamilyScope(IHttpContextAccessor accessor, ParentalDbContext db)
    {
        _accessor = accessor;
        _db = db;
    }

    public Guid FamilyId
    {
        get
        {
            var value = _accessor.HttpContext?.User.FindFirstValue(ParentalClaims.FamilyId);
            return Guid.TryParse(value, out var id) ? id : throw new UnauthorizedAccessException("Missing family scope.");
        }
    }

    public Guid ParentId
    {
        get
        {
            var value = _accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(value, out var id) ? id : throw new UnauthorizedAccessException("Missing parent identity.");
        }
    }

    public string DisplayName => _accessor.HttpContext?.User.FindFirstValue(ParentalClaims.DisplayName) ?? "Parent";

    public async Task<FamilyAccess> AccessAsync(CancellationToken ct = default)
    {
        if (_access is not null) return _access;
        var value = _accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(value, out var parentId))
            throw new PermissionDeniedException("Sign in again.");

        var parent = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == parentId, ct);
        if (parent is null)
            throw new PermissionDeniedException("This account is no longer active. Sign in again.");

        var family = await _db.Families.AsNoTracking().FirstOrDefaultAsync(f => f.Id == parent.FamilyId, ct);
        if (family is null || family.Status == FamilyStatus.Suspended)
            throw new PermissionDeniedException("This family is suspended. Contact the server administrator.");

        _access = new FamilyAccess(parent.Id, parent.FamilyId, parent.DisplayName, parent.RoleInFamily,
            parent.RoleInFamily == FamilyRole.Owner ? ParentPermissions.All : parent.Permissions,
            parent.IsServerAdmin, family.Status);
        return _access;
    }

    public async Task RequireAsync(ParentPermissions permission, CancellationToken ct = default) =>
        (await AccessAsync(ct)).Require(permission);
}
