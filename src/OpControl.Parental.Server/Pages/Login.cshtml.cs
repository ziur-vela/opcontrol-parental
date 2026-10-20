using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Server.Data;
using Microsoft.Extensions.Localization;
using OpControl.Parental.Server.Resources;
using OpControl.Parental.Server.Services;

namespace OpControl.Parental.Server.Pages;

public class LoginModel : PageModel
{
    private readonly SignInManager<ParentUser> _signIn;
    private readonly ParentalDbContext _db;
    private readonly LoginThrottle _throttle;
    private readonly IStringLocalizer<AppStrings> _l;

    public LoginModel(SignInManager<ParentUser> signIn, ParentalDbContext db, LoginThrottle throttle, IStringLocalizer<AppStrings> localizer)
    {
        _signIn = signIn;
        _db = db;
        _throttle = throttle;
        _l = localizer;
    }

    [BindProperty]
    public LoginInput Input { get; set; } = new();

    public string? Error { get; set; }

    public sealed class LoginInput
    {
        [Required, EmailAddress]
        public string Email { get; set; } = "";
        [Required, DataType(DataType.Password)]
        public string Password { get; set; } = "";
        public bool RememberMe { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(string? error)
    {
        if (!await _db.Families.AnyAsync())
            return RedirectToPage("/Setup");
        if (error == "suspended") Error = _l["Login.Suspended"];
        if (User.Identity?.IsAuthenticated == true && Error is null)
            return LocalRedirect("/");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        var key = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (_throttle.IsBlocked(key))
        {
            Response.StatusCode = 429;
            Error = _l["Login.TooMany"];
            return Page();
        }

        var result = await _signIn.PasswordSignInAsync(Input.Email, Input.Password, Input.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            _throttle.Reset(key);
            return LocalRedirect("/");
        }
        if (result.RequiresTwoFactor)
        {
            _throttle.Reset(key);
            return RedirectToPage("/LoginTwoFactor", new { rememberMe = Input.RememberMe });
        }

        _throttle.RecordFailure(key);
        Error = result.IsLockedOut ? _l["Login.LockedOut"] : _l["Login.Incorrect"];
        return Page();
    }
}
