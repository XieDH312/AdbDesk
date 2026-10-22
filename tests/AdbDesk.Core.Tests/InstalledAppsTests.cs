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

    public static async Task DetailsUserScopeAsync()
    {
        // A global permission search or the first User block would incorrectly grant CAMERA.
        const string raw = """
            Packages:
              Package [com.example.extra] (other):
                versionCode=999 minSdk=1 targetSdk=1
              Package [com.example] (active):
                versionCode=4294967297 minSdk=23 targetSdk=35
                versionName=2.3 beta
                firstInstallTime=2023-01-01 00:00:00
                lastUpdateTime=2026-10-20 11:12:13
                requested permissions:
                  android.permission.CAMERA
                  android.permission.INTERNET
                  android.permission.RECORD_AUDIO
                  com.example.permission.UNKNOWN
                install permissions:
                  android.permission.INTERNET: granted=true
                  android.permission.RECORD_AUDIO: granted=false
                User 0: installed=true
                  firstInstallTime=2024-01-01 00:00:00
                  runtime permissions:
                    android.permission.CAMERA: granted=true, flags=[ USER_SET ]
                    com.other.permission.PRIVATE: granted=true
                User 10: installed=true
                  firstInstallTime=2026-10-01 08:09:10
                  runtime permissions:
                    android.permission.CAMERA: granted=false, flags=[ USER_SET ]
                    android.permission.RECORD_AUDIO: granted=true
                User 11: installed=true
                  firstInstallTime=2025-01-01 00:00:00
                  runtime permissions:
                    com.third.permission.PRIVATE: granted=true
            Hidden system packages:
              Package [com.example] (backup):
                versionCode=1 minSdk=1 targetSdk=1
                versionName=factory
                requested permissions:
                  com.backup.permission.PRIVATE
            """;
        var details = await LoadDetailsAsync(raw);
        Check(details.MinSdk == 23 && details.TargetSdk == 35 && details.VersionCode == 4294967297 && details.VersionName == "2.3 beta", "exact active package metadata is selected");
        Check(details.FirstInstallTime == "2026-10-01 08:09:10" && details.LastUpdateTime == "2026-10-20 11:12:13", "selected user's install date overrides global date");
        Check(details.RequestedPermissionsAvailable && details.Permissions.Count == 4, "only requested and selected-scope permissions are exposed");
        var camera = details.Permissions.Single(p => p.Name == "android.permission.CAMERA");
        Check(camera.IsRequested && camera.InstallGranted is null && camera.RuntimeGranted == false, "requested does not imply granted and another user's grant is ignored");
        var internet = details.Permissions.Single(p => p.Name == "android.permission.INTERNET");
        Check(internet.InstallGranted == true && internet.RuntimeGranted is null, "install grant is not mislabelled as a runtime grant");
        var audio = details.Permissions.Single(p => p.Name == "android.permission.RECORD_AUDIO");
        Check(audio.InstallGranted == false && audio.RuntimeGranted == true, "install and runtime values remain distinct");
        var unknown = details.Permissions.Single(p => p.Name == "com.example.permission.UNKNOWN");
        Check(unknown.InstallGranted is null && unknown.RuntimeGranted is null, "absent grant is unknown, not denied");
        var summary = details.ToSummary(new("com.example", 10, 42, false, true));
        Check(summary.Contains("Android 用户：10") && summary.Contains("com.example") && summary.Contains("4294967297") && summary.Contains("第三方") && summary.Contains("已启用"), "copy summary identifies package, user, current detail version and app state");
        Check(summary.Contains("23") && summary.Contains("35") && summary.Contains("2026-10-01 08:09:10") && summary.Contains("/data/app/example/base.apk"), "copy summary includes SDK, dates and APK paths");
        Check(summary.Contains("已请求") && summary.Contains("安装：已授予") && summary.Contains("运行时：未授予") && summary.Contains("未知"), "copy summary preserves request and separate three-state grant information");
        Check(!summary.Contains("com.other.permission") && !summary.Contains("com.third.permission") && !summary.Contains("com.backup.permission"), "copy summary never includes other users or factory package permissions");
    }

    public static async Task DetailsMissingAndDuplicateBlocksAsync()
    {
        // Hidden data may precede the active package, and missing fields must remain unknown.
        const string raw = """
            Hidden system packages:
              Package [com.example] (backup):
                versionCode=1 minSdk=1 targetSdk=1
                firstInstallTime=2020-01-01 00:00:00
                requested permissions:
                  com.backup.permission.PRIVATE
            Packages:
              Package [com.example] (active):
                User 0: installed=true
                  firstInstallTime=2024-01-01 00:00:00
                  runtime permissions:
                    android.permission.CAMERA: granted=true
                User 10: installed=true
                  runtime permissions:
                    android.permission.RECORD_AUDIO: granted=unknown
            Disabled system packages:
              Package [com.example] (disabled):
                versionCode=2 minSdk=2 targetSdk=2
            """;
        var details = await LoadDetailsAsync(raw);
        Check(details.VersionName is null && details.VersionCode is null && details.MinSdk is null && details.TargetSdk is null, "missing active metadata is not filled from hidden or disabled packages");
        Check(details.FirstInstallTime is null && details.LastUpdateTime is null, "another user's date never supplies a missing date");
        Check(!details.RequestedPermissionsAvailable && details.Permissions.Count == 1 && details.Permissions[0].RuntimeGranted is null, "missing request list and malformed grants stay unknown");
        Check(!details.ToSummary(new("com.example", 10, 42, false, true)).Contains("未请求"), "absent request list cannot be represented as not requested");

        var empty = await LoadDetailsAsync("Packages:\n  Package [com.example] (active):\n    requested permissions:\n    User 10: installed=true\n");
        Check(empty.RequestedPermissionsAvailable && empty.Permissions.Count == 0, "confirmed empty request list differs from missing output");
        const string annotatedRequests = """
            Packages:
              Package [com.example] (active):
                requested permissions:
                  android.permission.READ_EXTERNAL_STORAGE: restricted=true
                  android.permission.WRITE_EXTERNAL_STORAGE: restricted=false
                  android.permission.INTERNET
                User 10: installed=true
            """;
        var annotated = await LoadDetailsAsync(annotatedRequests);
        Check(annotated.RequestedPermissionsAvailable && annotated.Permissions.Count == 3, "known restricted annotations preserve the complete requested list");
        Check(annotated.Permissions.All(p => p.IsRequested && p.InstallGranted is null && p.RuntimeGranted is null), "restricted true or false describes a request and never implies a grant or denial");
        Check(annotated.Permissions.Any(p => p.Name == "android.permission.READ_EXTERNAL_STORAGE") && annotated.Permissions.Any(p => p.Name == "android.permission.WRITE_EXTERNAL_STORAGE"), "permission names exclude restricted annotations");
        var unrecognized = await LoadDetailsAsync("Packages:\n  Package [com.example] (active):\n    requested permissions:\n      android.permission.CAMERA: vendorFlag=true\n    User 10: installed=true\n");
        Check(!unrecognized.RequestedPermissionsAvailable, "unknown requested-permission annotations cannot imply a complete request list");
        var globalDate = await LoadDetailsAsync("Packages:\n  Package [com.example] (active):\n    firstInstallTime=2023-01-01 00:00:00\n    User 0: installed=true\n      firstInstallTime=2024-01-01 00:00:00\n      runtime permissions:\n        android.permission.CAMERA: granted=true\n");
        Check(globalDate.FirstInstallTime == "2023-01-01 00:00:00" && globalDate.Permissions.Count == 0, "older package-level install date is available without leaking another user's runtime permissions");
        const string userInstall = """
            Packages:
              Package [com.example] (active):
                requested permissions:
                  android.permission.INTERNET
                  com.example.permission.LEGACY
                install permissions:
                  android.permission.INTERNET: granted=true
                  com.example.permission.LEGACY: granted=true
                User 0: installed=true
                  install permissions:
                    com.other.permission.PRIVATE: granted=true
                User 10: installed=true
                  install permissions:
                    android.permission.INTERNET: granted=false
                User 11: installed=true
                  install permissions:
                    com.third.permission.PRIVATE: granted=true
            """;
        var scopedInstall = await LoadDetailsAsync(userInstall);
        Check(scopedInstall.Permissions.Count == 2 && scopedInstall.Permissions.Single(p => p.Name == "android.permission.INTERNET").InstallGranted == false, "selected-user install section takes precedence over legacy package grants and other users");
        Check(scopedInstall.Permissions.Single(p => p.Name == "com.example.permission.LEGACY").InstallGranted is null, "missing selected-user install grant does not inherit a package-level grant");
        const string annotatedInstallUsers = """
            Packages:
              Package [com.example] (active):
                requested permissions:
                  android.permission.CAMERA
                  com.example.permission.CONFLICT
                  com.example.permission.UNKNOWN_USER
                  com.example.permission.OVERFLOW_USER
                  com.example.permission.EMPTY_USER
                  com.example.permission.DUPLICATE_USER
                install permissions:
                  android.permission.CAMERA: granted=true
                  android.permission.CAMERA: granted=false, flags=[ USER_SET ], userId=10
                  android.permission.CAMERA: granted=false,userId=11
                  com.example.permission.CONFLICT: granted=true
                  com.example.permission.CONFLICT: granted=false, userId=10
                  com.example.permission.CONFLICT: granted=true, userId=10
                  com.other.permission.PRIVATE: granted=true, userId=11
                  com.example.permission.UNKNOWN_USER: granted=true, userId=unknown
                  com.example.permission.OVERFLOW_USER: granted=true, userId=999999999999
                  com.example.permission.EMPTY_USER: granted=true, userId=
                  com.example.permission.DUPLICATE_USER: granted=true, userId=10, userId=11
                User 0: installed=true
                User 10: installed=true
                User 12: installed=true
            """;
        var selectedOverride = await LoadDetailsAsync(annotatedInstallUsers);
        Check(selectedOverride.Permissions.Single(p => p.Name == "android.permission.CAMERA").InstallGranted == false, "selected-user annotated grant overrides the unqualified package default");
        Check(selectedOverride.Permissions.Single(p => p.Name == "com.example.permission.CONFLICT").InstallGranted is null, "conflicting selected-scope grants are unknown rather than inherited defaults");
        Check(!selectedOverride.Permissions.Any(p => p.Name == "com.other.permission.PRIVATE"), "another user's override-only permission does not appear");
        Check(selectedOverride.Permissions.Where(p => p.Name.EndsWith("_USER", StringComparison.Ordinal)).All(p => p.InstallGranted is null), "malformed user annotations never become unqualified global grants");
        foreach (var user in new[] { 0, 12 })
        {
            var inherited = await LoadDetailsAsync(annotatedInstallUsers, user);
            Check(inherited.Permissions.Single(p => p.Name == "android.permission.CAMERA").InstallGranted == true, "system user and users without an override retain the unqualified default");
            Check(inherited.Permissions.Single(p => p.Name == "com.example.permission.CONFLICT").InstallGranted == true, "another user's conflicting overrides do not damage the current user's default");
        }
        var nestedOverridesAnnotations = await LoadDetailsAsync(annotatedInstallUsers.Replace("    User 10: installed=true", "    User 10: installed=true\n      install permissions:\n        android.permission.CAMERA: granted=true", StringComparison.Ordinal));
        Check(nestedOverridesAnnotations.Permissions.Single(p => p.Name == "android.permission.CAMERA").InstallGranted == true, "nested selected-user install section still takes precedence over annotated package overrides");
        foreach (var invalid in new[]
        {
            "Packages:\n  Package [com.example.extra] (other):\n    versionCode=1\n",
            "Hidden system packages:\n  Package [com.example] (backup):\n    versionCode=1\n",
            "Packages:\n  Package [com.example] (one):\n    versionCode=1\n  Package [com.example] (two):\n    versionCode=2\n"
        }) await ThrowsAsync<IOException>(() => LoadDetailsAsync(invalid));
    }

    public static async Task SettingsAsync()
    {
        var calls = new List<ConsoleCommand>();
        var app = new InstalledApp("com.example", 10, 42, false, false);
        var service = new InstalledAppsService((c, _) =>
        {
            calls.Add(c);
            return Task.FromResult(c.HistoryText switch
            {
                "shell am get-current-user" => "10",
                "shell pm resolve-activity --brief --user 10 -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -p com.example" => "No activity found",
                "shell am start --user 10 -a android.settings.APPLICATION_DETAILS_SETTINGS -d package:com.example" => "Starting: Intent { act=android.settings.APPLICATION_DETAILS_SETTINGS dat=package:com.example }",
                _ => throw new Exception(c.HistoryText)
            });
        });
        await ThrowsAsync<IOException>(() => service.LaunchAsync("phone-C", app, default));
        calls.Clear();
        await service.OpenSettingsAsync("phone-C", app, default);
        Check(calls.All(c => c.Arguments.Take(2).SequenceEqual(new[] { "-s", "phone-C" })), "settings always targets the selected serial");
        Check(calls.Count(c => c.HistoryText.Contains("am start --user 10 -a android.settings.APPLICATION_DETAILS_SETTINGS -d package:com.example")) == 1, "disabled app without launcher still opens its exact user-scoped settings target");
        Check(calls.All(c => !c.HistoryText.Contains("resolve-activity")), "settings does not depend on app launcher visibility");
        var before = calls.Count;
        await ThrowsAsync<ArgumentException>(() => service.OpenSettingsAsync("phone-C", app with { PackageName = "com.example;reboot" }, default));
        Check(before == calls.Count, "settings rejects invalid target before I/O");
        var switchedCalls = new List<string>();
        var switched = new InstalledAppsService((c, _) => { switchedCalls.Add(c.HistoryText); return Task.FromResult("0"); });
        await ThrowsAsync<IOException>(() => switched.OpenSettingsAsync("phone-C", app, default));
        Check(switchedCalls.Count == 1 && switchedCalls[0] == "shell am get-current-user", "switched users cannot dispatch the settings intent");
        foreach (var failure in new[] { "", "Success", "Error: Activity not started", "Starting: Intent {}\nError: Permission Denial", "unrecognized device output" })
        {
            var failed = new InstalledAppsService((c, _) => Task.FromResult(c.HistoryText == "shell am get-current-user" ? "10" : failure));
            await ThrowsAsync<IOException>(() => failed.OpenSettingsAsync("phone-C", app, default));
        }
        using var cancellation = new CancellationTokenSource();
        var cancelCalls = 0;
        var canceled = new InstalledAppsService((_, token) =>
        {
            Check(token == cancellation.Token, "settings cancellation is forwarded");
            cancelCalls++;
            cancellation.Cancel();
            return Task.FromResult("10");
        });
        await ThrowsAsync<OperationCanceledException>(() => canceled.OpenSettingsAsync("phone-C", app, cancellation.Token));
        Check(cancelCalls == 1, "cancellation before settings dispatch prevents action");
    }

    public static async Task DetailsFailureAndCancellationAsync()
    {
        var app = new InstalledApp("com.example", 10, 42, false, true);
        var dumpCalls = 0;
        var noPath = new InstalledAppsService((c, _) =>
        {
            if (c.HistoryText.Contains("dumpsys")) dumpCalls++;
            return Task.FromResult(c.HistoryText == "shell am get-current-user" ? "10" : "");
        });
        await ThrowsAsync<IOException>(() => noPath.DetailsAsync("serial", app, default));
        Check(dumpCalls == 0, "details require a selected-user installation before global dump");
        await ThrowsAsync<IOException>(() => LoadDetailsAsync("Error: package service unavailable"));
        using var cancellation = new CancellationTokenSource();
        var count = 0;
        var canceled = new InstalledAppsService((c, _) =>
        {
            count++;
            if (c.HistoryText.Contains("pm path")) cancellation.Cancel();
            return Task.FromResult(c.HistoryText == "shell am get-current-user" ? "10" : "package:/data/app/example/base.apk");
        });
        await ThrowsAsync<OperationCanceledException>(() => canceled.DetailsAsync("serial", app, cancellation.Token));
        Check(count == 2, "cancellation prevents a later global dump or parsed result");
    }

    private static Task<InstalledAppDetails> LoadDetailsAsync(string raw, int user = 10)
    {
        var service = new InstalledAppsService((c, _) => Task.FromResult(c.HistoryText switch
        {
            "shell am get-current-user" => user.ToString(System.Globalization.CultureInfo.InvariantCulture),
            var command when command == $"shell pm path --user {user} com.example" => "package:/data/app/example/base.apk",
            "shell dumpsys package com.example" => raw,
            _ => throw new Exception(c.HistoryText)
        }));
        return service.DetailsAsync("serial", new("com.example", user, 42, false, true), default);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
}
