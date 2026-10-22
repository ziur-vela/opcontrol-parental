// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Diagnostics;
using System.Management;
using System.Text.Json;
using OpControl.Parental.Contracts;
using OpControl.Parental.Localization;

namespace OpControl.Parental.Client;

public sealed class AppBlockService : BackgroundService
{
    private readonly LockRuntime _runtime;
    private readonly DataSyncService _sync;
    private readonly ClientSettings _settings;
    private readonly ILogger<AppBlockService> _logger;
    private readonly HashSet<int> _handled = [];
    private ManagementEventWatcher? _watcher;
    private bool _fallbackPolling;
    private int _ticks;

    public AppBlockService(LockRuntime runtime, DataSyncService sync, ClientSettings settings, ILogger<AppBlockService> logger)
    {
        _runtime = runtime;
        _sync = sync;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows()) return;
        StartWatcher();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_fallbackPolling ? 1 : 5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _ticks++;
                if (_fallbackPolling || _watcher is null || _ticks % 3 == 0)
                    ScanRunningProcesses();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "App block scan failed");
            }

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        StopWatcher();
    }

    private void StartWatcher()
    {
        try
        {
            var query = new WqlEventQuery("SELECT * FROM __InstanceCreationEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_Process'");
            _watcher = new ManagementEventWatcher(query);
            _watcher.EventArrived += (_, args) => OnProcessStarted(args);
            _watcher.Start();
            _logger.LogInformation("App block WMI watcher started");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WMI process watcher could not start; falling back to 1 s polling");
            _watcher = null;
            _fallbackPolling = true;
        }
    }

    private void StopWatcher()
    {
        try { _watcher?.Stop(); } catch { }
        try { _watcher?.Dispose(); } catch { }
        _watcher = null;
    }

    private void OnProcessStarted(EventArrivedEventArgs args)
    {
        try
        {
            var target = (ManagementBaseObject)args.NewEvent["TargetInstance"];
            var pid = Convert.ToInt32(target["ProcessId"]);
            var path = target["ExecutablePath"] as string;
            var name = target["Name"] as string ?? "";
            _ = Task.Run(() => EvaluateAndBlockAsync(pid, path, name));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WMI process event handling failed");
        }
    }

    private void ScanRunningProcesses()
    {
        var state = _runtime.DeviceState;
        if (state is null) return;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.SessionId == 0) continue;
                if (AppInventoryService.IsProtected(process.ProcessName + ".exe")) continue;
                if (!string.Equals(process.ProcessName, "OpControl.Parental.Helper", StringComparison.OrdinalIgnoreCase))
                {
                    if (process.ProcessName.StartsWith("OpControl.Parental", StringComparison.OrdinalIgnoreCase)) continue;
                }
                var path = process.MainModule?.FileName;
                _ = Task.Run(() => EvaluateAndBlockAsync(process.Id, path, process.ProcessName + ".exe"));
            }
            catch { }
            finally { process.Dispose(); }
        }
    }

    private async Task EvaluateAndBlockAsync(int pid, string? path, string processName)
    {
        try
        {
            if (pid <= 0 || AppInventoryService.IsProtected(processName)) return;
            var state = _runtime.DeviceState;
            if (state is null) return;
            var monitored = WindowsSession.GetMonitoredSessionId(_settings.Value.MonitoredAccount);
            if (monitored <= 0) return;

            Process process;
            try { process = Process.GetProcessById(pid); }
            catch { return; }
            using (process)
            {
                if (process.SessionId != monitored) return;
                if (path is null)
                {
                    try { path = process.MainModule?.FileName; } catch { }
                }
                if (string.IsNullOrWhiteSpace(path)) return;
                var file = Path.GetFileName(path);
                if (AppInventoryService.IsProtected(file)) return;

                var identity = AppIdentity.ForProcess(process, path);
                if (identity.Length == 0) return;
                if (!_runtime.IsAppBlocked(identity, path, out var reason)) return;

                var name = string.IsNullOrWhiteSpace(file) ? processName : file;
                try
                {
                    process.Kill(entireProcessTree: true);
                    _logger.LogInformation("Blocked app {App} ({Identity}) in session {Session} reason={Reason}", name, identity, monitored, reason);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not end blocked app {App}", name);
                }

                _sync.RecordBlockEvent("app", identity, name);
                WriteNotification(reason == "daily-limit" ? Loc.T("Client.AppBlock.DailyLimit") : Loc.T("Client.AppBlock.Blocked"), name);
                _handled.Add(pid);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "App block evaluation failed");
        }
        await Task.CompletedTask;
    }

    private void WriteNotification(string title, string message)
    {
        try
        {
            Directory.CreateDirectory(ClientPaths.UserDirectory);
            var data = new
            {
                title,
                message,
                utc = DateTime.UtcNow
            };
            var temp = ClientPaths.NotificationFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data));
            File.Move(temp, ClientPaths.NotificationFile, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not write app-block notification");
        }
    }
}
