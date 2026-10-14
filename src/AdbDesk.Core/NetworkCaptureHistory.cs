using System.Text.Json;

namespace AdbDesk.Core;

public sealed record NetworkCaptureHistoryItem(string Directory, string Serial, DateTimeOffset StartedAt, string DisplayName);
public sealed record NetworkCapturePage(IReadOnlyList<NetworkCaptureEntry> Entries, int TotalCount, int PageIndex, int PageCount, string Warning);

/// <summary>Reads local capture files without starting an engine or accessing a device.</summary>
public static class NetworkCaptureHistory
{
    public const int MaximumBodyBytes = 4 * 1024 * 1024;
    public const int MaximumEntries = 5000;

    public static IReadOnlyList<NetworkCaptureHistoryItem> List(string root)
    {
        if (!Directory.Exists(root)) return [];
        EnsureNoReparse(root);
        var result = new List<NetworkCaptureHistoryItem>();
        foreach (var directory in Directory.EnumerateDirectories(root).Take(5000))
        {
            try
            {
                var session = ReadSession(directory);
                result.Add(new(directory, session.Serial, session.StartedAt, $"{session.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {session.Serial}"));
            }
            catch (Exception ex) when (IsReadError(ex)) { }
        }
        return result.OrderByDescending(s => s.StartedAt).ToArray();
    }

    public static NetworkCapturePage ReadPage(string directory, int pageIndex, int pageSize = 200)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        if (pageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(pageSize));
        _ = ReadSession(directory);
        var records = new List<(string Path, DateTimeOffset Time)>();
        var skipped = 0;
        var incomplete = 0;
        var paths = EntryPaths(directory).ToArray();
        foreach (var path in paths.Take(MaximumEntries))
        {
            var entry = ReadEntry(path);
            if (entry is null) { skipped++; continue; }
            records.Add((path, entry.Time));
            if (!entry.Finished || !entry.RequestTargetComplete || !entry.RequestHeadersComplete || entry.RequestCapture?.Complete != true ||
                (entry.StatusCode is not null && (!entry.ResponseHeadersComplete || entry.ResponseCapture?.Complete != true))) incomplete++;
        }
        var pageCount = Math.Max(1, (records.Count + pageSize - 1) / pageSize);
        pageIndex = Math.Min(pageIndex, pageCount - 1);
        var entries = records.OrderBy(r => r.Time).ThenBy(r => r.Path, StringComparer.Ordinal)
            .Skip(pageIndex * pageSize).Take(pageSize).Select(r => ReadEntry(r.Path)).OfType<NetworkCaptureEntry>().ToArray();
        var warnings = new List<string>();
        var storageWarning = ReadStorageWarning(directory);
        if (storageWarning.Length > 0) warnings.Add("捕获时的存储诊断：" + storageWarning);
        if (skipped > 0) warnings.Add($"已跳过 {skipped} 条损坏、不兼容或不安全的记录。");
        if (incomplete > 0) warnings.Add($"{incomplete} 条记录包含尚未完成、旧版或不完整的捕获数据。");
        if (paths.Length > MaximumEntries) warnings.Add("已达到 5000 条历史读取上限。");
        return new(entries, records.Count, pageIndex, pageCount, string.Join(" ", warnings));
    }

    /// <summary>Enumerates bounded metadata one entry at a time; never holds every preview in memory.</summary>
    public static IEnumerable<NetworkCaptureEntry> ReadEntries(string directory)
    {
        _ = ReadSession(directory);
        var count = 0;
        foreach (var path in EntryPaths(directory))
        {
            if (++count > MaximumEntries) throw new InvalidDataException("历史记录超过 5000 条上限，未导出完整会话。");
            var entry = ReadEntry(path) ?? throw new InvalidDataException("历史记录损坏、不兼容或不安全，已停止导出：" + Path.GetFileName(path));
            yield return entry;
        }
    }

    public static string GetBodyPath(string directory, CapturedBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!NetworkCaptureProtocol.ValidBody(body) || body.FileName is null || !NetworkCaptureProtocol.IsSafeFileName(body.FileName))
            throw new InvalidDataException("正文引用无效或超过 4 MiB 限制。");
        var root = Path.GetFullPath(directory);
        EnsureNoReparse(root);
        var path = Path.GetFullPath(Path.Combine(root, body.FileName));
        if (!string.Equals(Path.GetDirectoryName(path), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("正文路径不在当前会话内。");
        EnsureNoReparse(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != body.SavedBytes || info.Length > MaximumBodyBytes)
            throw new InvalidDataException("正文文件缺失或大小与记录不符。");
        return path;
    }

    public static byte[] ReadBody(string directory, CapturedBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!NetworkCaptureProtocol.ValidBody(body)) throw new InvalidDataException("正文引用无效。");
        if (body.FileName is null && body.SavedBytes == 0)
        {
            EnsureNoReparse(Path.GetFullPath(directory));
            return [];
        }
        var path = GetBodyPath(directory, body);
        // A live detail read must never deny the engine's next append operation.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        EnsureNoReparse(path);
        if (stream.Length != body.SavedBytes || stream.Length > MaximumBodyBytes) throw new InvalidDataException("正文文件在读取时发生变化。");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.Length != body.SavedBytes) throw new InvalidDataException("正文仍在接收，请在请求完成后重试。");
        EnsureNoReparse(path);
        return bytes;
    }

    private static IEnumerable<string> EntryPaths(string directory) => Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
        .Where(p => !string.Equals(Path.GetFileName(p), "session.json", StringComparison.OrdinalIgnoreCase)).Take(MaximumEntries + 1);

    private static NetworkCaptureEntry? ReadEntry(string path)
    {
        try
        {
            var entry = ReadJson<NetworkCaptureEntry>(path, NetworkCaptureProtocol.MaximumLineLength);
            return NetworkCaptureProtocol.IsValid(entry) ? entry : null;
        }
        catch (Exception ex) when (IsReadError(ex)) { return null; }
    }

    private static SessionInfo ReadSession(string directory)
    {
        var session = ReadJson<SessionInfo>(Path.Combine(directory, "session.json"), 16_384);
        if (session is null || string.IsNullOrWhiteSpace(session.Serial) || session.Serial.Length > 1024 || session.Serial.Any(char.IsControl) || session.StartedAt == default)
            throw new InvalidDataException("抓包会话信息无效。");
        return session;
    }

    private static T? ReadJson<T>(string path, int maximumBytes)
    {
        EnsureNoReparse(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maximumBytes) throw new InvalidDataException("抓包元数据超过大小限制。");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return JsonSerializer.Deserialize<T>(bytes);
    }

    private static string ReadStorageWarning(string directory)
    {
        var path = Path.Combine(directory, "storage-warning.txt");
        if (!File.Exists(path)) return "";
        try
        {
            EnsureNoReparse(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > 4096) return "存储诊断文件无效。";
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (IsReadError(ex)) { return "无法读取存储诊断文件。"; }
    }

    private static void EnsureNoReparse(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("不允许通过符号链接或重解析点读取抓包文件。");
    }
    private static bool IsReadError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;
    private sealed record SessionInfo(string Serial, DateTimeOffset StartedAt);
}
