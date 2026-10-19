using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Pages;

public class LogoutModel : PageModel
{
    private readonly SignInManager<ParentUser> _signIn;

    public LogoutModel(SignInManager<ParentUser> signIn) => _signIn = signIn;

    public async Task<IActionResult> OnPostAsync()
    {
        await _signIn.SignOutAsync();
        return RedirectToPage("/Login");
    }
}
