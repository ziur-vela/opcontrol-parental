// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Services;
using System.Text.Encodings.Web;

namespace OpControl.Parental.Server.Auth;

public static class ParentalClaims
{
    public const string FamilyId = "family_id";
    public const string FamilyRole = "family_role";
    public const string DisplayName = "display_name";
    public const string DeviceId = "device_id";
    public const string ChildId = "child_id";
    public const string Permissions = "permissions";
    public const string ServerAdmin = "server_admin";
    public const string PreferredLanguage = "preferred_language";
}

public sealed class ParentUserClaimsPrincipalFactory : Microsoft.AspNetCore.Identity.UserClaimsPrincipalFactory<ParentUser, Microsoft.AspNetCore.Identity.IdentityRole<Guid>>
{
    public ParentUserClaimsPrincipalFactory(
        Microsoft.AspNetCore.Identity.UserManager<ParentUser> userManager,
        Microsoft.AspNetCore.Identity.RoleManager<Microsoft.AspNetCore.Identity.IdentityRole<Guid>> roleManager,
        IOptions<Microsoft.AspNetCore.Identity.IdentityOptions> options)
        : base(userManager, roleManager, options) { }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ParentUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(ParentalClaims.FamilyId, user.FamilyId.ToString()));
        identity.AddClaim(new Claim(ParentalClaims.FamilyRole, user.RoleInFamily.ToString()));
        identity.AddClaim(new Claim(ParentalClaims.DisplayName, user.DisplayName));
        identity.AddClaim(new Claim(ParentalClaims.Permissions, ((int)(user.RoleInFamily == FamilyRole.Owner ? ParentPermissions.All : user.Permissions)).ToString()));
        identity.AddClaim(new Claim(ParentalClaims.ServerAdmin, user.IsServerAdmin ? "true" : "false"));
        if (!string.IsNullOrWhiteSpace(user.PreferredLanguage))
            identity.AddClaim(new Claim(ParentalClaims.PreferredLanguage, user.PreferredLanguage));
        return identity;
    }
}

public sealed class DeviceTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "DeviceToken";

    private readonly ILogger<DeviceTokenAuthenticationHandler> _logger;

    public DeviceTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
        _logger = logger.CreateLogger<DeviceTokenAuthenticationHandler>();
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ReadToken();
        if (string.IsNullOrWhiteSpace(token))
            return AuthenticateResult.NoResult();

        var service = Context.RequestServices.GetRequiredService<DeviceAuthService>();
        var device = await service.ValidateAsync(token, Context.RequestAborted);
        if (device is null)
            return AuthenticateResult.Fail("Invalid device credential");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, device.Id.ToString()),
            new(ClaimTypes.Name, device.Name),
            new(ParentalClaims.DeviceId, device.Id.ToString()),
            new(ParentalClaims.FamilyId, device.FamilyId.ToString()),
            new(ParentalClaims.ChildId, device.ChildId.ToString())
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return AuthenticateResult.Success(ticket);
    }

    private string? ReadToken()
    {
        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();

        if (Request.Query.TryGetValue("access_token", out var query) && query.Count > 0)
            return query[0];

        return null;
    }
}
