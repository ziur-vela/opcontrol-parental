using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OpControl.Parental.Server.Data;
using QRCoder;

namespace OpControl.Parental.Server.Pages;

[Authorize]
public class AccountTwoFactorModel : PageModel
{
    private readonly UserManager<ParentUser> _users;
    private readonly SignInManager<ParentUser> _signIn;

    public AccountTwoFactorModel(UserManager<ParentUser> users, SignInManager<ParentUser> signIn)
    {
        _users = users;
        _signIn = signIn;
    }

    public bool IsEnabled { get; private set; }
    public string QrDataUri { get; private set; } = "";
    public string ManualKey { get; private set; } = "";
    public List<string> RecoveryCodes { get; private set; } = [];
    public string? Message { get; private set; }
    public bool IsError { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _users.GetUserAsync(User);
        if (user is null) return RedirectToPage("/Login");
        await LoadAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostEnableAsync(string code)
    {
        var user = await _users.GetUserAsync(User);
        if (user is null) return RedirectToPage("/Login");

        var key = await _users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await _users.ResetAuthenticatorKeyAsync(user);
            key = await _users.GetAuthenticatorKeyAsync(user);
        }

        var valid = await _users.VerifyTwoFactorTokenAsync(user, _users.Options.Tokens.AuthenticatorTokenProvider, code?.Replace(" ", "") ?? "");
        if (!valid)
        {
            Message = "The code is not valid. Check the device clock and try again.";
            IsError = true;
            await LoadAsync(user);
            return Page();
        }

        await _users.SetTwoFactorEnabledAsync(user, true);
        var codes = await _users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        RecoveryCodes = codes?.ToList() ?? [];
        Message = "Two-factor authentication enabled. Save your recovery codes now.";
        await LoadAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostDisableAsync()
    {
        var user = await _users.GetUserAsync(User);
        if (user is null) return RedirectToPage("/Login");
        await _users.SetTwoFactorEnabledAsync(user, false);
        await _users.ResetAuthenticatorKeyAsync(user);
        Message = "Two-factor authentication disabled.";
        await LoadAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostRegenerateRecoveryCodesAsync()
    {
        var user = await _users.GetUserAsync(User);
        if (user is null) return RedirectToPage("/Login");
        if (!await _users.GetTwoFactorEnabledAsync(user))
        {
            Message = "Enable two-factor authentication first.";
            IsError = true;
            await LoadAsync(user);
            return Page();
        }
        var codes = await _users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        RecoveryCodes = codes?.ToList() ?? [];
        Message = "New recovery codes generated. The previous codes no longer work.";
        await LoadAsync(user);
        return Page();
    }

    private async Task LoadAsync(ParentUser user)
    {
        IsEnabled = await _users.GetTwoFactorEnabledAsync(user);
        var key = await _users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await _users.ResetAuthenticatorKeyAsync(user);
            key = await _users.GetAuthenticatorKeyAsync(user);
        }

        ManualKey = FormatKey(key ?? "");
        var issuer = "Parental OpControl";
        var uri = $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(user.Email ?? user.UserName ?? "parent")}?secret={Uri.EscapeDataString(key ?? "")}&issuer={Uri.EscapeDataString(issuer)}&digits=6&period=30";
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(8);
        QrDataUri = "data:image/png;base64," + Convert.ToBase64String(png);
    }

    private static string FormatKey(string key) =>
        string.Join(" ", Enumerable.Range(0, (key.Length + 3) / 4).Select(i => key.Substring(i * 4, Math.Min(4, key.Length - i * 4))));
}
