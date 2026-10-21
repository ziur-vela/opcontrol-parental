using System.Text.Json;

namespace OpControl.Parental.Contracts;

public sealed record MessageOutboxEntry(string Text, DateTime CreatedUtc);

/// <summary>Local pending-message queue shared by the service and the helper.</summary>
public static class MessageOutbox
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static List<MessageOutboxEntry> Read(string path)
    {
        var result = new List<MessageOutboxEntry>();
        try
        {
            if (!File.Exists(path)) return result;
            foreach (var line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<MessageOutboxEntry>(line, Options);
                    if (entry is not null && !string.IsNullOrWhiteSpace(entry.Text)) result.Add(entry);
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    public static void Append(string path, MessageOutboxEntry entry)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.AppendAllText(path, JsonSerializer.Serialize(entry, Options) + Environment.NewLine);
        }
        catch { }
    }

    public static void Rewrite(string path, IReadOnlyList<MessageOutboxEntry> entries)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temp = path + ".tmp";
            var content = string.Join(Environment.NewLine, entries.Select(e => JsonSerializer.Serialize(e, Options)));
            if (content.Length > 0) content += Environment.NewLine;
            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        catch { }
    }
}
