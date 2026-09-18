using System.Text.Json;

namespace AdbDesk.Core;

public sealed class ConnectionHistoryStore(string path)
{
    private readonly string _path = Path.GetFullPath(path);
    private const int MaximumEntries = 10;

    public IReadOnlyList<string> Load()
    {
        if (!File.Exists(_path)) return [];
        // Only a small address list is expected; discard damaged or oversized settings.
        if (new FileInfo(_path).Length > 64 * 1024) return [];
        try
        {
            return Normalize(JsonSerializer.Deserialize<string?[]>(File.ReadAllText(_path)) ?? []);
        }
        catch (JsonException) { return []; }
    }

    public IReadOnlyList<string> Remember(string endpoint)
    {
        if (!MdnsServices.TryNormalizeEndpoint(endpoint, out var normalized))
            throw new ArgumentException("连接历史只接受有效的 IPv4 地址和端口。", nameof(endpoint));
        var entries = Normalize(new[] { normalized }.Concat(Load()));
        Save(entries);
        return entries;
    }

    public void Clear() => Save([]);

    public IReadOnlyList<string> Remove(string endpoint)
    {
        if (!MdnsServices.TryNormalizeEndpoint(endpoint, out var normalized))
            throw new ArgumentException("删除历史需要有效的 IPv4 地址和端口。", nameof(endpoint));
        var entries = Load().Where(entry => entry != normalized).ToArray();
        Save(entries);
        return entries;
    }

    private void Save(IReadOnlyList<string> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string?> candidates)
    {
        var entries = new List<string>();
        foreach (var candidate in candidates)
        {
            if (candidate is null || !MdnsServices.TryNormalizeEndpoint(candidate, out var endpoint) || entries.Contains(endpoint)) continue;
            entries.Add(endpoint);
            if (entries.Count == MaximumEntries) break;
        }
        return entries;
    }
}
