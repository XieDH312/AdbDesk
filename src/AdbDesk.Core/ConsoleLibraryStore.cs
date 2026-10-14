using System.Text.Json;

namespace AdbDesk.Core;

public sealed record ConsoleLibraryState(IReadOnlyList<string> Favorites, IReadOnlyList<string> Recent);

/// <summary>Persist stable preset IDs only; never store entered command arguments.</summary>
public sealed class ConsoleLibraryStore(string path)
{
    private readonly string _path = Path.GetFullPath(path);
    private readonly object _gate = new();
    private static readonly HashSet<string> KnownIds = ConsolePresetCatalog.All.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

    public ConsoleLibraryState Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return new([], []);
            if (new FileInfo(_path).Length > 64 * 1024) throw new IOException("命令库偏好文件过大，请移除损坏文件后重试。");
            ConsoleLibraryState raw;
            try { raw = JsonSerializer.Deserialize<ConsoleLibraryState>(File.ReadAllText(_path)) ?? new([], []); }
            catch (JsonException ex) { throw new IOException("命令库偏好文件损坏。", ex); }
            var normalized = new ConsoleLibraryState(Normalize(raw.Favorites, 200), Normalize(raw.Recent, 12));
            if (raw.Favorites is null || raw.Recent is null || !raw.Favorites.SequenceEqual(normalized.Favorites) || !raw.Recent.SequenceEqual(normalized.Recent))
                Save(normalized);
            return normalized;
        }
    }

    public ConsoleLibraryState ToggleFavorite(string id)
    {
        ValidateId(id);
        lock (_gate)
        {
            var state = Load();
            var next = state with { Favorites = state.Favorites.Contains(id)
                ? state.Favorites.Where(value => value != id).ToArray() : Normalize(state.Favorites.Append(id), 200) };
            Save(next);
            return next;
        }
    }

    public ConsoleLibraryState Remember(string id)
    {
        ValidateId(id);
        lock (_gate)
        {
            var previous = Load();
            var state = previous with { Recent = Normalize(new[] { id }.Concat(previous.Recent), 12) };
            Save(state);
            return state;
        }
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string>? ids, int limit) => (ids ?? [])
        .Where(id => id is not null && KnownIds.Contains(id)).Distinct(StringComparer.Ordinal).Take(limit).ToArray();
    private static void ValidateId(string id)
    {
        if (!KnownIds.Contains(id)) throw new ArgumentException("未知命令预设。", nameof(id));
    }
    private void Save(ConsoleLibraryState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
