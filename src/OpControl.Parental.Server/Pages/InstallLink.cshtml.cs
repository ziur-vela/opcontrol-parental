using Microsoft.AspNetCore.Mvc.RazorPages;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Services;

namespace OpControl.Parental.Server.Pages;

public class InstallLinkModel : PageModel
{
    private readonly InstallService _installs;
    private readonly ReleaseCatalog _releases;

    public InstallLinkModel(InstallService installs, ReleaseCatalog releases)
    {
        _installs = installs;
        _releases = releases;
    }

    public string Token { get; private set; } = "";
    public InstallLinkStatusDto Status { get; private set; } = new("invalid", null, null, DateTime.MinValue);
    public bool IsPhone { get; private set; }
    public bool HasInstaller { get; private set; }

    public async Task OnGetAsync(string token)
    {
        Token = token;
        Status = await _installs.StatusAsync(token);
        var agent = Request.Headers.UserAgent.ToString();
        IsPhone = agent.Contains("Android", StringComparison.OrdinalIgnoreCase)
            || agent.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
            || agent.Contains("iPad", StringComparison.OrdinalIgnoreCase)
            || agent.Contains("Mobile", StringComparison.OrdinalIgnoreCase);
        HasInstaller = _releases.Current()?.SetupPath is not null;
    }
}
