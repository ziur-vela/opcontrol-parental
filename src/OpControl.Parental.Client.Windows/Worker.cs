using Microsoft.AspNetCore.SignalR.Client;
using OpControl.Parental.Contracts;

namespace OpControl.Parental.Client;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly EnrollmentClient _enrollment;
    private readonly CredentialStore _credentials;
    private readonly DeviceStateStore _deviceStates;
    private readonly LockRuntime _runtime;
    private readonly OverrideSync _overrides;
    private readonly CategoryCache _categories;
    private readonly ClientSettings _settings;
    private readonly UpdateService _updates;
    private readonly TaskManagerPolicy _policy;
    private DeviceCredential? _credential;
    private ClientOptions _options = new();
    private string ClientVersion => typeof(Worker).Assembly.GetName().Version?.ToString(3) ?? "0.5.0";

    private readonly IServiceProvider _serviceProvider;

    public Worker(
        ILogger<Worker> logger,
        EnrollmentClient enrollment,
        CredentialStore credentials,
        DeviceStateStore deviceStates,
        LockRuntime runtime,
        OverrideSync overrides,
        CategoryCache categories,
        ClientSettings settings,
        UpdateService updates,
        TaskManagerPolicy policy,
        IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _enrollment = enrollment;
        _credentials = credentials;
        _deviceStates = deviceStates;
        _runtime = runtime;
        _overrides = overrides;
        _categories = categories;
        _settings = settings;
        _updates = updates;
        _policy = policy;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ClientPaths.EnsureDirectoriesWithAcl();
        CheckPreviousShutdown();
        _options = ClientOptions.Load();
        MsiConfig.Apply(_options);
        _settings.Value = _options;
        _logger.LogInformation("OpControl Parental client starting; server {Server}, monitored account {Account}", _options.ServerUrl, _options.MonitoredAccount);

        var cached = _deviceStates.Load();
        if (cached is not null)
        {
            _runtime.ApplyServerState(cached);
            _logger.LogInformation("Loaded cached device state for child {ChildId}", cached.ChildId);
        }

        var runtime = _runtime.Current;
        if (runtime is null || !runtime.IsLocked)
        {
            _policy.Restore(_options.MonitoredAccount);
            _logger.LogInformation("Task Manager policy restored after service start (device not locked)");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await EnsureCredentialAsync(stoppingToken))
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }
                await RunConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Client loop failed; retrying in 15 seconds");
                try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void CheckPreviousShutdown()
    {
        try
        {
            if (!File.Exists(ClientPaths.TamperBaselineFile))
            {
                File.WriteAllText(ClientPaths.TamperBaselineFile, DateTime.UtcNow.ToString("o"));
                if (File.Exists(ClientPaths.CleanShutdownFile)) File.Delete(ClientPaths.CleanShutdownFile);
                return;
            }
            if (File.Exists(ClientPaths.CleanShutdownFile))
            {
                File.Delete(ClientPaths.CleanShutdownFile);
                return;
            }
            if (File.Exists(ClientPaths.CredentialFile))
                QueueTamper("service-stopped", "The client service stopped without a clean shutdown (possible service stop or crash).");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not evaluate previous shutdown state");
        }
    }

    private void QueueTamper(string kind, string detail)
    {
        try
        {
            var dataSync = GetService<DataSyncService>();
            dataSync?.QueueTamper(new TamperEventDto(kind, detail, DateTime.UtcNow));
        }
        catch { }
    }

    private T? GetService<T>() where T : class => _serviceProvider.GetService(typeof(T)) as T;

    private async Task<bool> EnsureCredentialAsync(CancellationToken ct)
    {
        if (_credential is { } existing && existing.IsUsable)
            return true;

        _credential = _credentials.Load();
        if (_credential is { } loaded && loaded.IsUsable)
            return true;

        if (string.IsNullOrWhiteSpace(_options.EnrollmentCode))
        {
            _logger.LogError("No device credential and no enrollment code in {Config}", ClientPaths.ConfigFile);
            return false;
        }

        var result = await _enrollment.EnrollAsync(_options, _options.EnrollmentCode!, ct);
        _credential = new DeviceCredential
        {
            DeviceId = result.DeviceId,
            Credential = result.Credential,
            ExpiresUtc = result.CredentialExpiresUtc
        };
        _credentials.Save(_credential);
        _options.EnrollmentCode = null;
        _options.SaveWithoutCode();
        _logger.LogInformation("Device credential stored for {DeviceId}", result.DeviceId);
        return true;
    }

    private async Task RunConnectionAsync(CancellationToken stoppingToken)
    {
        var credential = _credential!;
        await _overrides.SyncPendingAsync(_options, stoppingToken);

        var connection = new HubConnectionBuilder()
            .WithUrl($"{_options.ServerUrl.TrimEnd('/')}/hubs/device", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(_credential?.Credential);
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
            })
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(30)
            })
            .Build();

        connection.KeepAliveInterval = TimeSpan.FromSeconds(15);
        connection.ServerTimeout = TimeSpan.FromSeconds(60);

        connection.On<UpdateInfoDto>(DeviceHubMethods.UpdateRequested, info => _updates.RequestUpdate(info));

        connection.On<DeviceStateDto>(DeviceHubMethods.DeviceStateChanged, state =>
        {
            _runtime.ApplyServerState(state);
            if (_overrides.HasPending && _overrides.LastPending is { } pending)
                _runtime.ApplyLocalOverride(pending.Option, pending.Minutes);
            _ = _categories.UpdateAsync(state.Categories, CancellationToken.None);
            _logger.LogInformation("Device state: manual={Manual} unlock={Unlock} bonus={Bonus} schedules={Schedules} sites={Sites} apps={Apps} categories={Categories}",
                state.ManualLock, state.UnlockUntilUtc, state.BonusUntilUtc, state.Schedules.Count,
                state.WebsiteRules.Count, state.AppRules.Count, state.Categories.Count);
        });

        connection.Reconnecting += error =>
        {
            _logger.LogWarning(error, "Hub connection lost; reconnecting");
            return Task.CompletedTask;
        };
        connection.Reconnected += connectionId =>
        {
            _logger.LogInformation("Hub reconnected as {ConnectionId}", connectionId);
            return Task.CompletedTask;
        };
        connection.Closed += error =>
        {
            _logger.LogWarning(error, "Hub connection closed");
            return Task.CompletedTask;
        };

        try
        {
            await connection.StartAsync(stoppingToken);
            _logger.LogInformation("Connected to hub as device {DeviceId}", credential.DeviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hub connection failed");
            await TryRefreshAsync(stoppingToken);
            throw;
        }

        var lastRefresh = DateTime.UtcNow;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (connection.State == HubConnectionState.Connected)
                {
                    try
                    {
                        await connection.InvokeAsync("Heartbeat", DateTime.UtcNow, TimeZoneInfo.Local.Id, ClientVersion, stoppingToken);
                        if (_overrides.HasPending)
                            await _overrides.SyncPendingAsync(_options, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Heartbeat failed");
                    }
                }

                if (DateTime.UtcNow - lastRefresh > TimeSpan.FromDays(1))
                {
                    await TryRefreshAsync(stoppingToken);
                    lastRefresh = DateTime.UtcNow;
                }

                if (connection.State == HubConnectionState.Disconnected)
                {
                    _logger.LogWarning("Hub is disconnected; cycling connection");
                    break;
                }
            }
        }
        finally
        {
            await connection.StopAsync(CancellationToken.None);
            await connection.DisposeAsync();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(ClientPaths.PrivateDirectory);
            var update = File.Exists(ClientPaths.UpdateInProgressFile);
            var uninstall = File.Exists(ClientPaths.UninstallingFile);
            var recentUpdate = false;
            try
            {
                if (File.Exists(ClientPaths.UpdateCompletedFile))
                {
                    var completed = File.GetLastWriteTimeUtc(ClientPaths.UpdateCompletedFile);
                    recentUpdate = DateTime.UtcNow - completed < TimeSpan.FromMinutes(10);
                }
            }
            catch { }
            if (!update && !uninstall && !recentUpdate)
            {
                QueueTamper("service-stopped", "The service was stopped without an update or uninstall in progress.");
                _logger.LogWarning("Service stopped while not updating or uninstalling");
            }
            File.WriteAllText(ClientPaths.CleanShutdownFile, DateTime.UtcNow.ToString("o"));
        }
        catch { }
        await base.StopAsync(cancellationToken);
    }

    private async Task TryRefreshAsync(CancellationToken ct)
    {
        if (_credential is null) return;
        var refreshed = await _enrollment.RefreshAsync(_options.ServerUrl, _credential.Credential, ct);
        if (refreshed is null) return;
        _credential.Credential = refreshed.Credential;
        _credential.ExpiresUtc = refreshed.CredentialExpiresUtc;
        _credential.UpdatedUtc = DateTime.UtcNow;
        _credentials.Save(_credential);
        _logger.LogInformation("Device credential refreshed; expires {Expires:u}", _credential.ExpiresUtc);
    }
}
