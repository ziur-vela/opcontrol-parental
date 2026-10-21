using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OpControl.Parental.Contracts;
using OpControl.Parental.Localization;

namespace OpControl.Parental.Client;

public sealed record SendMessageResult(bool Ok, bool Queued, string? Error, ChildMessageDto? Message);

public sealed class MessageSyncService
{
    private static readonly JsonSerializerOptions JsonOptions = new(ClientOptions.JsonOptions);
    private readonly IHttpClientFactory _httpFactory;
    private readonly CredentialStore _credentials;
    private readonly EnrollmentClient _enrollment;
    private readonly ClientSettings _settings;
    private readonly ILogger<MessageSyncService> _logger;
    private readonly object _gate = new();
    private readonly HashSet<Guid> _queuedPopups = [];

    public MessageSyncService(IHttpClientFactory httpFactory, CredentialStore credentials, EnrollmentClient enrollment,
        ClientSettings settings, ILogger<MessageSyncService> logger)
    {
        _httpFactory = httpFactory;
        _credentials = credentials;
        _enrollment = enrollment;
        _settings = settings;
        _logger = logger;
        LoadQueuedPopups();
    }

    public bool HasPendingOutbox
    {
        get
        {
            try { return File.Exists(ClientPaths.MessageOutboxFile) && new FileInfo(ClientPaths.MessageOutboxFile).Length > 0; }
            catch { return false; }
        }
    }

    public async Task PullAsync(CancellationToken ct)
    {
        var thread = await FetchThreadAsync(ct);
        if (thread is null) return;
        foreach (var message in thread.Where(m => m.Direction == MessageDirection.ParentToChild && m.SeenUtc is null))
            QueuePopup(message);
        foreach (var message in thread.Where(m => m.Direction == MessageDirection.ParentToChild && m.DeliveredUtc is null))
            await MarkAsync(message.Id, "delivered", ct);
    }

    public async Task HandleIncomingAsync(ChildMessageDto message, CancellationToken ct)
    {
        if (message.Deleted) return;
        UpsertThread(message);
        if (message.Direction != MessageDirection.ParentToChild) return;
        QueuePopup(message);
        if (message.DeliveredUtc is null)
            await MarkAsync(message.Id, "delivered", ct);
    }

    public async Task<List<ChildMessageDto>> GetThreadAsync(CancellationToken ct)
    {
        var fresh = await FetchThreadAsync(ct);
        if (fresh is not null) return fresh;
        return LoadThread();
    }

    public void ApplyStatusUpdate(ChildMessageDto message)
    {
        if (message.Deleted)
        {
            lock (_gate)
            {
                var thread = LoadThread();
                thread.RemoveAll(m => m.Id == message.Id);
                SaveThread(thread);
            }
            return;
        }
        UpsertThread(message);
    }

    public async Task MarkSeenAsync(Guid messageId, CancellationToken ct) =>
        await MarkAsync(messageId, "seen", ct);

    public async Task MarkAcknowledgedAsync(Guid messageId, CancellationToken ct) =>
        await MarkAsync(messageId, "ack", ct);

