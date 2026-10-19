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

public enum FamilyStatus
{
    Active = 0,
    Suspended = 1
}

[Flags]
public enum ParentPermissions
{
    None = 0,
    LockUnlockBonus = 1,
    WebsiteRules = 2,
    AppRules = 4,
    SchedulesLimits = 8,
    ViewReports = 16,
    ManageDevices = 32,
    ManagePin = 64,
    All = LockUnlockBonus | WebsiteRules | AppRules | SchedulesLimits | ViewReports | ManageDevices | ManagePin
}

public enum InvitationKind
{
    CoParent = 0,
    NewFamily = 1
}

public enum RuleMode
{
    Always = 0,
    Scheduled = 1
}

public enum RuleAction
{
    Blocked = 0,
    Allowed = 1,
    Removed = 2
}

public enum ItemRuleType
{
    Website = 0,
    App = 1
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
    IReadOnlyList<WebsiteRuleDto> WebsiteRules,
    IReadOnlyList<AppRuleDto> AppRules,
    IReadOnlyList<ItemBonusDto> ItemBonuses,
    IReadOnlyList<CategoryToggleDto> Categories,
    DateTime ServerUtc,
    IReadOnlyList<AppDailyUsageDto>? AppUsage = null,
    DailyLimitDto? DailyLimit = null,
    bool SpokenWarnings = false);

public sealed record DailyLimitDto(
    bool Enabled,
    int TodayMinutes,
    int UsedTodaySeconds,
    DateTime DayStartUtc,
    DateTime DayEndUtc);

public sealed record AppDailyUsageDto(
    string Identity,
    int UsedTodaySeconds,
    int? LimitMinutes,
    bool AlwaysAllowed);

public sealed record WebsiteRuleDto(
    Guid Id,
    string Domain,
    bool IsBlocked,
    RuleMode Mode,
    Guid? ScheduleId,
    DateTime UpdatedUtc);

public sealed record AppRuleDto(
    Guid Id,
    string AppIdentity,
    string DisplayName,
    string? Publisher,
    bool IsBlocked,
    RuleMode Mode,
    Guid? ScheduleId,
    DateTime UpdatedUtc,
    int? DailyLimitMinutes = null,
    bool AlwaysAllowed = false);

public sealed record ItemBonusDto(
    string RuleType,
    string Identity,
    DateTime UntilUtc,
    int Minutes);

public sealed record CategoryToggleDto(
    string Key,
    string Name,
    bool Enabled,
    int Version);

public sealed record CategoryDeltaDto(
    string Key,
    string Name,
    int Version,
    int PreviousVersion,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    bool Reset);

public sealed record VisitEventDto(
    string Domain,
    DateTime VisitedUtc,
    bool Blocked);

public sealed record VisitBatchRequest(
    IReadOnlyList<VisitEventDto> Visits);

public sealed record AppInventoryItemDto(
    string Identity,
    string DisplayName,
    string? Publisher,
    string? Path,
    string Source,
    string? Version);

public sealed record AppInventoryRequest(
    IReadOnlyList<AppInventoryItemDto> Apps,
    DateTime CollectedUtc);

public sealed record BlockEventDto(
    string RuleType,
    string Identity,
    string DisplayName,
    DateTime OccurredUtc);

public sealed record BlockEventRequest(
    IReadOnlyList<BlockEventDto> Events);

public sealed record ItemBonusRequest(
    string RuleType,
    string Identity,
    int Minutes,
    DateTime? AppliedUtc);

public sealed record RuleHistoryDto(
    Guid Id,
    string RuleType,
    string Identity,
    string DisplayName,
    string Action,
    string Mode,
    DateTime ChangedUtc);

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
    DateTime UpdatedUtc,
    int? DailyLimitRemainingSeconds = null,
    int? DailyLimitMinutes = null,
    bool SpokenWarnings = false,
    int? IdleThresholdSeconds = null);

public sealed record UsageIntervalDto(
    Guid Id,
    string AppIdentity,
    string AppDisplayName,
    DateTime StartUtc,
    int DurationSeconds,
    bool Active);

public sealed record WebsiteUsageDto(
    Guid Id,
    string Domain,
    DateTime StartUtc,
    int DurationSeconds);

