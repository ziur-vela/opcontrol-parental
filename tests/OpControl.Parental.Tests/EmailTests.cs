using System.Net;
using System.Net.Mail;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using OpControl.Parental.Server.Services;
using Xunit;

namespace OpControl.Parental.Tests;

public class EmailTests
{
    private const string ProductionBase = "https://parental.opcontrol.net";

    private static EmailRenderer Renderer(string baseUrl = ProductionBase, bool staging = false, string? logoUrl = null) =>
        new(Options.Create(new EmailOptions
            {
                Staging = staging,
                LogoUrl = logoUrl ?? $"{ProductionBase}/brand/email-lockup.png"
            }),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PublicBaseUrl"] = baseUrl
            }).Build());

    [Fact]
    public void Invitation_has_html_and_plain_text_with_https_links()
    {
        var renderer = Renderer();
        var email = renderer.RenderSample(EmailTemplateKind.InviteNewFamily, "en");

        Assert.Contains("https://parental.opcontrol.net/invite/sample-preview-token", email.HtmlBody);
        Assert.Contains("https://parental.opcontrol.net/invite/sample-preview-token", email.TextBody);
        Assert.Contains("https://parental.opcontrol.net/brand/email-lockup.png", email.HtmlBody);
        Assert.Contains("width=\"175\"", email.HtmlBody);
        Assert.Contains("height=\"60\"", email.HtmlBody);
        Assert.Contains("If the button doesn't work", WebUtility.HtmlDecode(email.HtmlBody));
        Assert.Contains(email.Preheader, WebUtility.HtmlDecode(email.HtmlBody));
        Assert.DoesNotContain("http://", email.HtmlBody);

        var linkLine = email.TextBody.Split('\n').Single(line => line.Contains("/invite/"));
        Assert.DoesNotContain("=\r", linkLine);
        Assert.DoesNotContain("=\n", linkLine);
    }

    [Fact]
    public void Message_email_shows_the_text_and_links_to_the_messages_page()
    {
        var renderer = Renderer();
        foreach (var language in new[] { "en", "es-MX" })
        {
            var email = renderer.RenderSample(EmailTemplateKind.Message, language);
            Assert.Contains("https://parental.opcontrol.net/messages", email.HtmlBody);
            Assert.Contains("https://parental.opcontrol.net/messages", email.TextBody);
            Assert.Contains(language == "en" ? "Can you come home at six?" : "¿Puedes venir a casa a las seis?", email.TextBody);
        }
    }

    [Fact]
    public void Invitation_expiry_uses_the_family_time_zone()
    {
        var email = Renderer().RenderInviteCoParent("en", "https://parental.opcontrol.net/invite/token",
            "Familia Ruiz", "Hendry", new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc), "America/Los_Angeles");
        Assert.Contains("20 Oct 2026, 17:00", email.TextBody);
        Assert.DoesNotContain("21 Oct 2026, 00:00", email.TextBody);
    }

    [Fact]
    public void Production_rejects_a_non_https_base_url()
    {
        var (ok, error) = Renderer("http://100.116.211.100:8097").ValidateConfiguration();
        Assert.False(ok);
        Assert.Contains("HTTPS", error);
    }

    [Fact]
    public void Staging_prefixes_subjects_and_keeps_staging_links()
    {
        var renderer = Renderer("http://100.116.211.100:8097", staging: true);
        var email = renderer.RenderSample(EmailTemplateKind.Test, "en");
        using var message = EmailMime.Compose(new EmailOutgoing("to@example.com", email),
            "from@example.com", "Parental OpControl", null, true);

        Assert.StartsWith("[STAGING] ", message.Subject);
        Assert.Contains("http://100.116.211.100:8097/", email.HtmlBody);
        Assert.Contains("Staging server", email.TextBody);
    }

    [Fact]
    public void Mime_is_multipart_alternative_and_the_link_is_not_broken()
    {
        var email = Renderer().RenderSample(EmailTemplateKind.InviteNewFamily, "en");
        var raw = ToRaw(email, out _);

        Assert.Contains("multipart/alternative", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("text/plain", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("text/html", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://parental.opcontrol.net/invite/sample-preview-token", raw);
        Assert.DoesNotContain("http://", raw);
    }

    [Fact]
    public void Alert_and_digest_carry_list_unsubscribe()
    {
        var renderer = Renderer();
        foreach (var kind in new[] { EmailTemplateKind.Alert, EmailTemplateKind.Digest })
        {
            var email = renderer.RenderSample(kind, "en");
            Assert.Equal("https://parental.opcontrol.net/settings", email.ListUnsubscribeUrl);
            Assert.Contains("https://parental.opcontrol.net/settings", email.HtmlBody);
            var raw = ToRaw(email, out _);
            Assert.Contains("List-Unsubscribe: <https://parental.opcontrol.net/settings>", raw);
        }
    }

    [Fact]
    public void Every_template_renders_in_both_languages_with_https_links()
    {
        var renderer = Renderer();
        foreach (var kind in Enum.GetValues<EmailTemplateKind>())
        {
            foreach (var language in new[] { "en", "es-MX" })
            {
                var email = renderer.RenderSample(kind, language);
                Assert.False(string.IsNullOrWhiteSpace(email.Subject), $"{kind}/{language} subject");
                Assert.StartsWith("<!DOCTYPE html>", email.HtmlBody);
                Assert.Contains("color-scheme", email.HtmlBody);
                Assert.Contains("max-width:600px", email.HtmlBody);
                Assert.DoesNotContain("<script", email.HtmlBody, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("https://parental.opcontrol.net/", email.TextBody);

                var hrefs = Regex.Matches(email.HtmlBody, "href=\"([^\"]+)\"")
                    .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value))
                    .ToList();
                Assert.NotEmpty(hrefs);
                Assert.All(hrefs, href => Assert.StartsWith("https://", href));
            }
        }
    }

    [Fact]
    public void Digest_groups_by_child_and_shows_counts()
    {
        var email = Renderer().RenderSample(EmailTemplateKind.Digest, "en");
        Assert.Contains("Emma", email.HtmlBody);
        Assert.Contains("Alex", email.HtmlBody);
        Assert.Contains("3 alerts", email.HtmlBody);
        Assert.Contains("1 alert", email.HtmlBody);
        Assert.Contains("Parental OpControl daily digest", email.Subject);
    }

    [Fact]
    public void Spanish_and_english_have_distinct_subjects()
    {
        var renderer = Renderer();
        Assert.NotEqual(renderer.RenderSample(EmailTemplateKind.Test, "en").Subject,
            renderer.RenderSample(EmailTemplateKind.Test, "es-MX").Subject);
        Assert.NotEqual(renderer.RenderSample(EmailTemplateKind.InviteNewFamily, "en").Subject,
            renderer.RenderSample(EmailTemplateKind.InviteNewFamily, "es-MX").Subject);
    }

    private static string ToRaw(RenderedEmail email, out string path)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ocp-email-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            using var message = EmailMime.Compose(new EmailOutgoing("to@example.com", email),
                "from@example.com", "Parental OpControl", null, false);
            using var client = new SmtpClient
            {
                DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory,
                PickupDirectoryLocation = directory
            };
            client.Send(message);
            path = Directory.GetFiles(directory).Single();
            return File.ReadAllText(path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
