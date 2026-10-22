// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpControl.Parental.Server.Auth;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Services;
using Microsoft.Extensions.Localization;
using OpControl.Parental.Server.Resources;

namespace OpControl.Parental.Server.Pages;

[Authorize(Policy = ParentalPolicies.ManagePin)]
public class PinModel : PageModel
{
    private readonly ParentalDbContext _db;
    private readonly FamilyScope _family;
    private readonly LockService _locks;
    private readonly IStringLocalizer<AppStrings> _l;

    public PinModel(ParentalDbContext db, FamilyScope family, LockService locks, IStringLocalizer<AppStrings> localizer)
    {
        _db = db;
        _family = family;
        _locks = locks;
        _l = localizer;
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
            return Fail(_l["Pin.Mismatch"]);
        try
        {
            await _locks.SetPinAsync(_family.FamilyId, ChildId, NewPin);
            Message = _l["Pin.Saved"];
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
        Message = _l["Pin.Cleared"];
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
