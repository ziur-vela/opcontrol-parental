// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Collections.Concurrent;

namespace OpControl.Parental.Server.Services;

public sealed class PendingSetupStore
{
    private readonly ConcurrentDictionary<string, PendingSetup> _items = new(StringComparer.Ordinal);

    public string Add(PendingSetup setup)
    {
        Prune();
        var id = Guid.NewGuid().ToString("N");
        _items[id] = setup with { CreatedUtc = DateTime.UtcNow };
        return id;
    }

    public PendingSetup? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !_items.TryGetValue(id, out var setup)) return null;
        return DateTime.UtcNow - setup.CreatedUtc > TimeSpan.FromMinutes(20) ? null : setup;
    }

    public void Remove(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id)) _items.TryRemove(id, out _);
    }

    private void Prune()
    {
        foreach (var pair in _items)
            if (DateTime.UtcNow - pair.Value.CreatedUtc > TimeSpan.FromMinutes(20))
                _items.TryRemove(pair.Key, out _);
    }
}

public sealed record PendingSetup(
    string FamilyName, string DisplayName, string ChildName, string Email, string Password,
    string PublicBaseUrl, string? SmtpHost, int SmtpPort, bool SmtpUseSsl, string? SmtpUsername,
    string? SmtpPassword, string? SmtpFrom, string? SmtpFromName, string AuthenticatorKey, DateTime CreatedUtc = default);
