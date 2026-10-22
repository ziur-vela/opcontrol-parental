// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Auth;

/// <summary>
/// Rebuilds the parent cookie claims when the database permissions or role differ from the
/// cookie issued at sign-in. This lets permission and role changes take effect without a
/// logout, including the Messaging flag added after an older cookie was issued.
/// </summary>
public static class ParentClaimsRefresher
{
    public static ClaimsPrincipal? RefreshIfNeeded(ClaimsPrincipal principal, ParentUser user)
    {
        var expectedPermissions = user.RoleInFamily == FamilyRole.Owner ? ParentPermissions.All : user.Permissions;
        var expectedRole = user.RoleInFamily.ToString();
        var expectedAdmin = user.IsServerAdmin ? "true" : "false";
        var expectedLanguage = user.PreferredLanguage ?? "";

        var currentRole = principal.FindFirst(ParentalClaims.FamilyRole)?.Value;
        var currentPermissions = principal.FindFirst(ParentalClaims.Permissions)?.Value;
        var currentDisplay = principal.FindFirst(ParentalClaims.DisplayName)?.Value;
        var currentAdmin = principal.FindFirst(ParentalClaims.ServerAdmin)?.Value;
        var currentLanguage = principal.FindFirst(ParentalClaims.PreferredLanguage)?.Value ?? "";

        var needsRefresh = !string.Equals(currentRole, expectedRole, StringComparison.Ordinal)
            || !string.Equals(currentPermissions, ((int)expectedPermissions).ToString(), StringComparison.Ordinal)
            || !string.Equals(currentDisplay, user.DisplayName, StringComparison.Ordinal)
            || !string.Equals(currentAdmin, expectedAdmin, StringComparison.Ordinal)
            || !string.Equals(currentLanguage, expectedLanguage, StringComparison.Ordinal);
        if (!needsRefresh) return null;

        var identity = principal.Identities.FirstOrDefault();
        var authenticationType = identity?.AuthenticationType ?? IdentityConstants.ApplicationScheme;
        var claims = principal.Claims
            .Where(c => c.Type is not (ParentalClaims.FamilyRole
                or ParentalClaims.Permissions
                or ParentalClaims.DisplayName
                or ParentalClaims.ServerAdmin
                or ParentalClaims.PreferredLanguage))
            .ToList();
        claims.Add(new Claim(ParentalClaims.FamilyRole, expectedRole));
        claims.Add(new Claim(ParentalClaims.Permissions, ((int)expectedPermissions).ToString()));
        claims.Add(new Claim(ParentalClaims.DisplayName, user.DisplayName));
        claims.Add(new Claim(ParentalClaims.ServerAdmin, expectedAdmin));
        if (!string.IsNullOrWhiteSpace(expectedLanguage))
            claims.Add(new Claim(ParentalClaims.PreferredLanguage, expectedLanguage));

        var refreshed = new ClaimsIdentity(claims, authenticationType,
            identity?.NameClaimType ?? ClaimTypes.Name,
            identity?.RoleClaimType ?? ClaimTypes.Role);
        return new ClaimsPrincipal(refreshed);
    }
}
