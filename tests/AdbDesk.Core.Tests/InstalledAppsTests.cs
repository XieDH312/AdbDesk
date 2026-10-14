using AdbDesk.Core;

internal static class InstalledAppsTests
{
    // These fixtures catch user-scope leakage, output accepted as success, and shell injection.
    public static async Task SnapshotAsync()
    {
        var calls = new List<ConsoleCommand>();
        var service = new InstalledAppsService((command, token) =>
        {
            calls.Add(command);
            return Task.FromResult(command.HistoryText switch
            {
                "shell am get-current-user" => "10\n",
                "shell pm list packages --user 10 --show-versioncode" => "package:com.example versionCode:4294967297\npackage:android versionCode:36\n",
                "shell pm list packages --user 10 -s" => "package:android\n",
                "shell pm list packages --user 10 -d" => "package:com.example\n",
                "shell pm list packages --user 10 -e" => "package:android\n",
                _ => throw new Exception("unexpected command " + command.HistoryText)
            });
        });
        var result = await service.LoadAsync("phone-A", default);
        Check(result.UserId == 10 && result.Apps.Count == 2, "current Android user is retained");
        var example = result.Apps.Single(a => a.PackageName == "com.example");
        Check(example.VersionCode == 4294967297 && !example.IsSystem && !example.IsEnabled, "long versionCode and disabled third-party classification");
        Check(result.Apps.Single(a => a.PackageName == "android").IsSystem, "single segment platform package accepted");
        Check(calls.All(c => c.Arguments.Take(2).SequenceEqual(new[] { "-s", "phone-A" })), "all queries explicitly target selected device");
        Check(calls.Where(c => c.HistoryText.Contains("pm ")).All(c => c.HistoryText.Contains("--user 10")), "package queries never default to another user");
        var userReads = 0;
        var changed = new InstalledAppsService((c, _) => Task.FromResult(c.HistoryText switch
        {
            "shell am get-current-user" => ++userReads == 1 ? "10" : "0",
            "shell pm list packages --user 10 --show-versioncode" => "package:com.example versionCode:1",
            "shell pm list packages --user 10 -e" => "package:com.example",
            _ => ""
        }));
        await ThrowsAsync<IOException>(() => changed.LoadAsync("phone-A", default));
    }

    public static async Task ActionsAsync()
    {
        var calls = new List<ConsoleCommand>();
        var app = new InstalledApp("com.example", 10, 42, false, true);
        var service = new InstalledAppsService((command, token) =>
        {
            calls.Add(command);
            return Task.FromResult(command.HistoryText switch
            {
                "shell am get-current-user" => "10",
                "shell pm resolve-activity --brief --user 10 -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -p com.example" => "com.example/.Main$Inner\n",
                "shell am start --user 10 -n 'com.example/.Main$Inner' -a android.intent.action.MAIN -c android.intent.category.LAUNCHER" => "Starting: Intent { cmp=com.example/.Main$Inner }\n",
                "shell am force-stop --user 10 com.example" => "",
                "shell pm clear --user 10 com.example" => "Success\n",
                "shell pm uninstall --user 10 com.example" => "Success\n",
                _ => throw new Exception(command.HistoryText)
            });
        });
        await service.LaunchAsync("phone-B", app, default);
        await service.StopAsync("phone-B", app, default);
        await service.ClearDataAsync("phone-B", app, default);
        await service.UninstallAsync("phone-B", app, default);
        Check(calls.All(c => c.Arguments.Take(2).SequenceEqual(new[] { "-s", "phone-B" })), "actions target requested serial");
        Check(calls.Where(c => !c.HistoryText.EndsWith("get-current-user")).All(c => c.HistoryText.Contains("--user 10")), "all actions use fixed user");
        var before = calls.Count;
        await ThrowsAsync<ArgumentException>(() => service.ClearDataAsync("phone-B", app with { PackageName = "com.example;reboot" }, default));
        await ThrowsAsync<ArgumentException>(() => service.UninstallAsync("phone-B", app with { UserId = -1 }, default));
        Check(calls.Count == before, "invalid identifiers rejected before I/O");
        var switched = new InstalledAppsService((_, _) => Task.FromResult("0"));
        await ThrowsAsync<IOException>(() => switched.ClearDataAsync("phone-B", app, default));
    }

