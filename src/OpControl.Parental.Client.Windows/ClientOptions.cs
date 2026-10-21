using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpControl.Parental.Client;

public sealed class ClientOptions
{
    public string ServerUrl { get; set; } = "https://parental.opcontrol.net";
    public string? EnrollmentCode { get; set; }
    public string? DeviceName { get; set; }
    public string? OsAccountName { get; set; }
    public string MonitoredAccount { get; set; } = "";
    public string? HelperPath { get; set; }
    public int IdleThresholdSeconds { get; set; } = 180;

    public static ClientOptions Load()
    {
        var path = ClientPaths.ConfigFile;
        if (!File.Exists(path))
            return new ClientOptions();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ClientOptions>(json, JsonOptions) ?? new ClientOptions();
    }

    public void Save()
    {
        ClientPaths.EnsureDataDirectory();
        File.WriteAllText(ClientPaths.ConfigFile, JsonSerializer.Serialize(this, JsonOptions));
    }

    public void SaveWithoutCode()
    {
        var copy = new ClientOptions
        {
            ServerUrl = ServerUrl,
            DeviceName = DeviceName,
            OsAccountName = OsAccountName,
            MonitoredAccount = MonitoredAccount,
            HelperPath = HelperPath,
            IdleThresholdSeconds = IdleThresholdSeconds
        };
        ClientPaths.EnsureDataDirectory();
        File.WriteAllText(ClientPaths.ConfigFile, JsonSerializer.Serialize(copy, JsonOptions));
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

public static class ClientPaths
{
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpControlParental");

    public static string ConfigFile => Path.Combine(DataDirectory, "config.json");
    public static string PrivateDirectory => Path.Combine(DataDirectory, "private");
    public static string PublicDirectory => Path.Combine(DataDirectory, "public");
    public static string UserDirectory => Path.Combine(DataDirectory, "user");
    public static string CredentialFile => Path.Combine(PrivateDirectory, "device.json");
    public static string DeviceStateFile => Path.Combine(PrivateDirectory, "device-state.json");
    public static string LockoutFile => Path.Combine(PrivateDirectory, "lockout.json");
    public static string OverrideQueueFile => Path.Combine(PrivateDirectory, "override-queue.jsonl");
    public static string PolicyStateFile => Path.Combine(PrivateDirectory, "taskmgr-policy.json");
    public static string DnsStateFile => Path.Combine(PrivateDirectory, "dns-state.json");
    public static string VisitQueueFile => Path.Combine(PrivateDirectory, "visit-queue.jsonl");
    public static string BlockEventQueueFile => Path.Combine(PrivateDirectory, "block-event-queue.jsonl");
    public static string UsageQueueFile => Path.Combine(UserDirectory, "usage-queue.jsonl");
    public static string WebsiteUsageQueueFile => Path.Combine(UserDirectory, "website-usage-queue.jsonl");
    public static string LastVisitFile => Path.Combine(PublicDirectory, "last-visit.json");
    public static string TamperQueueFile => Path.Combine(PrivateDirectory, "tamper-queue.jsonl");
    public static string UnaccountedQueueFile => Path.Combine(PrivateDirectory, "unaccounted-queue.jsonl");
    public static string CleanShutdownFile => Path.Combine(PrivateDirectory, "clean-shutdown.json");
    public static string TamperBaselineFile => Path.Combine(PrivateDirectory, "tamper-baseline.json");
    public static string UpdateDirectory => Path.Combine(DataDirectory, "updates");
    public static string UpdatePendingFile => Path.Combine(UpdateDirectory, "pending-update.json");
    public static string UpdateInProgressFile => Path.Combine(UpdateDirectory, "update-in-progress.flag");
    public static string UpdateHealthyFile => Path.Combine(UpdateDirectory, "update-healthy.flag");
    public static string UpdateLogFile => Path.Combine(UpdateDirectory, "update.log");
    public static string UpdateCompletedFile => Path.Combine(UpdateDirectory, "update-completed.json");
    public static string ForceRollbackFile => Path.Combine(UpdateDirectory, "force-rollback.flag");
    public static string UninstallingFile => Path.Combine(DataDirectory, "uninstall-in-progress.flag");
    public static string InventorySnapshotFile => Path.Combine(PrivateDirectory, "app-inventory.json");
    public static string InventoryHashFile => Path.Combine(PrivateDirectory, "app-inventory.hash");
    public static string CategoryCacheFile => Path.Combine(PrivateDirectory, "categories.json");
    public static string NotificationFile => Path.Combine(UserDirectory, "notification.json");
    public static string StateFile => Path.Combine(PublicDirectory, "state.json");
    public static string LogFile => Path.Combine(PublicDirectory, "service.log");
    public static string MessageDirectory => Path.Combine(UserDirectory, "messages");
    public static string MessageThreadFile => Path.Combine(MessageDirectory, "thread.json");
    public static string MessageOutboxFile => Path.Combine(MessageDirectory, "outbox.jsonl");
    public static string MessageSendTimesFile => Path.Combine(MessageDirectory, "send-times.json");
    public static string MessageQueuedFile => Path.Combine(MessageDirectory, "queued-popups.json");
    public static string MessagePopupDirectory => Path.Combine(MessageDirectory, "popup");
    public static string HelperStatusFile => Path.Combine(UserDirectory, "helper-status.json");
    public static string HelperEventsFile => Path.Combine(UserDirectory, "helper-events.log");
    public static string SummaryFile => Path.Combine(UserDirectory, "child-summary.json");
    public static string OnlineFile => Path.Combine(PublicDirectory, "online.json");
    public static string UiCommandFile => Path.Combine(UserDirectory, "ui-command.json");

    public static void EnsureDataDirectory()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(PrivateDirectory);
        Directory.CreateDirectory(PublicDirectory);
        Directory.CreateDirectory(UserDirectory);
        Directory.CreateDirectory(UpdateDirectory);
    }

    public static void EnsureDirectoriesWithAcl()
    {
        EnsureDataDirectory();
        SecureFile.SystemOnlyDirectory(PrivateDirectory);
        SecureFile.UsersReadOnlyDirectory(PublicDirectory);
        SecureFile.UsersWritableDirectory(UserDirectory);
    }
}

public sealed class DeviceCredential
{
    public Guid DeviceId { get; set; }
    public string Credential { get; set; } = "";
    public DateTime ExpiresUtc { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public bool IsUsable => DeviceId != Guid.Empty && !string.IsNullOrWhiteSpace(Credential) && ExpiresUtc > DateTime.UtcNow.AddDays(7);
}

public sealed class CredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(ClientOptions.JsonOptions);
    private readonly object _gate = new();

