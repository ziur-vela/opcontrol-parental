// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Net.Mail;
using System.Net.Mime;
using System.Text;

namespace OpControl.Parental.Server.Services;

public static class EmailMime
{
    public const string StagingPrefix = "[STAGING] ";

    public static MailMessage Compose(EmailOutgoing outgoing, string fromAddress, string? fromName, string? replyTo, bool staging)
    {
        var email = outgoing.Email;
        var subject = staging ? StagingPrefix + email.Subject : email.Subject;
        var encoding = new UTF8Encoding(false);
        var message = new MailMessage
        {
            From = new MailAddress(fromAddress, string.IsNullOrWhiteSpace(fromName) ? "Parental OpControl" : fromName, encoding),
            Subject = subject,
            SubjectEncoding = encoding,
            Body = email.TextBody,
            BodyEncoding = encoding,
            BodyTransferEncoding = TransferEncoding.EightBit,
            IsBodyHtml = false,
            HeadersEncoding = encoding
        };
        message.To.Add(outgoing.To);
        if (!string.IsNullOrWhiteSpace(replyTo))
            message.ReplyToList.Add(new MailAddress(replyTo));

        var html = AlternateView.CreateAlternateViewFromString(email.HtmlBody, encoding, "text/html");
        html.TransferEncoding = TransferEncoding.EightBit;
        message.AlternateViews.Add(html);

        if (email.IsAlertOrDigest && !string.IsNullOrWhiteSpace(email.ListUnsubscribeUrl))
        {
            message.Headers.Add("List-Unsubscribe", $"<{email.ListUnsubscribeUrl}>");
        }

        return message;
    }
}
