using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed class ServerAdminService
{
    private readonly ParentalDbContext _db;
    private readonly ILogger<ServerAdminService> _logger;

    public ServerAdminService(ParentalDbContext db, ILogger<ServerAdminService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<List<FamilyAdminDto>> ListFamiliesAsync(CancellationToken ct = default)
    {
        var families = await _db.Families.AsNoTracking().OrderBy(f => f.CreatedUtc).ToListAsync(ct);
        var result = new List<FamilyAdminDto>();
        foreach (var family in families)
        {
            var ownerRow = await _db.Users.AsNoTracking()
                .Where(u => u.FamilyId == family.Id && u.RoleInFamily == FamilyRole.Owner)
                .OrderBy(u => u.CreatedUtc)
                .Select(u => new { u.Email, u.DisplayName })
                .FirstOrDefaultAsync(ct);
            var owner = new { Email = ownerRow?.Email ?? "", DisplayName = ownerRow?.DisplayName ?? "" };
            var children = await _db.Children.AsNoTracking().CountAsync(c => c.FamilyId == family.Id, ct);
            var devices = await _db.Devices.AsNoTracking().CountAsync(d => d.FamilyId == family.Id, ct);
            var lastDevice = await _db.Devices.AsNoTracking().Where(d => d.FamilyId == family.Id)
                .MaxAsync(d => (DateTime?)d.LastSeenUtc, ct);
            var lastAlert = await _db.Alerts.AsNoTracking().Where(a => a.FamilyId == family.Id)
                .MaxAsync(a => (DateTime?)a.CreatedUtc, ct);
            var lastUsage = await _db.UsageIntervals.AsNoTracking().Where(u => u.FamilyId == family.Id)
                .MaxAsync(u => (DateTime?)u.StartUtc, ct);
            var last = new[] { lastDevice, lastAlert, lastUsage }.Where(x => x is not null).Max();
            result.Add(new FamilyAdminDto(family.Id, family.Name, owner.Email, owner.DisplayName, family.CreatedUtc,
                children, devices, last, family.Status));
        }
        return result;
    }

    public async Task SetStatusAsync(Guid familyId, FamilyStatus status, Guid actorId, string actorEmail, CancellationToken ct = default)
    {
        var family = await _db.Families.FirstOrDefaultAsync(f => f.Id == familyId, ct)
            ?? throw new InvalidOperationException("Family not found.");
        family.Status = status;
        family.SuspendedUtc = status == FamilyStatus.Suspended ? DateTime.UtcNow : null;

        if (status == FamilyStatus.Suspended)
        {
            var parents = await _db.Users.Where(u => u.FamilyId == familyId).ToListAsync(ct);
            foreach (var parent in parents)
            {
                parent.SecurityStamp = Guid.NewGuid().ToString();
                await _db.SaveChangesAsync(ct);
            }
        }

        await _db.SaveChangesAsync(ct);
        await AuditAsync(actorId, actorEmail, status == FamilyStatus.Suspended ? "family.suspend" : "family.reactivate", familyId, family.Name, ct);
        _logger.LogWarning("Family {FamilyId} set to {Status} by {Actor}", familyId, status, actorEmail);
    }

    public async Task DeleteFamilyAsync(Guid familyId, Guid actorId, string actorEmail, CancellationToken ct = default)
    {
        var family = await _db.Families.AsNoTracking().FirstOrDefaultAsync(f => f.Id == familyId, ct)
            ?? throw new InvalidOperationException("Family not found.");
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            var userIds = await _db.Users.AsNoTracking().Where(u => u.FamilyId == familyId).Select(u => u.Id).ToListAsync(ct);
            var childIds = await _db.Children.AsNoTracking().Where(c => c.FamilyId == familyId).Select(c => c.Id).ToListAsync(ct);

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await _db.UserRoles.Where(x => userIds.Contains(x.UserId)).ExecuteDeleteAsync(ct);
            await _db.UserClaims.Where(x => userIds.Contains(x.UserId)).ExecuteDeleteAsync(ct);
            await _db.UserLogins.Where(x => userIds.Contains(x.UserId)).ExecuteDeleteAsync(ct);
            await _db.UserTokens.Where(x => userIds.Contains(x.UserId)).ExecuteDeleteAsync(ct);
            await _db.Users.Where(u => u.FamilyId == familyId).ExecuteDeleteAsync(ct);

            await _db.Alerts.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.VisitRecords.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.AppUsageRecords.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.UsageIntervals.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.WebsiteUsages.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.UnaccountedIntervals.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.BlockEvents.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.ClientTamperEvents.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.DeviceUpdateEvents.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.InstalledApps.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.RuleHistory.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.ItemBonuses.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.OverrideEvents.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.WebsiteRules.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.AppRules.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.Schedules.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.EnrollmentCodes.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.InstallLinks.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.DeviceAccounts.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.Devices.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.ScreenTimeLimits.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.LockStates.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.ChildCategories.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.AlertPreferences.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.FamilySettings.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.Invitations.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.ScheduleRuntimeStates.Where(x => childIds.Contains(x.ChildId)).ExecuteDeleteAsync(ct);
            await _db.Children.Where(x => x.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await _db.Families.Where(x => x.Id == familyId).ExecuteDeleteAsync(ct);
            await _db.ServerAdminAudits.AddAsync(new ServerAdminAudit
            {
                ActorParentId = actorId,
                ActorEmail = actorEmail,
                Action = "family.delete",
                FamilyId = familyId,
                Detail = family.Name,
                CreatedUtc = DateTime.UtcNow
            }, ct);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        _logger.LogWarning("Family {FamilyId} ({Name}) deleted by {Actor}", familyId, family.Name, actorEmail);
    }

    public async Task AuditAsync(Guid? actorId, string actorEmail, string action, Guid? familyId, string? detail, CancellationToken ct = default)
    {
        _db.ServerAdminAudits.Add(new ServerAdminAudit
        {
            ActorParentId = actorId,
            ActorEmail = actorEmail,
            Action = action,
            FamilyId = familyId,
            Detail = detail is { Length: > 1000 } ? detail[..1000] : detail,
            CreatedUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);
    }

    public Task<List<ServerAdminAudit>> RecentAuditAsync(int take = 100, CancellationToken ct = default) =>
        _db.ServerAdminAudits.AsNoTracking().OrderByDescending(a => a.CreatedUtc).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
}
