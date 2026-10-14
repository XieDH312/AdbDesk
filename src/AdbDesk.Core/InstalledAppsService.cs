using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AdbDesk.Core;

public sealed record InstalledApp(string PackageName, int UserId, long VersionCode, bool IsSystem, bool IsEnabled)
{
    public string Kind => IsSystem ? "系统" : "第三方";
    public string EnabledState => IsEnabled ? "已启用" : "已停用";
}

public sealed record InstalledAppsSnapshot(int UserId, IReadOnlyList<InstalledApp> Apps, DateTimeOffset CapturedAt);
public sealed record InstalledAppDetails(string? VersionName, long? VersionCode, IReadOnlyList<string> ApkPaths, string RawDetails);

/// <summary>Installed packages for a fixed device and its explicitly identified Android user.</summary>
public sealed class InstalledAppsService
{
    private const int OutputLimit = 4 * 1024 * 1024;
    private readonly Func<ConsoleCommand, CancellationToken, Task<string>> _execute;
    private readonly RunningAppsService _runningApps;

    public InstalledAppsService(ToolPaths paths)
    {
        _execute = async (command, token) =>
        {
            var output = new StringBuilder();
            var error = new StringBuilder();
            var gate = new object();
            var overflow = false;
            var exit = await new ConsoleCommandRunner(paths).RunAsync(command, (text, stderr) =>
            {
                lock (gate)
                {
                    if (output.Length + error.Length + text.Length > OutputLimit) { overflow = true; return; }
                    (stderr ? error : output).Append(text);
                }
            }, TimeSpan.FromSeconds(command.Arguments.Contains("pull") ? 180 : 30), token).ConfigureAwait(false);
            if (overflow) throw new IOException("设备输出超过 4 MB，请在 ADB 控制台检查原始结果。");
            if (exit != 0) throw new IOException($"ADB 退出码 {exit}：{Limit(error.Length > 0 ? error.ToString() : output.ToString())}");
            RejectFailure(error.ToString());
            return output.ToString();
        };
        _runningApps = new RunningAppsService(ExecuteAsync);
    }

    internal InstalledAppsService(Func<ConsoleCommand, CancellationToken, Task<string>> execute)
    {
        _execute = execute;
        _runningApps = new RunningAppsService(ExecuteAsync);
    }

    public async Task<InstalledAppsSnapshot> LoadAsync(string serial, CancellationToken token)
    {
        var user = await CurrentUserAsync(serial, token).ConfigureAwait(false);
        var versions = ParsePackages(await RunAsync($"shell pm list packages --user {user} --show-versioncode", serial, token).ConfigureAwait(false), true);
        if (versions.Count == 0) throw new IOException("未取得已安装应用；设备输出为空，无法确认应用列表。");
        var system = ParsePackages(await RunAsync($"shell pm list packages --user {user} -s", serial, token).ConfigureAwait(false), false);
        var disabled = ParsePackages(await RunAsync($"shell pm list packages --user {user} -d", serial, token).ConfigureAwait(false), false);
        var enabled = ParsePackages(await RunAsync($"shell pm list packages --user {user} -e", serial, token).ConfigureAwait(false), false);
        // A changing package set must not silently produce an incorrect type/enabled state.
        if (system.Keys.Concat(disabled.Keys).Concat(enabled.Keys).Any(p => !versions.ContainsKey(p)) ||
            versions.Keys.Any(p => enabled.ContainsKey(p) == disabled.ContainsKey(p)))
            throw new IOException("读取期间应用列表或启用状态发生变化，请刷新后重试。");
        await EnsureUserAsync(serial, user, token).ConfigureAwait(false);
        return new(user, versions.Select(p => new InstalledApp(p.Key, user, p.Value, system.ContainsKey(p.Key), enabled.ContainsKey(p.Key)))
            .OrderBy(p => p.PackageName, StringComparer.OrdinalIgnoreCase).ToArray(), DateTimeOffset.Now);
    }

