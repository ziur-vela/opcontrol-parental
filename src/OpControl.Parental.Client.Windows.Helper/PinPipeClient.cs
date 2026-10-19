using System.IO.Pipes;
using System.Text.Json;
using OpControl.Parental.Contracts;

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

    private static async Task<PinPipeResponse> SendAsync(PinPipeRequest request, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(3000, timeout.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
            var line = await reader.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(line))
                return new PinPipeResponse(false, "The device service did not respond.");
            return JsonSerializer.Deserialize<PinPipeResponse>(line, JsonOptions) ?? new PinPipeResponse(false, "Invalid response.");
        }
        catch (Exception ex)
        {
            return new PinPipeResponse(false, "The device service is not available: " + ex.Message);
        }
    }
}
