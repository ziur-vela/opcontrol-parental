using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpControl.Parental.Server.Auth;

namespace OpControl.Parental.Server.Services;

public sealed class FamilyScope
{
    private readonly IHttpContextAccessor _accessor;

    public FamilyScope(IHttpContextAccessor accessor) => _accessor = accessor;

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
}
