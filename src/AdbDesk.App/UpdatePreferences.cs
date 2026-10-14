using System.IO;
using System.Reflection;
using System.Text.Json;

namespace AdbDesk.App;

internal sealed record UpdatePreferences(bool AutoCheck = true, bool IncludePrereleases = true)
{
    internal static string CurrentVersion => typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0-beta.6";
    internal static long CurrentVersionCode => long.TryParse(typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .SingleOrDefault(attribute => attribute.Key == "VersionCode")?.Value, out var code) && code > 0
        ? code : throw new InvalidOperationException("程序缺少有效的 VersionCode，请重新构建。");
    internal static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdbDesk");
    private static string SettingsPath => Path.Combine(DataDirectory, "update-settings.json");

    internal static UpdatePreferences Load()
    {
        try { return JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, SettingsPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
