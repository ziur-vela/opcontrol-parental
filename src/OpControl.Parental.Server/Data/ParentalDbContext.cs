// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;

namespace OpControl.Parental.Server.Data;

public class ParentalDbContext : IdentityDbContext<ParentUser, IdentityRole<Guid>, Guid>
{
    public ParentalDbContext(DbContextOptions<ParentalDbContext> options) : base(options) { }

    public DbSet<Family> Families => Set<Family>();
    public DbSet<Child> Children => Set<Child>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceAccount> DeviceAccounts => Set<DeviceAccount>();
    public DbSet<Schedule> Schedules => Set<Schedule>();
    public DbSet<WebsiteRule> WebsiteRules => Set<WebsiteRule>();
    public DbSet<AppRule> AppRules => Set<AppRule>();
    public DbSet<RuleHistory> RuleHistory => Set<RuleHistory>();
    public DbSet<LockState> LockStates => Set<LockState>();
    public DbSet<OverrideEvent> OverrideEvents => Set<OverrideEvent>();
    public DbSet<VisitRecord> VisitRecords => Set<VisitRecord>();
    public DbSet<AppUsageRecord> AppUsageRecords => Set<AppUsageRecord>();
    public DbSet<EnrollmentCode> EnrollmentCodes => Set<EnrollmentCode>();
    public DbSet<ItemBonus> ItemBonuses => Set<ItemBonus>();
    public DbSet<BlockEvent> BlockEvents => Set<BlockEvent>();
    public DbSet<ScheduleRuntimeState> ScheduleRuntimeStates => Set<ScheduleRuntimeState>();
    public DbSet<InstalledApp> InstalledApps => Set<InstalledApp>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<CategoryDomain> CategoryDomains => Set<CategoryDomain>();
    public DbSet<ChildCategory> ChildCategories => Set<ChildCategory>();
    public DbSet<UsageInterval> UsageIntervals => Set<UsageInterval>();
    public DbSet<WebsiteUsage> WebsiteUsages => Set<WebsiteUsage>();
    public DbSet<ScreenTimeLimit> ScreenTimeLimits => Set<ScreenTimeLimit>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<FamilySettings> FamilySettings => Set<FamilySettings>();
    public DbSet<ClientTamperEvent> ClientTamperEvents => Set<ClientTamperEvent>();
    public DbSet<InstallLink> InstallLinks => Set<InstallLink>();
    public DbSet<UnaccountedInterval> UnaccountedIntervals => Set<UnaccountedInterval>();
    public DbSet<DeviceUpdateEvent> DeviceUpdateEvents => Set<DeviceUpdateEvent>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<ServerSettings> ServerSettings => Set<ServerSettings>();
    public DbSet<AlertPreference> AlertPreferences => Set<AlertPreference>();
    public DbSet<ServerAdminAudit> ServerAdminAudits => Set<ServerAdminAudit>();
    public DbSet<RemoteSession> RemoteSessions => Set<RemoteSession>();
    public DbSet<CameraConsent> CameraConsents => Set<CameraConsent>();
    public DbSet<ChildMessage> ChildMessages => Set<ChildMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Family>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.DefaultLanguage).HasMaxLength(16).HasDefaultValue("en");
        });

        builder.Entity<ParentUser>(e =>
        {
            e.Property(x => x.DisplayName).HasMaxLength(120).IsRequired();
            e.Property(x => x.RoleInFamily).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Permissions).HasConversion<int>();
            e.Property(x => x.PreferredLanguage).HasMaxLength(16);
            e.HasIndex(x => x.FamilyId);
            e.HasIndex(x => x.IsServerAdmin);
            e.HasOne(x => x.Family).WithMany(x => x.Parents).HasForeignKey(x => x.FamilyId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Child>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.HasIndex(x => x.FamilyId);
            e.HasOne(x => x.Family).WithMany(x => x.Children).HasForeignKey(x => x.FamilyId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.LockState).WithOne(x => x.Child).HasForeignKey<LockState>(x => x.ChildId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Device>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Platform).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.OsVersion).HasMaxLength(120);
            e.Property(x => x.TimeZoneId).HasMaxLength(80).IsRequired();
            e.Property(x => x.CameraNoticeEnabled).HasDefaultValue(true);
            e.Property(x => x.ClientLanguage).HasMaxLength(16);
            e.Property(x => x.CredentialHash).HasMaxLength(64);
            e.Property(x => x.ClientVersion).HasMaxLength(40);
            e.Property(x => x.PreviousClientVersion).HasMaxLength(40);
            e.Property(x => x.UpdateTargetVersion).HasMaxLength(40);
            e.Property(x => x.UpdateStatus).HasMaxLength(40);
            e.HasIndex(x => x.FamilyId);
            e.HasIndex(x => new { x.FamilyId, x.ChildId });
            e.HasIndex(x => x.CredentialHash).IsUnique();
            e.HasOne(x => x.Child).WithMany(x => x.Devices).HasForeignKey(x => x.ChildId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<DeviceAccount>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.OsAccountName).HasMaxLength(120).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(120);
            e.HasIndex(x => new { x.DeviceId, x.OsAccountName }).IsUnique();
            e.HasOne(x => x.Device).WithMany(x => x.Accounts).HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Schedule>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.HasIndex(x => x.FamilyId);
            e.HasIndex(x => x.ChildId);
            e.HasOne(x => x.Child).WithMany(x => x.Schedules).HasForeignKey(x => x.ChildId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<WebsiteRule>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Domain).HasMaxLength(255).IsRequired();
            e.Property(x => x.Mode).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.FamilyId, x.ChildId, x.Domain });
            e.HasOne(x => x.Child).WithMany(x => x.WebsiteRules).HasForeignKey(x => x.ChildId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Schedule).WithMany().HasForeignKey(x => x.ScheduleId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<AppRule>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AppIdentity).HasMaxLength(255).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(255).IsRequired();
            e.Property(x => x.Publisher).HasMaxLength(255);
            e.Property(x => x.Mode).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.FamilyId, x.ChildId, x.AppIdentity });
            e.HasOne(x => x.Child).WithMany(x => x.AppRules).HasForeignKey(x => x.ChildId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Schedule).WithMany().HasForeignKey(x => x.ScheduleId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<RuleHistory>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.RuleType).HasMaxLength(16).IsRequired();
            e.Property(x => x.Identity).HasMaxLength(255).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(255).IsRequired();
            e.Property(x => x.Action).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Mode).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.FamilyId, x.ChildId, x.ChangedUtc });
            e.HasIndex(x => new { x.ChildId, x.RuleType, x.Identity, x.ChangedUtc });
        });

        builder.Entity<LockState>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.OverridePinHash).HasMaxLength(256);
            e.HasIndex(x => x.ChildId).IsUnique();
            e.HasIndex(x => x.FamilyId);
        });

        builder.Entity<OverrideEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Option).HasMaxLength(40).IsRequired();
            e.HasIndex(x => new { x.FamilyId, x.ChildId, x.CreatedUtc });
        });

        builder.Entity<VisitRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Domain).HasMaxLength(255).IsRequired();
            e.HasIndex(x => new { x.FamilyId, x.ChildId, x.VisitedUtc });
            e.HasIndex(x => new { x.DeviceId, x.VisitedUtc });
            e.HasIndex(x => new { x.ChildId, x.Blocked, x.VisitedUtc });
        });

        builder.Entity<ItemBonus>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.RuleType).HasMaxLength(16).IsRequired();
            e.Property(x => x.Identity).HasMaxLength(255).IsRequired();
            e.HasIndex(x => new { x.FamilyId, x.ChildId, x.UntilUtc });
            e.HasIndex(x => new { x.ChildId, x.RuleType, x.Identity });
        });

        builder.Entity<ScheduleRuntimeState>(e =>
        {
            e.HasKey(x => x.ChildId);
            e.Property(x => x.ActiveScheduleName).HasMaxLength(120);
        });

        builder.Entity<BlockEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.RuleType).HasMaxLength(16).IsRequired();
            e.Property(x => x.Identity).HasMaxLength(255).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(255).IsRequired();
            e.HasIndex(x => new { x.FamilyId, x.ChildId, x.OccurredUtc });
        });

        builder.Entity<InstalledApp>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Identity).HasMaxLength(255).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(255).IsRequired();
            e.Property(x => x.Publisher).HasMaxLength(255);
            e.Property(x => x.Path).HasMaxLength(1024);
            e.Property(x => x.Source).HasMaxLength(16).IsRequired();
            e.Property(x => x.Version).HasMaxLength(80);
            e.HasIndex(x => new { x.DeviceId, x.Identity }).IsUnique();
            e.HasIndex(x => new { x.FamilyId, x.ChildId, x.RemovedUtc });
            e.HasIndex(x => new { x.ChildId, x.IsNew });
        });

        builder.Entity<Category>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(80).IsRequired();
            e.Property(x => x.SourceUrl).HasMaxLength(512);
        });

        builder.Entity<CategoryDomain>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.CategoryKey).HasMaxLength(32).IsRequired();
            e.Property(x => x.Domain).HasMaxLength(255).IsRequired();
            e.HasIndex(x => new { x.CategoryKey, x.Domain }).IsUnique();
            e.HasIndex(x => new { x.CategoryKey, x.AddedVersion });
            e.HasIndex(x => new { x.CategoryKey, x.RemovedVersion });
            e.HasOne(x => x.Category).WithMany(x => x.Domains).HasForeignKey(x => x.CategoryKey).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ChildCategory>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.CategoryKey).HasMaxLength(32).IsRequired();
            e.HasIndex(x => new { x.ChildId, x.CategoryKey }).IsUnique();
        });

        builder.Entity<AppUsageRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AppIdentity).HasMaxLength(255).IsRequired();
            e.HasIndex(x => new { x.FamilyId, x.ChildId, x.StartedUtc });
            e.HasIndex(x => new { x.DeviceId, x.StartedUtc });
        });

        builder.Entity<EnrollmentCode>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.CodeHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.CodeHash).IsUnique();
            e.HasIndex(x => new { x.FamilyId, x.ChildId });
        });

        builder.Entity<UsageInterval>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AppIdentity).HasMaxLength(255).IsRequired();
            e.Property(x => x.AppDisplayName).HasMaxLength(255).IsRequired();
            e.HasIndex(x => new { x.FamilyId, x.ChildId, x.StartUtc });
            e.HasIndex(x => new { x.DeviceId, x.StartUtc });
            e.HasIndex(x => new { x.ChildId, x.Active, x.StartUtc });
        });

        builder.Entity<WebsiteUsage>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Domain).HasMaxLength(255).IsRequired();
            e.HasIndex(x => new { x.FamilyId, x.ChildId, x.StartUtc });
            e.HasIndex(x => new { x.DeviceId, x.StartUtc });
        });

        builder.Entity<ScreenTimeLimit>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ChildId).IsUnique();
            e.HasIndex(x => x.FamilyId);
        });

        builder.Entity<Alert>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasMaxLength(40).IsRequired();
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Detail).HasMaxLength(500).IsRequired();
            e.Property(x => x.TitleKey).HasMaxLength(80);
            e.Property(x => x.DetailKey).HasMaxLength(80);
            e.Property(x => x.ArgsJson).HasMaxLength(1000);
            e.HasIndex(x => new { x.FamilyId, x.CreatedUtc });
            e.HasIndex(x => new { x.ChildId, x.ReadUtc });
            e.HasIndex(x => new { x.ChildId, x.Type, x.CreatedUtc });
            e.HasIndex(x => x.EmailedUtc);
            e.HasIndex(x => x.NextEmailAttemptUtc);
        });

        builder.Entity<FamilySettings>(e =>
        {
            e.HasKey(x => x.FamilyId);
            e.Property(x => x.SmtpHost).HasMaxLength(255);
            e.Property(x => x.SmtpUsername).HasMaxLength(255);
            e.Property(x => x.SmtpPasswordProtected).HasMaxLength(1024);
            e.Property(x => x.FromAddress).HasMaxLength(255);
            e.Property(x => x.NoticeText).HasMaxLength(80).IsRequired().HasDefaultValue("Parent's watching you");
        });

        builder.Entity<ClientTamperEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(40).IsRequired();
            e.Property(x => x.Detail).HasMaxLength(500);
            e.HasIndex(x => new { x.FamilyId, x.ChildId, x.OccurredUtc });
        });

        builder.Entity<InstallLink>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Platform).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.CodeHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.CodeProtected).HasMaxLength(512).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.CodeHash);
            e.HasIndex(x => new { x.FamilyId, x.ChildId, x.CreatedUtc });
        });

        builder.Entity<UnaccountedInterval>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Reason).HasMaxLength(80).IsRequired();
            e.HasIndex(x => new { x.FamilyId, x.ChildId, x.StartUtc });
            e.HasIndex(x => new { x.DeviceId, x.StartUtc });
        });

        builder.Entity<DeviceUpdateEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FromVersion).HasMaxLength(40).IsRequired();
            e.Property(x => x.ToVersion).HasMaxLength(40).IsRequired();
            e.Property(x => x.Status).HasMaxLength(40).IsRequired();
            e.Property(x => x.Detail).HasMaxLength(500);
            e.HasIndex(x => new { x.DeviceId, x.CreatedUtc });
        });

        builder.Entity<Invitation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.FamilyName).HasMaxLength(120);
            e.Property(x => x.Email).HasMaxLength(255);
            e.Property(x => x.Permissions).HasConversion<int>();
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.TokenProtected).HasMaxLength(1024).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.FamilyId, x.Kind, x.CreatedUtc });
            e.HasIndex(x => new { x.Email, x.UsedUtc });
        });

        builder.Entity<ServerSettings>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SmtpHost).HasMaxLength(255);
            e.Property(x => x.SmtpUsername).HasMaxLength(255);
            e.Property(x => x.SmtpPasswordProtected).HasMaxLength(1024);
            e.Property(x => x.FromAddress).HasMaxLength(255);
            e.Property(x => x.FromName).HasMaxLength(120);
            e.Property(x => x.PublicBaseUrl).HasMaxLength(500);
            e.Property(x => x.LatestVersion).HasMaxLength(50);
            e.Property(x => x.LatestReleaseUrl).HasMaxLength(500);
            e.Property(x => x.LatestManifestUrl).HasMaxLength(500);
            e.Property(x => x.LastNotifiedVersion).HasMaxLength(50);
        });

        builder.Entity<AlertPreference>(e =>
        {
            e.HasKey(x => x.FamilyId);
        });

        builder.Entity<ServerAdminAudit>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ActorEmail).HasMaxLength(255).IsRequired();
            e.Property(x => x.Action).HasMaxLength(80).IsRequired();
            e.Property(x => x.Detail).HasMaxLength(1000);
            e.HasIndex(x => x.CreatedUtc);
        });

        builder.Entity<CameraConsent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(512);
            e.HasIndex(x => new { x.ParentId, x.Version }).IsUnique();
            e.HasIndex(x => new { x.FamilyId, x.AcceptedUtc });
        });

        builder.Entity<RemoteSession>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.EndReason).HasMaxLength(80);
            e.HasIndex(x => new { x.FamilyId, x.CreatedUtc });
            e.HasIndex(x => new { x.DeviceId, x.Status });
            e.HasIndex(x => new { x.ChildId, x.CreatedUtc });
        });

        builder.Entity<ChildMessage>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Direction).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Text).HasMaxLength(500).IsRequired();
            e.Property(x => x.Preset).HasMaxLength(40);
            e.HasIndex(x => new { x.FamilyId, x.ChildId, x.CreatedUtc });
            e.HasIndex(x => new { x.ChildId, x.Direction, x.CreatedUtc });
            e.HasIndex(x => new { x.ChildId, x.ParentReadUtc });
            e.HasIndex(x => new { x.DeviceId, x.Direction, x.DeliveredUtc });
        });
    }
}
