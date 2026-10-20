using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpControl.Parental.Contracts;

namespace OpControl.Parental.Client;

public sealed class UpdateService : BackgroundService
{
    private sealed class PendingUpdate
    {
        public string FromVersion { get; set; } = "";
        public string ToVersion { get; set; } = "";
        public string TargetMsi { get; set; } = "";
        public string? PreviousMsi { get; set; }
        public string HealthFile { get; set; } = "";
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public bool ForceRollback { get; set; }
    }

    private const string RunnerScript = """
param(
    [string]$TargetMsi,
    [string]$PreviousMsi,
    [string]$HealthFile,
    [string]$ServiceName,
    [string]$Log,
    [switch]$ForceRollback
)
function Write-UpdateLog([string]$message) {
    try { Add-Content -Path $Log -Value ("{0:o} {1}" -f (Get-Date), $message) } catch { }
}
try {
    Write-UpdateLog "starting update to $TargetMsi (force rollback: $ForceRollback)"
    $process = Start-Process msiexec.exe -ArgumentList "/i `"$TargetMsi`" /qn /norestart" -Wait -PassThru
    Write-UpdateLog "msiexec exited $($process.ExitCode)"
    function Invoke-Rollback {
        $entry = Get-ChildItem 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
            ForEach-Object { Get-ItemProperty $_.PSPath } |
            Where-Object { $_.DisplayName -eq 'Parental OpControl' } |
            Sort-Object DisplayVersion -Descending | Select-Object -First 1
        if ($entry -and $entry.PSChildName) {
            Write-UpdateLog "uninstalling current product $($entry.DisplayVersion) ($($entry.PSChildName)) without removing device data"
            $uninstall = Start-Process msiexec.exe -ArgumentList "/x `"$($entry.PSChildName)`" /qn /norestart SUPPRESSDATA=1" -Wait -PassThru
            Write-UpdateLog "uninstall exited $($uninstall.ExitCode)"
        }
        if ($PreviousMsi -and (Test-Path $PreviousMsi)) {
            $rollback = Start-Process msiexec.exe -ArgumentList "/i `"$PreviousMsi`" /qn /norestart" -Wait -PassThru
            Write-UpdateLog "rollback install exited $($rollback.ExitCode)"
            return $rollback.ExitCode
        }
        Write-UpdateLog "no previous MSI available for rollback"
        return 1
    }
    if ($ForceRollback) {
        Start-Sleep -Seconds 15
        Write-UpdateLog "forced rollback test"
        $code = Invoke-Rollback
        exit $code
    }
    $deadline = (Get-Date).AddMinutes(5)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $HealthFile) {
            Write-UpdateLog "new service reported healthy"
            exit 0
        }
        Start-Sleep -Seconds 5
    }
    Write-UpdateLog "health was not reported; rolling back"
    $code = Invoke-Rollback
    exit $code
}
catch {
    Write-UpdateLog "update runner failed: $_"
    exit 3
}
""";

    private static readonly JsonSerializerOptions JsonOptions = new(ClientOptions.JsonOptions) { WriteIndented = false };
    private readonly ClientSettings _settings;
    private readonly CredentialStore _credentials;
    private readonly EnrollmentClient _enrollment;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<UpdateService> _logger;
    private readonly SemaphoreSlim _trigger = new(0, 1);
    private DateTime _lastCheckUtc = DateTime.MinValue;
    private int _busy;

    public UpdateService(ClientSettings settings, CredentialStore credentials, EnrollmentClient enrollment, IHttpClientFactory httpFactory, ILogger<UpdateService> logger)
    {
        _settings = settings;
        _credentials = credentials;
        _enrollment = enrollment;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public static string Version => typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.7.3";

    public void RequestUpdate(UpdateInfoDto? info)
    {
        if (info is not null)
        {
            try { File.WriteAllText(Path.Combine(ClientPaths.UpdateDirectory, "requested-update.json"), JsonSerializer.Serialize(info, JsonOptions)); }
            catch { }
        }
        if (_trigger.CurrentCount == 0) _trigger.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(ClientPaths.UpdateDirectory);
        await ResolvePendingAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var due = DateTime.UtcNow - _lastCheckUtc > TimeSpan.FromHours(24);
                if (due) await CheckAsync(false, stoppingToken);

                var triggered = await _trigger.WaitAsync(TimeSpan.FromMinutes(15), stoppingToken);
                if (triggered) await CheckAsync(true, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Update check failed");
            }
        }
    }

