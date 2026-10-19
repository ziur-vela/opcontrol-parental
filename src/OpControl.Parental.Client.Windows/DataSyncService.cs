using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OpControl.Parental.Contracts;

namespace OpControl.Parental.Client;

public sealed class DataSyncService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(ClientOptions.JsonOptions);
    private static readonly JsonSerializerOptions LineJson = new(ClientOptions.JsonOptions) { WriteIndented = false };
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _lastVisit = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastBlockEvent = new(StringComparer.OrdinalIgnoreCase);
    private readonly IHttpClientFactory _httpFactory;
    private readonly CredentialStore _credentials;
    private readonly EnrollmentClient _enrollment;
    private readonly ClientSettings _settings;
    private readonly ILogger<DataSyncService> _logger;
    private AppInventoryRequest? _pendingInventory;

    public DataSyncService(IHttpClientFactory httpFactory, CredentialStore credentials, EnrollmentClient enrollment, ClientSettings settings, ILogger<DataSyncService> logger)
    {
        _httpFactory = httpFactory;
        _credentials = credentials;
        _enrollment = enrollment;
        _settings = settings;
        _logger = logger;
    }

    public void RecordVisit(string registrableDomain, bool blocked)
    {
        if (DomainUtil.IsIgnored(registrableDomain)) return;
        var key = (blocked ? "B:" : "A:") + registrableDomain;
        var now = DateTime.UtcNow;
        if (_lastVisit.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(60)) return;
        _lastVisit[key] = now;
        AppendLine(ClientPaths.VisitQueueFile, new VisitEventDto(registrableDomain, now, blocked));
    }

    public void RecordBlockEvent(string ruleType, string identity, string displayName)
    {
        var key = ruleType + ":" + identity;
        var now = DateTime.UtcNow;
        if (_lastBlockEvent.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(5)) return;
        _lastBlockEvent[key] = now;
        AppendLine(ClientPaths.BlockEventQueueFile, new BlockEventDto(ruleType, identity, displayName, now));
    }

    public void QueueInventory(AppInventoryRequest request) => _pendingInventory = request;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Data sync failed");
            }

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SyncAsync(CancellationToken ct)
    {
        var credential = _credentials.Load();
        if (credential is null || !credential.IsUsable) return;
        var client = _httpFactory.CreateClient("device");
        var server = _settings.Value.ServerUrl.TrimEnd('/');

        var visits = await TakeLinesAsync(ClientPaths.VisitQueueFile, 2000);
        if (visits.Count > 0)
        {
            var events = visits.Select(Deserialize<VisitEventDto>).Where(v => v is not null).Cast<VisitEventDto>().ToList();
            if (events.Count > 0 && await PostAsync(client, $"{server}/api/device/visits", new VisitBatchRequest(events), credential, ct))
                await RemoveLinesAsync(ClientPaths.VisitQueueFile, visits);
        }

        var blocks = await TakeLinesAsync(ClientPaths.BlockEventQueueFile, 500);
        if (blocks.Count > 0)
        {
            var events = blocks.Select(Deserialize<BlockEventDto>).Where(v => v is not null).Cast<BlockEventDto>().ToList();
            if (events.Count > 0 && await PostAsync(client, $"{server}/api/device/block-events", new BlockEventRequest(events), credential, ct))
                await RemoveLinesAsync(ClientPaths.BlockEventQueueFile, blocks);
        }

        var inventory = _pendingInventory;
        if (inventory is not null)
        {
            if (await PostAsync(client, $"{server}/api/device/apps/inventory", inventory, credential, ct))
                _pendingInventory = null;
        }
    }

    private async Task<bool> PostAsync<T>(HttpClient client, string url, T body, DeviceCredential credential, CancellationToken ct)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body, options: JsonOptions)
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Credential);
            using var response = await client.SendAsync(message, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                var refreshed = await _enrollment.RefreshAsync(_settings.Value.ServerUrl, credential.Credential, ct);
                if (refreshed is null) return false;
                credential.Credential = refreshed.Credential;
                credential.ExpiresUtc = refreshed.CredentialExpiresUtc;
                credential.UpdatedUtc = DateTime.UtcNow;
                _credentials.Save(credential);
                return await PostAsync(client, url, body, credential, ct);
            }
            if (response.IsSuccessStatusCode) return true;
            _logger.LogWarning("Sync {Url} failed with status {Status}", url, (int)response.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private void AppendLine<T>(string path, T value)
    {
        try
        {
            _fileGate.Wait();
            try
            {
                Directory.CreateDirectory(ClientPaths.PrivateDirectory);
                File.AppendAllText(path, JsonSerializer.Serialize(value, LineJson) + Environment.NewLine);
                if (new FileInfo(path).Length > 4 * 1024 * 1024)
                {
                    var lines = File.ReadAllLines(path);
                    File.WriteAllLines(path, lines.Skip(lines.Length / 2));
                }
            }
            finally { _fileGate.Release(); }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not append sync record");
        }
    }

    private async Task<List<string>> TakeLinesAsync(string path, int max)
    {
        await _fileGate.WaitAsync();
        try
        {
            if (!File.Exists(path)) return [];
            var lines = await File.ReadAllLinesAsync(path);
            return lines.Where(l => !string.IsNullOrWhiteSpace(l)).Take(max).ToList();
        }
        catch { return []; }
        finally { _fileGate.Release(); }
    }

    private async Task RemoveLinesAsync(string path, List<string> sent)
    {
        await _fileGate.WaitAsync();
        try
        {
            if (!File.Exists(path)) return;
            var remaining = (await File.ReadAllLinesAsync(path))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Skip(sent.Count)
                .ToArray();
            if (remaining.Length == 0) File.Delete(path);
            else await File.WriteAllLinesAsync(path, remaining);
        }
        catch { }
        finally { _fileGate.Release(); }
    }

    private static T? Deserialize<T>(string line)
    {
        try { return JsonSerializer.Deserialize<T>(line, LineJson); }
        catch { return default; }
    }
}
