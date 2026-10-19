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

public sealed record ScheduleDto(
    Guid Id,
    string Name,
    int DaysMask,
    int StartMinutes,
    int EndMinutes,
    bool IsEnabled);

public sealed record DeviceStateDto(
    Guid ChildId,
    string ChildName,
    string? ParentDisplayName,
    bool ManualLock,
    DateTime? UnlockUntilUtc,
    DateTime? BonusUntilUtc,
    int? BonusMinutes,
    string? OverridePinHash,
    IReadOnlyList<ScheduleDto> Schedules,
    DateTime ServerUtc);

public sealed record LockStateDto(
    Guid ChildId,
    bool IsLocked,
    bool ManualLock,
    DateTime? UnlockUntilUtc,
    DateTime? BonusUntilUtc,
    int? BonusMinutes,
    DateTime? EndsUtc,
    DateTime ServerUtc,
    string Reason,
    string? ScheduleName);

public sealed record RuntimeLockStateDto(
    Guid ChildId,
    string ChildName,
    string? ParentDisplayName,
    bool IsLocked,
    string Reason,
    string? ScheduleName,
    DateTime? EndsUtc,
    DateTime? NextLockUtc,
    DateTime? BonusUntilUtc,
    int? BonusMinutes,
    DateTime UpdatedUtc);

public sealed record DeviceStatusDto(
    Guid DeviceId,
    Guid ChildId,
    string Name,
    DevicePlatform Platform,
    bool Online,
    DateTime? LastSeenUtc,
    string? TimeZoneId,
    string? OsAccountName,
    string? ClientVersion);

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

public sealed record DeviceOverrideRequest(
    string Option,
    int? Minutes);

public sealed record OverrideEventDto(
    Guid Id,
    string Option,
    int? Minutes,
    DateTime CreatedUtc);

public static class DeviceGroups
{
    public static string Device(Guid deviceId) => $"device:{deviceId:N}";
    public static string Child(Guid childId) => $"child:{childId:N}";
}

public static class DeviceHubMethods
{
    public const string DeviceStateChanged = "DeviceStateChanged";
}

public sealed record PinPipeRequest(
    string Type,
    string? Pin = null,
    string? Option = null,
    int? Minutes = null,
    string? Ticket = null);

public sealed record PinPipeResponse(
    bool Ok,
    string? Error = null,
    bool LockedOut = false,
    int? RemainingSeconds = null,
    int? AttemptsRemaining = null,
    string? Ticket = null,
    RuntimeLockStateDto? State = null);
