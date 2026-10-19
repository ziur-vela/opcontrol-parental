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
    public RuleAction Action { get; set; }
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
    public DateTime VisitedUtc { get; set; } = DateTime.UtcNow;
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
