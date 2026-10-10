using System.Text.Json;
using System.Text.Json.Serialization;

namespace DikTap.Services.Usage;

// A dictation sent to OpenAI. No text, audio or key: only figures and the last 4 characters of the key so it can be filtered.
internal sealed record UsageRecord
{
    public DateTimeOffset Time { get; init; }
    public string Model { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public double AudioSeconds { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public double CostUsd { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(UsageRecord))]
internal sealed partial class UsageJsonContext : JsonSerializerContext;

internal interface IUsageStore
{
    void Append(UsageRecord record);
    IReadOnlyList<UsageRecord> Load();
    // Deletes the records matching the predicate and returns how many.
    int Remove(Func<UsageRecord, bool> predicate);
}

// One JSON record per line: appending is cheap and nothing is kept in memory at idle.
internal sealed class JsonlUsageStore : IUsageStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public JsonlUsageStore(string path) => _path = path;

    public void Append(UsageRecord record)
    {
        string line = JsonSerializer.Serialize(record, UsageJsonContext.Default.UsageRecord);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, line + Environment.NewLine);
        }
    }

    public IReadOnlyList<UsageRecord> Load()
    {
        lock (_gate) return ReadAll();
    }

    public int Remove(Func<UsageRecord, bool> predicate)
    {
        lock (_gate)
        {
            var all = ReadAll();
            var keep = all.Where(r => !predicate(r)).ToList();
            int removed = all.Count - keep.Count;
            if (removed == 0) return 0;

            if (keep.Count == 0)
            {
                File.Delete(_path);
                return removed;
            }

            string tmp = _path + $".{Environment.ProcessId}.tmp";
            try
            {
                File.WriteAllLines(tmp, keep.Select(r => JsonSerializer.Serialize(r, UsageJsonContext.Default.UsageRecord)));
                File.Move(tmp, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            return removed;
        }
    }

    private List<UsageRecord> ReadAll()
    {
        var list = new List<UsageRecord>();
        if (!File.Exists(_path)) return list;
        foreach (string line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize(line, UsageJsonContext.Default.UsageRecord) is { } r) list.Add(r);
            }
            catch (JsonException)
            {
                // A damaged line (power cut while writing) does not invalidate the rest.
            }
        }
        return list;
    }
}
