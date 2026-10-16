using System.IO;
using System.Text.Json;

namespace AdbDesk.App;

internal sealed record MirrorPreferences(string Quality = "1920,60", bool Audio = false,
    string DisplayMode = "complete", bool FullScreen = false)
{
    private static string SettingsPath => Path.Combine(UpdatePreferences.DataDirectory, "mirror-preferences.json");

    internal static MirrorPreferences Load()
    {
        try
        {
            if (!File.Exists(SettingsPath) || new FileInfo(SettingsPath).Length > 16384) return new();
            return JsonSerializer.Deserialize<MirrorPreferences>(File.ReadAllText(SettingsPath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal void Save()
    {
        Directory.CreateDirectory(UpdatePreferences.DataDirectory);
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, SettingsPath, true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
