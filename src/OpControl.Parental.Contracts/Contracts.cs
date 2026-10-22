// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

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
    RemoteView = 128,
    Messaging = 256,
    All = LockUnlockBonus | WebsiteRules | AppRules | SchedulesLimits | ViewReports | ManageDevices | ManagePin | RemoteView | Messaging
}

public enum MessageDirection
{
    ParentToChild = 0,
    ChildToParent = 1
}

public enum MessageStatus
{
    Sent = 0,
    Delivered = 1,
    Seen = 2,
    Acknowledged = 3
}

public enum MessageUiStatus
{
    Pending = 0,
    Failed = 1,
    Sent = 2,
    Delivered = 3,
    Read = 4
}

public static class MessageStatusRules
{
    public static MessageUiStatus ForDisplay(ChildMessageDto message) =>
        message.Direction == MessageDirection.ParentToChild
            ? message.AcknowledgedUtc is not null || message.SeenUtc is not null
                ? MessageUiStatus.Read
                : message.DeliveredUtc is not null ? MessageUiStatus.Delivered : MessageUiStatus.Sent
            : message.ParentReadUtc is not null
                ? MessageUiStatus.Read
                : message.DeliveredUtc is not null ? MessageUiStatus.Delivered : MessageUiStatus.Sent;

    public static string LabelKey(MessageUiStatus status) => status switch
    {
        MessageUiStatus.Pending => "Messages.Status.Pending",
        MessageUiStatus.Failed => "Messages.Status.Failed",
        MessageUiStatus.Delivered => "Messages.Status.Delivered",
        MessageUiStatus.Read => "Messages.Status.Read",
        _ => "Messages.Status.Sent"
    };

    public static string Icon(MessageUiStatus status) => status switch
    {
        MessageUiStatus.Pending => "◷",
        MessageUiStatus.Failed => "!",
        MessageUiStatus.Delivered => "✓✓",
        MessageUiStatus.Read => "✓✓",
        _ => "✓"
    };
}

public enum InvitationKind
{
    CoParent = 0,
    NewFamily = 1
}

public enum RemoteSessionKind
{
    Screen = 0,
    Camera = 1
}

public enum RemoteSessionStatus
{
    Requested = 0,
    Active = 1,
    Ended = 2
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
    bool SpokenWarnings = false,
    string? Language = null);

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
    int? IdleThresholdSeconds = null,
    string? Language = null,
    string? UnlockScheduleName = null);

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
    public const string RemoteSessionRequested = "RemoteSessionRequested";
    public const string RemoteSessionStopped = "RemoteSessionStopped";
    public const string MessageReceived = "MessageReceived";
    public const string MessageStatusChanged = "MessageStatusChanged";
}

public sealed record ChildMessageDto(
    Guid Id,
    Guid ChildId,
    Guid? DeviceId,
    MessageDirection Direction,
    Guid? SenderParentId,
    string SenderName,
    string Text,
    string? Preset,
    bool SpeakAloud,
    bool RequireAck,
    DateTime CreatedUtc,
    DateTime? DeliveredUtc,
    DateTime? SeenUtc,
    DateTime? AcknowledgedUtc,
    DateTime? ParentReadUtc,
    MessageStatus Status,
    bool Deleted,
    string? DeliveredBy = null,
    string? SeenBy = null,
    string? AcknowledgedBy = null,
    string? ParentDeliveredBy = null,
    string? ParentReadBy = null);

public sealed record SummaryWeekdayDto(int Day, int Minutes);

public sealed record SummaryScheduleDto(
    Guid Id,
    string Name,
    int DaysMask,
    int StartMinutes,
    int EndMinutes,
    bool IsEnabled);

public sealed record SummaryCategoryDto(string Key, string Name);

public sealed record SummaryWebsiteDto(
    string Domain,
    bool Allowed,
    bool Always,
    string? ScheduleName);

public sealed record SummaryAppDto(
    string Identity,
    string DisplayName,
    bool Allowed,
    bool AlwaysAllowed,
    string? ScheduleName,
    int? DailyLimitMinutes,
    int UsedTodaySeconds);

public sealed record SummaryAppUsageDto(string Identity, string DisplayName, int Seconds);

public sealed record SummaryVisitDto(string Domain, DateTime VisitedUtc, bool Blocked);

public sealed record SummaryHistoryDto(string Kind, int? Minutes, DateTime CreatedUtc);

/// <summary>Read-only, per-child summary for the child's "My OpControl" window.</summary>
public sealed record ChildSummaryDto(
    Guid ChildId,
    string ChildName,
    string DeviceName,
    DateTime GeneratedUtc,
    bool ScreenTimeEnabled,
    int TodayLimitMinutes,
    int UsedTodaySeconds,
    int UsedWeekSeconds,
    IReadOnlyList<SummaryWeekdayDto> Weekdays,
    IReadOnlyList<SummaryScheduleDto> Schedules,
    IReadOnlyList<SummaryCategoryDto> Categories,
    IReadOnlyList<SummaryWebsiteDto> Websites,
    IReadOnlyList<SummaryAppDto> Apps,
    IReadOnlyList<SummaryAppUsageDto> AppsToday,
    IReadOnlyList<SummaryAppUsageDto> AppsWeek,
    IReadOnlyList<SummaryVisitDto> Visits,
    IReadOnlyList<SummaryHistoryDto> History,
    IReadOnlyList<ChildMessageDto> RecentMessages,
    int UnreadMessages);

public sealed record ParentMessageRequest(
    string Text,
    string? Preset,
    bool SpeakAloud,
    bool RequireAck,
    Guid? DeviceId);

public sealed record DeviceMessageRequest(
    string Text,
    string? Preset = null);

public sealed record MessageReadRequest(Guid ChildId);

public sealed record PinPipeRequest(
    string Type,
    string? Pin = null,
    string? Option = null,
    int? Minutes = null,
    string? Ticket = null,
    string? Text = null,
    Guid? MessageId = null,
    string? Tab = null,
    bool? MessagesOnly = null,
    string? Theme = null,
    string? Language = null);

public sealed record PinPipeResponse(
    bool Ok,
    string? Error = null,
    bool LockedOut = false,
    int? RemainingSeconds = null,
    int? AttemptsRemaining = null,
    string? Ticket = null,
    RuntimeLockStateDto? State = null,
    IReadOnlyList<ChildMessageDto>? Messages = null,
    ChildMessageDto? Message = null,
    int? RetryAfterSeconds = null,
    bool Queued = false);

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
    bool MutedChildMessage,
    int QuietStartMinutes,
    int QuietEndMinutes,
    bool DailyDigest,
    int DigestHour);


public sealed record RemoteSessionRequest(
    Guid SessionId,
    Guid DeviceId,
    Guid ChildId,
    RemoteSessionKind Kind,
    bool ShowNotice = false,
    string? NoticeText = null);

public sealed record RemoteSessionCreatedDto(
    Guid Id,
    Guid DeviceId,
    Guid ChildId,
    RemoteSessionKind Kind,
    DateTime CreatedUtc,
    DateTime ExpiresUtc);

public sealed record RemoteSessionStopRequest(Guid SessionId);
