// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

/// <summary>
/// Ends remote-view rows that cannot still be alive: rows left behind by a
/// previous server process and sessions that disappeared from the in-memory
/// relay registry (crash, circuit loss or a missed stop).
/// </summary>
public sealed class RemoteSessionSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly RemoteSessionRegistry _registry;
    private readonly ILogger<RemoteSessionSweeper> _logger;

    public RemoteSessionSweeper(IServiceScopeFactory scopes, RemoteSessionRegistry registry, ILogger<RemoteSessionSweeper> logger)
    {
        _scopes = scopes;
        _registry = registry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SweepAsync(stoppingToken, serverStart: true);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            await SweepAsync(stoppingToken, serverStart: false);
        }
    }

    private async Task SweepAsync(CancellationToken ct, bool serverStart)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ParentalDbContext>();
            var service = scope.ServiceProvider.GetRequiredService<RemoteSessionService>();
            var stale = await db.RemoteSessions
                .Where(s => s.Status == RemoteSessionStatus.Requested || s.Status == RemoteSessionStatus.Active)
                .ToListAsync(ct);
            var now = DateTime.UtcNow;
            foreach (var session in stale)
            {
                if (!serverStart && _registry.Contains(session.Id)) continue;
                if (!serverStart && session.CreatedUtc > now.AddMinutes(-11)) continue;
                var reason = serverStart ? "server-restart" : "stale";
                if (_registry.Contains(session.Id)) await _registry.StopAsync(session.Id, reason, ct);
                else await service.EndAsync(session.Id, reason, ct);
                _logger.LogWarning("Remote session {SessionId} on device {DeviceId} ended as {Reason}", session.Id, session.DeviceId, reason);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Remote session sweep failed");
        }
    }
}
