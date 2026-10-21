using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed class InvitationException(string message) : Exception(message);

public sealed class InvitationService
{
    private readonly ParentalDbContext _db;
    private readonly UserManager<ParentUser> _users;
    private readonly EmailSender _email;
    private readonly EmailRenderer _renderer;
    private readonly IDataProtector _protector;
    private readonly ILogger<InvitationService> _logger;

    public InvitationService(
        ParentalDbContext db,
        UserManager<ParentUser> users,
        EmailSender email,
        EmailRenderer renderer,
        IDataProtectionProvider protection,
        ILogger<InvitationService> logger)
    {
        _db = db;
        _users = users;
        _email = email;
        _renderer = renderer;
        _protector = protection.CreateProtector("OpControl.Parental.Invitations");
        _logger = logger;
    }

    private static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    public async Task<InvitationCreatedDto> CreateCoParentAsync(Guid familyId, Guid inviterId, string? email, ParentPermissions permissions, CancellationToken ct = default)
    {
        var family = await _db.Families.FirstOrDefaultAsync(f => f.Id == familyId, ct)
            ?? throw new InvitationException(AppText.Get("Invite.FamilyNotFound"));
        var address = NormalizeEmail(email);
        if (address.Length > 0)
        {
            var exists = await _db.Users.AsNoTracking().AnyAsync(u => u.Email != null && u.Email.ToLower() == address, ct);
            if (exists) throw new InvitationException(AppText.Get("Invite.EmailExists"));
        }

        return await CreateAsync(InvitationKind.CoParent, familyId, family.Name, address.Length == 0 ? null : address,
            permissions & ParentPermissions.All, inviterId, TimeSpan.FromHours(72), ct);
    }

    public async Task<InvitationCreatedDto> CreateFamilyInviteAsync(Guid adminId, string? email, string? familyName, CancellationToken ct = default)
    {
        var address = NormalizeEmail(email);
        return await CreateAsync(InvitationKind.NewFamily, null, string.IsNullOrWhiteSpace(familyName) ? null : familyName.Trim(),
            address.Length == 0 ? null : address, ParentPermissions.All, adminId, TimeSpan.FromDays(7), ct);
    }

    private async Task<InvitationCreatedDto> CreateAsync(InvitationKind kind, Guid? familyId, string? familyName, string? email,
        ParentPermissions permissions, Guid inviterId, TimeSpan lifetime, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var token = SecretToken.NewToken();
        var invitation = new Invitation
        {
            Kind = kind,
            FamilyId = familyId,
            FamilyName = familyName,
            Email = email,
            Permissions = permissions,
            TokenHash = SecretToken.Hash(token),
            TokenProtected = _protector.Protect(token),
            CreatedByParentId = inviterId,
            CreatedUtc = now,
            ExpiresUtc = now.Add(lifetime)
        };
        _db.Invitations.Add(invitation);
        await _db.SaveChangesAsync(ct);

        var link = _renderer.Link($"/invite/{token}");
        var emailSent = false;
        if (email is not null)
            emailSent = await SendInviteEmailAsync(invitation, link, ct);

        return new InvitationCreatedDto(invitation.Id, kind, email, token, link, invitation.CreatedUtc, invitation.ExpiresUtc, invitation.Permissions, emailSent);
    }