    public async Task<SendMessageResult> SendAsync(string text, CancellationToken ct)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0)
            return new SendMessageResult(false, false, Loc.T("Client.Messages.TooLong"), null);
        if (value.Length > 500)
            return new SendMessageResult(false, false, Loc.T("Client.Messages.TooLong"), null);

        if (!TryReserveSend())
        {
            _logger.LogInformation("message-send reserve failed rate-limit remaining={Minutes}", RateLimitRemainingMinutes());
            return new SendMessageResult(false, false, Loc.T("Client.Messages.RateLimited", RateLimitRemainingMinutes()), null);
        }
        _logger.LogInformation("message-send reserve ok length={Length}", value.Length);

        try
        {
            var message = await PostChildMessageAsync(value, ct);
            if (message is not null)
            {
                UpsertThread(message);
                RecordSent();
                _logger.LogInformation("message-send stored id={MessageId}", message.Id);
                return new SendMessageResult(true, false, null, message);
            }
            _logger.LogWarning("message-send POST returned no message; queueing offline");
        }
        catch (RateLimitedException ex)
        {
            _logger.LogWarning("message-send rejected by rate limit: {Error}", ex.Message);
            return new SendMessageResult(false, false, ex.Message, null);
        }
        catch (SendRejectedException ex)
        {
            _logger.LogWarning("message-send rejected by server: {Error}", ex.Message);
            return new SendMessageResult(false, false, ex.Message, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Child message could not be sent; queueing locally");
        }

        QueueOutbox(value);
        RecordSent();
        _logger.LogInformation("message-send queued offline length={Length}", value.Length);
        return new SendMessageResult(false, true, Loc.T("Client.Messages.SendFailed"), null);
    }

    public async Task FlushOutboxAsync(CancellationToken ct)
    {
        var pending = MessageOutbox.Read(ClientPaths.MessageOutboxFile);
        if (pending.Count == 0) return;
        _logger.LogInformation("message-outbox flush count={Count}", pending.Count);
        var remaining = new List<MessageOutboxEntry>();
        for (var i = 0; i < pending.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var message = await PostChildMessageAsync(pending[i].Text, ct);
                if (message is not null)
                {
                    UpsertThread(message);
                    _logger.LogInformation("message-outbox sent id={MessageId}", message.Id);
                    continue;
                }
                _logger.LogWarning("message-outbox send returned no message");
                remaining.AddRange(pending.Skip(i));
                break;
            }
            catch (RateLimitedException)
            {
                remaining.AddRange(pending.Skip(i));
                break;
            }
            catch (Exception)
            {
                remaining.AddRange(pending.Skip(i));
                break;
            }
        }
        MessageOutbox.Rewrite(ClientPaths.MessageOutboxFile, remaining);
        if (remaining.Count > 0)
            _logger.LogInformation("message-outbox remaining={Count}", remaining.Count);
    }

    public async Task<int> UnreadThreadCountAsync(CancellationToken ct) =>
        (await GetThreadAsync(ct)).Count(m => m.Direction == MessageDirection.ChildToParent && m.ParentReadUtc is null);

    public static ChildMessageDto? ReadPopupFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ChildMessageDto>(json, JsonOptions);
        }
        catch { return null; }
    }

    private async Task<ChildMessageDto?> PostChildMessageAsync(string text, CancellationToken ct)
    {
        var (client, credential) = CreateClient();
        if (client is null || credential is null) return null;
        using (client)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Url("/api/device/messages"))
            {
                Content = JsonContent.Create(new DeviceMessageRequest(text), options: JsonOptions)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Credential);
            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogInformation("message-send POST status={Status}", (int)response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new RateLimitedException(Loc.T("Client.Messages.RateLimited", 10));
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("message-send POST unauthorized; credential may need refresh");
                return null;
            }
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new SendRejectedException(ReadError(body) ?? Loc.T("Client.Messages.SendFailedHint"));
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("message-send POST failed with HTTP {Status}", (int)response.StatusCode);
                return null;
            }
            return JsonSerializer.Deserialize<ChildMessageDto>(body, JsonOptions);
        }
    }

    private async Task MarkAsync(Guid messageId, string status, CancellationToken ct)
    {
        try
        {
            var (client, credential) = CreateClient();
            if (client is null || credential is null) return;
            using (client)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Url($"/api/device/messages/{messageId}/{status}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Credential);
                using var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                    _logger.LogDebug("Message status {Status} for {MessageId} returned {Code}", status, messageId, (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not report message status {Status}", status);
        }
    }

    private async Task<List<ChildMessageDto>?> FetchThreadAsync(CancellationToken ct)
    {
        try
        {
            var (client, credential) = CreateClient();
            if (client is null || credential is null) return null;
            using (client)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Url("/api/device/messages"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Credential);
                using var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) return null;
                var json = await response.Content.ReadAsStringAsync(ct);
                var thread = JsonSerializer.Deserialize<List<ChildMessageDto>>(json, JsonOptions);
                if (thread is not null) SaveThread(thread);
                return thread;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not fetch message thread");
            return null;
        }
    }

    private (HttpClient? Client, DeviceCredential? Credential) CreateClient()
    {
        var credential = _credentials.Load();
        if (credential is null || !credential.IsUsable) return (null, null);
        var client = _httpFactory.CreateClient("device");
        client.Timeout = TimeSpan.FromSeconds(20);
        return (client, credential);
    }

    private string Url(string path) => $"{_settings.Value.ServerUrl.TrimEnd('/')}{path}";

    private void QueuePopup(ChildMessageDto message)
    {
        if (message.Deleted || message.SeenUtc is not null) return;
        if (DateTime.UtcNow - message.CreatedUtc > TimeSpan.FromHours(24)) return;
        lock (_gate)
        {
            if (_queuedPopups.Contains(message.Id)) return;
            try
            {
                Directory.CreateDirectory(ClientPaths.MessagePopupDirectory);
                var path = Path.Combine(ClientPaths.MessagePopupDirectory, message.Id.ToString("N") + ".json");
                if (!File.Exists(path))
                {
                    var temp = path + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(message, JsonOptions));
                    File.Move(temp, path, overwrite: true);
                }
                _queuedPopups.Add(message.Id);
                SaveQueuedPopups();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not queue message pop-up {MessageId}", message.Id);
            }
        }
    }

    private void LoadQueuedPopups()
    {
        try
        {
            if (!File.Exists(ClientPaths.MessageQueuedFile)) return;
            var ids = JsonSerializer.Deserialize<List<Guid>>(File.ReadAllText(ClientPaths.MessageQueuedFile), JsonOptions);
            if (ids is null) return;
            foreach (var id in ids) _queuedPopups.Add(id);
        }
        catch { }
    }

    private void SaveQueuedPopups()
    {
        try
        {
            Directory.CreateDirectory(ClientPaths.MessageDirectory);
            var temp = ClientPaths.MessageQueuedFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_queuedPopups.ToList(), JsonOptions));
            File.Move(temp, ClientPaths.MessageQueuedFile, overwrite: true);
        }
        catch { }
    }

    private void UpsertThread(ChildMessageDto message)
    {
        lock (_gate)
        {
            var thread = LoadThread();
            var index = thread.FindIndex(m => m.Id == message.Id);
            if (index >= 0) thread[index] = message;
            else thread.Add(message);
            thread.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));
            SaveThread(thread);
        }
    }

    private static List<ChildMessageDto> LoadThread()
    {
        try
        {
            if (!File.Exists(ClientPaths.MessageThreadFile)) return [];
            var thread = JsonSerializer.Deserialize<List<ChildMessageDto>>(File.ReadAllText(ClientPaths.MessageThreadFile), JsonOptions);
            return thread ?? [];
        }
        catch { return []; }
    }

    private static void SaveThread(List<ChildMessageDto> thread)
    {
        try
        {
            Directory.CreateDirectory(ClientPaths.MessageDirectory);
            var temp = ClientPaths.MessageThreadFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(thread, JsonOptions));
            File.Move(temp, ClientPaths.MessageThreadFile, overwrite: true);
        }
        catch { }
    }

    private bool TryReserveSend()
    {
        lock (_gate)
        {
            var times = ReadSendTimes();
            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(10);
            times.RemoveAll(t => t < cutoff);
            if (times.Count >= 10) return false;
            return true;
        }
    }

    private int RateLimitRemainingMinutes()
    {
        var times = ReadSendTimes();
        if (times.Count < 10) return 1;
        var oldest = times.OrderBy(t => t).First();
        return Math.Max(1, (int)Math.Ceiling((oldest + TimeSpan.FromMinutes(10) - DateTime.UtcNow).TotalMinutes));
    }

    private List<DateTime> ReadSendTimes()
    {
        try
        {
            if (!File.Exists(ClientPaths.MessageSendTimesFile)) return [];
            return JsonSerializer.Deserialize<List<DateTime>>(File.ReadAllText(ClientPaths.MessageSendTimesFile), JsonOptions) ?? [];
        }
        catch { return []; }
    }

    private void RecordSent()
    {
        lock (_gate)
        {
            var times = ReadSendTimes();
            times.RemoveAll(t => t < DateTime.UtcNow - TimeSpan.FromMinutes(10));
            times.Add(DateTime.UtcNow);
            try
            {
                Directory.CreateDirectory(ClientPaths.MessageDirectory);
                File.WriteAllText(ClientPaths.MessageSendTimesFile, JsonSerializer.Serialize(times, JsonOptions));
            }
            catch { }
        }
    }

    private void QueueOutbox(string text)
    {
        lock (_gate)
        {
            try
            {
                MessageOutbox.Append(ClientPaths.MessageOutboxFile, new MessageOutboxEntry(text, DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not queue outgoing child message");
            }
        }
    }

    private static string? ReadError(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch { return null; }
    }

    private sealed class RateLimitedException(string message) : Exception(message);
    private sealed class SendRejectedException(string message) : Exception(message);
}
