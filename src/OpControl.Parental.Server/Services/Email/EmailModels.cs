// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace OpControl.Parental.Server.Services;

public enum EmailTemplateKind
{
    InviteNewFamily,
    InviteCoParent,
    Alert,
    Digest,
    Message,
    UpdateAvailable,
    Test
}

public sealed record EmailDetail(string Label, string Value);

public sealed record EmailAlertEntry(string Title, string Detail, string Time);

public sealed record EmailDigestChild(string ChildName, int Count, IReadOnlyList<EmailAlertEntry> Items, int Remaining);

public sealed record RenderedEmail(
    EmailTemplateKind Kind,
    string Subject,
    string HtmlBody,
    string TextBody,
    string Preheader,
    string? ListUnsubscribeUrl)
{
    public bool IsAlertOrDigest => Kind is EmailTemplateKind.Alert or EmailTemplateKind.Digest;
}

public sealed record EmailOutgoing(string To, RenderedEmail Email);
