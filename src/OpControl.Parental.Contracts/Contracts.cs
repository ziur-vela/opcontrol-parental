namespace OpControl.Parental.Contracts;

public enum DevicePlatform
{
    Windows = 0,
    Linux = 1,
    Android = 2
}

public enum FamilyRole
{
    Owner = 0,
    Parent = 1
}

public enum RuleMode
{
    Always = 0,
    Scheduled = 1
}

public enum RuleAction
{
    Blocked = 0,
    Allowed = 1
}

public sealed record LockStateDto(
    Guid ChildId,
    bool IsLocked,
    bool ManualLock,
    DateTime? UnlockUntilUtc,
    DateTime? BonusUntilUtc,
    DateTime ServerUtc,
    string Reason);

public sealed record DeviceStatusDto(
    Guid DeviceId,
    Guid ChildId,
    string Name,
    DevicePlatform Platform,
    bool Online,
    DateTime? LastSeenUtc,
    string? TimeZoneId);

public sealed record EnrollRequest(
    string Code,
    string DeviceName,
    DevicePlatform Platform,
    string? OsVersion,
    string? TimeZoneId,
    string? OsAccountName);

public sealed record EnrollResponse(
    Guid DeviceId,
    Guid ChildId,
    string DeviceName,
    string Credential,
    DateTime CredentialExpiresUtc,
    LockStateDto LockState);

public sealed record DeviceRefreshResponse(
    string Credential,
    DateTime CredentialExpiresUtc);

public sealed record HeartbeatRequest(
    DateTime Utc,
    string? TimeZoneId);

public static class DeviceGroups
{
    public static string Device(Guid deviceId) => $"device:{deviceId:N}";
    public static string Child(Guid childId) => $"child:{childId:N}";
}

public static class DeviceHubMethods
{
    public const string LockStateChanged = "LockStateChanged";
}