    public DeviceCredential? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(ClientPaths.CredentialFile))
                return null;
            try { return JsonSerializer.Deserialize<DeviceCredential>(File.ReadAllText(ClientPaths.CredentialFile), JsonOptions); }
            catch { return null; }
        }
    }

    public void Save(DeviceCredential credential)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(ClientPaths.PrivateDirectory);
            SecureFile.SystemOnlyDirectory(ClientPaths.PrivateDirectory);
            var temp = ClientPaths.CredentialFile + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                SecureFile.SystemOnly(temp);
                JsonSerializer.Serialize(stream, credential, JsonOptions);
            }
            File.Move(temp, ClientPaths.CredentialFile, overwrite: true);
            SecureFile.SystemOnly(ClientPaths.CredentialFile);
        }
    }

    public void Delete()
    {
        lock (_gate)
        {
            if (File.Exists(ClientPaths.CredentialFile)) File.Delete(ClientPaths.CredentialFile);
        }
    }
}

public sealed class DeviceStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(ClientOptions.JsonOptions);
    private readonly object _gate = new();

    public Contracts.DeviceStateDto? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(ClientPaths.DeviceStateFile)) return null;
            try
            {
                var state = JsonSerializer.Deserialize<Contracts.DeviceStateDto>(File.ReadAllText(ClientPaths.DeviceStateFile), JsonOptions);
                if (state is null) return null;
                return state with
                {
                    Schedules = state.Schedules ?? [],
                    WebsiteRules = state.WebsiteRules ?? [],
                    AppRules = state.AppRules ?? [],
                    ItemBonuses = state.ItemBonuses ?? [],
                    Categories = state.Categories ?? []
                };
            }
            catch { return null; }
        }
    }

    public void Save(Contracts.DeviceStateDto state)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(ClientPaths.PrivateDirectory);
            SecureFile.SystemOnlyDirectory(ClientPaths.PrivateDirectory);
            var temp = ClientPaths.DeviceStateFile + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                SecureFile.SystemOnly(temp);
                JsonSerializer.Serialize(stream, state, JsonOptions);
            }
            File.Move(temp, ClientPaths.DeviceStateFile, overwrite: true);
            SecureFile.SystemOnly(ClientPaths.DeviceStateFile);
        }
    }
}

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(ClientOptions.JsonOptions);
    private readonly object _gate = new();

    public void Save(Contracts.RuntimeLockStateDto state)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(ClientPaths.PublicDirectory);
            var json = JsonSerializer.Serialize(state, JsonOptions);
            var temp = ClientPaths.StateFile + ".tmp";
            File.WriteAllText(temp, json);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    File.Move(temp, ClientPaths.StateFile, overwrite: true);
                    SecureFile.UsersReadable(ClientPaths.StateFile);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(25);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(25);
                }
            }

            using var stream = new FileStream(ClientPaths.StateFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            using var writer = new StreamWriter(stream);
            writer.Write(json);
            SecureFile.UsersReadable(ClientPaths.StateFile);
        }
    }
}

public static class SecureFile
{
    public static void SystemOnly(string path) =>
        RunIcacls($"\"{path}\" /inheritance:r /grant:r \"SYSTEM:F\" /remove:g \"BUILTIN\\Administrators\"");

    public static void SystemOnlyDirectory(string path) =>
        RunIcacls($"\"{path}\" /inheritance:r /grant:r \"SYSTEM:(OI)(CI)F\" \"Administrators:(OI)(CI)F\"");

    public static void UsersReadOnlyDirectory(string path) =>
        RunIcacls($"\"{path}\" /inheritance:r /grant:r \"SYSTEM:(OI)(CI)F\" \"Administrators:(OI)(CI)F\" \"Users:(OI)(CI)RX\"");

    public static void UsersWritableDirectory(string path) =>
        RunIcacls($"\"{path}\" /inheritance:r /grant:r \"SYSTEM:(OI)(CI)F\" \"Administrators:(OI)(CI)F\" \"Users:(OI)(CI)M\"");

    public static void UsersReadable(string path) =>
        RunIcacls($"\"{path}\" /inheritance:r /grant:r \"SYSTEM:F\" \"Administrators:F\" \"Users:R\"");

    private static void RunIcacls(string arguments)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "icacls.exe",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit(10000);
        }
        catch { }
    }
}
