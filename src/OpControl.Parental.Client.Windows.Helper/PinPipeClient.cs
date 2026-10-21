using System.IO.Pipes;
using System.Text.Json;
using OpControl.Parental.Contracts;
using OpControl.Parental.Localization;

namespace OpControl.Parental.Helper;

public static class PinPipeClient
{
    private const string PipeName = "OpControlParental";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public static async Task<PinPipeResponse> VerifyPinAsync(string pin, CancellationToken ct)
    {
        return await SendAsync(new PinPipeRequest("verify-pin", Pin: pin), ct);
    }

    public static async Task<PinPipeResponse> ApplyOverrideAsync(string ticket, string option, int? minutes, CancellationToken ct)
    {
        return await SendAsync(new PinPipeRequest("override", Option: option, Minutes: minutes, Ticket: ticket), ct);
    }

    public static async Task<PinPipeResponse> GetThreadAsync(CancellationToken ct) =>
        await SendAsync(new PinPipeRequest("messages-thread"), ct);

    public static async Task<PinPipeResponse> SendMessageAsync(string text, CancellationToken ct) =>
        await SendAsync(new PinPipeRequest("message-send", Text: text), ct, timeoutSeconds: 25);

    public static async Task<PinPipeResponse> MarkSeenAsync(Guid messageId, CancellationToken ct) =>
        await SendAsync(new PinPipeRequest("message-seen", MessageId: messageId), ct);

    public static async Task<PinPipeResponse> MarkAcknowledgedAsync(Guid messageId, CancellationToken ct) =>
        await SendAsync(new PinPipeRequest("message-ack", MessageId: messageId), ct);

    public static async Task<PinPipeResponse> OpenKidAsync(string tab, string? theme, string? language, bool messagesOnly, CancellationToken ct) =>
        await SendAsync(new PinPipeRequest("open-kid", Tab: tab, Theme: theme, Language: language, MessagesOnly: messagesOnly), ct);

    private static async Task<PinPipeResponse> SendAsync(PinPipeRequest request, CancellationToken ct, int timeoutSeconds = 5)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(Math.Min(5000, timeoutSeconds * 1000), timeout.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
            var line = await reader.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(line))
                return new PinPipeResponse(false, Loc.T("Client.Pipe.NoService"));
            return JsonSerializer.Deserialize<PinPipeResponse>(line, JsonOptions) ?? new PinPipeResponse(false, Loc.T("Client.Pipe.InvalidResponse"));
        }
        catch (Exception ex)
        {
            HelperPaths.LogEvent($"pipe-request-failed type={request.Type} {ex.GetType().Name}: {ex.Message}");
            return new PinPipeResponse(false, Loc.T("Client.Pipe.ServiceUnavailable", ex.Message));
        }
    }
}
