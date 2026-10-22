using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AdbDesk.Core;

/// <summary>Reads launchable enabled app labels for an explicitly checked owner-user snapshot.</summary>
public sealed class InstalledAppNamesService
{
    private readonly ToolPaths _paths;
    private readonly Func<InstalledAppNamesCommand, CancellationToken, Task<ProcessResult>> _execute;

    public InstalledAppNamesService(ToolPaths paths) : this(paths, InstalledAppNamesProcess.RunAsync) { }

    internal InstalledAppNamesService(ToolPaths paths,
        Func<InstalledAppNamesCommand, CancellationToken, Task<ProcessResult>> execute)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadAsync(string serial, int userId, CancellationToken token)
    {
        // scrcpy 4.1 obtains labels through FakeContext's system context. A current
        // secondary user does not prove that those labels belong to that user.
        if (userId != 0) throw new NotSupportedException("当前名称读取仅支持 Android 用户 0；其他用户保留包名显示。");
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        if (serial.Any(char.IsControl)) throw new ArgumentException("设备序列号不能包含控制字符。", nameof(serial));
        token.ThrowIfCancellationRequested();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try
        {
            var adb = Path.GetFullPath(_paths.Adb);
            var scrcpy = Path.GetFullPath(_paths.Scrcpy);
            var directory = Path.GetDirectoryName(scrcpy)!;
            var currentUser = new InstalledAppNamesCommand(adb,
                ["-s", serial, "shell", "am", "get-current-user"], Path.GetDirectoryName(adb)!,
                new Dictionary<string, string>());
            await EnsureOwnerUserAsync(currentUser, linked.Token).ConfigureAwait(false);
            var list = new InstalledAppNamesCommand(scrcpy, ["-s", serial, "--list-apps", "--verbosity=info"],
                directory, new Dictionary<string, string>
                {
                    ["ADB"] = adb,
                    // Pin the matching portable server; an ambient override could
                    // otherwise change the protocol or the user-context behavior.
                    ["SCRCPY_SERVER_PATH"] = Path.Combine(directory, "scrcpy-server")
                });
            var output = await ExecuteAsync(list, linked.Token).ConfigureAwait(false);
            var names = InstalledAppNamesParser.Parse(output);
            await EnsureOwnerUserAsync(currentUser, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return names;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            throw new TimeoutException("应用名称读取超过 30 秒，已停止本次读取。");
        }
    }

    private async Task EnsureOwnerUserAsync(InstalledAppNamesCommand command, CancellationToken token)
    {
        var output = await ExecuteAsync(command, token).ConfigureAwait(false);
        if (output.Trim() != "0") throw new IOException("读取期间 Android 用户不是 0 或已切换，请刷新应用列表后重试。");
    }

    private async Task<string> ExecuteAsync(InstalledAppNamesCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = await _execute(command, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if ((long)result.StandardOutput.Length + result.StandardError.Length > InstalledAppNamesProcess.OutputLimit)
            throw new IOException("应用名称读取输出过大，已放弃本次结果。");
        if (result.ExitCode != 0)
            throw new IOException($"名称读取工具退出码 {result.ExitCode}：{Limit(result.StandardError.Length > 0 ? result.StandardError : result.StandardOutput)}");
        InstalledAppNamesParser.RejectFailures(result.StandardError, containsAppRows: false);
        // A successful process exit alone does not imply that the server printed a list.
        InstalledAppNamesParser.RejectFailures(result.StandardOutput);
        return result.StandardOutput;
    }

    private static string Limit(string text) => text.Length <= 500 ? text.Trim() : text[..500].Trim() + "…";
}

internal sealed record InstalledAppNamesCommand(string Executable, IReadOnlyList<string> Arguments,
    string WorkingDirectory, IReadOnlyDictionary<string, string> Environment);

internal static class InstalledAppNamesParser
{
    private const string Header = "[server] INFO: List of apps:";
    private const int PackageColumn = 34;
    private static readonly string ContinuationPrefix = new(' ', PackageColumn);

    // v4.1 LogUtils.buildAppListMessage uses Java UTF-16 length, a 30-unit label
    // column, and a separate package line whenever the label has >= 30 units.
    // https://github.com/Genymobile/scrcpy/blob/v4.1/server/src/main/java/com/genymobile/scrcpy/util/LogUtils.java
    internal static IReadOnlyDictionary<string, string> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Length > InstalledAppNamesProcess.OutputLimit) throw new IOException("应用名称列表过大。");
        RejectFailures(output);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var foundHeader = false;
        string? pendingRow = null;
        using var lines = new StringReader(output);
        while (lines.ReadLine() is { } line)
        {
            if (line == Header)
            {
                if (foundHeader) throw Malformed();
                foundHeader = true;
                continue;
            }
            if (!foundHeader) continue; // banner, device selection and server push output
            if (line.StartsWith(ContinuationPrefix, StringComparison.Ordinal))
            {
                if (pendingRow is null || pendingRow.Length - 3 < 30) throw Malformed();
                Add(pendingRow[3..], line[PackageColumn..]);
                pendingRow = null;
                continue;
            }
            if (pendingRow is not null)
            {
                AddInline(pendingRow);
                pendingRow = null;
            }
            if (line.Length == 0) continue;
            if (line.StartsWith(" * ", StringComparison.Ordinal) || line.StartsWith(" - ", StringComparison.Ordinal))
            {
                if (line.Length > 8192) throw Malformed();
                pendingRow = line;
                continue;
            }
            // Host diagnostics may arrive after the server's complete list.
            if (line.StartsWith("INFO: ", StringComparison.Ordinal) || line.StartsWith("WARN: ", StringComparison.Ordinal) ||
                line.StartsWith("[server] INFO: ", StringComparison.Ordinal) || line.StartsWith("[server] WARN: ", StringComparison.Ordinal)) continue;
            throw Malformed();
        }
        if (!foundHeader) throw new IOException("工具未返回可识别的应用名称列表；保留原有包名显示。");
        if (pendingRow is not null) AddInline(pendingRow);
        return new ReadOnlyDictionary<string, string>(names);

        void AddInline(string row)
        {
            // The final label-column unit must be padding for a short label.
            if (row.Length <= PackageColumn || row[32] != ' ' || row[33] != ' ') throw Malformed();
            Add(row.Substring(3, 30).TrimEnd(' '), row[PackageColumn..]);
        }

        void Add(string name, string package)
        {
            name = name.Trim();
            if (name.Length is 0 or > 4096 || name.Any(char.IsControl) ||
                package.Length is 0 or > 255 || !RunningAppsParser.IsPackage(package)) throw Malformed();
            if (names.TryGetValue(package, out var previous))
            {
                if (!string.Equals(previous, name, StringComparison.Ordinal)) throw new IOException("应用名称列表包含同一包名的冲突名称，已放弃本次结果。");
            }
            else
            {
                if (names.Count >= 10000) throw new IOException("应用名称列表条目过多。");
                names.Add(package, name);
            }
        }
    }

