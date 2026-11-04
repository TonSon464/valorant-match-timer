using System.Text.Json;
using System.Text.Json.Serialization;

namespace ValorantMatchTimer.Core;

public sealed class HistoryEntry
{
    [JsonPropertyName("seconds")]
    public double Seconds { get; set; }

    [JsonPropertyName("map")]
    public string? Map { get; set; }

    [JsonPropertyName("broad")]
    public string? Broad { get; set; }

    [JsonPropertyName("at")]
    public string? At { get; set; }
}

public sealed class HistoryStore
{
    private const int Limit = 40;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, List<HistoryEntry>> _data;

    public HistoryStore()
    {
        _data = Load();
    }

    private static Dictionary<string, List<HistoryEntry>> Load()
    {
        try
        {
            if (!File.Exists(AppPaths.HistoryFile))
            {
                return new Dictionary<string, List<HistoryEntry>>(StringComparer.Ordinal);
            }

            var json = File.ReadAllText(AppPaths.HistoryFile);
            var data = JsonSerializer.Deserialize<Dictionary<string, List<HistoryEntry>>>(json, JsonOptions);
            return data is null
                ? new Dictionary<string, List<HistoryEntry>>(StringComparer.Ordinal)
                : new Dictionary<string, List<HistoryEntry>>(data, StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, List<HistoryEntry>>(StringComparer.Ordinal);
        }
    }

    public void Add(string key, double seconds, string? map, string? broad)
    {
        if (key == "custom" || seconds < 120 || seconds > 3 * 3600)
        {
            return;
        }

        if (!_data.TryGetValue(key, out var entries))
        {
            entries = new List<HistoryEntry>();
            _data[key] = entries;
        }

        entries.Add(new HistoryEntry
        {
            Seconds = Math.Round(seconds, 1),
            Map = map,
            Broad = broad,
            At = DateTimeOffset.Now.ToString("o")
        });

        if (entries.Count > Limit)
        {
            entries.RemoveRange(0, entries.Count - Limit);
        }

        Save();
    }

    public (double Average, int Count) Stats(string key)
    {
        if (!_data.TryGetValue(key, out var entries))
        {
            return (0, 0);
        }

        var values = entries
            .Where(e => e.Seconds > 0)
            .Select(e => e.Seconds)
            .TakeLast(20)
            .ToArray();

        return values.Length == 0 ? (0, 0) : (values.Average(), values.Length);
    }

    public (int Predicted, int Count, double Average) Predicted(string key, int defaultSeconds)
    {
        var (average, count) = Stats(key);
        if (count == 0)
        {
            return (defaultSeconds, 0, defaultSeconds);
        }
        if (count == 1)
        {
            return ((int)Math.Round((average + defaultSeconds) / 2), 1, average);
        }
        return ((int)Math.Round(average), count, average);
    }

    public void Clear()
    {
        _data.Clear();
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AppDir);
            var json = JsonSerializer.Serialize(_data, JsonOptions);
            File.WriteAllText(AppPaths.HistoryFile, json);
        }
        catch
        {
            // History is a convenience; ignore write failures.
        }
    }
}