    private async Task ResolvePendingAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(ClientPaths.UpdatePendingFile)) return;
            var pending = JsonSerializer.Deserialize<PendingUpdate>(await File.ReadAllTextAsync(ClientPaths.UpdatePendingFile, ct), JsonOptions);
            if (pending is null) return;
            var current = Version;
            if (string.Equals(current, pending.ToVersion, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(ClientPaths.ForceRollbackFile))
                {
                    _logger.LogWarning("Forced rollback test is active; not reporting {Version} healthy", pending.ToVersion);
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                Directory.CreateDirectory(Path.GetDirectoryName(pending.HealthFile)!);
                await File.WriteAllTextAsync(pending.HealthFile, DateTime.UtcNow.ToString("o"), ct);
                await PostHealthAsync(pending.ToVersion, pending.FromVersion, "healthy", "New service started and reported healthy.", ct);
                CleanupPending();
                _logger.LogInformation("Update to {Version} reported healthy", pending.ToVersion);
            }
            else if (string.Equals(current, pending.FromVersion, StringComparison.OrdinalIgnoreCase))
            {
                await PostHealthAsync(pending.FromVersion, pending.ToVersion, "rolledback", "The previous version is running after the update watchdog.", ct);
                CleanupPending();
                _logger.LogWarning("Update to {Version} appears to have rolled back", pending.ToVersion);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve pending update state");
        }
    }

    private async Task CheckAsync(bool force, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            _lastCheckUtc = DateTime.UtcNow;
            var credential = _credentials.Load();
            if (credential is null || !credential.IsUsable) return;
            var options = _settings.Value;
            var client = _httpFactory.CreateClient("device");
            UpdateInfoDto? info = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{options.ServerUrl.TrimEnd('/')}/api/device/update");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Credential);
                using var response = await client.SendAsync(request, ct);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    var refreshed = await _enrollment.RefreshAsync(options.ServerUrl, credential.Credential, ct);
                    if (refreshed is null) return;
                    credential.Credential = refreshed.Credential;
                    credential.ExpiresUtc = refreshed.CredentialExpiresUtc;
                    _credentials.Save(credential);
                    return;
                }
                if (!response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent) return;
                var body = await response.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(body)) return;
                info = JsonSerializer.Deserialize<UpdateInfoDto>(body, ClientOptions.JsonOptions);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return;
            }

            if (info is null) return;
            var installed = ParseVersion(Version);
            var target = ParseVersion(info.Version);
            if (!force && target <= installed) return;
            if (target <= installed)
            {
                _logger.LogInformation("Server requested update but {Version} is not newer than {Installed}", info.Version, Version);
                return;
            }
            await DownloadAndLaunchAsync(client, credential, info, ct);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task DownloadAndLaunchAsync(HttpClient client, DeviceCredential credential, UpdateInfoDto info, CancellationToken ct)
    {
        var targetPath = Path.Combine(ClientPaths.UpdateDirectory, $"OpControlParental-{info.Version}.msi");
        var previousPath = string.IsNullOrWhiteSpace(info.PreviousVersion)
            ? null
            : Path.Combine(ClientPaths.UpdateDirectory, $"OpControlParental-{info.PreviousVersion}.msi");

        _logger.LogInformation("Downloading client update {Version}", info.Version);
        if (!await DownloadAsync(client, credential, info.DownloadPath, targetPath, ct)) return;
        if (previousPath is not null && !File.Exists(previousPath))
        {
            var previousUrl = $"/api/device/update/download?version={Uri.EscapeDataString(info.PreviousVersion!)}";
            await DownloadAsync(client, credential, previousUrl, previousPath, ct);
        }

        var expected = (info.Sha256 ?? "").Trim().ToLowerInvariant();
        if (expected.Length == 0 || !string.Equals(HashFile(targetPath), expected, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("Update {Version} failed SHA-256 verification", info.Version);
            try { File.Delete(targetPath); } catch { }
            await PostHealthAsync(info.Version, Version, "failed", "SHA-256 verification failed.", ct);
            return;
        }

        var forceRollback = File.Exists(ClientPaths.ForceRollbackFile);
        var pending = new PendingUpdate
        {
            FromVersion = Version,
            ToVersion = info.Version,
            TargetMsi = targetPath,
            PreviousMsi = previousPath,
            HealthFile = ClientPaths.UpdateHealthyFile,
            StartedUtc = DateTime.UtcNow,
            ForceRollback = forceRollback
        };
        try { File.Delete(ClientPaths.UpdateHealthyFile); } catch { }
        await File.WriteAllTextAsync(ClientPaths.UpdatePendingFile, JsonSerializer.Serialize(pending, JsonOptions), ct);
        await File.WriteAllTextAsync(ClientPaths.UpdateInProgressFile, DateTime.UtcNow.ToString("o"), ct);

        var scriptPath = Path.Combine(ClientPaths.UpdateDirectory, "update-runner.ps1");
        await File.WriteAllTextAsync(scriptPath, RunnerScript, ct);
        var arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -TargetMsi \"{targetPath}\" -PreviousMsi \"{previousPath}\" " +
                        $"-HealthFile \"{ClientPaths.UpdateHealthyFile}\" -ServiceName OpControlParental -Log \"{ClientPaths.UpdateLogFile}\"" +
                        (forceRollback ? " -ForceRollback" : "");

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = ClientPaths.UpdateDirectory
        });
        _logger.LogInformation("Update runner started (pid {Pid}); service will be replaced by MSI", process?.Id);
        await PostHealthAsync(info.Version, Version, "installing", "The installer was verified and the update runner started.", ct);
    }

    private async Task<bool> DownloadAsync(HttpClient client, DeviceCredential credential, string path, string destination, CancellationToken ct)
    {
        if (File.Exists(destination))
        {
            try { File.Delete(destination); } catch { }
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_settings.Value.ServerUrl.TrimEnd('/')}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Credential);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Update download failed with status {Status}", (int)response.StatusCode);
            return false;
        }
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, ct);
        return true;
    }

    private async Task PostHealthAsync(string version, string? previous, string status, string detail, CancellationToken ct)
    {
        try
        {
            var credential = _credentials.Load();
            if (credential is null || !credential.IsUsable) return;
            using var client = _httpFactory.CreateClient("device");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_settings.Value.ServerUrl.TrimEnd('/')}/api/device/update/health")
            {
                Content = new StringContent(JsonSerializer.Serialize(new DeviceUpdateHealthRequest(version, previous, status, detail), ClientOptions.JsonOptions), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Credential);
            using var response = await client.SendAsync(request, ct);
            _logger.LogInformation("Update health {Status} reported: {Code}", status, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not report update health");
        }
    }

    private static void CleanupPending()
    {
        try { File.WriteAllText(ClientPaths.UpdateCompletedFile, DateTime.UtcNow.ToString("o")); } catch { }
        try { File.Delete(ClientPaths.UpdatePendingFile); } catch { }
        try { File.Delete(ClientPaths.UpdateInProgressFile); } catch { }
        // Keep update-healthy.flag for the runner to observe; the next update deletes it before starting.
        try { File.Delete(ClientPaths.ForceRollbackFile); } catch { }
    }

    private static System.Version ParseVersion(string value)
    {
        var cleaned = (value ?? "").Split('-', '+')[0].Trim();
        return System.Version.TryParse(cleaned, out var parsed) ? parsed : new System.Version(0, 0);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
