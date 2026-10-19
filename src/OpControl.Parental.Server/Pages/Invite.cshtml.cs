using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Services;

namespace OpControl.Parental.Server.Pages;

public class InviteModel : PageModel
{
    private readonly InvitationService _invites;
    private readonly SignInManager<ParentUser> _signIn;
    private readonly LoginThrottle _throttle;

    public InviteModel(InvitationService invites, SignInManager<ParentUser> signIn, LoginThrottle throttle)
    {
        _invites = invites;
        _signIn = signIn;
        _throttle = throttle;
    }

    [BindProperty(SupportsGet = true)]
    public string Token { get; set; } = "";

    [BindProperty]
    public InviteInput Input { get; set; } = new();

    public InvitationKind? Kind { get; private set; }
    public string? InvitationFamilyName { get; private set; }
    public string? InvitationEmail { get; private set; }
    public string? Error { get; private set; }
    public bool Invalid { get; private set; }

    public sealed class InviteInput
    {
        [Required, MaxLength(120)]
        public string DisplayName { get; set; } = "";
        [Required, EmailAddress, MaxLength(255)]
        public string Email { get; set; } = "";
        [Required, MinLength(8), DataType(DataType.Password)]
        public string Password { get; set; } = "";
        [Required, Compare(nameof(Password)), DataType(DataType.Password)]
        public string ConfirmPassword { get; set; } = "";
        [MaxLength(120)]
        public string FamilyName { get; set; } = "";
        [MaxLength(120)]
        public string ChildName { get; set; } = "";
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var invitation = await _invites.FindValidAsync(Token, ct);
        if (invitation is null)
        {
            Invalid = true;
            return Page();
        }
        Kind = invitation.Kind;
        InvitationFamilyName = invitation.FamilyName;
        InvitationEmail = invitation.Email;
        Input.Email = invitation.Email ?? "";
        Input.FamilyName = invitation.FamilyName ?? "";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var invitation = await _invites.FindValidAsync(Token, ct);
        if (invitation is null)
        {
            Invalid = true;
            return Page();
        }
        Kind = invitation.Kind;
        InvitationFamilyName = invitation.FamilyName;
        InvitationEmail = invitation.Email;

        var key = "invite:" + (HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        if (_throttle.IsBlocked(key))
        {
            Response.StatusCode = 429;
            Error = "Too many attempts. Try again in a few minutes.";
            return Page();
        }

        if (!ModelState.IsValid)
            return Page();

        try
        {
            ParentUser user;
            if (invitation.Kind == InvitationKind.NewFamily)
            {
                (user, _) = await _invites.AcceptNewFamilyAsync(Token, Input.FamilyName, Input.DisplayName, Input.Email, Input.Password, Input.ChildName, ct);
            }
            else
            {
                (user, _) = await _invites.AcceptCoParentAsync(Token, Input.Email, Input.DisplayName, Input.Password, ct);
            }
            _throttle.Reset(key);
            await _signIn.SignInAsync(user, isPersistent: false);
            return LocalRedirect("/");
        }
        catch (InvitationException ex)
        {
            _throttle.RecordFailure(key);
            Error = ex.Message;
            return Page();
        }
    }
}
