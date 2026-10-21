using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using OpControl.Parental.Server.Auth;
using OpControl.Parental.Server.Resources;
using OpControl.Parental.Server.Services;

namespace OpControl.Parental.Server.Pages;

[Authorize(Policy = ParentalPolicies.ServerAdmin)]
public class EmailPreviewModel : PageModel
{
    private readonly EmailRenderer _renderer;
    private readonly EmailSender _sender;
    private readonly IStringLocalizer<AppStrings> _l;

    public EmailPreviewModel(EmailRenderer renderer, EmailSender sender, IStringLocalizer<AppStrings> localizer)
    {
        _renderer = renderer;
        _sender = sender;
        _l = localizer;
    }

    public sealed record Preview(EmailTemplateKind Kind, string TitleKey, string EnglishSubject, string EnglishHtml,
        string EnglishText, string SpanishSubject, string SpanishHtml, string SpanishText);

    public List<Preview> Previews { get; private set; } = [];

    [TempData]
    public string? Notice { get; set; }

    [TempData]
    public string? Error { get; set; }

    public void OnGet()
    {
        Previews = Enum.GetValues<EmailTemplateKind>().Select(kind =>
        {
            var english = _renderer.RenderSample(kind, AppCulture.English);
            var spanish = _renderer.RenderSample(kind, AppCulture.Spanish);
            return new Preview(kind, TitleKey(kind), english.Subject, english.HtmlBody, english.TextBody,
                spanish.Subject, spanish.HtmlBody, spanish.TextBody);
        }).ToList();
    }

    public async Task<IActionResult> OnPostSendAsync(string kind, string language, CancellationToken ct)
    {
        var address = User.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(address))
        {
            Error = _l["EmailPreview.NoEmail"];
            return RedirectToPage();
        }
        if (!Enum.TryParse<EmailTemplateKind>(kind, true, out var template))
            return BadRequest();

        var rendered = _renderer.RenderSample(template, AppCulture.Normalize(language));
        var (sent, sendError) = await _sender.SendAsync(new EmailOutgoing(address, rendered), null, ct);
        if (sent)
            Notice = _l["EmailPreview.Sent", address];
        else
            Error = _l["EmailPreview.Failed", sendError ?? ""];
        return RedirectToPage();
    }

    private static string TitleKey(EmailTemplateKind kind) => kind switch
    {
        EmailTemplateKind.InviteNewFamily => "EmailPreview.InviteNewFamily",
        EmailTemplateKind.InviteCoParent => "EmailPreview.InviteCoParent",
        EmailTemplateKind.Alert => "EmailPreview.Alert",
        EmailTemplateKind.Digest => "EmailPreview.Digest",
        EmailTemplateKind.Test => "EmailPreview.Test",
        _ => "EmailPreview.Title"
    };
}
