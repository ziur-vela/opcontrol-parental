// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpControl.Parental.Updater;

public sealed class UpdaterOptions
{
    public string Secret { get; set; } = "";
    public string ComposeFile { get; set; } = "/stack/docker-compose.yml";
    public string ComposeDir { get; set; } = "/stack";
    public string ProjectName { get; set; } = "opcontrol-parental";
    public string WebContainer { get; set; } = "parental-web";
    public string WebUrl { get; set; } = "http://web:8080";
    public string Network { get; set; } = "opcontrol-parental_default";
    public string DbHost { get; set; } = "db";
    public string DbUser { get; set; } = "parental";
    public string DbName { get; set; } = "parental";
    public string DbPassword { get; set; } = "";
    public string BackupsPath { get; set; } = "/data/backups";
    public string StatePath { get; set; } = "/data/state.json";
    public string DockerPath { get; set; } = "docker";
    public string PostgresImage { get; set; } = "postgres:17.6-bookworm";
    public int HealthTimeoutSeconds { get; set; } = 180;
    public int KeepBackups { get; set; } = 5;
}

public sealed record UpdateRequest(string Version, string ManifestUrl);
public sealed record RestoreRequest(string Backup, bool Confirm);
public sealed record Manifest(string Version, string? Image, string? ImageDigest, string? MinimumVersion,
    [property: JsonPropertyName("notes")] ManifestNotes? Notes, bool? Prerelease)
{
    public string? NotesEn => Notes?.En;
    public string? NotesEs => Notes?.Es;
}
public sealed record ManifestNotes(string? En, string? Es);
public sealed record UpdateState(
    string Id, string FromVersion, string ToVersion, string Status, string Step, string? Message,
    string? Backup, string? PreviousImage, string? NewImage, DateTime StartedUtc, DateTime? FinishedUtc,
    bool RolledBack);

public sealed class UpdaterState
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly int _keep;
    public List<UpdateState> History { get; private set; } = [];
    public UpdateState? Current { get; private set; }
    public bool Running => Current is { FinishedUtc: null };

    public UpdaterState(UpdaterOptions options)
    {
        _path = options.StatePath;
        _keep = Math.Max(5, options.KeepBackups);
        Load();
    }

    public void Begin(UpdateState state)
    {
        lock (_gate) { Current = state; Save(); }
    }

    public void Step(string step, string? message = null)
    {
        lock (_gate)
        {
            if (Current is null) return;
            Current = Current with { Step = step, Message = message };
            Save();
        }
    }

    public void Patch(string? backup = null, string? previousImage = null, string? newImage = null)
    {
        lock (_gate)
        {
            if (Current is null) return;
            Current = Current with
            {
                Backup = backup ?? Current.Backup,
                PreviousImage = previousImage ?? Current.PreviousImage,
                NewImage = newImage ?? Current.NewImage
            };
            Save();
        }
    }

    public void Finish(string status, string? message, bool rolledBack)
    {
        lock (_gate)
        {
            if (Current is null) return;
            Current = Current with { Status = status, Message = message, FinishedUtc = DateTime.UtcNow, Step = "done", RolledBack = rolledBack };
            History.Insert(0, Current);
            if (History.Count > 10) History = History.Take(10).ToList();
            Current = null;
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var payload = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(_path));
            History = payload?.History ?? [];
            Current = payload?.Current;
            if (Current is not null && Current.FinishedUtc is not null) Current = null;
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new StateFile(History, Current),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, true);
        }
        catch { }
    }

    private sealed record StateFile(List<UpdateState>? History, UpdateState? Current);
}

public sealed class UpdateRunner
{
    private readonly UpdaterOptions _options;
    private readonly UpdaterState _state;
    private readonly ILogger<UpdateRunner> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UpdateRunner(UpdaterOptions options, UpdaterState state, ILogger<UpdateRunner> logger)
    {
        _options = options;
        _state = state;
        _logger = logger;
    }

    public object Status() => new { running = _state.Running, current = _state.Current, history = _state.History };

