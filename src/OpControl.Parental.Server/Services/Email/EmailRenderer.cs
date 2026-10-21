using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using OpControl.Parental.Contracts;

namespace OpControl.Parental.Server.Services;

public sealed class EmailRenderer
{
    private const string BrandGreen = "#00C853";
    private const string OnBrand = "#04150B";
    private const string Background = "#F1F4F1";
    private const string Card = "#FFFFFF";
    private const string Text = "#12201B";
    private const string Muted = "#4C6259";
    private const string Faint = "#687D74";
    private const string Line = "#D5DED8";
    private const string Panel = "#F5F8F5";
    private const string LinkColor = "#0B6B57";
    private const string FontStack = "-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif";
    private const string LogoWidth = "175";
    private const string LogoHeight = "60";

    private readonly EmailOptions _options;
    private readonly IConfiguration _configuration;

    public EmailRenderer(IOptions<EmailOptions> options, IConfiguration configuration)
    {
        _options = options.Value;
        _configuration = configuration;
    }

    public string BaseUrl => (_configuration["PublicBaseUrl"] ?? "").TrimEnd('/');

    public bool Staging => _options.Staging;

    public string LogoUrl => string.IsNullOrWhiteSpace(_options.LogoUrl)
        ? $"{BaseUrl}/brand/email-lockup.png"
        : _options.LogoUrl.Trim();

    public string Link(string path) => $"{BaseUrl}{(path.StartsWith('/') ? path : "/" + path)}";

    public string SettingsUrl => Link("/settings");

    public string AlertsUrl => Link("/alerts");

    public string MessagesUrl => Link("/messages");

    public string DashboardUrl => Link("/");

