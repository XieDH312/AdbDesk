using AdbDesk.Core;

internal static class ToolboxTests
{
    public static Task ParseAsync()
    {
        var foreground = RunningAppsParser.Foreground("""
            ACTIVITY MANAGER ACTIVITIES (dumpsys activity activities)
            mResumedActivity: ActivityRecord{ab u0 com.example.alpha/.Main t10}
            topResumedActivity=ActivityRecord{ab u0 com.example.alpha/.Main t10}
            mResumedActivity: ActivityRecord{cd u10 com.example.beta/.View t11}
            mLastPausedActivity: ActivityRecord{ef u0 com.example.old/.Main t9}
            """);
        Check(foreground.Count == 2 && foreground[1].UserId == 10, "resumed only, deduplicated, multiple users");
        var recent = RunningAppsParser.Recent("""
            ACTIVITY MANAGER RECENT TASKS (dumpsys activity recents)

            * Recent #0: Task{abc #42 type=standard A=10012:com.example.alpha U=0 visible=false}
              userId=0 effectiveUid=10012
              intent={act=android.intent.action.MAIN cmp=com.example.alpha/.Main}
              realActivity=com.example.alpha/.Main
            * Recent #1: TaskRecord{def #41 A=com.example.beta U=10 StackId=1 sz=1}
              userId=10 effectiveUid=1010013
              realActivity=com.example.beta/.View
            """);
        Check(recent.Count == 2 && recent[0].TaskId == 42 && recent[1].UserId == 10, "task order and user preserved");
        var modern = RunningAppsParser.Recent("""
            * Recent #0: Task{abc #42 U=0}
              mActivityComponent=com.example.alpha/.Main
            * Recent #1: Task{def #41 U=10}
            Visible recent tasks (most recent first):
              realActivity=com.example.wrong/.Main
            """);
        Check(modern.Count == 1 && modern[0].PackageName == "com.example.alpha", "new component field and visible-section isolation");
        var processes = RunningAppsParser.Processes("""
            ACTIVITY MANAGER RUNNING PROCESSES (dumpsys activity processes)

            *APP* UID 10012 ProcessRecord{aa 1234:com.example.alpha/u0a12}
              packageList={com.example.alpha}
              curProcState=2 setProcState=2

            *APP* UID 1010013 ProcessRecord{bb 2222:com.example.beta:worker/u10a13}
              packageList={com.example.beta, com.example.shared}
              curProcState=19 setProcState=19
            *PERS* UID 1000 ProcessRecord{cc 789:system/1000}
              packageList={android}
            *APP* UID 10099 ProcessRecord{dd 0:com.example.pending/u0a99}
              packageList={com.example.pending}
              curProcState=20
            """);
        Check(processes.Count == 4 && processes.Any(p => p.PackageName == "com.example.shared" && p.UserId == 10 && p.Status.Contains("缓存")), "package mapping, cached and shared processes");
        Check(RunningAppsParser.ApkPaths("package:/data/app/a/base.apk\npackage:/data/app/a/split_config.arm64.apk\n").Count == 2, "all split APKs");
        try { RunningAppsParser.ApkPaths("package:/data/app/../../bad.apk"); throw new Exception("accepted traversal"); }
        catch (ArgumentException) { }
        return Task.CompletedTask;
    }

    public static async Task ServiceAsync()
    {
        var calls = new List<ConsoleCommand>();
        var service = new RunningAppsService((command, token) =>
        {
            calls.Add(command);
            return Task.FromResult(command.HistoryText switch
            {
                "shell dumpsys activity -a activities" => "ACTIVITY MANAGER ACTIVITIES\nmResumedActivity: ActivityRecord{aa u0 com.example/.Main t1}",
                "shell dumpsys activity -a recents" => "Permission Denial: cannot dump",
                "shell dumpsys activity -a processes" => "unknown output format",
                _ => ""
            });
        });
        var snapshot = await service.LoadAsync("phone-A", default);
        Check(snapshot.Foreground.Count == 1 && snapshot.Warnings.Count == 2, "partial results retain per-source errors");
        Check(calls.All(c => c.Arguments.Take(2).SequenceEqual(new[] { "-s", "phone-A" })), "every query explicitly targets selected phone");
        calls.Clear();
        await service.RestartServerAsync(default);
        Check(calls.Select(c => c.HistoryText).SequenceEqual(new[] { "kill-server", "start-server", "server-status" }), "restart sequence is awaited and global");
        calls.Clear();
        var failing = new RunningAppsService((command, token) =>
        {
            calls.Add(command);
            throw new IOException("failed");
        });
        try { await failing.RestartServerAsync(default); throw new Exception("expected failure"); }
        catch (IOException) { }
        Check(calls.Count == 1, "restart must not claim success after kill failure");
        calls.Clear();
        await service.StopAsync("phone-B", new RunningApp("com.example", 10, "", ""), default);
        Check(calls.Single().Preview.Contains("phone-B") && calls.Single().HistoryText == "shell am force-stop --user 10 com.example", "mutations target the owning user and selected serial");
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new Exception(reason);
    }
}
