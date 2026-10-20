using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Services;

namespace OpControl.Parental.Server.Pages;

public class SetupModel : PageModel
{
    private readonly ParentalDbContext _db;
    private readonly UserManager<ParentUser> _users;
    private readonly SignInManager<ParentUser> _signIn;

    public SetupModel(ParentalDbContext db, UserManager<ParentUser> users, SignInManager<ParentUser> signIn)
    {
        _db = db;
        _users = users;
        _signIn = signIn;
    }

    private string BrowserLanguage => AppCulture.FromAcceptLanguage(Request.Headers.AcceptLanguage);

    [BindProperty]
    public SetupInput Input { get; set; } = new();

    public List<string> Errors { get; } = [];

    public sealed class SetupInput
    {
        [Required, MaxLength(120)]
        public string FamilyName { get; set; } = "";
        [Required, MaxLength(120)]
        public string DisplayName { get; set; } = "";
        [Required, MaxLength(120)]
        public string ChildName { get; set; } = "";
        [Required, EmailAddress]
        public string Email { get; set; } = "";
        [Required, MinLength(8), DataType(DataType.Password)]
        public string Password { get; set; } = "";
        [Required, Compare(nameof(Password)), DataType(DataType.Password)]
        public string ConfirmPassword { get; set; } = "";
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await _db.Families.AnyAsync())
            return RedirectToPage("/Login");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await _db.Families.AnyAsync())
            return RedirectToPage("/Login");
        if (!ModelState.IsValid)
            return Page();

        var family = new Family { Name = Input.FamilyName.Trim(), DefaultLanguage = BrowserLanguage };
        _db.Families.Add(family);

        var user = new ParentUser
        {
            FamilyId = family.Id,
            DisplayName = Input.DisplayName.Trim(),
            RoleInFamily = FamilyRole.Owner,
            UserName = Input.Email.Trim(),
            Email = Input.Email.Trim(),
            PreferredLanguage = BrowserLanguage,
            EmailConfirmed = true
        };

        var created = await _users.CreateAsync(user, Input.Password);
        if (!created.Succeeded)
        {
            Errors.Add(IdentityErrorText.Describe(created.Errors));
            _db.Families.Remove(family);
            await _db.SaveChangesAsync();
            return Page();
        }

        var child = new Child { FamilyId = family.Id, Name = Input.ChildName.Trim() };
        _db.Children.Add(child);
        _db.LockStates.Add(new LockState { FamilyId = family.Id, ChildId = child.Id, ManualLock = false, UpdatedUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        await _signIn.SignInAsync(user, isPersistent: false);
        return LocalRedirect("/");
    }
}
