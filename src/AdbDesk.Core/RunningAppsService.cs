using System.Text;
using System.Text.RegularExpressions;

namespace AdbDesk.Core;

public sealed record RunningApp(string PackageName, int UserId, string Activity, string Status,
    int? Pid = null, int? TaskId = null)
{
    public string Identity => $"{PackageName} · 用户 {UserId}";
}

public sealed record RunningAppsSnapshot(IReadOnlyList<RunningApp> Foreground,
    IReadOnlyList<RunningApp> Recent, IReadOnlyList<RunningApp> Processes,
    IReadOnlyList<string> Warnings, DateTimeOffset CapturedAt);

public static class RunningAppsParser
{
    private const string PackagePattern = @"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*";
    private const string ComponentPattern = @"(?<package>" + PackagePattern + @")/(?<activity>[A-Za-z0-9_.$]+)";

    public static IReadOnlyList<RunningApp> Foreground(string output)
    {
        var entries = new List<RunningApp>();
        foreach (var line in output.Split('\n'))
        {
            if (!Regex.IsMatch(line, @"(?:mResumedActivity|topResumedActivity|ResumedActivity)\s*[:=]")) continue;
            var match = Regex.Match(line, @"ActivityRecord\{[^\r\n]*?\bu(?<user>\d+)\s+" + ComponentPattern);
            if (match.Success) entries.Add(FromMatch(match, "前台（已恢复）"));
        }
        return entries.DistinctBy(x => (x.PackageName, x.UserId, x.Activity)).ToArray();
    }

    public static IReadOnlyList<RunningApp> Recent(string output)
    {
        // The later launcher-facing section has a different record shape. Never
        // let its components be attributed to the final raw task above it.
        var visibleSection = output.IndexOf("Visible recent tasks", StringComparison.Ordinal);
        if (visibleSection >= 0) output = output[..visibleSection];
        var entries = new List<RunningApp>();
        var blocks = Regex.Matches(output, @"(?ms)^[ \t]*\*?[ \t]*Recent[ \t]+#\d+:[^\r\n]*.*?(?=^[ \t]*\*?[ \t]*Recent[ \t]+#\d+:|\z)");
        foreach (Match block in blocks)
        {
            var component = Regex.Match(block.Value, @"(?:realActivity|mActivityComponent)=" + ComponentPattern);
            if (!component.Success) component = Regex.Match(block.Value, @"\bcmp=" + ComponentPattern);
            if (!component.Success) continue;
            var user = Regex.Match(block.Value, @"\b(?:userId|U)=(?<user>\d+)");
            // Do not guess ownership of work-profile tasks when a vendor omits the user.
            if (!user.Success || !int.TryParse(user.Groups["user"].Value, out var userId)) continue;
            var task = Regex.Match(block.Value.Split('\n')[0], @"(?:Task|TaskRecord)\{[^\r\n]*?#(?<task>\d+)");
            int? taskId = task.Success && int.TryParse(task.Groups["task"].Value, out var id) ? id : null;
            entries.Add(new(component.Groups["package"].Value, userId,
                component.Groups["package"].Value + "/" + component.Groups["activity"].Value,
                "最近任务（不代表正在运行）", TaskId: taskId));
        }
        return entries;
    }

    public static IReadOnlyList<RunningApp> Processes(string output)
    {
        var entries = new List<RunningApp>();
        var blocks = Regex.Matches(output, @"(?ms)^[ \t]*\*(?:APP|PERS)\*[^\r\n]*.*?(?=^[ \t]*\*(?:APP|PERS)\*|\z)");
        foreach (Match block in blocks)
        {
            var process = Regex.Match(block.Value.Split('\n')[0], @"ProcessRecord\{\S+\s+(?<pid>\d+):[^/\s]+/(?<uid>[^}\s]+)");
            if (!process.Success || !int.TryParse(process.Groups["pid"].Value, out var pid) || pid <= 0) continue;
            var uid = process.Groups["uid"].Value;
            var user = Regex.Match(uid, @"^u(?<user>\d+)");
            int userId;
            if (user.Success && int.TryParse(user.Groups["user"].Value, out var parsedUser)) userId = parsedUser;
            else if (int.TryParse(uid, out var numericUid)) userId = numericUid / 100000;
            else continue;
            var packages = Regex.Match(block.Value, @"packageList=\{(?<packages>[^}\r\n]*)\}");
            if (!packages.Success) continue;
            var state = Regex.Match(block.Value, @"\bcurProcState=(?<state>\d+)");
            var stateValue = state.Success && int.TryParse(state.Groups["state"].Value, out var value) ? value : -1;
            if (stateValue == 20) continue; // PROCESS_STATE_NONEXISTENT is not a live process.
            var cached = Regex.IsMatch(block.Value, @"\bcached=true\b") || stateValue is >= 16 and <= 19;
            foreach (var package in packages.Groups["packages"].Value.Split(',').Select(p => p.Trim()).Distinct())
            {
                if (!IsPackage(package)) continue;
                entries.Add(new(package, userId, "", cached ? "缓存进程" : "进程存在", Pid: pid));
            }
        }
        return entries.DistinctBy(x => (x.PackageName, x.UserId, x.Pid)).ToArray();
    }

