// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Services;
using QRCoder;

namespace OpControl.Parental.Server.Pages;

public class SetupModel : PageModel
{
    private readonly ParentalDbContext _db;
    private readonly UserManager<ParentUser> _users;
    private readonly SignInManager<ParentUser> _signIn;
    private readonly SetupTokenService _tokens;
    private readonly PendingSetupStore _pending;
    private readonly EmailSender _email;

    public SetupModel(ParentalDbContext db, UserManager<ParentUser> users, SignInManager<ParentUser> signIn,
        SetupTokenService tokens, PendingSetupStore pending, EmailSender email)
    {
        _db = db;
        _users = users;
        _signIn = signIn;
        _tokens = tokens;
        _pending = pending;
        _email = email;
    }

    private string BrowserLanguage => AppCulture.FromAcceptLanguage(Request.Headers.AcceptLanguage);

    [BindProperty]
    public SetupInput Input { get; set; } = new();

    public List<string> Errors { get; } = [];
    public int Step { get; private set; } = 1;
    public string QrDataUri { get; private set; } = "";
    public string ManualKey { get; private set; } = "";
    public string? PendingId { get; private set; }

    public sealed class SetupInput
    {
        [Required, MaxLength(128)]
        public string Token { get; set; } = "";
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
        [Required, MaxLength(500)]
        public string PublicBaseUrl { get; set; } = "";
        [MaxLength(255)]
        public string? SmtpHost { get; set; }
        public int SmtpPort { get; set; } = 587;
        public bool SmtpUseSsl { get; set; } = true;
        [MaxLength(255)]
        public string? SmtpUsername { get; set; }
        [MaxLength(255)]
        public string? SmtpPassword { get; set; }
        [MaxLength(255)]
        public string? SmtpFrom { get; set; }
        [MaxLength(120)]
        public string? SmtpFromName { get; set; }
        [MaxLength(10)]
        public string? Code { get; set; }
    }

    private async Task<bool> SetupIsClosedAsync() => await _db.Users.AnyAsync(u => u.IsServerAdmin);

    public async Task<IActionResult> OnGetAsync(string? token)
    {
        if (await SetupIsClosedAsync()) return RedirectToPage("/Login");
        Input.Token = token ?? "";
        Input.PublicBaseUrl = $"{Request.Scheme}://{Request.Host}";
        return Page();
    }

    public async Task<IActionResult> OnPostBeginAsync()
    {
        if (await SetupIsClosedAsync()) return RedirectToPage("/Login");
        if (!_tokens.IsValid(Input.Token))
        {
            Errors.Add(AppText.Get("Setup.Error.Token"));
            Step = 1;
            return Page();
        }
        if (!ModelState.IsValid)
        {
            Step = 1;
            return Page();
        }
        if (!Uri.TryCreate(Input.PublicBaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            Errors.Add(AppText.Get("Setup.Error.BaseUrl"));
            Step = 1;
            return Page();
        }

        var key = Totp.NewSecret();
        PendingId = _pending.Add(new PendingSetup(
            Input.FamilyName.Trim(), Input.DisplayName.Trim(), Input.ChildName.Trim(), Input.Email.Trim().ToLowerInvariant(),
            Input.Password, Input.PublicBaseUrl.TrimEnd('/'), NullIfEmpty(Input.SmtpHost), Input.SmtpPort, Input.SmtpUseSsl,
            NullIfEmpty(Input.SmtpUsername), NullIfEmpty(Input.SmtpPassword), NullIfEmpty(Input.SmtpFrom),
            NullIfEmpty(Input.SmtpFromName), key));
        ManualKey = Totp.FormatSecret(key);
        QrDataUri = BuildQr(key, Input.Email.Trim());
        Step = 2;
        return Page();
    }

    public async Task<IActionResult> OnPostCompleteAsync(string? pendingId)
    {
        if (await SetupIsClosedAsync()) return RedirectToPage("/Login");
        var pending = _pending.Get(pendingId);
        if (pending is null)
        {
            Errors.Add(AppText.Get("Setup.Error.Expired"));
            Step = 1;
            return Page();
        }
        PendingId = pendingId;
        if (!Totp.Verify(pending.AuthenticatorKey, Input.Code))
        {
            Errors.Add(AppText.Get("Setup.Error.Code"));
            Step = 2;
            ManualKey = Totp.FormatSecret(pending.AuthenticatorKey);
            QrDataUri = BuildQr(pending.AuthenticatorKey, pending.Email);
            return Page();
        }

        var family = new Family { Name = pending.FamilyName, DefaultLanguage = BrowserLanguage };
        _db.Families.Add(family);
        var user = new ParentUser
        {
            FamilyId = family.Id,
            DisplayName = pending.DisplayName,
            RoleInFamily = FamilyRole.Owner,
            UserName = pending.Email,
            Email = pending.Email,
            PreferredLanguage = BrowserLanguage,
            EmailConfirmed = true,
            IsServerAdmin = true
        };
        var created = await _users.CreateAsync(user, pending.Password);
        if (!created.Succeeded)
        {
            Errors.Add(IdentityErrorText.Describe(created.Errors));
            _db.Families.Remove(family);
            Step = 2;
            PendingId = pendingId;
            return Page();
        }

        await _users.SetAuthenticationTokenAsync(user, "[AspNetUserStore]", "AuthenticatorKey", pending.AuthenticatorKey);
        await _users.SetTwoFactorEnabledAsync(user, true);

        var child = new Child { FamilyId = family.Id, Name = pending.ChildName };
        _db.Children.Add(child);
        _db.LockStates.Add(new LockState { FamilyId = family.Id, ChildId = child.Id, ManualLock = false, UpdatedUtc = DateTime.UtcNow });
        var settings = await _db.ServerSettings.FirstOrDefaultAsync();
        if (settings is null)
        {
            settings = new ServerSettings();
            _db.ServerSettings.Add(settings);
        }
        settings.PublicBaseUrl = pending.PublicBaseUrl;
        settings.SetupCompletedUtc = DateTime.UtcNow;
        settings.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        if (!string.IsNullOrWhiteSpace(pending.SmtpHost))
        {
            try
            {
                await _email.SaveSettingsAsync(pending.SmtpHost, pending.SmtpPort, pending.SmtpUseSsl,
                    pending.SmtpUsername, pending.SmtpPassword, pending.SmtpFrom, pending.SmtpFromName);
            }
            catch (Exception ex)
            {
                Errors.Add(AppText.Get("Setup.Error.Smtp", ex.Message));
            }
        }

        EmailRenderer.DatabaseBaseUrl = pending.PublicBaseUrl;
        _pending.Remove(pendingId);
        await _signIn.SignInAsync(user, isPersistent: false);
        return LocalRedirect("/");
    }

    private string BuildQr(string key, string email)
    {
        var issuer = "Parental OpControl";
        var uri = $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(email)}?secret={Uri.EscapeDataString(key)}&issuer={Uri.EscapeDataString(issuer)}&digits=6&period=30";
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(8);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
