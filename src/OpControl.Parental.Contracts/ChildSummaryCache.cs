using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpControl.Parental.Contracts;

/// <summary>
/// The device service writes the summary cache into the shared user directory; the helper reads it.
/// Reading a missing or damaged file returns null so the window can fall back to what it has.
/// </summary>
public static class ChildSummaryCache
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static ChildSummaryDto? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<ChildSummaryDto>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void Write(string path, ChildSummaryDto summary)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(summary, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}