    public static IReadOnlyList<string> ApkPaths(string output)
    {
        var paths = new List<string>();
        foreach (var line in output.Split('\n').Select(x => x.Trim()).Where(x => x.StartsWith("package:", StringComparison.Ordinal)))
        {
            var path = line[8..];
            if (!path.StartsWith('/') || !path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) ||
                path.Split('/').Any(x => x is ".." or ".") || path.Any(char.IsControl) || path.Contains('\\'))
                throw new ArgumentException("手机返回了无法安全导出的 APK 路径。");
            paths.Add(path);
        }
        return paths.Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static bool IsPackage(string package) => Regex.IsMatch(package, "^" + PackagePattern + "$", RegexOptions.CultureInvariant);

    private static RunningApp FromMatch(Match match, string status) => new(match.Groups["package"].Value,
        int.Parse(match.Groups["user"].Value), match.Groups["package"].Value + "/" + match.Groups["activity"].Value, status);
}

public sealed class RunningAppsService
{
    private readonly Func<ConsoleCommand, CancellationToken, Task<string>> _run;
    public RunningAppsService(ToolPaths paths)
    {
        _run = async (command, token) =>
        {
            var output = new StringBuilder();
            var error = new StringBuilder();
            var outputLock = new object();
            var overflow = false;
            var exit = await new ConsoleCommandRunner(paths).RunAsync(command, (text, stderr) =>
            {
                lock (outputLock)
                {
                    var buffer = stderr ? error : output;
                    if (buffer.Length + text.Length > 4 * 1024 * 1024) { overflow = true; return; }
                    buffer.Append(text);
                }
            }, TimeSpan.FromSeconds(command.Arguments.Contains("pull") ? 180 : 25), token).ConfigureAwait(false);
            if (overflow) throw new IOException("设备输出超过 4 MB，请在 ADB 控制台查看原始结果。");
            if (exit != 0) throw new IOException($"ADB 退出码 {exit}：{Limit(error.Length > 0 ? error.ToString() : output.ToString())}");
            var result = output.ToString();
            if (result.Contains("Permission Denial", StringComparison.OrdinalIgnoreCase) ||
                result.Contains("Can't find service", StringComparison.OrdinalIgnoreCase)) throw new IOException(Limit(result));
            return result;
        };
    }

    internal RunningAppsService(Func<ConsoleCommand, CancellationToken, Task<string>> run) => _run = run;

