using System.Text.Json;

namespace AdbDesk.Core;

public enum MirrorScaleMode { Complete, Fill, FillHeight, FillWidth, Custom }

// Edges are normalized screenshot coordinates, not margins in natural device orientation.
public sealed record MirrorCropRegion(double Left = 0, double Top = 0, double Right = 1, double Bottom = 1)
{
    public static MirrorCropRegion Full { get; } = new();
    public void Validate()
    {
        if (!double.IsFinite(Left) || !double.IsFinite(Top) || !double.IsFinite(Right) || !double.IsFinite(Bottom) ||
            Left < 0 || Top < 0 || Right > 1 || Bottom > 1 || Right - Left < .02 - 1e-10 || Bottom - Top < .02 - 1e-10)
            throw new ArgumentOutOfRangeException(nameof(MirrorCropRegion), "裁剪区域须在截图内，宽高至少保留 2%。");
    }
}

public sealed record MirrorDisplaySettings(MirrorScaleMode Mode = MirrorScaleMode.Complete, bool FullScreen = false,
    MirrorCropRegion? Region = null, MirrorDisplaySize? SourceSize = null)
{
    public MirrorCropRegion EffectiveRegion => Region ?? MirrorCropRegion.Full;
    public void Validate()
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
        EffectiveRegion.Validate();
        if (SourceSize is { } size && (size.Width is < 2 or > 32768 || size.Height is < 2 or > 32768))
            throw new ArgumentOutOfRangeException(nameof(SourceSize));
        if (EffectiveRegion != MirrorCropRegion.Full && SourceSize is null)
            throw new ArgumentException("自定义裁剪缺少截图尺寸，请重新选择区域。");
    }
}

public sealed class MirrorDisplaySettingsStore(string path)
{
    private readonly string _path = Path.GetFullPath(path);
    public Dictionary<string, MirrorDisplaySettings> Load()
    {
        var result = new Dictionary<string, MirrorDisplaySettings>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 256 * 1024) return result;
            var entries = JsonSerializer.Deserialize<Dictionary<string, MirrorDisplaySettings?>>(File.ReadAllText(_path));
            foreach (var (key, value) in entries ?? [])
            {
                if (string.IsNullOrWhiteSpace(key) || key.Length > 512 || value is null) continue;
                try { value.Validate(); }
                catch (ArgumentException) { continue; }
                result[key] = value;
                if (result.Count == 100) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return result;
    }

    public void Save(string deviceKey, MirrorDisplaySettings settings)
    {
        if (string.IsNullOrWhiteSpace(deviceKey) || deviceKey.Length > 512) throw new ArgumentException("设备标识无效。");
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var entries = Load();
        entries.Remove(deviceKey);
        if (entries.Count >= 100) entries.Remove(entries.Keys.First());
        entries[deviceKey] = settings;
        Write(entries);
    }

    public void Remove(string deviceKey)
    {
        var entries = Load();
        if (entries.Remove(deviceKey)) Write(entries);
    }

    private void Write(Dictionary<string, MirrorDisplaySettings> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
            File.Move(temporary, _path, true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