    internal static void RejectFailures(string output, bool containsAppRows = true)
    {
        using var lines = new StringReader(output);
        while (lines.ReadLine() is { } line)
        {
            // App labels begin with an explicit row marker. Do not scan inside
            // labels for words such as ERROR, Failure or a package named error.
            if (containsAppRows && (line.StartsWith(" * ", StringComparison.Ordinal) || line.StartsWith(" - ", StringComparison.Ordinal) ||
                line.StartsWith(ContinuationPrefix, StringComparison.Ordinal))) continue;
            var text = line.TrimStart();
            if (text.StartsWith("[server] ", StringComparison.Ordinal)) text = text[9..];
            if (Regex.IsMatch(text,
                @"^(?:(?:ERROR|CRITICAL|FATAL|error):|adb(?:\.exe)?:\s*(?:error:|device\b|no devices\b|failed\b|cannot\b)|Failure(?:\s|\[)|Exception occurred\b|java\.[\w.]+(?:Exception|Error)\b|SecurityException\b|Permission Denial\b|device (?:offline|unauthorized)\b|no devices/emulators found\b|failed to\b|cannot\s|could not\s)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new IOException("应用名称读取失败：" + (text.Length <= 500 ? text : text[..500] + "…"));
        }
    }

    private static IOException Malformed() => new("应用名称列表格式不完整或无效，已放弃本次结果。");
}

/// <summary>Small UTF-8 reader for name queries; never starts a shell or stops the shared ADB server.</summary>
internal static class InstalledAppNamesProcess
{
    internal const int OutputLimit = 4 * 1024 * 1024;

    internal static async Task<ProcessResult> RunAsync(InstalledAppNamesCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = command.Executable,
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true)
        };
        foreach (var argument in command.Arguments) startInfo.ArgumentList.Add(argument);
        foreach (var pair in command.Environment) startInfo.Environment[pair.Key] = pair.Value;
        using var process = new Process { StartInfo = startInfo };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (!process.Start()) throw new IOException("无法启动应用名称读取工具。");
        using var registration = stop.Token.Register(() => KillClient(process));
        process.StandardInput.Close();
        var stdout = ReadPipeAsync(process.StandardOutput);
        var stderr = ReadPipeAsync(process.StandardError);
        var pipes = Task.WhenAll(stdout, stderr);
        try
        {
            await process.WaitForExitAsync(stop.Token).ConfigureAwait(false);
            await pipes.WaitAsync(stop.Token).ConfigureAwait(false);
            stop.Token.ThrowIfCancellationRequested();
            return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch
        {
            KillClient(process);
            stop.Cancel();
            try { await Task.WhenAll(process.WaitForExitAsync(), pipes).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException or InvalidOperationException or DecoderFallbackException)
            {
                process.StandardOutput.Close();
                process.StandardError.Close();
            }
            _ = pipes.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            if (stdout.IsFaulted) await stdout.ConfigureAwait(false);
            if (stderr.IsFaulted) await stderr.ConfigureAwait(false);
            throw;
        }

        async Task<string> ReadPipeAsync(StreamReader reader)
        {
            try { return await ReadBoundedAsync(reader, OutputLimit / 2, stop.Token).ConfigureAwait(false); }
            catch
            {
                try { stop.Cancel(); } catch (ObjectDisposedException) { }
                throw;
            }
        }
    }

    internal static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) return output.ToString();
            if (output.Length + count > limit) throw new IOException("应用名称读取输出过大，已停止本次读取。");
            output.Append(buffer, 0, count);
        }
    }

    private static void KillClient(Process process)
    {
        // adb can spawn the shared server as a descendant. Never kill a whole
        // process tree or issue kill-server, even when canceling scrcpy.
        try { if (!process.HasExited) process.Kill(entireProcessTree: false); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