    public async Task<InstalledAppDetails> DetailsAsync(string serial, InstalledApp app, CancellationToken token)
    {
        Validate(app);
        await EnsureUserAsync(serial, app.UserId, token).ConfigureAwait(false);
        var paths = RunningAppsParser.ApkPaths(await RunAsync($"shell pm path --user {app.UserId} {app.PackageName}", serial, token).ConfigureAwait(false));
        if (paths.Count == 0) throw new IOException("此用户没有可读取的 APK 路径，应用可能已卸载。");
        // dumpsys package exposes global package metadata and has no portable --user option.
        // Installation is checked above using pm path --user; raw output is labelled in the UI.
        var raw = await RunAsync($"shell dumpsys package {app.PackageName}", serial, token).ConfigureAwait(false);
        var block = Regex.Match(raw, @"(?ms)^[ \t]*Package \[" + Regex.Escape(app.PackageName) + @"\][^\r\n]*.*?(?=^[ \t]*Package \[|\z)");
        if (!block.Success) throw new IOException("无法识别包详情，应用可能已卸载或系统不支持此输出格式。");
        var name = Regex.Match(block.Value, @"(?m)^[ \t]*versionName=(?<value>[^\r\n]*)");
        var version = Regex.Match(block.Value, @"\bversionCode=(?<value>[0-9]+)\b");
        long? code = version.Success && long.TryParse(version.Groups["value"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
        await EnsureUserAsync(serial, app.UserId, token).ConfigureAwait(false);
        return new(name.Success ? name.Groups["value"].Value.Trim() : null, code, paths, raw);
    }

    public async Task<string> LaunchAsync(string serial, InstalledApp app, CancellationToken token)
    {
        Validate(app);
        await EnsureUserAsync(serial, app.UserId, token).ConfigureAwait(false);
        var resolved = await RunAsync($"shell pm resolve-activity --brief --user {app.UserId} -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -p {app.PackageName}", serial, token).ConfigureAwait(false);
        var components = resolved.Split('\n').Select(l => l.Trim()).Where(l => Regex.IsMatch(l,
            "^" + Regex.Escape(app.PackageName) + @"/[A-Za-z0-9_.$]+$", RegexOptions.CultureInvariant)).Distinct().ToArray();
        if (components.Length != 1) throw new IOException("未找到唯一的桌面启动入口；该应用可能没有启动页面、已停用或系统不支持此查询。");
        await EnsureUserAsync(serial, app.UserId, token).ConfigureAwait(false);
        // Single quotes preserve the '$' in nested Android activity class names.
        var output = await RunAsync($"shell am start --user {app.UserId} -n '{components[0]}' -a android.intent.action.MAIN -c android.intent.category.LAUNCHER", serial, token).ConfigureAwait(false);
        if (!output.Contains("Starting: Intent", StringComparison.Ordinal) && !output.Contains("brought to the front", StringComparison.OrdinalIgnoreCase))
            throw new IOException("系统未确认启动结果：" + Limit(output));
        return output;
    }

    public async Task<string> StopAsync(string serial, InstalledApp app, CancellationToken token)
    {
        Validate(app);
        await EnsureUserAsync(serial, app.UserId, token).ConfigureAwait(false);
        var output = await _runningApps.StopAsync(serial, AsRunning(app), token).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(output)) throw new IOException("无法确认强制停止结果：" + Limit(output));
        return output;
    }

    public Task<string> ClearDataAsync(string serial, InstalledApp app, CancellationToken token) => MutateAsync("clear", serial, app, token);
    public Task<string> UninstallAsync(string serial, InstalledApp app, CancellationToken token) => MutateAsync("uninstall", serial, app, token);

    public async Task<string> ExportAsync(string serial, InstalledApp app, string directory, CancellationToken token)
    {
        Validate(app);
        await EnsureUserAsync(serial, app.UserId, token).ConfigureAwait(false);
        return await _runningApps.ExportAsync(serial, AsRunning(app), directory, token).ConfigureAwait(false);
    }

    private async Task<string> MutateAsync(string verb, string serial, InstalledApp app, CancellationToken token)
    {
        Validate(app);
        await EnsureUserAsync(serial, app.UserId, token).ConfigureAwait(false);
        var output = await RunAsync($"shell pm {verb} --user {app.UserId} {app.PackageName}", serial, token).ConfigureAwait(false);
        if (!string.Equals(output.Trim(), "Success", StringComparison.Ordinal))
            throw new IOException("系统未确认操作成功：" + Limit(output));
        return output;
    }

    private async Task<int> CurrentUserAsync(string serial, CancellationToken token)
    {
        var output = (await RunAsync("shell am get-current-user", serial, token).ConfigureAwait(false)).Trim();
        if (!Regex.IsMatch(output, "^[0-9]+$", RegexOptions.CultureInvariant) ||
            !int.TryParse(output, NumberStyles.None, CultureInfo.InvariantCulture, out var user))
            throw new IOException("无法确认当前 Android 用户：" + Limit(output));
        return user;
    }
    private async Task EnsureUserAsync(string serial, int expected, CancellationToken token)
    {
        if (await CurrentUserAsync(serial, token).ConfigureAwait(false) != expected)
            throw new IOException("手机的当前 Android 用户已切换，请刷新应用列表后重试。");
    }
    private static Dictionary<string, long> ParsePackages(string output, bool withVersion)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            var match = Regex.Match(line, withVersion ? @"^package:(?<name>\S+) versionCode:(?<code>[0-9]+)$" : @"^package:(?<name>\S+)$");
            long code = 0;
            if (!match.Success || !RunningAppsParser.IsPackage(match.Groups["name"].Value) ||
                (withVersion && !long.TryParse(match.Groups["code"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out code)) ||
                !result.TryAdd(match.Groups["name"].Value, code))
                throw new IOException("无法识别已安装应用输出：" + Limit(line));
        }
        return result;
    }
    private Task<string> RunAsync(string command, string serial, CancellationToken token) => ExecuteAsync(ConsoleCommand.Parse(command, serial), token);
    private async Task<string> ExecuteAsync(ConsoleCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = await _execute(command, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (result.Length > OutputLimit) throw new IOException("设备输出超过 4 MB。");
        RejectFailure(result);
        return result;
    }
    private static void RejectFailure(string output)
    {
        if (Regex.IsMatch(output, @"(?im)^\s*(?:Fail(?:ure|ed)\b|Error\b|Exception\b|SecurityException\b|java\.[\w.]*Exception\b|Permission Denial\b|Unknown (?:command|option)\b|Can't find service\b|adb: error\b)") ||
            output.Contains("Permission Denial", StringComparison.OrdinalIgnoreCase))
            throw new IOException(Limit(output));
    }
    private static RunningApp AsRunning(InstalledApp app) => new(app.PackageName, app.UserId, "", "");
    private static void Validate(InstalledApp app)
    {
        if (!RunningAppsParser.IsPackage(app.PackageName) || app.UserId < 0) throw new ArgumentException("无效的应用包名或 Android 用户。");
    }
    private static string Limit(string text) => text.Length > 2000 ? text[..2000] : text.Trim();
}
