using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Services;

namespace OpControl.Parental.Server.Pages;

[Authorize]
public class AddDeviceModel : PageModel
{
    private readonly ParentalDbContext _db;
    private readonly FamilyScope _family;
    private readonly EnrollmentService _enrollment;
    private readonly ILogger<AddDeviceModel> _logger;

    public AddDeviceModel(ParentalDbContext db, FamilyScope family, EnrollmentService enrollment, ILogger<AddDeviceModel> logger)
    {
        _db = db;
        _family = family;
        _enrollment = enrollment;
        _logger = logger;
    }

    [BindProperty]
    public Guid ChildId { get; set; }

    public List<Child> Children { get; private set; } = [];
    public string? Code { get; private set; }
    public DateTime? ExpiresUtc { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (_family.FamilyId == Guid.Empty)
            return RedirectToPage("/Login");
        await LoadChildrenAsync();
        if (Children.Count > 0)
            ChildId = Children[0].Id;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadChildrenAsync();
        try
        {
            var (code, expires) = await _enrollment.CreateCodeAsync(_family.FamilyId, ChildId, _family.ParentId);
            Code = code;
            ExpiresUtc = expires;
            _logger.LogInformation("Enrollment code generated for child {ChildId} by parent {ParentId}", ChildId, _family.ParentId);
        }
        catch (Exception ex) when (ex is EnrollmentException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
        return Page();
    }

    private async Task LoadChildrenAsync()
    {
        Children = await _db.Children.AsNoTracking()
            .Where(c => c.FamilyId == _family.FamilyId)
            .OrderBy(c => c.CreatedUtc)
            .ToListAsync();
    }
}
