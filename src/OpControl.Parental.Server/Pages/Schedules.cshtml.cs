using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Services;

namespace OpControl.Parental.Server.Pages;

[Authorize]
public class SchedulesModel : PageModel
{
    public static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    private readonly ParentalDbContext _db;
    private readonly FamilyScope _family;
    private readonly LockService _locks;

    public SchedulesModel(ParentalDbContext db, FamilyScope family, LockService locks)
    {
        _db = db;
        _family = family;
        _locks = locks;
    }

    public Guid ChildId { get; set; }
    public string ChildName { get; private set; } = "";
    public List<Schedule> Schedules { get; private set; } = [];
    public string? Error { get; private set; }

    [BindProperty] public string NewName { get; set; } = "";
    [BindProperty] public string NewStart { get; set; } = "21:00";
    [BindProperty] public string NewEnd { get; set; } = "07:00";

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        if (ChildId == Guid.Empty) return RedirectToPage("/Login");
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync(int[] days)
    {
        await LoadAsync();
        if (ChildId == Guid.Empty) return RedirectToPage("/Login");

        if (string.IsNullOrWhiteSpace(NewName) || NewName.Length > 120)
            return Fail("Schedule name is required.");
        if (days.Length == 0)
            return Fail("Pick at least one day.");
        if (!TimeOnly.TryParse(NewStart, out var start) || !TimeOnly.TryParse(NewEnd, out var end))
            return Fail("Start and end must be valid times.");
        var startMinutes = start.Hour * 60 + start.Minute;
        var endMinutes = end.Hour * 60 + end.Minute;
        if (startMinutes == endMinutes)
            return Fail("Start and end cannot be the same.");

        var mask = 0;
        foreach (var day in days.Where(d => d is >= 0 and <= 6)) mask |= 1 << day;
        if (mask == 0) return Fail("Pick at least one day.");

        _db.Schedules.Add(new Schedule
        {
            FamilyId = _family.FamilyId,
            ChildId = ChildId,
            Name = NewName.Trim(),
            DaysMask = mask,
            StartMinutes = startMinutes,
            EndMinutes = endMinutes,
            IsEnabled = true
        });
        await _db.SaveChangesAsync();
        await _locks.PublishDeviceStateAsync(_family.FamilyId, ChildId);
        return RedirectToPage("/Schedules");
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid id)
    {
        await LoadAsync();
        var schedule = await _db.Schedules.FirstOrDefaultAsync(s => s.Id == id && s.FamilyId == _family.FamilyId);
        if (schedule is not null)
        {
            schedule.IsEnabled = !schedule.IsEnabled;
            await _db.SaveChangesAsync();
            await _locks.PublishDeviceStateAsync(_family.FamilyId, schedule.ChildId);
        }
        return RedirectToPage("/Schedules");
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        await LoadAsync();
        var schedule = await _db.Schedules.FirstOrDefaultAsync(s => s.Id == id && s.FamilyId == _family.FamilyId);
        if (schedule is not null)
        {
            _db.Schedules.Remove(schedule);
            await _db.SaveChangesAsync();
            await _locks.PublishDeviceStateAsync(_family.FamilyId, schedule.ChildId);
        }
        return RedirectToPage("/Schedules");
    }

    private IActionResult Fail(string message)
    {
        Error = message;
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
        Schedules = await _db.Schedules.AsNoTracking()
            .Where(s => s.FamilyId == _family.FamilyId && s.ChildId == child.Id)
            .OrderBy(s => s.StartMinutes)
            .ToListAsync();
    }

    public static string TimeLabel(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";

    public static string DaysLabel(int mask)
    {
        if (mask == 0x7F) return "Every day";
        return string.Join(", ", Enumerable.Range(0, 7).Where(i => (mask & (1 << i)) != 0).Select(i => DayNames[i]));
    }
}
