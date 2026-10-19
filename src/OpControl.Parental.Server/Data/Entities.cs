using Microsoft.AspNetCore.Identity;
using OpControl.Parental.Contracts;

namespace OpControl.Parental.Server.Data;

public class Family
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<ParentUser> Parents { get; set; } = [];
    public List<Child> Children { get; set; } = [];
}

public class ParentUser : IdentityUser<Guid>
{
    public Guid FamilyId { get; set; }
    public string DisplayName { get; set; } = "";
    public FamilyRole RoleInFamily { get; set; } = FamilyRole.Parent;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public Family? Family { get; set; }
}

public class Child
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FamilyId { get; set; }
    public string Name { get; set; } = "";
    public DateOnly? DateOfBirth { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public Family? Family { get; set; }
    public List<Device> Devices { get; set; } = [];
    public LockState? LockState { get; set; }
    public List<WebsiteRule> WebsiteRules { get; set; } = [];
    public List<AppRule> AppRules { get; set; } = [];
    public List<Schedule> Schedules { get; set; } = [];
}

public class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FamilyId { get; set; }
    public Guid ChildId { get; set; }
    public string Name { get; set; } = "";
    public DevicePlatform Platform { get; set; } = DevicePlatform.Windows;
    public string? OsVersion { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public DateTime EnrolledUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenUtc { get; set; }
    public string? CredentialHash { get; set; }
    public DateTime? CredentialIssuedUtc { get; set; }
    public DateTime? CredentialExpiresUtc { get; set; }
    public bool IsRevoked { get; set; }
    public string? ClientVersion { get; set; }
    public Child? Child { get; set; }
    public List<DeviceAccount> Accounts { get; set; } = [];
}

public class DeviceAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FamilyId { get; set; }
    public Guid DeviceId { get; set; }
    public string OsAccountName { get; set; } = "";
    public string? DisplayName { get; set; }
    public bool IsMonitored { get; set; } = true;
    public Device? Device { get; set; }
}

public class Schedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FamilyId { get; set; }
    public Guid ChildId { get; set; }
    public string Name { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public int DaysMask { get; set; }
    public int StartMinutes { get; set; }
    public int EndMinutes { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public Child? Child { get; set; }
}

public class WebsiteRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FamilyId { get; set; }
    public Guid ChildId { get; set; }
    public string Domain { get; set; } = "";
    public bool IsBlocked { get; set; } = true;
    public RuleMode Mode { get; set; } = RuleMode.Always;
    public Guid? ScheduleId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public Child? Child { get; set; }
    public Schedule? Schedule { get; set; }
}

public class AppRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FamilyId { get; set; }
    public Guid ChildId { get; set; }
    public string AppIdentity { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Publisher { get; set; }
    public bool IsBlocked { get; set; } = true;
    public RuleMode Mode { get; set; } = RuleMode.Always;
    public Guid? ScheduleId { get; set; }
    public bool AlwaysAllowed { get; set; }
    public int? DailyLimitMinutes { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public Child? Child { get; set; }
    public Schedule? Schedule { get; set; }
}

public class RuleHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FamilyId { get; set; }
    public Guid ChildId { get; set; }
    public string RuleType { get; set; } = "";
    public Guid RuleId { get; set; }
    public string Identity { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public RuleAction Action { get; set; }
    public RuleMode Mode { get; set; }
    public Guid? ScheduleId { get; set; }
    public Guid? ChangedByParentId { get; set; }
    public DateTime ChangedUtc { get; set; } = DateTime.UtcNow;
}

public class LockState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FamilyId { get; set; }
    public Guid ChildId { get; set; }
    public bool ManualLock { get; set; }
    public DateTime? UnlockUntilUtc { get; set; }
    public DateTime? BonusUntilUtc { get; set; }
    public int? BonusMinutes { get; set; }
    public string? OverridePinHash { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public Child? Child { get; set; }
}

public class OverrideEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FamilyId { get; set; }
    public Guid ChildId { get; set; }
    public Guid? DeviceId { get; set; }
    public string Option { get; set; } = "";
    public string? Detail { get; set; }
    public int? Minutes { get; set; }
    public Guid? CreatedByParentId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class VisitRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FamilyId { get; set; }
    public Guid ChildId { get; set; }
    public Guid DeviceId { get; set; }
    public string Domain { get; set; } = "";
    public bool Blocked { get; set; }
    public DateTime VisitedUtc { get; set; } = DateTime.UtcNow;
}