public sealed record UsageBatchRequest(
    IReadOnlyList<UsageIntervalDto> Intervals,
    IReadOnlyList<WebsiteUsageDto>? Sites = null);

public sealed record TamperEventDto(
    string Kind,
    string? Detail,
    DateTime OccurredUtc);

public sealed record TamperEventRequest(
    IReadOnlyList<TamperEventDto> Events);

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

public sealed record InstallLinkCreateRequest(Guid ChildId, DevicePlatform Platform = DevicePlatform.Windows);

public sealed record InstallLinkCreatedDto(
    Guid Id,
    Guid ChildId,
    string ChildName,
    DevicePlatform Platform,
    string Token,
    string Link,
    string Code,
    DateTime CreatedUtc,
    DateTime ExpiresUtc);

public sealed record InstallLinkDto(
    Guid Id,
    Guid ChildId,
    string ChildName,
    DevicePlatform Platform,
    DateTime CreatedUtc,
    DateTime ExpiresUtc,
    DateTime? UsedUtc,
    DateTime? RevokedUtc,
    string? DeviceName);

public sealed record InstallLinkStatusDto(
    string Status,
    string? ChildName,
    string? DeviceName,
    DateTime ExpiresUtc,
    string? Error = null);

public sealed record ReleaseInfoDto(
    string Version,
    string Sha256,
    long SizeBytes,
    DateTime PublishedUtc,
    string SetupFileName,
    string MsiFileName,
    string? PreviousVersion);

public sealed record UpdateInfoDto(
    string Version,
    string? CurrentVersion,
    string Sha256,
    long SizeBytes,
    string DownloadPath,
    DateTime PublishedUtc,
    string? PreviousVersion);

public sealed record DeviceUpdateHealthRequest(
    string Version,
    string? PreviousVersion,
    string Status,
    string? Detail = null);

public sealed record UnaccountedIntervalDto(
    Guid Id,
    DateTime StartUtc,
    int DurationSeconds,
    string Reason);

public sealed record UnaccountedBatchRequest(
    IReadOnlyList<UnaccountedIntervalDto> Intervals);

public sealed record UninstallReportRequest(string? Detail = null);

public sealed record DeviceOverrideRequest(
    string Option,
    int? Minutes,
    DateTime? AppliedUtc = null);

public sealed record OverrideEventDto(
    Guid Id,
    string Option,
    int? Minutes,
    DateTime CreatedUtc,
    string? Detail = null);

public static class DeviceGroups
{
    public static string Device(Guid deviceId) => $"device:{deviceId:N}";
    public static string Child(Guid childId) => $"child:{childId:N}";
}

public static class DeviceHubMethods
{
    public const string DeviceStateChanged = "DeviceStateChanged";
    public const string UpdateRequested = "UpdateRequested";
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

public sealed record InvitationCreatedDto(
    Guid Id,
    InvitationKind Kind,
    string? Email,
    string Token,
    string Link,
    DateTime CreatedUtc,
    DateTime ExpiresUtc,
    ParentPermissions Permissions,
    bool EmailSent);

public sealed record InvitationDto(
    Guid Id,
    InvitationKind Kind,
    Guid? FamilyId,
    string? FamilyName,
    string? Email,
    ParentPermissions Permissions,
    DateTime CreatedUtc,
    DateTime ExpiresUtc,
    DateTime? UsedUtc,
    DateTime? RevokedUtc);

public sealed record FamilyAdminDto(
    Guid Id,
    string Name,
    string OwnerEmail,
    string OwnerName,
    DateTime CreatedUtc,
    int Children,
    int Devices,
    DateTime? LastActivityUtc,
    FamilyStatus Status);

public sealed record VersionInfoDto(
    string Version,
    string DisplayVersion,
    string Commit,
    string BuildDate);

public sealed record AlertPreferencesDto(
    bool MutedNewApp,
    bool MutedRepeatedBlock,
    bool MutedPinOverride,
    bool MutedLimit,
    bool MutedOffline,
    bool MutedTampering,
    int QuietStartMinutes,
    int QuietEndMinutes,
    bool DailyDigest,
    int DigestHour);