    public async Task<RunningAppsSnapshot> LoadAsync(string serial, CancellationToken token)
    {
        var warnings = new List<string>();
        // Sequential probes avoid overwhelming wireless devices; partial errors remain visible.
        var foreground = await Query("当前前台", "activities", RunningAppsParser.Foreground, "ACTIVITY MANAGER ACTIVITIES", "ResumedActivity");
        var recent = await Query("最近任务", "recents", RunningAppsParser.Recent, "ACTIVITY MANAGER RECENT", "Recent #");
        var processes = await Query("后台进程", "processes", RunningAppsParser.Processes, "ACTIVITY MANAGER RUNNING PROCESSES", "*APP*");
        return new(foreground, recent, processes, warnings, DateTimeOffset.Now);

        async Task<IReadOnlyList<RunningApp>> Query(string label, string section,
            Func<string, IReadOnlyList<RunningApp>> parse, string heading, string marker)
        {
            try
            {
                var text = await RunAsync($"shell dumpsys activity -a {section}", serial, token).ConfigureAwait(false);
                if (text.Contains("Permission Denial", StringComparison.OrdinalIgnoreCase)) throw new IOException("设备拒绝读取。");
                var result = parse(text);
                if (result.Count == 0 && (!text.Contains(heading, StringComparison.OrdinalIgnoreCase) || text.Contains(marker, StringComparison.Ordinal) || (section == "processes" && text.Contains("*PERS*", StringComparison.Ordinal))))
                    throw new IOException("无法识别该系统的输出，请在 ADB 控制台查看原始命令。");
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { warnings.Add($"{label}：{ex.Message}"); return []; }
        }
    }

    public async Task<string> DetailsAsync(string serial, RunningApp app, CancellationToken token)
    {
        Validate(app);
        var paths = await PathsAsync(serial, app, token).ConfigureAwait(false);
        var details = await RunAsync($"shell dumpsys package {app.PackageName}", serial, token).ConfigureAwait(false);
        return $"应用：{app.PackageName}\r\n用户：{app.UserId}\r\nActivity：{app.Activity}\r\nPID：{app.Pid}\r\n任务 ID：{app.TaskId}\r\n\r\nAPK 路径：\r\n{string.Join("\r\n", paths)}\r\n\r\n{details}";
    }

    public Task<string> StopAsync(string serial, RunningApp app, CancellationToken token)
    {
        Validate(app);
        return RunAsync($"shell am force-stop --user {app.UserId} {app.PackageName}", serial, token);
    }

    public async Task<string> ExportAsync(string serial, RunningApp app, string directory, CancellationToken token)
    {
        Validate(app);
        var paths = await PathsAsync(serial, app, token).ConfigureAwait(false);
        if (paths.Count == 0) throw new IOException("没有取得 APK 路径，应用可能已卸载或当前用户无权访问。");
        var output = Path.Combine(Path.GetFullPath(directory), $"{app.PackageName}-u{app.UserId}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        try
        {
            for (var index = 0; index < paths.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var path = paths[index];
                // Fixed generated names prevent a device-provided filename from escaping the output directory.
                var local = Path.Combine(output, $"{index:D2}-{(path.EndsWith("/base.apk", StringComparison.Ordinal) ? "base" : "split")}.apk");
                await RunAsync($"pull {ConsoleCommand.QuoteArgument(path)} {ConsoleCommand.QuoteArgument(local)}", serial, token).ConfigureAwait(false);
                if (!File.Exists(local) || new FileInfo(local).Length == 0) throw new IOException("导出的 APK 文件为空或不存在。");
            }
            await File.WriteAllLinesAsync(Path.Combine(output, "APK路径.txt"), paths, token).ConfigureAwait(false);
            return output;
        }
        catch (OperationCanceledException) { throw new OperationCanceledException($"已停止导出，未完成的文件保留在：{output}", token); }
        catch (Exception ex) { throw new IOException($"导出未完成，已有文件保留在：{output}\r\n{ex.Message}", ex); }
    }

    public async Task<string> RestartServerAsync(CancellationToken token)
    {
        await RunAsync("kill-server", null, token).ConfigureAwait(false);
        try
        {
            await RunAsync("start-server", null, token).ConfigureAwait(false);
            return await RunAsync("server-status", null, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new IOException($"ADB 已停止，但启动或检查失败；请重试或运行 adb start-server。{ex.Message}", ex); }
    }

    private async Task<IReadOnlyList<string>> PathsAsync(string serial, RunningApp app, CancellationToken token) =>
        RunningAppsParser.ApkPaths(await RunAsync($"shell pm path --user {app.UserId} {app.PackageName}", serial, token).ConfigureAwait(false));

    private Task<string> RunAsync(string command, string? serial, CancellationToken token) => _run(ConsoleCommand.Parse(command, serial), token);
    private static string Limit(string text) => text.Length > 2000 ? text[..2000] : text.Trim();
    private static void Validate(RunningApp app)
    {
        if (!RunningAppsParser.IsPackage(app.PackageName) || app.UserId < 0) throw new ArgumentException("无效的应用标识。");
    }
}
