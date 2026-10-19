using System.Text.Json;
using OpControl.Parental.Contracts;

namespace OpControl.Parental.Helper;

public static class HelperPaths
{
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpControlParental");
    public static string PublicDirectory => Path.Combine(DataDirectory, "public");
    public static string UserDirectory => Path.Combine(DataDirectory, "user");
    public static string StateFile => Path.Combine(PublicDirectory, "state.json");
    public static string StatusFile => Path.Combine(UserDirectory, "helper-status.json");
    public static string EventsFile => Path.Combine(UserDirectory, "helper-events.log");

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public static RuntimeLockStateDto? ReadState()
    {
        try
        {
            if (!File.Exists(StateFile)) return null;
            return JsonSerializer.Deserialize<RuntimeLockStateDto>(File.ReadAllText(StateFile), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void WriteStatus(bool locked, bool visible, string? reason, string? banner = null)
    {
        try
        {
            Directory.CreateDirectory(UserDirectory);
            var data = new
            {
                sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId,
                user = Environment.UserName,
                locked,
                visible,
                reason,
                banner,
                updatedUtc = DateTime.UtcNow
            };
            var temp = StatusFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data));
            File.Move(temp, StatusFile, overwrite: true);
        }
        catch { }
    }

    public static void LogEvent(string message)
    {
        try
        {
            Directory.CreateDirectory(UserDirectory);
            File.AppendAllText(EventsFile, $"{DateTime.UtcNow:o} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
