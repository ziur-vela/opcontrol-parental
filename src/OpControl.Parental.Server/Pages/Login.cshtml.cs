using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Pages;

public class LoginModel : PageModel
{
    private readonly SignInManager<ParentUser> _signIn;
    private readonly ParentalDbContext _db;

    public LoginModel(SignInManager<ParentUser> signIn, ParentalDbContext db)
    {
        _signIn = signIn;
        _db = db;
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

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await _db.Families.AnyAsync())
            return RedirectToPage("/Setup");
        if (User.Identity?.IsAuthenticated == true)
            return LocalRedirect("/");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        var result = await _signIn.PasswordSignInAsync(Input.Email, Input.Password, Input.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
            return LocalRedirect("/");

        Error = result.IsLockedOut ? "This account is temporarily locked." : "Email or password is incorrect.";
        return Page();
    }
}
