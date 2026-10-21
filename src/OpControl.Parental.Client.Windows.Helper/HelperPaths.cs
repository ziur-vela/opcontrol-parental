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
    public static string NotificationFile => Path.Combine(UserDirectory, "notification.json");
    public static string UsageQueueFile => Path.Combine(UserDirectory, "usage-queue.jsonl");
    public static string WebsiteUsageQueueFile => Path.Combine(UserDirectory, "website-usage-queue.jsonl");
    public static string LastVisitFile => Path.Combine(PublicDirectory, "last-visit.json");
    public static string MessageDirectory => Path.Combine(UserDirectory, "messages");
    public static string MessageThreadFile => Path.Combine(MessageDirectory, "thread.json");
    public static string MessageOutboxFile => Path.Combine(MessageDirectory, "outbox.jsonl");
    public static string MessagePopupDirectory => Path.Combine(MessageDirectory, "popup");
    public static string SummaryFile => Path.Combine(UserDirectory, "child-summary.json");
    public static string OnlineFile => Path.Combine(PublicDirectory, "online.json");
    public static string UiCommandFile => Path.Combine(UserDirectory, "ui-command.json");
    public static string WindowBoundsFile => Path.Combine(UserDirectory, "my-opcontrol-window.json");

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
            using var stream = new FileStream(StateFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return JsonSerializer.Deserialize<RuntimeLockStateDto>(reader.ReadToEnd(), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static ChildSummaryDto? ReadSummary() => ChildSummaryCache.Read(SummaryFile);

    public static bool ReadOnline()
    {
        try
        {
            if (!File.Exists(OnlineFile)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(OnlineFile));
            var root = document.RootElement;
            if (!root.TryGetProperty("online", out var online) || !online.GetBoolean()) return false;
            if (!root.TryGetProperty("utc", out var utc) || !utc.TryGetDateTime(out var when)) return false;
            return DateTime.UtcNow - when.ToUniversalTime() < TimeSpan.FromMinutes(2);
        }
        catch
        {
            return false;
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
