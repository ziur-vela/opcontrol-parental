// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace OpControl.Parental.Client;

public sealed class TaskManagerPolicy
{
    private sealed class PreviousValue
    {
        public bool HadValue { get; set; }
        public int? Value { get; set; }
    }

    private readonly object _gate = new();
    private readonly ILogger<TaskManagerPolicy> _logger;
    private PreviousValue? _previous;
    private bool _loaded;

    public TaskManagerPolicy(ILogger<TaskManagerPolicy> logger) => _logger = logger;

    public void Sync(bool locked, string account)
    {
        lock (_gate)
        {
            Load();
            var sid = ResolveSid(account);
            if (sid is null) return;
            var path = $"{sid}\\Software\\Microsoft\\Windows\\CurrentVersion\\Policies\\System";

            try
            {
            if (locked)
            {
                using var key = Registry.Users.CreateSubKey(path, writable: true);
                if (key is null) return;
                if (_previous is null)
                {
                    var current = key.GetValue("DisableTaskMgr");
                    _previous = new PreviousValue
                    {
                        HadValue = current is not null,
                        Value = current is int value ? value : null
                    };
                    Save();
                }
                key.SetValue("DisableTaskMgr", 1, RegistryValueKind.DWord);
            }
            else if (_previous is not null)
            {
                using var key = Registry.Users.OpenSubKey(path, writable: true);
                if (key is null) return;
                if (_previous.HadValue && _previous.Value is { } value)
                    key.SetValue("DisableTaskMgr", value, RegistryValueKind.DWord);
                else
                    key.DeleteValue("DisableTaskMgr", throwOnMissingValue: false);
                _previous = null;
                Save();
                _logger.LogInformation("Task Manager policy restored for {Account}", account);
            }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                _logger.LogDebug(ex, "Task Manager policy not applied yet for {Account}", account);
            }
        }
    }

    public void Restore(string account) => Sync(false, account);

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(ClientPaths.PolicyStateFile))
                _previous = JsonSerializer.Deserialize<PreviousValue>(File.ReadAllText(ClientPaths.PolicyStateFile));
        }
        catch { _previous = null; }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(ClientPaths.PrivateDirectory);
            if (_previous is null)
                File.Delete(ClientPaths.PolicyStateFile);
            else
                File.WriteAllText(ClientPaths.PolicyStateFile, JsonSerializer.Serialize(_previous));
        }
        catch { }
    }

    private static string? ResolveSid(string account)
    {
        try
        {
            var name = account.Contains('\\') ? account : $"{Environment.MachineName}\\{account}";
            var sid = new NTAccount(name).Translate(typeof(SecurityIdentifier));
            return sid.Value;
        }
        catch
        {
            return null;
        }
    }
}
