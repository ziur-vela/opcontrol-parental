// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed class UpdateCheckerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<UpdateCheckerService> _logger;

    public UpdateCheckerService(IServiceScopeFactory scopes, ILogger<UpdateCheckerService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled update check failed");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var updates = scope.ServiceProvider.GetRequiredService<ServerUpdateService>();
        var settings = await updates.GetSettingsAsync(ct);
        if (!settings.GitHubCheckEnabled) return;
        if (settings.LastUpdateCheckUtc is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(6)) return;
        var info = await updates.GetInfoAsync(refresh: true, ct);
        if (!info.UpdateAvailable || !settings.NotifyNewVersion || string.IsNullOrWhiteSpace(settings.LatestVersion)) return;
        if (string.Equals(settings.LastNotifiedVersion, settings.LatestVersion, StringComparison.OrdinalIgnoreCase)) return;

        var db = scope.ServiceProvider.GetRequiredService<ParentalDbContext>();
        var renderer = scope.ServiceProvider.GetRequiredService<EmailRenderer>();
        var sender = scope.ServiceProvider.GetRequiredService<EmailSender>();
        var admins = await db.Users.AsNoTracking()
            .Where(u => u.IsServerAdmin && u.Email != null)
            .Select(u => new { u.Email, u.DisplayName, u.PreferredLanguage })
            .ToListAsync(ct);
        var url = settings.LatestReleaseUrl ?? renderer.Link("/server-admin/updates");
        var sentAny = false;
        foreach (var admin in admins)
        {
            var rendered = renderer.RenderUpdateAvailable(admin.PreferredLanguage, _displayVersion(info.CurrentVersion),
                _displayVersion(settings.LatestVersion), info.NotesEn, url);
            var (sent, error) = await sender.SendAsync(new EmailOutgoing(admin.Email!, rendered), null, ct);
            if (sent) sentAny = true;
            else _logger.LogWarning("Could not email update notification to {Email}: {Error}", admin.Email, error);
        }
        if (sentAny)
        {
            settings.LastNotifiedVersion = settings.LatestVersion;
            settings.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private static string _displayVersion(string value) => value.StartsWith('v') ? value : $"v{value}";
}
