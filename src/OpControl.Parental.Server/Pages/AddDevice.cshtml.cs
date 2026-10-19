using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpControl.Parental.Server.Auth;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Services;

namespace OpControl.Parental.Server.Pages;

[Authorize(Policy = ParentalPolicies.ManageDevices)]
public class AddDeviceModel : PageModel
{
    private readonly ParentalDbContext _db;
    private readonly FamilyScope _family;
    private readonly InstallService _installs;
    private readonly ILogger<AddDeviceModel> _logger;

    public AddDeviceModel(ParentalDbContext db, FamilyScope family, InstallService installs, ILogger<AddDeviceModel> logger)
    {
        _db = db;
        _family = family;
        _installs = installs;
        _logger = logger;
    }

    [BindProperty]
    public Guid ChildId { get; set; }

    [BindProperty]
    public DevicePlatform Platform { get; set; } = DevicePlatform.Windows;

    public List<Child> Children { get; private set; } = [];
    public List<InstallLinkDto> Links { get; private set; } = [];
    public InstallLinkCreatedDto? Created { get; private set; }
    public string BaseUrl { get; private set; } = "";
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (_family.FamilyId == Guid.Empty)
            return RedirectToPage("/Login");
        await LoadAsync();
        if (Children.Count > 0)
            ChildId = Children[0].Id;
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        await LoadAsync();
        try
        {
            Created = await _installs.CreateAsync(_family.FamilyId, ChildId, _family.ParentId, Platform);
            _logger.LogInformation("Install link generated for child {ChildId} by parent {ParentId}", ChildId, _family.ParentId);
            Links = await _installs.ListAsync(_family.FamilyId);
        }
        catch (Exception ex) when (ex is EnrollmentException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid linkId)
    {
        await _installs.RevokeAsync(_family.FamilyId, linkId);
        return RedirectToPage("/AddDevice");
    }

    private async Task LoadAsync()
    {
        Children = await _db.Children.AsNoTracking()
            .Where(c => c.FamilyId == _family.FamilyId)
            .OrderBy(c => c.CreatedUtc)
            .ToListAsync();
        Links = await _installs.ListAsync(_family.FamilyId);
        BaseUrl = _installs.BaseUrl;
    }
}
