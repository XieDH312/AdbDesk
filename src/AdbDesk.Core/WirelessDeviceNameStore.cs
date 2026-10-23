using System.Text.Json;

namespace AdbDesk.Core;

internal sealed record WirelessDeviceName(string Name, string Type, string DisplayName);
internal sealed record WirelessDeviceNames(IReadOnlyList<WirelessDeviceName> Entries, string Warning);

/// <summary>Names belong to an exact advertised identity, never to an address or port.</summary>
internal sealed class WirelessDeviceNameStore(string path)
{
    private const int MaximumEntries = 200;
    private const int MaximumBytes = 1024 * 1024;
    private readonly string _path = Path.GetFullPath(path);

    internal WirelessDeviceNames Load()
    {
        try
        {
            using var stream = File.OpenRead(_path);
            if (stream.Length > MaximumBytes) return new([], "无线设备名称缓存过大，已忽略。");
            // Read a bounded snapshot even if another process is replacing the file.
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) return new([], "无线设备名称缓存已变化，已忽略。");
            var candidates = JsonSerializer.Deserialize<WirelessDeviceName?[]>(bytes);
            if (candidates is null) return new([], "无线设备名称缓存格式无效，已忽略。");
            var entries = new List<WirelessDeviceName>();
            var seen = new HashSet<(string, string)>();
            var invalid = false;
            foreach (var entry in candidates)
            {
                if (entry is null || !ValidIdentity(entry.Name, entry.Type) || NormalizeDisplayName(entry.DisplayName).Length == 0)
                {
                    invalid = true;
                    continue;
                }
                if (entries.Count < MaximumEntries && seen.Add((entry.Name, entry.Type)))
                    entries.Add(entry with { DisplayName = NormalizeDisplayName(entry.DisplayName) });
            }
            return new(entries, invalid ? "无线设备名称缓存包含无效记录，已忽略这些记录。" : "");
        }
        catch (FileNotFoundException) { return new([], ""); }
        catch (DirectoryNotFoundException) { return new([], ""); }
        catch (JsonException) { return new([], "无线设备名称缓存损坏，已忽略。"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new([], "无法读取无线设备历史名称：" + exception.Message);
        }
    }

    internal string Remember(IReadOnlyList<WirelessServiceEntry> services, IReadOnlyList<WirelessDeviceName> previous)
    {
        var fresh = services.Where(entry => entry.IsConnected && !entry.IsHistoricalName &&
                NormalizeDisplayName(entry.Device!.DeviceName).Length + NormalizeDisplayName(entry.Device.Model).Length > 0)
            .Select(entry => new WirelessDeviceName(entry.Name, entry.Type, entry.DisplayName));
        var entries = fresh.Concat(previous).Where(entry => ValidIdentity(entry.Name, entry.Type))
            .DistinctBy(entry => (entry.Name, entry.Type)).Take(MaximumEntries).ToArray();
        if (entries.SequenceEqual(previous)) return "";
        string? temporaryPath = null;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(entries);
            if (bytes.Length > MaximumBytes) return "无线设备名称缓存超过大小限制，未保存。";
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, _path, overwrite: true);
            return "";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "无法保存无线设备历史名称：" + exception.Message;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static string NormalizeDisplayName(string? value)
    {
        var name = (value ?? "").Trim();
        return name.Length is 0 or > 256 || name.Any(char.IsControl) ||
            name.Equals("null", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("unknown", StringComparison.OrdinalIgnoreCase) || name is "?" or "-"
                ? "" : name;
    }

    private static bool ValidIdentity(string? name, string? type) =>
        name is { Length: > 0 and <= 255 } && !name.Any(char.IsWhiteSpace) && !name.Any(char.IsControl) &&
        !name.EndsWith('.') && type is MdnsServices.ConnectType or MdnsServices.LegacyType;
}