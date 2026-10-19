namespace OpControl.Parental.Client;

public sealed class DnsFilterService : BackgroundService
{
    private readonly DnsProxy _proxy;
    private readonly NetworkDnsConfigurator _network;
    private readonly ClientSettings _settings;
    private readonly ILogger<DnsFilterService> _logger;
    private bool _filtering;
    private int _ticks;

    public DnsFilterService(DnsProxy proxy, NetworkDnsConfigurator network, ClientSettings settings, ILogger<DnsFilterService> logger)
    {
        _proxy = proxy;
        _network = network;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BrowserDohPolicy.Apply(_logger);
        _proxy.Start();
        if (!_proxy.Ready)
        {
            _logger.LogError("DNS filter could not start; adapters will not be redirected");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                _ticks++;
                var session = WindowsSession.GetMonitoredSessionId(_settings.Value.MonitoredAccount);
                if (session > 0 && !_filtering)
                {
                    _network.Apply(_proxy.Ipv4Bound, _proxy.Ipv6Bound);
                    _filtering = true;
                    _logger.LogInformation("DNS filtering enabled for monitored session {Session}", session);
                }
                else if (session == 0 && _filtering)
                {
                    _network.Restore();
                    _filtering = false;
                    _logger.LogInformation("Monitored account signed out; DNS filtering disabled and adapters restored");
                }
                else if (_filtering && _ticks % 15 == 0)
                {
                    _network.EnsureApplied();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DNS filter supervision failed");
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_filtering) _network.Restore();
        }
        catch { }
        _proxy.Stop();
        return base.StopAsync(cancellationToken);
    }
}
