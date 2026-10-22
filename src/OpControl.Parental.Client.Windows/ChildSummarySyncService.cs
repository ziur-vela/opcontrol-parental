// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using OpControl.Parental.Contracts;

namespace OpControl.Parental.Client;

/// <summary>
/// Pulls the read-only child summary and caches it in the shared user directory so the
/// helper can open "My OpControl" while offline. Refreshes on hub pushes and every 5 minutes.
/// </summary>
public sealed class ChildSummarySyncService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(ClientOptions.JsonOptions);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IHttpClientFactory _httpFactory;
    private readonly CredentialStore _credentials;
    private readonly EnrollmentClient _enrollment;
    private readonly ClientSettings _settings;
    private readonly ILogger<ChildSummarySyncService> _logger;
    private int _refreshRequested;
    private DateTime _lastRefreshUtc = DateTime.MinValue;

    public ChildSummarySyncService(IHttpClientFactory httpFactory, CredentialStore credentials,
        EnrollmentClient enrollment, ClientSettings settings, ILogger<ChildSummarySyncService> logger)
    {
        _httpFactory = httpFactory;
        _credentials = credentials;
        _enrollment = enrollment;
        _settings = settings;
        _logger = logger;
    }

    public void RequestRefresh() => Interlocked.Exchange(ref _refreshRequested, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var requested = Interlocked.Exchange(ref _refreshRequested, 0) == 1;
            if (requested || DateTime.UtcNow - _lastRefreshUtc >= Interval)
                await RefreshAsync(stoppingToken);
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            _lastRefreshUtc = DateTime.UtcNow;
            var credential = _credentials.Load();
            if (credential is null || !credential.IsUsable) return;
            var summary = await FetchAsync(credential, ct);
            if (summary is null) return;
            ChildSummaryCache.Write(ClientPaths.SummaryFile, summary);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Child summary refresh failed; keeping the cached copy");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ChildSummaryDto?> FetchAsync(DeviceCredential credential, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("device");
        var server = _settings.Value.ServerUrl.TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{server}/api/device/child-summary");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Credential);
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var refreshed = await _enrollment.RefreshAsync(_settings.Value.ServerUrl, credential.Credential, ct);
            if (refreshed is null) return null;
            credential.Credential = refreshed.Credential;
            credential.ExpiresUtc = refreshed.CredentialExpiresUtc;
            credential.UpdatedUtc = DateTime.UtcNow;
            _credentials.Save(credential);
            return await FetchAsync(credential, ct);
        }
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Child summary returned HTTP {Status}", (int)response.StatusCode);
            return null;
        }
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ChildSummaryDto>(json, JsonOptions);
    }
}
