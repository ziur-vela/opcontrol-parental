using Microsoft.AspNetCore.Authorization;
using OpControl.Parental.Contracts;

namespace OpControl.Parental.Server.Auth;

public static class ParentalPolicies
{
    public const string Owner = "FamilyOwner";
    public const string ServerAdmin = "ServerAdmin";
    public const string LockUnlockBonus = "Permission.LockUnlockBonus";
    public const string WebsiteRules = "Permission.WebsiteRules";
    public const string AppRules = "Permission.AppRules";
    public const string SchedulesLimits = "Permission.SchedulesLimits";
    public const string ViewReports = "Permission.ViewReports";
    public const string ManageDevices = "Permission.ManageDevices";
    public const string ManagePin = "Permission.ManagePin";
    public const string RemoteView = "Permission.RemoteView";
    public const string Messaging = "Permission.Messaging";

    public static void AddParentalPolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(Owner, policy => policy.RequireAssertion(ctx =>
            string.Equals(ctx.User.FindFirst(ParentalClaims.FamilyRole)?.Value, FamilyRole.Owner.ToString(), StringComparison.OrdinalIgnoreCase)));
        options.AddPolicy(ServerAdmin, policy => policy.RequireAssertion(ctx =>
            string.Equals(ctx.User.FindFirst(ParentalClaims.ServerAdmin)?.Value, "true", StringComparison.OrdinalIgnoreCase)));

        AddPermission(options, LockUnlockBonus, ParentPermissions.LockUnlockBonus);
        AddPermission(options, WebsiteRules, ParentPermissions.WebsiteRules);
        AddPermission(options, AppRules, ParentPermissions.AppRules);
        AddPermission(options, SchedulesLimits, ParentPermissions.SchedulesLimits);
        AddPermission(options, ViewReports, ParentPermissions.ViewReports);
        AddPermission(options, ManageDevices, ParentPermissions.ManageDevices);
        AddPermission(options, ManagePin, ParentPermissions.ManagePin);
        AddPermission(options, RemoteView, ParentPermissions.RemoteView);
        AddPermission(options, Messaging, ParentPermissions.Messaging);
    }

    private static void AddPermission(AuthorizationOptions options, string name, ParentPermissions permission)
    {
        options.AddPolicy(name, policy => policy.RequireAssertion(ctx =>
        {
            if (string.Equals(ctx.User.FindFirst(ParentalClaims.FamilyRole)?.Value, FamilyRole.Owner.ToString(), StringComparison.OrdinalIgnoreCase))
                return true;
            return int.TryParse(ctx.User.FindFirst(ParentalClaims.Permissions)?.Value, out var value)
                && ((ParentPermissions)value & permission) == permission;
        }));
    }
}
