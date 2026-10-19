using Microsoft.AspNetCore.SignalR.Client;
using OpControl.Parental.Contracts;

namespace OpControl.Parental.Client;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly EnrollmentClient _enrollment;
    private readonly CredentialStore _credentials;
    private readonly StateStore _state;
    private DeviceCredential? _credential;
    private ClientOptions _options = new();

    public Worker(ILogger<Worker> logger, EnrollmentClient enrollment, CredentialStore credentials, StateStore state)
    {
        _logger = logger;
        _enrollment = enrollment;
        _credentials = credentials;
        _state = state;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ClientPaths.EnsureDataDirectory();
        _options = ClientOptions.Load();
        _logger.LogInformation("OpControl Parental client starting; server {Server}", _options.ServerUrl);

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
        _state.Save(result.LockState);
        _options.EnrollmentCode = null;
        _options.SaveWithoutCode();
        _logger.LogInformation("Device credential stored for {DeviceId}", result.DeviceId);
        return true;
    }

    private async Task RunConnectionAsync(CancellationToken stoppingToken)
    {
        var credential = _credential!;
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

        connection.On<LockStateDto>(DeviceHubMethods.LockStateChanged, state =>
        {
            _state.Save(state);
            _logger.LogInformation("Lock state: locked={Locked} reason={Reason} bonusUntil={BonusUntil}",
                state.IsLocked, state.Reason, state.BonusUntilUtc);
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
                        await connection.InvokeAsync("Heartbeat", DateTime.UtcNow, TimeZoneInfo.Local.Id, stoppingToken);
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
