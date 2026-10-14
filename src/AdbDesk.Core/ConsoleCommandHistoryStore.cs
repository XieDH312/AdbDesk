using System.Text.Json;

namespace AdbDesk.Core;

public sealed class ConsoleCommandHistoryStore(string path)
{
    private readonly string _path = Path.GetFullPath(path);
    private readonly object _gate = new();
    private const int MaximumEntries = 30;

    public IReadOnlyList<string> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return [];
            if (new FileInfo(_path).Length > 512 * 1024) { Save([]); return []; }
            string?[] raw;
            try { raw = JsonSerializer.Deserialize<string?[]>(File.ReadAllText(_path)) ?? []; }
            catch (JsonException) { Save([]); return []; }
            var normalized = Normalize(raw);
            // Remove unsafe legacy entries from disk, not only the visible list.
            if (!raw.SequenceEqual(normalized)) Save(normalized);
            return normalized;
        }
    }

    public IReadOnlyList<string> Remember(string command)
    {
        var normalized = NormalizeOne(command) ?? throw new ArgumentException("该命令可能包含敏感数据，不能加入历史记录。", nameof(command));
        lock (_gate)
        {
            var entries = Normalize(new[] { normalized }.Concat(Load()));
            Save(entries);
            return entries;
        }
    }

    public IReadOnlyList<string> Remove(string command)
    {
        var normalized = NormalizeOne(command) ?? throw new ArgumentException("无效的历史命令。", nameof(command));
        lock (_gate)
        {
            var entries = Load().Where(value => value != normalized).ToArray();
            Save(entries);
            return entries;
        }
    }

    public void Clear() { lock (_gate) Save([]); }

    private static string? NormalizeOne(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            // A synthetic selection validates device commands without preserving
            // any previous target. Explicit serial arguments are rejected.
            var command = ConsoleCommand.Parse(value, "<history-device>");
            return command.CanRemember ? command.HistoryText : null;
        }
        catch (ArgumentException) { return null; }
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string?> values) => values
        .Select(NormalizeOne).Where(value => value is not null).Cast<string>()
        .Distinct(StringComparer.Ordinal).Take(MaximumEntries).ToArray();

    private void Save(IReadOnlyList<string> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