public class ItemBonus
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FamilyId { get; set; }
    public Guid ChildId { get; set; }
    public string RuleType { get; set; } = "";
    public string Identity { get; set; } = "";
    public int Minutes { get; set; }
    public DateTime UntilUtc { get; set; }
    public Guid? CreatedByParentId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class InstalledApp
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FamilyId { get; set; }
    public Guid ChildId { get; set; }
    public Guid DeviceId { get; set; }
    public string Identity { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Publisher { get; set; }
    public string? Path { get; set; }
    public string Source { get; set; } = "win32";
    public string? Version { get; set; }
    public bool IsNew { get; set; } = true;
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime? RemovedUtc { get; set; }
    public DateTime? ReviewedUtc { get; set; }
}

public class ScheduleRuntimeState
{
    public Guid ChildId { get; set; }
    public Guid? ActiveScheduleId { get; set; }
    public string? ActiveScheduleName { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public class BlockEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FamilyId { get; set; }
    public Guid ChildId { get; set; }
    public Guid DeviceId { get; set; }
    public string RuleType { get; set; } = "";
    public string Identity { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTime OccurredUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class Category
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public int Version { get; set; }
    public DateTime FetchedUtc { get; set; } = DateTime.UtcNow;
    public string? SourceUrl { get; set; }
    public List<CategoryDomain> Domains { get; set; } = [];
}

public class CategoryDomain
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CategoryKey { get; set; } = "";
    public string Domain { get; set; } = "";
    public int AddedVersion { get; set; }
    public int? RemovedVersion { get; set; }
    public Category? Category { get; set; }
}

public class ChildCategory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FamilyId { get; set; }
    public Guid ChildId { get; set; }
    public string CategoryKey { get; set; } = "";
    public bool Enabled { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public class AppUsageRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FamilyId { get; set; }
    public Guid ChildId { get; set; }
    public Guid DeviceId { get; set; }
    public string AppIdentity { get; set; } = "";
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public int DurationSeconds { get; set; }
}

public class UsageInterval
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FamilyId { get; set; }
    public Guid ChildId { get; set; }
    public Guid DeviceId { get; set; }
    public string AppIdentity { get; set; } = "";
    public string AppDisplayName { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public int DurationSeconds { get; set; }
    public bool Active { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class WebsiteUsage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FamilyId { get; set; }
    public Guid ChildId { get; set; }
    public Guid DeviceId { get; set; }
    public string Domain { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public int DurationSeconds { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class ScreenTimeLimit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FamilyId { get; set; }
    public Guid ChildId { get; set; }
    public bool Enabled { get; set; }
    public bool SpokenWarnings { get; set; }
    public int MondayMinutes { get; set; } = 120;
    public int TuesdayMinutes { get; set; } = 120;
    public int WednesdayMinutes { get; set; } = 120;
    public int ThursdayMinutes { get; set; } = 120;
    public int FridayMinutes { get; set; } = 120;
    public int SaturdayMinutes { get; set; } = 240;
    public int SundayMinutes { get; set; } = 240;
    public Guid? UpdatedByParentId { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public class Alert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FamilyId { get; set; }
    public Guid ChildId { get; set; }
    public Guid? DeviceId { get; set; }
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ReadUtc { get; set; }
    public DateTime? EmailedUtc { get; set; }
}

public class FamilySettings
{
    public Guid FamilyId { get; set; }
    public bool AlertEmailsEnabled { get; set; }
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string? SmtpUsername { get; set; }
    public string? SmtpPasswordProtected { get; set; }
    public string? FromAddress { get; set; }
    public int OfflineAlertMinutes { get; set; } = 120;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public class ClientTamperEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FamilyId { get; set; }
    public Guid ChildId { get; set; }
    public Guid DeviceId { get; set; }
    public string Kind { get; set; } = "";
    public string? Detail { get; set; }
    public DateTime OccurredUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class EnrollmentCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FamilyId { get; set; }
    public Guid ChildId { get; set; }
    public string CodeHash { get; set; } = "";
    public Guid CreatedByParentId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; }
    public DateTime? UsedUtc { get; set; }
}
