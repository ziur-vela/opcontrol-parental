// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed class LockExpiryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<LockExpiryService> _logger;
    private readonly ConcurrentDictionary<Guid, DateTime> _notified = new();

    public LockExpiryService(IServiceScopeFactory scopes, ILogger<LockExpiryService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckExpiriesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lock expiry check failed");
            }
        }
    }

    private async Task CheckExpiriesAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var cutoff = now.AddSeconds(-15);
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ParentalDbContext>();
        var due = await db.LockStates.AsNoTracking()
            .Where(s => (s.BonusUntilUtc != null && s.BonusUntilUtc <= now && s.BonusUntilUtc > cutoff)
                     || (s.UnlockUntilUtc != null && s.UnlockUntilUtc <= now && s.UnlockUntilUtc > cutoff))
            .ToListAsync(ct);

        foreach (var state in due)
        {
            var expiry = state.BonusUntilUtc is { } bonus && bonus <= now && bonus > cutoff
                ? bonus
                : state.UnlockUntilUtc!.Value;
            if (_notified.TryGetValue(state.Id, out var previous) && previous == expiry)
                continue;

            _notified[state.Id] = expiry;
            var locks = scope.ServiceProvider.GetRequiredService<LockService>();
            var dto = await locks.PublishCurrentStateAsync(state.FamilyId, state.ChildId, ct);
            _logger.LogInformation("Lock expiry pushed for child {ChildId}: locked={Locked} reason={Reason}", state.ChildId, dto.IsLocked, dto.Reason);
        }

        if (_notified.Count > 10_000)
        {
            var keep = now.AddDays(-1);
            foreach (var pair in _notified.Where(p => p.Value < keep).Take(1000))
                _notified.TryRemove(pair.Key, out _);
        }
    }
}
