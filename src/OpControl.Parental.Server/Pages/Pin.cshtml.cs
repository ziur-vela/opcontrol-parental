using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpControl.Parental.Server.Auth;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Services;

namespace OpControl.Parental.Server.Pages;

[Authorize(Policy = ParentalPolicies.ManagePin)]
public class PinModel : PageModel
{
    private readonly ParentalDbContext _db;
    private readonly FamilyScope _family;
    private readonly LockService _locks;

    public PinModel(ParentalDbContext db, FamilyScope family, LockService locks)
    {
        _db = db;
        _family = family;
        _locks = locks;
    }

    public Guid ChildId { get; set; }
    public string ChildName { get; private set; } = "";
    public bool HasPin { get; private set; }
    public string? Message { get; private set; }
    public bool IsError { get; private set; }

    [BindProperty] public string NewPin { get; set; } = "";
    [BindProperty] public string ConfirmPin { get; set; } = "";

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        if (ChildId == Guid.Empty) return RedirectToPage("/Login");
        return Page();
    }

    public async Task<IActionResult> OnPostSetAsync()
    {
        await LoadAsync();
        if (ChildId == Guid.Empty) return RedirectToPage("/Login");
        if (NewPin != ConfirmPin)
            return Fail("The PINs do not match.");
        try
        {
            await _locks.SetPinAsync(_family.FamilyId, ChildId, NewPin);
            Message = "PIN saved and synced to the child's devices.";
            HasPin = true;
        }
        catch (InvalidOperationException ex)
        {
            return Fail(ex.Message);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostClearAsync()
    {
        await LoadAsync();
        if (ChildId == Guid.Empty) return RedirectToPage("/Login");
        await _locks.ClearPinAsync(_family.FamilyId, ChildId);
        Message = "PIN cleared.";
        HasPin = false;
        return Page();
    }

    private IActionResult Fail(string message)
    {
        Message = message;
        IsError = true;
        return Page();
    }

    private async Task LoadAsync()
    {
        var child = await _db.Children.AsNoTracking()
            .Where(c => c.FamilyId == _family.FamilyId)
            .OrderBy(c => c.CreatedUtc)
            .FirstOrDefaultAsync();
        if (child is null) return;
        ChildId = child.Id;
        ChildName = child.Name;
        HasPin = await _locks.HasPinAsync(_family.FamilyId, child.Id);
    }
}
