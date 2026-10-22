// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace OpControl.Parental.Server.Services;

/// <summary>Small maintained list of common services shown in the website review panel.</summary>
public static class KnownSites
{
    private static readonly Dictionary<string, string> Notes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["whatsapp.com"] = "SiteReview.Purpose.WhatsApp",
        ["whatsapp.net"] = "SiteReview.Purpose.WhatsApp",
        ["khanacademy.org"] = "SiteReview.Purpose.KhanAcademy",
        ["youtube.com"] = "SiteReview.Purpose.YouTube",
        ["youtu.be"] = "SiteReview.Purpose.YouTube",
        ["tiktok.com"] = "SiteReview.Purpose.TikTok",
        ["instagram.com"] = "SiteReview.Purpose.Instagram",
        ["facebook.com"] = "SiteReview.Purpose.Facebook",
        ["discord.com"] = "SiteReview.Purpose.Discord",
        ["discordapp.com"] = "SiteReview.Purpose.Discord",
        ["roblox.com"] = "SiteReview.Purpose.Roblox",
        ["minecraft.net"] = "SiteReview.Purpose.Minecraft",
        ["netflix.com"] = "SiteReview.Purpose.Netflix",
        ["spotify.com"] = "SiteReview.Purpose.Spotify",
        ["wikipedia.org"] = "SiteReview.Purpose.Wikipedia",
        ["google.com"] = "SiteReview.Purpose.Google",
        ["scratch.mit.edu"] = "SiteReview.Purpose.Scratch",
        ["duolingo.com"] = "SiteReview.Purpose.Duolingo",
        ["twitch.tv"] = "SiteReview.Purpose.Twitch"
    };

    public static string? KeyFor(string domain)
    {
        var value = (domain ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        while (value.Length > 0)
        {
            if (Notes.TryGetValue(value, out var key)) return key;
            var dot = value.IndexOf('.');
            if (dot < 0) break;
            value = value[(dot + 1)..];
        }
        return null;
    }
}
