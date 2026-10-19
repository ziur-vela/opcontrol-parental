using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OpControl.Parental.Contracts;

namespace OpControl.Parental.Client;

public sealed class OverrideSync
{
    private static readonly JsonSerializerOptions JsonOptions = new(ClientOptions.JsonOptions);
    private static readonly JsonSerializerOptions QueueJson = new(ClientOptions.JsonOptions) { WriteIndented = false };
    private readonly object _gate = new();
    private readonly LockRuntime _runtime;
    private readonly CredentialStore _credentials;
    private readonly EnrollmentClient _enrollment;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<OverrideSync> _logger;

    public OverrideSync(LockRuntime runtime, CredentialStore credentials, EnrollmentClient enrollment, IHttpClientFactory httpFactory, ILogger<OverrideSync> logger)
    {
        _runtime = runtime;
        _credentials = credentials;
        _enrollment = enrollment;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public bool HasPending
    {
        get { lock (_gate) return File.Exists(ClientPaths.OverrideQueueFile) && new FileInfo(ClientPaths.OverrideQueueFile).Length > 0; }
    }

    public DeviceOverrideRequest? LastPending
    {
        get
        {
            lock (_gate)
            {
                if (!File.Exists(ClientPaths.OverrideQueueFile)) return null;
                var line = File.ReadLines(ClientPaths.OverrideQueueFile).LastOrDefault();
                return line is null ? null : JsonSerializer.Deserialize<DeviceOverrideRequest>(line, QueueJson);
            }
        }
    }

    public async Task ApplyAsync(string option, int? minutes, ClientOptions options, CancellationToken ct)
    {
        _runtime.ApplyLocalOverride(option, minutes);
        lock (_gate)
        {
            Directory.CreateDirectory(ClientPaths.PrivateDirectory);
            File.AppendAllText(ClientPaths.OverrideQueueFile,
                JsonSerializer.Serialize(new DeviceOverrideRequest(option, minutes), QueueJson) + Environment.NewLine);
        }
        await SyncPendingAsync(options, ct);
    }

    public async Task<bool> SyncPendingAsync(ClientOptions options, CancellationToken ct)
    {
        List<string> lines;
        lock (_gate)
        {
            if (!File.Exists(ClientPaths.OverrideQueueFile)) return true;
            lines = File.ReadAllLines(ClientPaths.OverrideQueueFile).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        }
        if (lines.Count == 0) return true;

        var credential = _credentials.Load();
        if (credential is null || !credential.IsUsable) return false;

        var remaining = new List<string>();
        var client = _httpFactory.CreateClient("device");
        foreach (var line in lines)
        {
            DeviceOverrideRequest? request;
            try { request = JsonSerializer.Deserialize<DeviceOverrideRequest>(line, QueueJson); }
            catch (JsonException) { _logger.LogWarning("Discarding malformed queued override"); continue; }
            if (request is null) continue;
            var ok = await PostAsync(client, options, credential, request, ct);
            if (!ok) remaining.Add(line);
        }

        lock (_gate)
        {
            if (remaining.Count == 0)
                File.Delete(ClientPaths.OverrideQueueFile);
            else
                File.WriteAllLines(ClientPaths.OverrideQueueFile, remaining);
        }
        return remaining.Count == 0;
    }

    private async Task<bool> PostAsync(HttpClient client, ClientOptions options, DeviceCredential credential, DeviceOverrideRequest request, CancellationToken ct)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, $"{options.ServerUrl.TrimEnd('/')}/api/device/override")
            {
                Content = JsonContent.Create(request, options: JsonOptions)
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Credential);
            using var response = await client.SendAsync(message, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                var refreshed = await _enrollment.RefreshAsync(options.ServerUrl, credential.Credential, ct);
                if (refreshed is null) return false;
                credential.Credential = refreshed.Credential;
                credential.ExpiresUtc = refreshed.CredentialExpiresUtc;
                credential.UpdatedUtc = DateTime.UtcNow;
                _credentials.Save(credential);
                return await PostAsync(client, options, credential, request, ct);
            }
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Override {Option} synced to server", request.Option);
                return true;
            }
            _logger.LogWarning("Override sync failed with status {Status}", (int)response.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