    public (bool Started, string? Error) Start(UpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Version) || string.IsNullOrWhiteSpace(request.ManifestUrl))
            return (false, "Version and manifest URL are required.");
        if (!_gate.Wait(0)) return (false, "An update is already running.");
        var from = CurrentVersionSync();
        var state = new UpdateState(Guid.NewGuid().ToString("N"), from, request.Version, "running", "starting",
            null, null, null, null, DateTime.UtcNow, null, false);
        _state.Begin(state);
        _ = Task.Run(async () =>
        {
            try { await RunAsync(request, from); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update failed");
                _state.Finish("failed", ex.Message, false);
            }
            finally { _gate.Release(); }
        });
        return (true, null);
    }

    private async Task RunAsync(UpdateRequest request, string fromVersion)
    {
        _state.Step("manifest", "Downloading release manifest");
        var manifest = await DownloadManifestAsync(request.ManifestUrl);
        if (manifest.Version != request.Version)
            throw new InvalidOperationException($"Manifest version {manifest.Version} does not match requested {request.Version}.");
        if (manifest.Prerelease == true)
            throw new InvalidOperationException("Pre-release versions cannot be installed on the stable channel.");
        if (!Version.TryParse(fromVersion.TrimStart('v'), out var from) || !Version.TryParse(manifest.Version.TrimStart('v'), out var to))
            throw new InvalidOperationException("Could not parse versions.");
        if (to < from)
            throw new InvalidOperationException($"Downgrades are not allowed ({fromVersion} -> {manifest.Version}).");
        if (!string.IsNullOrWhiteSpace(manifest.MinimumVersion)
            && Version.TryParse(manifest.MinimumVersion, out var minimum) && from < minimum)
            throw new InvalidOperationException($"This release requires version {manifest.MinimumVersion} or newer.");
        var digest = manifest.ImageDigest ?? "";
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71)
            throw new InvalidOperationException("The manifest does not contain a valid image digest.");
        var image = string.IsNullOrWhiteSpace(manifest.Image) ? "ghcr.io/ziur-vela/parental-opcontrol" : manifest.Image;
        var target = $"{image}@{digest}";

        _state.Step("backup", "Backing up the database");
        var backup = await BackupAsync();
        _state.Patch(backup: backup, newImage: target);

        var previous = await RunAsync("docker", ["inspect", "--format", "{{.Config.Image}}", _options.WebContainer], null);
        var previousImage = previous.Trim();
        if (string.IsNullOrWhiteSpace(previousImage))
            throw new InvalidOperationException("Could not read the current web image.");
        _state.Patch(previousImage: previousImage);

        _state.Step("pull", $"Pulling {target}");
        await RunAsync("docker", ["pull", target], null);

        _state.Step("recreate", "Recreating the web container");
        await ComposeUpAsync(target);

        _state.Step("health", "Waiting for the new version to report healthy");
        if (!await WaitHealthyAsync(manifest.Version))
        {
            _state.Step("rollback", "Health check failed; rolling back");
            await ComposeUpAsync(previousImage);
            if (!await WaitHealthyAsync(fromVersion))
            {
                _state.Finish("failed", "Rollback also failed. Restore the database backup only with the administrator's confirmation.", true);
                _logger.LogError("Rollback failed; previous image {Image} did not become healthy", previousImage);
                return;
            }
            _state.Finish("rolled-back", $"Update to {manifest.Version} failed its health check and was rolled back to {fromVersion}.", true);
            return;
        }

        _state.Finish("success", $"Updated from {fromVersion} to {manifest.Version}. Backup: {backup}", false);
    }

    private async Task<Manifest> DownloadManifestAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Manifest URL must be HTTPS.");
        using var response = await _http.GetAsync(uri);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        var manifest = await JsonSerializer.DeserializeAsync<Manifest>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return manifest ?? throw new InvalidOperationException("The release manifest is empty.");
    }

    private async Task<string> BackupAsync()
    {
        Directory.CreateDirectory(_options.BackupsPath);
        PruneBackups();
        var name = $"parental-pre-update-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.sql";
        var path = Path.Combine(_options.BackupsPath, name);
        var args = new List<string>
        {
            "run", "--rm", "--network", _options.Network,
            "-e", $"PGPASSWORD={_options.DbPassword}",
            _options.PostgresImage, "pg_dump", "-h", _options.DbHost, "-U", _options.DbUser,
            "-d", _options.DbName, "--clean", "--if-exists"
        };
        var (exit, stdout, stderr) = await RunCaptureAsync(args, null);
        if (exit != 0)
            throw new InvalidOperationException($"pg_dump failed: {stderr.Trim()}");
        await File.WriteAllTextAsync(path, stdout);
        var text = await File.ReadAllTextAsync(path);
        if (!text.Contains("PostgreSQL database dump complete", StringComparison.Ordinal))
        {
            try { File.Delete(path); } catch { }
            throw new InvalidOperationException("pg_dump did not report a complete dump.");
        }
        _logger.LogInformation("Database backup written to {Path} ({Bytes} bytes)", path, text.Length);
        return path;
    }

    private void PruneBackups()
    {
        try
        {
            var files = new DirectoryInfo(_options.BackupsPath).GetFiles("parental-pre-update-*.sql")
                .OrderByDescending(f => f.LastWriteTimeUtc).Skip(_options.KeepBackups).ToList();
            foreach (var file in files) file.Delete();
        }
        catch { }
    }

    private async Task<bool> WaitHealthyAsync(string expectedVersion)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_options.HealthTimeoutSeconds);
        var wanted = expectedVersion.TrimStart('v');
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await _http.GetAsync($"{_options.WebUrl.TrimEnd('/')}/health");
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    var version = doc.RootElement.TryGetProperty("version", out var value) ? value.GetString() ?? "" : "";
                    if (version.Contains(wanted, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        return false;
    }

    private async Task ComposeUpAsync(string image)
    {
        var args = new List<string>
        {
            "compose", "--project-name", _options.ProjectName, "--project-directory", _options.ComposeDir,
            "-f", _options.ComposeFile, "up", "-d", "--no-deps", "--force-recreate", "--pull", "never", "web"
        };
        var (exit, _, stderr) = await RunCaptureAsync(args, new Dictionary<string, string> { ["WEB_IMAGE"] = image });
        if (exit != 0)
            throw new InvalidOperationException($"docker compose failed: {stderr.Trim()}");
    }

    private string CurrentVersionSync()
    {
        try
        {
            using var response = _http.GetAsync($"{_options.WebUrl.TrimEnd('/')}/version").GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return "0.0.0";
            using var doc = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var value = doc.RootElement.TryGetProperty("version", out var property) ? property.GetString() ?? "0.0.0" : "0.0.0";
            return value.TrimStart('v');
        }
        catch { return "0.0.0"; }
    }

    private Task<string> RunAsync(string file, string[] args, IDictionary<string, string>? env)
        => RunCaptureAsync(args, env, file).ContinueWith(t => t.Result.Exit == 0 ? t.Result.Stdout : t.Result.Stderr);

    private async Task<(int Exit, string Stdout, string Stderr)> RunCaptureAsync(
        IEnumerable<string> args, IDictionary<string, string>? env, string? file = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = file ?? _options.DockerPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (env is not null)
            foreach (var pair in env) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    public async Task<(bool Started, string? Error)> RestoreAsync(RestoreRequest request)
    {
        if (!request.Confirm) return (false, "Restore requires confirmation.");
        var path = Path.GetFullPath(request.Backup);
        if (!path.StartsWith(Path.GetFullPath(_options.BackupsPath), StringComparison.Ordinal) || !File.Exists(path))
            return (false, "Backup file not found in the backup directory.");
        if (!_gate.Wait(0)) return (false, "An update is already running.");
        _ = Task.Run(async () =>
        {
            var state = new UpdateState(Guid.NewGuid().ToString("N"), CurrentVersionSync(), CurrentVersionSync(), "running",
                "restore", null, path, null, null, DateTime.UtcNow, null, false);
            _state.Begin(state);
            try
            {
                _state.Step("restore", "Stopping the web container");
                await RunCaptureAsync(["compose", "--project-name", _options.ProjectName, "--project-directory", _options.ComposeDir,
                    "-f", _options.ComposeFile, "stop", "web"], null);
                _state.Step("restore", "Restoring the database");
                var start = new ProcessStartInfo
                {
                    FileName = _options.DockerPath,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                foreach (var arg in new[] { "run", "--rm", "-i", "--network", _options.Network, "-e", $"PGPASSWORD={_options.DbPassword}",
                    _options.PostgresImage, "psql", "-h", _options.DbHost, "-U", _options.DbUser, "-d", _options.DbName, "-v", "ON_ERROR_STOP=1" })
                    start.ArgumentList.Add(arg);
                using (var process = Process.Start(start)!)
                {
                    await using (var input = File.OpenRead(path))
                    using (var stdin = process.StandardInput.BaseStream)
                        await input.CopyToAsync(stdin);
                    var errors = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    if (process.ExitCode != 0) throw new InvalidOperationException($"psql restore failed: {errors.Trim()}");
                }
                _state.Step("restore", "Starting the web container");
                await RunCaptureAsync(["compose", "--project-name", _options.ProjectName, "--project-directory", _options.ComposeDir,
                    "-f", _options.ComposeFile, "start", "web"], null);
                _state.Finish("success", $"Database restored from {Path.GetFileName(path)}.", false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Restore failed");
                _state.Finish("failed", ex.Message, false);
            }
            finally { _gate.Release(); }
        });
        return (true, null);
    }
}