    public static async Task FailureAndCancellationAsync()
    {
        var app = new InstalledApp("com.example", 10, 42, false, true);
        foreach (var failure in new[] { "Failure [DELETE_FAILED_INTERNAL_ERROR]", "Error: device offline", "", "Success\nFailure [blocked]" })
        {
            var service = new InstalledAppsService((c, _) => Task.FromResult(c.HistoryText.EndsWith("get-current-user") ? "10" : failure));
            await ThrowsAsync<IOException>(() => service.ClearDataAsync("serial", app, default));
            await ThrowsAsync<IOException>(() => service.UninstallAsync("serial", app, default));
        }
        foreach (var malformed in new[] { "current user: 10", "-1", "10\n0", "999999999999" })
        {
            var service = new InstalledAppsService((_, _) => Task.FromResult(malformed));
            await ThrowsAsync<IOException>(() => service.LoadAsync("serial", default));
        }
        foreach (var failure in new[] { "Failure [blocked]", "Failed to stop package", "unrecognized device output" })
        {
            var service = new InstalledAppsService((c, _) => Task.FromResult(c.HistoryText.EndsWith("get-current-user") ? "10" : failure));
            await ThrowsAsync<IOException>(() => service.StopAsync("serial", app, default));
        }
        foreach (var malformed in new[] { "package:com.example versionCode:bad", "package:com.example", "unknown output", "package:com.example versionCode:1\npackage:com.example versionCode:2" })
        {
            var service = new InstalledAppsService((c, _) => Task.FromResult(c.HistoryText.EndsWith("get-current-user") ? "10" : malformed));
            await ThrowsAsync<IOException>(() => service.LoadAsync("serial", default));
        }
        foreach (var component in new[] { "com.other/.Main", "com.example/.Main;reboot", "No activity found", "com.example/.One\ncom.example/.Two" })
        {
            var service = new InstalledAppsService((c, _) => Task.FromResult(c.HistoryText.EndsWith("get-current-user") ? "10" : component));
            await ThrowsAsync<IOException>(() => service.LaunchAsync("serial", app, default));
        }
        using var cancellation = new CancellationTokenSource();
        var count = 0;
        var cancelService = new InstalledAppsService((_, token) =>
        {
            Check(token == cancellation.Token, "token reaches executor");
            count++;
            cancellation.Cancel();
            return Task.FromResult("10");
        });
        await ThrowsAsync<OperationCanceledException>(() => cancelService.LoadAsync("serial", cancellation.Token));
        Check(count == 1, "cancel stops subsequent commands");
        await ThrowsAsync<OperationCanceledException>(() => cancelService.ClearDataAsync("serial", app, cancellation.Token));
        Check(count == 1, "pre-cancelled mutation does not reach executor");
        var oversized = new InstalledAppsService((_, _) => Task.FromResult(new string('x', 4 * 1024 * 1024 + 1)));
        await ThrowsAsync<IOException>(() => oversized.LoadAsync("serial", default));
    }

    public static async Task DetailsAsync()
    {
        var service = new InstalledAppsService((c, _) => Task.FromResult(c.HistoryText switch
        {
            "shell am get-current-user" => "10",
            "shell pm path --user 10 com.example" => "package:/data/app/example/base.apk\npackage:/data/app/example/split.apk",
            "shell dumpsys package com.example" => "Packages:\n  Package [com.example] (abc):\n    versionCode=4294967297 minSdk=23\n    versionName=2.3 beta\n    User 10: installed=true\n",
            _ => throw new Exception(c.HistoryText)
        }));
        var details = await service.DetailsAsync("serial", new("com.example", 10, 42, false, true), default);
        Check(details.VersionName == "2.3 beta" && details.VersionCode == 4294967297 && details.ApkPaths.Count == 2, "lazy package version and all split paths");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
}
