// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Diagnostics;
using OpControl.Parental.Contracts;

namespace OpControl.Parental.Client;

public sealed class ClockMonitor : BackgroundService
{
    private readonly DataSyncService _sync;
    private readonly ILogger<ClockMonitor> _logger;
    private readonly Stopwatch _monotonic = Stopwatch.StartNew();
    private DateTime _wallBase = DateTime.UtcNow;
    private DateTime _lastReportUtc = DateTime.MinValue;

    public ClockMonitor(DataSyncService sync, ILogger<ClockMonitor> logger)
    {
        _sync = sync;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var expected = _wallBase.Add(_monotonic.Elapsed);
                var delta = DateTime.UtcNow - expected;
                if (Math.Abs(delta.TotalMinutes) > 5 && DateTime.UtcNow - _lastReportUtc > TimeSpan.FromHours(1))
                {
                    _lastReportUtc = DateTime.UtcNow;
                    _sync.QueueTamper(new TamperEventDto("clock-changed",
                        ((int)delta.TotalMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture), DateTime.UtcNow));
                    _logger.LogWarning("System clock jump detected: {Minutes} minutes", (int)delta.TotalMinutes);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Clock monitor failed");
            }
        }
    }
}

public sealed class UnaccountedTracker : BackgroundService
{
    private readonly ClientSettings _settings;
    private readonly DataSyncService _sync;
    private readonly ILogger<UnaccountedTracker> _logger;
    private DateTime? _downSince;
    private DateTime _lastFlushUtc = DateTime.UtcNow;

    public UnaccountedTracker(ClientSettings settings, DataSyncService sync, ILogger<UnaccountedTracker> logger)
    {
        _settings = settings;
        _sync = sync;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var account = _settings.Value.MonitoredAccount;
                var session = WindowsSession.GetMonitoredSessionId(account);
                var now = DateTime.UtcNow;
                if (session == 0)
                {
                    _downSince = null;
                    continue;
                }

                var running = WindowsSession.IsHelperRunning(session);
                if (running)
                {
                    if (_downSince is { } start && now - start > TimeSpan.FromSeconds(60))
                        Flush(start, now);
                    _downSince = null;
                    continue;
                }

                _downSince ??= now;
                if (now - _downSince > TimeSpan.FromSeconds(60) && now - _lastFlushUtc >= TimeSpan.FromMinutes(5))
                {
                    Flush(_downSince.Value, now);
                    _downSince = now;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unaccounted tracker failed");
            }
        }
    }

    private void Flush(DateTime start, DateTime end)
    {
        var seconds = (int)(end - start).TotalSeconds;
        if (seconds < 60) return;
        var bucket = new DateTime(start.Ticks - start.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        _sync.QueueUnaccounted(new UnaccountedIntervalDto(Guid.NewGuid(), bucket, Math.Min(seconds, 3600), "helper-down"));
        _lastFlushUtc = DateTime.UtcNow;
        _logger.LogInformation("Queued unaccounted helper-down time: {Seconds}s from {Start:u}", Math.Min(seconds, 3600), bucket);
    }
}
