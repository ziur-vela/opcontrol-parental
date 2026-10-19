using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Services;

namespace OpControl.Parental.Server.Pages;

public class LoginTwoFactorModel : PageModel
{
    private readonly SignInManager<ParentUser> _signIn;
    private readonly LoginThrottle _throttle;

    public LoginTwoFactorModel(SignInManager<ParentUser> signIn, LoginThrottle throttle)
    {
        _signIn = signIn;
        _throttle = throttle;
    }

    [BindProperty] public string Code { get; set; } = "";
    [BindProperty] public bool RememberMe { get; set; }
    [BindProperty] public bool UseRecoveryCode { get; set; }
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(bool rememberMe = false)
    {
        var user = await _signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null) return RedirectToPage("/Login");
        RememberMe = rememberMe;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await _signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null) return RedirectToPage("/Login");

        var key = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (_throttle.IsBlocked(key))
        {
            Response.StatusCode = 429;
            Error = "Too many attempts. Try again in a few minutes.";
            return Page();
        }

        var result = UseRecoveryCode
            ? await _signIn.TwoFactorRecoveryCodeSignInAsync(Code?.Trim() ?? "")
            : await _signIn.TwoFactorAuthenticatorSignInAsync(Code?.Replace(" ", "") ?? "", RememberMe, rememberClient: false);

        if (result.Succeeded)
        {
            _throttle.Reset(key);
            return LocalRedirect("/");
        }

        _throttle.RecordFailure(key);
        Error = result.IsLockedOut ? "This account is temporarily locked." : "The code is not valid.";
        return Page();
    }
}
