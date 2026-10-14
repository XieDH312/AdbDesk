using System.Text.Json;
using AdbDesk.Core;

internal static class ConsoleLibraryTests
{
    public static Task CatalogAsync()
    {
        var all = ConsolePresetCatalog.All;
        Check(all.Count >= 100 && all.Select(p => p.Category).Distinct().Count() == 10, "expanded useful categorized catalog");
        Check(all.Select(p => p.Id).Distinct().Count() == all.Count, "stable unique IDs");
        foreach (var preset in all)
        {
            var values = preset.Parameters.Select(p => p.DefaultValue.Length > 0 ? p.DefaultValue : Sample(p.Kind)).ToArray();
            var built = preset.BuildCommand(values);
            var parsed = ConsoleCommand.Parse(built, preset.Global ? null : "selected-phone");
            Check(parsed.IsGlobal == preset.Global, "catalog and parser scope agree: " + preset.Id);
            Check(preset.Global || parsed.Arguments.Take(2).SequenceEqual(["-s", "selected-phone"]), "explicit target: " + preset.Id);
        }
        Check(all.Single(p => p.Id == "app.clear").Confirmation.Length > 0, "data loss confirmation");
        Check(all.Single(p => p.Id == "adb.root").Requirements.Contains("userdebug"), "root is labelled");
        Check(all.Single(p => p.Id == "file.private").Requirements.Contains("调试"), "run-as is labelled");
        Check(all.Where(p => p.Matches("前台 Activity")).Any(), "multiword descriptive search");
        foreach (var preset in all.Where(p => p.Confirmation.Length > 0))
        {
            var values = preset.Parameters.Select(p => p.DefaultValue.Length > 0 ? p.DefaultValue : Sample(p.Kind)).ToArray();
            var parsed = ConsoleCommand.Parse(preset.BuildCommand(values), preset.Global ? null : "chosen-phone");
            Check(ConsoleCommandConfirmation.Describe(parsed) is not null, "editing a shipped mutation keeps confirmation: " + preset.Id);
        }
        return Task.CompletedTask;
    }

    public static Task ParametersAsync()
    {
        var text = ConsolePresetCatalog.All.Single(p => p.Id == "input.text");
        var built = text.BuildCommand(["{1} ' ; $(id) %s"]);
        Check(built == "shell input text '{1} '\"'\"' ; $(id) %s'", "single-pass Android argument quoting, no template reinjection");
        var install = ConsolePresetCatalog.All.Single(p => p.Id == "app.install");
        var path = "C:\\test folder\\it's {1} $a.apk";
        Check(ConsoleCommand.Parse(install.BuildCommand([path]), "chosen").Arguments[^1] == path, "host argument round trip");
        foreach (var value in new[] { "", "0", "65536", "5555;id", "５５５５", "-1" })
            Throws(() => ConsolePresetCatalog.All.Single(p => p.Id == "network.forward").BuildCommand([value, "8080"]));
        Throws(() => ConsolePresetCatalog.All.Single(p => p.Id == "app.clear").BuildCommand(["com.example;id"]));
        Throws(() => text.BuildCommand(["a\nb"]));
        Throws(() => text.BuildCommand([]));
        Throws(() => ConsolePresetCatalog.All.Single(p => p.Id == "file.delete").BuildCommand(["-rf"]));
        Throws(() => ConsolePresetCatalog.All.Single(p => p.Id == "screen.size.set").BuildCommand(["1080：1920"]));
        Check(ConsoleCommandConfirmation.Describe(ConsoleCommand.Parse("shell pm clear 'com.example.app'", "phone")) is not null, "custom/history clear still confirms");
        Check(ConsoleCommandConfirmation.Describe(ConsoleCommand.Parse("uninstall com.example.app", "phone")) is not null, "custom uninstall confirms");
        Check(ConsoleCommandConfirmation.Describe(ConsoleCommand.Parse("shell getprop", "phone")) is null, "read-only query has no confirmation");
        return Task.CompletedTask;
    }

    public static Task PreferencesAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AdbDesk-library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "library.json");
            var store = new ConsoleLibraryStore(path);
            Check(store.Load().Favorites.Count == 0, "new empty state");
            store.ToggleFavorite("app.clear");
            store.Remember("app.clear");
            store.Remember("device.model");
            store.Remember("app.clear");
            var loaded = new ConsoleLibraryStore(path).Load();
            Check(loaded.Favorites.SequenceEqual(["app.clear"]), "favorite survives restart");
            Check(loaded.Recent.SequenceEqual(["app.clear", "device.model"]), "recent is deduplicated MRU");
            Check(!File.ReadAllText(path).Contains("com.example"), "no filled command values on disk");
            foreach (var item in ConsolePresetCatalog.All.Take(30)) store.Remember(item.Id);
            Check(store.Load().Recent.Count == 12, "bounded recent list");
            Check(store.ToggleFavorite("app.clear").Favorites.Count == 0, "remove favorite");
            Throws(() => store.Remember("unknown-preset"));
            File.WriteAllText(path, JsonSerializer.Serialize(new { Favorites = new[] { "app.clear", "app.clear", "stale", "shell pm clear secret" }, Recent = new[] { "stale", "device.model" } }));
            loaded = store.Load();
            Check(loaded.Favorites.SequenceEqual(["app.clear"]) && loaded.Recent.SequenceEqual(["device.model"]), "unknown IDs and legacy values rejected");
            Check(!File.ReadAllText(path).Contains("secret"), "unknown legacy values removed from disk");
        }
        finally { Directory.Delete(directory, recursive: true); }
        return Task.CompletedTask;
    }

    private static string Sample(string kind) => kind switch
    {
        "package" => "com.example.app", "component" => "com.example.app/.MainActivity",
        "permission" => "android.permission.CAMERA", "endpoint" => "192.168.1.2:5555",
        "port" => "8080", "pid" => "1234", "coordinate" => "100", "duration" => "10",
        "density" => "420", "user" => "0", "size" => "1080x1920", "code" => "123456", "url" => "example://screen?id=1",
        "remote-path" => "/sdcard/Download/example.txt", "file" or "apk" => "C:\\sample folder\\sample.apk",
        "folder" => "C:\\sample folder", _ => "example"
    };
    private static void Throws(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new Exception("Expected argument rejection.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
