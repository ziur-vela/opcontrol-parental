using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpControl.Parental.Client;

public sealed class ClientOptions
{
    public string ServerUrl { get; set; } = "https://parental.opcontrol.net";
    public string? EnrollmentCode { get; set; }
    public string? DeviceName { get; set; }
    public string? OsAccountName { get; set; }

    public static ClientOptions Load()
    {
        var path = ClientPaths.ConfigFile;
        if (!File.Exists(path))
            return new ClientOptions();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ClientOptions>(json, JsonOptions) ?? new ClientOptions();
    }

    public void SaveWithoutCode()
    {
        var copy = new ClientOptions
        {
            ServerUrl = ServerUrl,
            DeviceName = DeviceName,
            OsAccountName = OsAccountName
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
    public static string CredentialFile => Path.Combine(DataDirectory, "device.json");
    public static string StateFile => Path.Combine(DataDirectory, "state.json");
    public static string LogFile => Path.Combine(DataDirectory, "service.log");

    public static void EnsureDataDirectory() => Directory.CreateDirectory(DataDirectory);
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
            try
            {
                return JsonSerializer.Deserialize<DeviceCredential>(File.ReadAllText(ClientPaths.CredentialFile), JsonOptions);
            }
            catch
            {
                return null;
            }
        }
    }

    public void Save(DeviceCredential credential)
    {
        lock (_gate)
        {
            ClientPaths.EnsureDataDirectory();
            var temp = ClientPaths.CredentialFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(credential, JsonOptions));
            File.Move(temp, ClientPaths.CredentialFile, overwrite: true);
            SecureFile.SystemOnly(ClientPaths.CredentialFile);
        }
    }

    public void Delete()
    {
        lock (_gate)
        {
            if (File.Exists(ClientPaths.CredentialFile))
                File.Delete(ClientPaths.CredentialFile);
        }
    }
}

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(ClientOptions.JsonOptions);
    private readonly object _gate = new();

    public void Save(Contracts.LockStateDto state)
    {
        lock (_gate)
        {
            ClientPaths.EnsureDataDirectory();
            var temp = ClientPaths.StateFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temp, ClientPaths.StateFile, overwrite: true);
            SecureFile.UsersReadable(ClientPaths.StateFile);
        }
    }

    public Contracts.LockStateDto? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(ClientPaths.StateFile))
                return null;
            try
            {
                return JsonSerializer.Deserialize<Contracts.LockStateDto>(File.ReadAllText(ClientPaths.StateFile), JsonOptions);
            }
            catch
            {
                return null;
            }
        }
    }
}

public static class SecureFile
{
    public static void SystemOnly(string path) =>
        RunIcacls($"\"{path}\" /inheritance:r /grant:r \"SYSTEM:F\" \"Administrators:F\"");

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
        catch
        {
        }
    }
}
