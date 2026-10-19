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

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Family>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
        });

        builder.Entity<ParentUser>(e =>
        {
            e.Property(x => x.DisplayName).HasMaxLength(120).IsRequired();
            e.Property(x => x.RoleInFamily).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => x.FamilyId);
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
            e.Property(x => x.CredentialHash).HasMaxLength(64);
            e.Property(x => x.ClientVersion).HasMaxLength(40);
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
            e.Property(x => x.Action).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.FamilyId, x.ChildId, x.ChangedUtc });
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
    }
}