    public async Task<Invitation?> FindValidAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 512) return null;
        var hash = SecretToken.Hash(token);
        var now = DateTime.UtcNow;
        return await _db.Invitations.AsNoTracking()
            .FirstOrDefaultAsync(i => i.TokenHash == hash && i.UsedUtc == null && i.RevokedUtc == null && i.ExpiresUtc > now, ct);
    }

    public async Task<(ParentUser User, Guid FamilyId)> AcceptCoParentAsync(string token, string email, string displayName, string password, CancellationToken ct = default)
    {
        var invitation = await ValidForUpdateAsync(token, ct);
        if (invitation.Kind != InvitationKind.CoParent || invitation.FamilyId is null)
            throw new InvitationException(AppText.Get("Invite.NotCoParent"));
        var family = await _db.Families.FirstOrDefaultAsync(f => f.Id == invitation.FamilyId, ct)
            ?? throw new InvitationException(AppText.Get("Invite.FamilyNotFound"));
        if (family.Status == FamilyStatus.Suspended)
            throw new InvitationException(AppText.Get("Invite.FamilySuspended"));

        var address = NormalizeEmail(email);
        if (address.Length == 0) throw new InvitationException(AppText.Get("Invite.EnterEmail"));
        if (invitation.Email is not null && !string.Equals(invitation.Email, address, StringComparison.OrdinalIgnoreCase))
            throw new InvitationException(AppText.Get("Invite.UseInvitedEmail"));

        var user = new ParentUser
        {
            FamilyId = family.Id,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? address.Split('@')[0] : displayName.Trim(),
            RoleInFamily = FamilyRole.Parent,
            Permissions = invitation.Permissions,
            UserName = address,
            Email = address,
            EmailConfirmed = true
        };
        var created = await _users.CreateAsync(user, password);
        if (!created.Succeeded)
            throw new InvitationException(IdentityErrorText.Describe(created.Errors));

        invitation.UsedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Co-parent {Email} joined family {FamilyId}", address, family.Id);
        return (user, family.Id);
    }

    public async Task<(ParentUser User, Guid FamilyId)> AcceptNewFamilyAsync(string token, string familyName, string ownerName, string email, string password, string childName, string? language = null, CancellationToken ct = default)
    {
        var invitation = await ValidForUpdateAsync(token, ct);
        if (invitation.Kind != InvitationKind.NewFamily)
            throw new InvitationException(AppText.Get("Invite.NotNewFamily"));
        if (string.IsNullOrWhiteSpace(familyName)) throw new InvitationException(AppText.Get("Invite.EnterFamilyName"));
        if (string.IsNullOrWhiteSpace(childName)) throw new InvitationException(AppText.Get("Invite.EnterChildName"));

        var address = NormalizeEmail(email);
        if (address.Length == 0) throw new InvitationException(AppText.Get("Invite.EnterEmail"));
        if (invitation.Email is not null && !string.Equals(invitation.Email, address, StringComparison.OrdinalIgnoreCase))
            throw new InvitationException(AppText.Get("Invite.UseInvitedEmail"));

        var family = new Family { Name = familyName.Trim(), DefaultLanguage = AppCulture.Normalize(language) };
        _db.Families.Add(family);
        var user = new ParentUser
        {
            FamilyId = family.Id,
            DisplayName = string.IsNullOrWhiteSpace(ownerName) ? address.Split('@')[0] : ownerName.Trim(),
            RoleInFamily = FamilyRole.Owner,
            Permissions = ParentPermissions.All,
            UserName = address,
            Email = address,
            PreferredLanguage = AppCulture.Normalize(language),
            EmailConfirmed = true
        };
        var created = await _users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            _db.Families.Remove(family);
            throw new InvitationException(IdentityErrorText.Describe(created.Errors));
        }

        var child = new Child { FamilyId = family.Id, Name = childName.Trim() };
        _db.Children.Add(child);
        _db.LockStates.Add(new LockState { FamilyId = family.Id, ChildId = child.Id, ManualLock = false, UpdatedUtc = DateTime.UtcNow });
        _db.ScreenTimeLimits.Add(new ScreenTimeLimit { FamilyId = family.Id, ChildId = child.Id });
        _db.AlertPreferences.Add(new AlertPreference { FamilyId = family.Id });
        invitation.UsedUtc = DateTime.UtcNow;
        invitation.CreatedFamilyId = family.Id;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("New family {FamilyId} registered by {Email}", family.Id, address);
        return (user, family.Id);
    }

    private async Task<Invitation> ValidForUpdateAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new InvitationException(AppText.Get("Invite.Missing"));
        var hash = SecretToken.Hash(token);
        var now = DateTime.UtcNow;
        var invitation = await _db.Invitations.FirstOrDefaultAsync(i => i.TokenHash == hash, ct)
            ?? throw new InvitationException(AppText.Get("Invite.Invalid"));
        if (invitation.RevokedUtc is not null) throw new InvitationException(AppText.Get("Invite.Revoked"));
        if (invitation.UsedUtc is not null) throw new InvitationException(AppText.Get("Invite.Used"));
        if (invitation.ExpiresUtc <= now) throw new InvitationException(AppText.Get("Invite.Expired"));
        return invitation;
    }

    public async Task<List<InvitationDto>> ListForFamilyAsync(Guid familyId, CancellationToken ct = default)
    {
        var rows = await _db.Invitations.AsNoTracking()
            .Where(i => i.Kind == InvitationKind.CoParent && i.FamilyId == familyId)
            .OrderByDescending(i => i.CreatedUtc)
            .Take(50)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<List<InvitationDto>> ListAllPendingAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var rows = await _db.Invitations.AsNoTracking()
            .Where(i => i.UsedUtc == null && i.RevokedUtc == null && i.ExpiresUtc > now)
            .OrderByDescending(i => i.CreatedUtc)
            .Take(200)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<bool> RevokeAsync(Guid invitationId, Guid? familyId = null, CancellationToken ct = default)
    {
        var invitation = await _db.Invitations.FirstOrDefaultAsync(i => i.Id == invitationId, ct);
        if (invitation is null || invitation.UsedUtc is not null) return false;
        if (familyId is not null && invitation.FamilyId != familyId) return false;
        invitation.RevokedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<(bool Ok, string? Error, bool EmailSent)> ResendAsync(Guid invitationId, Guid? familyId = null, CancellationToken ct = default)
    {
        var invitation = await _db.Invitations.FirstOrDefaultAsync(i => i.Id == invitationId, ct);
        if (invitation is null) return (false, AppText.Get("Invite.NotFound"), false);
        if (familyId is not null && invitation.FamilyId != familyId) return (false, AppText.Get("Invite.NotFound"), false);
        if (invitation.UsedUtc is not null || invitation.RevokedUtc is not null || invitation.ExpiresUtc <= DateTime.UtcNow)
            return (false, AppText.Get("Invite.CannotResend"), false);
        if (invitation.Email is null) return (false, AppText.Get("Invite.NoEmail"), false);

        var token = _protector.Unprotect(invitation.TokenProtected);
        var sent = await SendInviteEmailAsync(invitation, _renderer.Link($"/invite/{token}"), ct);
        return (true, sent ? null : AppText.Get("Invite.EmailFailed"), sent);
    }

    private async Task<bool> SendInviteEmailAsync(Invitation invitation, string link, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(invitation.Email)) return false;
        var language = await InvitationLanguageAsync(invitation, ct);
        var inviterName = await _db.Users.AsNoTracking()
            .Where(u => u.Id == invitation.CreatedByParentId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct);
        var rendered = invitation.Kind == InvitationKind.NewFamily
            ? _renderer.RenderInviteNewFamily(language, link, invitation.FamilyName, inviterName, invitation.ExpiresUtc)
            : _renderer.RenderInviteCoParent(language, link, invitation.FamilyName ?? "", inviterName, invitation.ExpiresUtc);
        var (sent, _) = await _email.SendAsync(new EmailOutgoing(invitation.Email, rendered), null, ct);
        return sent;
    }

    private async Task<string> InvitationLanguageAsync(Invitation invitation, CancellationToken ct)
    {
        if (invitation.FamilyId is { } familyId)
        {
            var familyLanguage = await _db.Families.AsNoTracking()
                .Where(f => f.Id == familyId).Select(f => f.DefaultLanguage).FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(familyLanguage)) return AppCulture.Normalize(familyLanguage);
        }
        var inviter = await _db.Users.AsNoTracking()
            .Where(u => u.Id == invitation.CreatedByParentId)
            .Select(u => new { u.PreferredLanguage, u.FamilyId })
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(inviter?.PreferredLanguage))
            return AppCulture.Normalize(inviter.PreferredLanguage);
        var inviterFamilyLanguage = inviter is null
            ? null
            : await _db.Families.AsNoTracking().Where(f => f.Id == inviter.FamilyId).Select(f => f.DefaultLanguage).FirstOrDefaultAsync(ct);
        return AppCulture.Normalize(inviterFamilyLanguage);
    }

    private static InvitationDto ToDto(Invitation i) =>
        new(i.Id, i.Kind, i.FamilyId, i.FamilyName, i.Email, i.Permissions, i.CreatedUtc, i.ExpiresUtc, i.UsedUtc, i.RevokedUtc);
}