    public (bool Ok, string? Error) ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
            return (false, AppText.Get("Email.Error.BaseUrlMissing"));

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri))
            return (false, AppText.Get("Email.Error.BaseUrlMissing"));

        if (!Staging && !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return (false, AppText.Get("Email.Error.BaseUrlNotHttps"));

        if (!Uri.TryCreate(LogoUrl, UriKind.Absolute, out var logoUri)
            || !string.Equals(logoUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return (false, AppText.Get("Email.Error.LogoNotHttps"));

        return (true, null);
    }

    public RenderedEmail RenderInviteNewFamily(string? language, string link, string? familyName, string? inviterName, DateTime expiresUtc, string? timeZoneId = null)
    {
        var lang = AppCulture.Normalize(language);
        var subject = AppText.Get("Email.InviteNewFamilySubject", lang);
        var preheader = AppText.Get("Email.InviteNewFamily.Preheader", lang);
        var headline = AppText.Get("Email.InviteNewFamily.Headline", lang);
        var paragraphs = new List<string> { AppText.Get("Email.InviteNewFamily.Intro", lang) };
        var details = new List<EmailDetail>();
        if (!string.IsNullOrWhiteSpace(familyName))
            details.Add(new EmailDetail(AppText.Get("Email.Invite.Detail.Family", lang), familyName));
        if (!string.IsNullOrWhiteSpace(inviterName))
            details.Add(new EmailDetail(AppText.Get("Email.Invite.Detail.InvitedBy", lang), inviterName));
        details.Add(new EmailDetail(AppText.Get("Email.Invite.Detail.Expires", lang), FormatTime(expiresUtc, timeZoneId, lang)));

        return Build(lang, EmailTemplateKind.InviteNewFamily, subject, preheader, headline, paragraphs,
            AppText.Get("Email.InviteNewFamily.Button", lang), link, details,
            AppText.Get("Email.Layout.Footer.Invite", lang), false, null);
    }

    public RenderedEmail RenderInviteCoParent(string? language, string link, string familyName, string? inviterName, DateTime expiresUtc, string? timeZoneId = null)
    {
        var lang = AppCulture.Normalize(language);
        var subject = AppText.Get("Email.InviteCoParentSubject", lang);
        var preheader = AppText.Get("Email.InviteCoParent.Preheader", lang, familyName);
        var headline = AppText.Get("Email.InviteCoParent.Headline", lang, familyName);
        var paragraphs = new List<string> { AppText.Get("Email.InviteCoParent.Intro", lang, familyName) };
        var details = new List<EmailDetail>
        {
            new(AppText.Get("Email.Invite.Detail.Family", lang), familyName)
        };
        if (!string.IsNullOrWhiteSpace(inviterName))
            details.Add(new EmailDetail(AppText.Get("Email.Invite.Detail.InvitedBy", lang), inviterName));
        details.Add(new EmailDetail(AppText.Get("Email.Invite.Detail.Expires", lang), FormatTime(expiresUtc, timeZoneId, lang)));

        return Build(lang, EmailTemplateKind.InviteCoParent, subject, preheader, headline, paragraphs,
            AppText.Get("Email.InviteCoParent.Button", lang), link, details,
            AppText.Get("Email.Layout.Footer.Invite", lang), false, null);
    }

    public RenderedEmail RenderAlert(string? language, string title, string detail, string childName,
        string? deviceName, string time, string dashboardUrl)
    {
        var lang = AppCulture.Normalize(language);
        var subject = AppText.Get("Email.Alert.Subject", lang, title);
        var preheader = AppText.Get("Email.Alert.Preheader", lang, childName);
        var paragraphs = string.IsNullOrWhiteSpace(detail) ? [] : new List<string> { detail };
        var details = new List<EmailDetail>
        {
            new(AppText.Get("Email.Alert.Detail.Child", lang), childName)
        };
        if (!string.IsNullOrWhiteSpace(deviceName))
            details.Add(new EmailDetail(AppText.Get("Email.Alert.Detail.Device", lang), deviceName));
        details.Add(new EmailDetail(AppText.Get("Email.Alert.Detail.Time", lang), time));

        return Build(lang, EmailTemplateKind.Alert, subject, preheader, title, paragraphs,
            AppText.Get("Email.Alert.Button", lang), dashboardUrl, details,
            AppText.Get("Email.Layout.Footer.Alert", lang), true, SettingsUrl);
    }

    public RenderedEmail RenderMessage(string? language, string childName, string messageText, string time, string messagesUrl)
    {
        var lang = AppCulture.Normalize(language);
        var subject = AppText.Get("Email.MessageSubject", lang, childName);
        var preheader = AppText.Get("Email.Message.Preheader", lang, childName);
        var headline = AppText.Get("Email.Message.Headline", lang, childName);
        var paragraphs = new List<string> { messageText };
        var details = new List<EmailDetail>
        {
            new(AppText.Get("Email.Alert.Detail.Child", lang), childName),
            new(AppText.Get("Email.Alert.Detail.Time", lang), time)
        };
        return Build(lang, EmailTemplateKind.Message, subject, preheader, headline, paragraphs,
            AppText.Get("Email.Message.Button", lang), messagesUrl, details,
            AppText.Get("Email.Layout.Footer.Alert", lang), true, null);
    }

    public RenderedEmail RenderDigest(string? language, int totalCount, IReadOnlyList<EmailDigestChild> groups, string dashboardUrl)
    {
        var lang = AppCulture.Normalize(language);
        var subject = AppText.Get("Email.DigestSubject", lang);
        var preheader = AppText.Get("Email.Digest.Preheader", lang, totalCount.ToString(AppCulture.Culture(lang)));
        var headline = AppText.Get("Email.Digest.Headline", lang);
        var intro = AppText.Get(totalCount == 1 ? "Email.DigestIntroOne" : "Email.DigestIntroMany", lang,
            totalCount.ToString(AppCulture.Culture(lang)));

        var html = new StringBuilder();
        html.Append("<p style=\"margin:0 0 20px;font-family:").Append(FontStack)
            .Append(";font-size:15px;line-height:24px;color:").Append(Muted).Append(";\">")
            .Append(Html(intro)).Append("</p>");

        var text = new List<string> { intro };
        foreach (var group in groups)
        {
            html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"margin:0 0 18px;\">")
                .Append("<tr><td style=\"padding:12px 0 6px;border-top:1px solid ").Append(Line).Append(";\">")
                .Append("<span style=\"font-family:").Append(FontStack).Append(";font-size:16px;line-height:22px;font-weight:800;color:")
                .Append(Text).Append(";\">").Append(Html(group.ChildName)).Append("</span>")
                .Append("<span style=\"font-family:").Append(FontStack).Append(";font-size:12px;line-height:18px;color:")
                .Append(Faint).Append(";\"> &middot; ")
                .Append(Html(AppText.Get(group.Count == 1 ? "Email.Digest.CountOne" : "Email.Digest.CountMany", lang,
                    group.Count.ToString(AppCulture.Culture(lang))))).Append("</span></td></tr>");

            text.Add($"{group.ChildName} · {AppText.Get(group.Count == 1 ? "Email.Digest.CountOne" : "Email.Digest.CountMany", lang, group.Count.ToString(AppCulture.Culture(lang)))}");

            foreach (var item in group.Items)
            {
                html.Append("<tr><td style=\"padding:4px 0 4px 10px;font-family:").Append(FontStack)
                    .Append(";font-size:13px;line-height:20px;color:").Append(Text).Append(";\">")
                    .Append("<strong>").Append(Html(item.Title)).Append("</strong>")
                    .Append("<span style=\"color:").Append(Faint).Append(";\"> &middot; ").Append(Html(item.Time)).Append("</span>");
                if (!string.IsNullOrWhiteSpace(item.Detail))
                    html.Append("<br /><span style=\"color:").Append(Muted).Append(";\">").Append(Html(item.Detail)).Append("</span>");
                html.Append("</td></tr>");
                text.Add($"- {item.Title} ({item.Time})");
                if (!string.IsNullOrWhiteSpace(item.Detail)) text.Add($"  {item.Detail}");
            }

            if (group.Remaining > 0)
            {
                var more = AppText.Get("Email.Digest.MoreItems", lang, group.Remaining.ToString(AppCulture.Culture(lang)));
                html.Append("<tr><td style=\"padding:4px 0 4px 10px;font-family:").Append(FontStack)
                    .Append(";font-size:12px;line-height:18px;color:").Append(Faint).Append(";\">")
                    .Append(Html(more)).Append("</td></tr>");
                text.Add($"  {more}");
            }

            html.Append("</table>");
            text.Add("");
        }

        return Build(lang, EmailTemplateKind.Digest, subject, preheader, headline, html.ToString(), text,
            AppText.Get("Email.Digest.Button", lang), dashboardUrl, [],
            AppText.Get("Email.Layout.Footer.Alert", lang), true, SettingsUrl);
    }

    public RenderedEmail RenderTest(string? language, string? recipientName, DateTime utcNow)
    {
        var lang = AppCulture.Normalize(language);
        var subject = AppText.Get("Email.TestSubject", lang);
        var preheader = AppText.Get("Email.Test.Preheader", lang);
        var headline = AppText.Get("Email.Test.Headline", lang);
        var paragraphs = new List<string> { AppText.Get("Email.Test.Intro", lang) };
        var details = new List<EmailDetail>
        {
            new(AppText.Get("Email.Test.Detail.Server", lang), BaseUrl),
            new(AppText.Get("Email.Test.Detail.Time", lang), FormatUtc(utcNow, lang)),
            new(AppText.Get("Email.Test.Detail.Language", lang), AppText.Get(lang == AppCulture.Spanish ? "Language.Spanish" : "Language.English", lang))
        };
        if (!string.IsNullOrWhiteSpace(recipientName))
            details.Insert(0, new EmailDetail(AppText.Get("Email.Test.Detail.Recipient", lang), recipientName));

        return Build(lang, EmailTemplateKind.Test, subject, preheader, headline, paragraphs,
            AppText.Get("Email.Test.Button", lang), DashboardUrl, details,
            AppText.Get("Email.Layout.Footer.Test", lang), false, null);
    }

    public RenderedEmail RenderSample(EmailTemplateKind kind, string? language)
    {
        var lang = AppCulture.Normalize(language);
        var now = DateTime.UtcNow;
        return kind switch
        {
            EmailTemplateKind.InviteNewFamily => RenderInviteNewFamily(lang, Link("/invite/sample-preview-token"),
                "Familia Ruiz", "Hendry", now.AddDays(7)),
            EmailTemplateKind.InviteCoParent => RenderInviteCoParent(lang, Link("/invite/sample-preview-token"),
                "Familia Ruiz", "Hendry", now.AddHours(72)),
            EmailTemplateKind.Alert => RenderSampleAlert(lang, now),
            EmailTemplateKind.Digest => RenderSampleDigest(lang, now),
            EmailTemplateKind.Message => RenderMessage(lang, "Emma",
                AppText.Get("Email.Message.Sample", lang), FormatUtc(now, lang), MessagesUrl),
            EmailTemplateKind.Test => RenderTest(lang, "Hendry", now),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    private RenderedEmail RenderSampleAlert(string language, DateTime now)
    {
        var alert = new Data.Alert
        {
            Type = "new-app",
            TitleKey = "Alert.NewApp.Title",
            DetailKey = "Alert.NewApp.Detail",
            ArgsJson = AlertText.SerializeArgs(["Minecraft", "app:minecraft", "ZIUR-X1-LAPTOP"]),
            CreatedUtc = now
        };
        var (title, detail) = AlertText.Render(alert, language);
        return RenderAlert(language, title, detail, "Emma", "ZIUR-X1-LAPTOP", FormatUtc(now, language), AlertsUrl);
    }

    private RenderedEmail RenderSampleDigest(string language, DateTime now)
    {
        static EmailAlertEntry Entry(DateTime time, string title, string detail, string language) =>
            new(title, detail, FormatUtc(time, language));

        var emma = new List<EmailAlertEntry>
        {
            Entry(now.AddMinutes(-12), AppText.Get("Alert.NewApp.Title", language, "Minecraft"),
                AppText.Get("Alert.NewApp.Detail", language, "Minecraft", "app:minecraft", "ZIUR-X1-LAPTOP"), language),
            Entry(now.AddMinutes(-48), AppText.Get("Alert.RepeatedBlocked.Title", language, "roblox.com"),
                AppText.Get("Alert.RepeatedBlocked.Detail", language, "roblox.com", "5", "ZIUR-X1-LAPTOP"), language),
            Entry(now.AddHours(-2), AppText.Get("Alert.DailyLimit.Title", language),
                AppText.Get("Alert.DailyLimit.Detail", language, "120", "Emma"), language)
        };
        var alex = new List<EmailAlertEntry>
        {
            Entry(now.AddHours(-4), AppText.Get("Alert.DeviceOffline.Title", language, "ALEX-PC"),
                AppText.Get("Alert.DeviceOffline.Detail", language, "ALEX-PC", FormatUtc(now.AddHours(-5), language)), language)
        };

        var groups = new List<EmailDigestChild>
        {
            new("Emma", emma.Count, emma, 2),
            new("Alex", alex.Count, alex, 0)
        };
        return RenderDigest(language, emma.Count + alex.Count, groups, AlertsUrl);
    }

    private RenderedEmail Build(string language, EmailTemplateKind kind, string subject, string preheader,
        string headline, IReadOnlyList<string> paragraphs, string? buttonText, string? buttonUrl,
        IReadOnlyList<EmailDetail> details, string footerReason, bool showSettingsLink, string? listUnsubscribe)
    {
        var contentHtml = new StringBuilder();
        foreach (var paragraph in paragraphs)
            contentHtml.Append("<p style=\"margin:0 0 16px;font-family:").Append(FontStack)
                .Append(";font-size:15px;line-height:24px;color:").Append(Muted).Append(";\">")
                .Append(Html(paragraph)).Append("</p>");

        return Build(language, kind, subject, preheader, headline, contentHtml.ToString(), paragraphs,
            buttonText, buttonUrl, details, footerReason, showSettingsLink, listUnsubscribe);
    }

    private RenderedEmail Build(string language, EmailTemplateKind kind, string subject, string preheader,
        string headline, string contentHtml, IReadOnlyList<string> contentText, string? buttonText, string? buttonUrl,
        IReadOnlyList<EmailDetail> details, string footerReason, bool showSettingsLink, string? listUnsubscribe)
    {
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"").Append(Html(language)).Append("\"><head>")
            .Append("<meta charset=\"utf-8\" />")
            .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\" />")
            .Append("<meta name=\"color-scheme\" content=\"light only\" />")
            .Append("<meta name=\"supported-color-schemes\" content=\"light\" />")
            .Append("<title>").Append(Html(subject)).Append("</title></head>")
            .Append("<body style=\"margin:0;padding:0;background-color:").Append(Background)
            .Append(";-webkit-text-size-adjust:100%;-ms-text-size-adjust:100%;\">")
            .Append("<div style=\"display:none;font-size:1px;color:").Append(Background)
            .Append(";line-height:1px;max-height:0;max-width:0;opacity:0;overflow:hidden;mso-hide:all;\">")
            .Append(Html(preheader)).Append("</div>")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"width:100%;background-color:")
            .Append(Background).Append(";\"><tr><td align=\"center\" style=\"padding:24px 12px;\">")
            .Append("<table role=\"presentation\" width=\"600\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"width:100%;max-width:600px;background-color:")
            .Append(Card).Append(";border:1px solid ").Append(Line).Append(";\">")
            .Append("<tr><td style=\"padding:24px 28px;background-color:").Append(Card)
            .Append(";border-bottom:3px solid ").Append(BrandGreen).Append(";\">")
            .Append("<img src=\"").Append(Html(LogoUrl)).Append("\" width=\"").Append(LogoWidth).Append("\" height=\"")
            .Append(LogoHeight).Append("\" alt=\"").Append(Html(AppText.Get("App.Name", language)))
            .Append("\" style=\"display:block;width:").Append(LogoWidth).Append("px;height:").Append(LogoHeight)
            .Append("px;border:0;outline:none;text-decoration:none;\" /></td></tr>")
            .Append("<tr><td style=\"padding:30px 28px 10px;\">")
            .Append("<h1 style=\"margin:0 0 18px;font-family:").Append(FontStack)
            .Append(";font-size:24px;line-height:32px;font-weight:800;letter-spacing:-0.01em;color:").Append(Text)
            .Append(";\">").Append(Html(headline)).Append("</h1>")
            .Append(contentHtml)
            .Append("</td></tr>");

        if (!string.IsNullOrWhiteSpace(buttonText) && !string.IsNullOrWhiteSpace(buttonUrl))
        {
            html.Append("<tr><td style=\"padding:8px 28px 0;\">")
                .Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\"><tr>")
                .Append("<td align=\"center\" bgcolor=\"").Append(BrandGreen).Append("\" style=\"background-color:")
                .Append(BrandGreen).Append(";\">")
                .Append("<a href=\"").Append(Html(buttonUrl))
                .Append("\" target=\"_blank\" rel=\"noopener\" style=\"display:inline-block;padding:14px 26px;font-family:")
                .Append(FontStack).Append(";font-size:15px;line-height:20px;font-weight:700;color:").Append(OnBrand)
                .Append(";text-decoration:none;\">").Append(Html(buttonText)).Append("</a></td></tr></table>")
                .Append("<p style=\"margin:18px 0 0;font-family:").Append(FontStack)
                .Append(";font-size:12px;line-height:18px;color:").Append(Faint).Append(";\">")
                .Append(Html(AppText.Get("Email.Layout.ButtonFallback", language))).Append("</p>")
                .Append("<p style=\"margin:4px 0 0;font-family:").Append(FontStack)
                .Append(";font-size:12px;line-height:18px;overflow-wrap:anywhere;word-break:break-word;\">")
                .Append("<a href=\"").Append(Html(buttonUrl)).Append("\" target=\"_blank\" rel=\"noopener\" style=\"color:")
                .Append(LinkColor).Append(";text-decoration:underline;\">").Append(Html(buttonUrl)).Append("</a></p>")
                .Append("</td></tr>");
        }

        if (details.Count > 0)
        {
            html.Append("<tr><td style=\"padding:24px 28px 0;\">")
                .Append("<div style=\"border-top:1px solid ").Append(Line).Append(";padding-top:14px;\">")
                .Append("<div style=\"font-family:").Append(FontStack)
                .Append(";font-size:11px;line-height:16px;letter-spacing:0.12em;text-transform:uppercase;font-weight:700;color:")
                .Append(Faint).Append(";margin-bottom:8px;\">")
                .Append(Html(AppText.Get("Email.Layout.Details", language))).Append("</div>")
                .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\">");
            foreach (var detail in details)
            {
                html.Append("<tr><td style=\"padding:3px 12px 3px 0;font-family:").Append(FontStack)
                    .Append(";font-size:12px;line-height:18px;color:").Append(Faint).Append(";vertical-align:top;\">")
                    .Append(Html(detail.Label)).Append("</td><td style=\"padding:3px 0;font-family:").Append(FontStack)
                    .Append(";font-size:12px;line-height:18px;color:").Append(Text).Append(";vertical-align:top;\">")
                    .Append(Html(detail.Value)).Append("</td></tr>");
            }
            html.Append("</table></div></td></tr>");
        }

        html.Append("<tr><td style=\"padding:30px 28px;\"></td></tr>")
            .Append("<tr><td style=\"padding:20px 28px;background-color:").Append(Panel)
            .Append(";border-top:1px solid ").Append(Line).Append(";font-family:").Append(FontStack)
            .Append(";font-size:12px;line-height:18px;color:").Append(Faint).Append(";\">")
            .Append("<p style=\"margin:0 0 8px;\">").Append(Html(footerReason)).Append("</p>");

        if (showSettingsLink)
            html.Append("<p style=\"margin:0 0 8px;\"><a href=\"").Append(Html(SettingsUrl))
                .Append("\" target=\"_blank\" rel=\"noopener\" style=\"color:").Append(LinkColor)
                .Append(";text-decoration:underline;\">")
                .Append(Html(AppText.Get("Email.Layout.Footer.Settings", language))).Append("</a></p>");

        html.Append("<p style=\"margin:0;color:").Append(Faint).Append(";\">")
            .Append(Html(AppText.Get("Email.Layout.Footer.CreatedByLabel", language)))
            .Append("&nbsp;&nbsp;<img src=\"").Append(Html(Link("/brand/opcontrol-logo.png")))
            .Append("\" width=\"101\" height=\"22\" alt=\"OpControl\" style=\"display:inline-block;vertical-align:middle;width:101px;height:22px;border:0;outline:none;text-decoration:none;\" /></p>");

        if (Staging)
            html.Append("<p style=\"margin:8px 0 0;color:").Append(Faint).Append(";\">")
                .Append(Html(AppText.Get("Email.Layout.Footer.Staging", language))).Append("</p>");

        html.Append("</td></tr></table></td></tr></table></body></html>");

        var text = new StringBuilder();
        text.AppendLine(headline).AppendLine();
        foreach (var line in contentText)
        {
            text.AppendLine(line);
            text.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(buttonText) && !string.IsNullOrWhiteSpace(buttonUrl))
        {
            text.AppendLine($"{buttonText}:");
            text.AppendLine(buttonUrl);
            text.AppendLine();
        }
        if (details.Count > 0)
        {
            text.AppendLine($"{AppText.Get("Email.Layout.Details", language)}:");
            foreach (var detail in details)
                text.AppendLine($"{detail.Label}: {detail.Value}");
            text.AppendLine();
        }
        text.AppendLine(footerReason);
        if (showSettingsLink)
            text.AppendLine($"{AppText.Get("Email.Layout.Footer.Settings", language)}: {SettingsUrl}");
        text.AppendLine(AppText.Get("Email.Layout.Footer.CreatedBy", language));
        if (Staging)
            text.AppendLine(AppText.Get("Email.Layout.Footer.Staging", language));

        return new RenderedEmail(kind, subject, html.ToString(), text.ToString(), preheader, listUnsubscribe);
    }

    private static string FormatUtc(DateTime value, string language) =>
        DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc)
            .ToString("d MMM yyyy, HH:mm 'UTC'", AppCulture.Culture(language));

    private static string FormatTime(DateTime value, string? timeZoneId, string language)
    {
        var timeZone = TimeZoneResolver.Resolve(timeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), timeZone);
        var suffix = string.IsNullOrWhiteSpace(timeZoneId) || timeZone.Id == "UTC" ? " 'UTC'" : "";
        return local.ToString("d MMM yyyy, HH:mm" + suffix, AppCulture.Culture(language));
    }

    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? "");
}
