using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using OpenCvSharp;

namespace OpControl.Parental.Helper;

/// <summary>
/// Local capture endpoint. The service connects to this pipe for one remote-view
/// session and receives JPEG frames. Screen capture is silent; camera capture shows
/// the eye indicator (owned by HelperContext).
/// </summary>
internal sealed class RemoteCaptureHost : IDisposable
{
    public const string PipeName = "OpControlParental.Remote";
    private const byte KindScreen = 0;
    private const byte KindCamera = 1;
    private const byte KindStatus = 2;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private readonly string _monitored;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _noticeActive;
    private volatile string? _noticeText;
    private Task? _loop;

    public RemoteCaptureHost(string monitored) => _monitored = monitored;

    public bool NoticeActive => _noticeActive;
    public string? NoticeText => _noticeText;

    /// <summary>Raised on the capture thread when a session starts or ends.</summary>
    public event Action? NoticeChanged;

    public void Start() => _loop ??= Task.Run(() => AcceptLoopAsync(_cts.Token));

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(ct);
                await HandleAsync(pipe, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { HelperPaths.LogEvent($"remote-capture-error {ex.GetType().Name}"); }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        try
        {
            var name = _monitored.Contains('\\') ? _monitored : $"{Environment.MachineName}\\{_monitored}";
            var sid = (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier));
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        }
        catch { }
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        var line = await reader.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(line)) return;
        string kind;
        try { kind = JsonSerializer.Deserialize<RemoteCommand>(line, JsonOptions)?.Kind ?? ""; }
        catch { return; }
        var command = JsonSerializer.Deserialize<RemoteCommand>(line, JsonOptions);
        kind = command?.Kind ?? "";
        var showNotice = command?.ShowNotice == true;
        var noticeText = command?.NoticeText;
        _noticeText = string.IsNullOrWhiteSpace(noticeText) ? "Parent's watching you" : noticeText.Trim();
        _noticeActive = showNotice;
        NoticeChanged?.Invoke();
        try
        {
        if (kind == "camera")
        {
            HelperPaths.LogEvent("remote-camera-start");
            try { await CameraLoopAsync(pipe, ct); }
            finally { HelperPaths.LogEvent("remote-camera-stop"); }
        }
        else if (kind == "screen")
        {
            HelperPaths.LogEvent("remote-screen-start");
            try { await ScreenLoopAsync(pipe, ct); }
            finally { HelperPaths.LogEvent("remote-screen-stop"); }
        }
        }
        finally
        {
            _noticeActive = false;
            _noticeText = null;
            NoticeChanged?.Invoke();
        }
    }

    private static async Task ScreenLoopAsync(Stream pipe, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && pipe.CanWrite)
        {
            try
            {
                var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
                using var full = new Bitmap(bounds.Width, bounds.Height);
                using (var graphics = Graphics.FromImage(full))
                    graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);

                var width = Math.Min(1280, full.Width);
                var height = (int)Math.Round(full.Height * (width / (double)full.Width));
                using var resized = new Bitmap(width, height);
                using (var graphics = Graphics.FromImage(resized))
                    graphics.DrawImage(full, 0, 0, width, height);
                using var memory = new MemoryStream();
                resized.Save(memory, ImageFormat.Jpeg);
                await WriteFrameAsync(pipe, KindScreen, memory.ToArray(), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await WriteStatusAsync(pipe, "Screen capture failed: " + ex.Message, ct);
                return;
            }
            await Task.Delay(200, ct);
        }
    }

    private async Task CameraLoopAsync(Stream pipe, CancellationToken ct)
    {
        try
        {
            VideoCapture? capture = null;
            var selected = "";
            foreach (var (index, api, name) in new[]
            {
                (1, VideoCaptureAPIs.DSHOW, "DirectShow/1"),
                (0, VideoCaptureAPIs.DSHOW, "DirectShow/0"),
                (1, VideoCaptureAPIs.MSMF, "MediaFoundation/1"),
                (0, VideoCaptureAPIs.MSMF, "MediaFoundation/0")
            })
            {
                var candidate = new VideoCapture();
                try
                {
                    var task = Task.Run(() => candidate.Open(index, api));
                    if (!task.Wait(TimeSpan.FromSeconds(5)) || !task.Result)
                    {
                        HelperPaths.LogEvent($"camera-open-failed {name}");
                        candidate.Dispose();
                        continue;
                    }
                    candidate.Set(VideoCaptureProperties.FrameWidth, 640);
                    candidate.Set(VideoCaptureProperties.FrameHeight, 480);
                    candidate.Set(VideoCaptureProperties.BufferSize, 1);
                    using var probe = new Mat();
                    var lit = false;
                    for (var i = 0; i < 8 && !lit; i++)
                    {
                        if (candidate.Read(probe) && !probe.Empty())
                        {
                            var mean = Cv2.Mean(probe);
                            var brightness = (mean.Val0 + mean.Val1 + mean.Val2) / 3.0;
                            if (brightness > 2.5) lit = true;
                        }
                        await Task.Delay(120, ct);
                    }
                    HelperPaths.LogEvent($"camera-open {name} lit={lit}");
                    if (lit) { capture = candidate; selected = name; break; }
                }
                catch (Exception ex)
                {
                    HelperPaths.LogEvent($"camera-open-error {name} {ex.GetType().Name}");
                }
                candidate.Dispose();
            }

            if (capture is null)
            {
                await WriteStatusAsync(pipe, "No camera detected (or the camera is covered).", ct);
                return;
            }
            using (capture)
            {
                    HelperPaths.LogEvent($"camera-selected {selected}");
                using var frame = new Mat();
                while (!ct.IsCancellationRequested && pipe.CanWrite)
                {
                    if (!capture.Read(frame) || frame.Empty())
                    {
                        await WriteStatusAsync(pipe, "Camera stopped responding.", ct);
                        return;
                    }
                    if (frame.Width > 640)
                    {
                        var height = (int)Math.Round(frame.Height * (640.0 / frame.Width));
                        Cv2.Resize(frame, frame, new OpenCvSharp.Size(640, height));
                    }
                    Cv2.ImEncode(".jpg", frame, out var jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 62));
                    await WriteFrameAsync(pipe, KindCamera, jpeg, ct);
                    await Task.Delay(200, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            HelperPaths.LogEvent($"remote-camera-error {ex.GetType().Name}");
            try { await WriteStatusAsync(pipe, "Camera error: " + ex.Message, ct); } catch { }
        }
    }

    private static async Task WriteFrameAsync(Stream pipe, byte kind, byte[] payload, CancellationToken ct)
    {
        var header = new byte[5];
        header[0] = kind;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), payload.Length);
        await pipe.WriteAsync(header, ct);
        await pipe.WriteAsync(payload, ct);
        await pipe.FlushAsync(ct);
    }

    private static Task WriteStatusAsync(Stream pipe, string message, CancellationToken ct) =>
        WriteFrameAsync(pipe, KindStatus, Encoding.UTF8.GetBytes(message), ct);

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }

    private sealed record RemoteCommand(string Kind, bool ShowNotice = false, string? NoticeText = null);
}
