// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Security.Claims;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Auth;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Services;
using Xunit;

namespace OpControl.Parental.Tests;

public class PermissionRefreshTests
{
    private static ClaimsPrincipal StalePrincipal(ParentUser user, string permissions)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ParentalClaims.FamilyId, user.FamilyId.ToString()),
            new Claim(ParentalClaims.Permissions, permissions),
            new Claim(ParentalClaims.DisplayName, "Old name"),
            new Claim(ParentalClaims.ServerAdmin, "false")
        ], "test");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public void Stale_owner_cookie_is_refreshed_to_all_permissions_and_role()
    {
        var user = new ParentUser
        {
            FamilyId = Guid.NewGuid(),
            DisplayName = "Hendry",
            RoleInFamily = FamilyRole.Owner,
            Permissions = ParentPermissions.None,
            IsServerAdmin = true
        };
        var stale = StalePrincipal(user, "127");
        var refreshed = ParentClaimsRefresher.RefreshIfNeeded(stale, user);

        Assert.NotNull(refreshed);
        Assert.Equal("Owner", refreshed.FindFirst(ParentalClaims.FamilyRole)?.Value);
        Assert.Equal(((int)ParentPermissions.All).ToString(), refreshed.FindFirst(ParentalClaims.Permissions)?.Value);
        Assert.Equal("Hendry", refreshed.FindFirst(ParentalClaims.DisplayName)?.Value);
        Assert.Equal("true", refreshed.FindFirst(ParentalClaims.ServerAdmin)?.Value);
        Assert.Null(ParentClaimsRefresher.RefreshIfNeeded(refreshed, user));
    }

    [Fact]
    public void Stale_co_parent_cookie_picks_up_the_messaging_flag()
    {
        var user = new ParentUser
        {
            FamilyId = Guid.NewGuid(),
            DisplayName = "Parent One",
            RoleInFamily = FamilyRole.Parent,
            Permissions = ParentPermissions.Messaging
        };
        var stale = StalePrincipal(user, "0");
        var refreshed = ParentClaimsRefresher.RefreshIfNeeded(stale, user);

        Assert.NotNull(refreshed);
        Assert.Equal("Parent", refreshed.FindFirst(ParentalClaims.FamilyRole)?.Value);
        Assert.Equal(((int)ParentPermissions.Messaging).ToString(), refreshed.FindFirst(ParentalClaims.Permissions)?.Value);
        Assert.Null(ParentClaimsRefresher.RefreshIfNeeded(refreshed, user));
    }

    [Fact]
    public void Owners_always_pass_every_permission_check()
    {
        var access = new FamilyAccess(Guid.NewGuid(), Guid.NewGuid(), "Hendry", FamilyRole.Owner,
            ParentPermissions.None, false, FamilyStatus.Active);
        Assert.True(access.Has(ParentPermissions.Messaging));
        Assert.True(access.Has(ParentPermissions.RemoteView));
        access.Require(ParentPermissions.Messaging);
        access.Require(ParentPermissions.ManagePin);
    }
}
