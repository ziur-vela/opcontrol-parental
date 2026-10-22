// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace OpControl.Parental.Server.Services;

/// <summary>Small maintained list of common services shown in the website review panel.</summary>
public static class KnownSites
{
    public sealed record KnownService(string Key, string LabelKey, IReadOnlyList<string> Domains);

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

    private static readonly KnownService[] Services =
    [
        new("whatsapp", "SiteReview.Service.WhatsApp", ["whatsapp.com", "whatsapp.net"]),
        new("youtube", "SiteReview.Service.YouTube", ["youtube.com", "youtu.be", "googlevideo.com", "ytimg.com"]),
        new("discord", "SiteReview.Service.Discord", ["discord.com", "discord.gg", "discordapp.com", "discordapp.net"]),
        new("instagram", "SiteReview.Service.Instagram", ["instagram.com", "cdninstagram.com"]),
        new("facebook", "SiteReview.Service.Facebook", ["facebook.com", "fbcdn.net"]),
        new("tiktok", "SiteReview.Service.TikTok", ["tiktok.com", "tiktokcdn.com", "tiktokv.com"]),
        new("roblox", "SiteReview.Service.Roblox", ["roblox.com", "rbxcdn.com"]),
        new("minecraft", "SiteReview.Service.Minecraft", ["minecraft.net", "mojang.com"]),
        new("netflix", "SiteReview.Service.Netflix", ["netflix.com", "nflxvideo.net", "nflximg.net"]),
        new("spotify", "SiteReview.Service.Spotify", ["spotify.com", "scdn.co", "spotifycdn.com"]),
        new("wikipedia", "SiteReview.Service.Wikipedia", ["wikipedia.org", "wikimedia.org"]),
        new("google", "SiteReview.Service.Google", ["google.com", "gstatic.com", "googleapis.com"]),
        new("khanacademy", "SiteReview.Service.KhanAcademy", ["khanacademy.org", "kastatic.org", "kasandbox.org"]),
        new("scratch", "SiteReview.Service.Scratch", ["scratch.mit.edu", "cdn.scratch.mit.edu"]),
        new("duolingo", "SiteReview.Service.Duolingo", ["duolingo.com", "duolingo.cn"]),
        new("twitch", "SiteReview.Service.Twitch", ["twitch.tv", "ttvnw.net", "twitchcdn.net"])
    ];

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

    /// <summary>The service that the supplied host belongs to, if it is in the maintained list.</summary>
    public static KnownService? ServiceFor(string domain)
    {
        var value = (domain ?? "").Trim().Trim('.').ToLowerInvariant();
        if (value.Length == 0) return null;
        foreach (var service in Services)
        {
            foreach (var known in service.Domains)
            {
                if (value.Equals(known, StringComparison.OrdinalIgnoreCase)
                    || value.EndsWith("." + known, StringComparison.OrdinalIgnoreCase))
                    return service;
            }
        }
        return null;
    }

    /// <summary>Other domains of the same service, excluding the domain the parent just allowed.</summary>
    public static IReadOnlyList<string> RelatedDomains(string domain)
    {
        var service = ServiceFor(domain);
        if (service is null) return [];
        var value = (domain ?? "").Trim().Trim('.').ToLowerInvariant();
        return service.Domains
            .Where(known => !value.Equals(known, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
