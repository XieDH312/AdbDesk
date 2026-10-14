using System.Diagnostics;
using System.Text;

namespace AdbDesk.Core;

public sealed record LogCaptureSession(string Folder, string Scope, string Command);
public sealed record LogCaptureResult(LogCaptureSession Session, long BytesWritten, int FileCount, bool Stopped);

public sealed class LogCaptureService(ToolPaths paths)
{
    public async Task<LogCaptureResult> CaptureAsync(LogCaptureOptions options, Action<LogCaptureLine> onLine,
        Action<LogCaptureSession> onStarted, CancellationToken token)
    {
        options.Validate();
        token.ThrowIfCancellationRequested();
        int? uid = null;
        if (options.PackageName is { } package)
        {
            var help = await QueryAsync(ConsoleCommand.Parse("logcat --help", options.Serial), token).ConfigureAwait(false);
            if (!help.Contains("--uid", StringComparison.Ordinal))
                throw new NotSupportedException("设备 logcat 不支持 --uid 应用筛选；请切换为全部设备日志。没有开始采集。");
            var output = await QueryAsync(ConsoleCommand.Parse($"shell pm list packages -U --user {options.UserId} {package}", options.Serial), token).ConfigureAwait(false);
            uid = LogCaptureOptions.ParseUid(output, package, options.UserId);
        }
        token.ThrowIfCancellationRequested();
        var command = LogCaptureOptions.BuildCommand(options.Serial, options.CrashOnly, uid);
        var folder = Path.Combine(Path.GetFullPath(options.ParentFolder), $"capture-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        var scope = uid is { } value ? $"{options.PackageName} · 用户 {options.UserId} · UID {value}" : "全部设备日志";
        var session = new LogCaptureSession(folder, scope, command.Preview);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "session.txt"),
            $"开始时间：{DateTimeOffset.Now:O}\n设备：{options.Serial}\n范围：{scope}\n命令：{command.Preview}\n" +
            "日志包含设备缓冲区已有记录及后续新记录；未清空设备日志。预览筛选不改变保存内容。\n" +
            "UID 筛选可能包含共享 UID 的其他包，不包含独立 UID 的隔离进程或系统进程代报的信息。\n",
            new UTF8Encoding(false), token).ConfigureAwait(false);
        onStarted(session);
        using var writer = new LogCaptureFileWriter(folder);
        using var errors = new LogCaptureFileWriter(Path.Combine(folder, "adb-diagnostics"));
        var decoder = new LogCaptureLineDecoder();
        var errorTail = new StringBuilder();
        var start = new ProcessStartInfo(paths.Adb)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(paths.Adb))!
        };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("无法启动日志采集进程。");
        process.StandardInput.Close();
        using var registration = token.Register(() => KillClient(process));
        // Do not cancel pipe reads on Stop: kill this client, then drain its
        // buffered stdout to disk before notifying the window that it is idle.
        var stdout = PumpAsync(process.StandardOutput, text => { writer.Write(text); decoder.Append(text, onLine); });
        var stderr = PumpAsync(process.StandardError, text =>
        {
            errors.Write(text);
            errorTail.Append(text);
            if (errorTail.Length > 3000) errorTail.Remove(0, errorTail.Length - 3000);
        });
        var pipes = Task.WhenAll(stdout, stderr);
        try
        {
            try { await process.WaitForExitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { KillClient(process); }
            try { await Task.WhenAll(process.WaitForExitAsync(), pipes).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { throw new IOException("停止后管道未在 5 秒内排空，日志尾部可能不完整。已保存文件仍在采集目录中。"); }
            decoder.Complete(onLine);
            if (!token.IsCancellationRequested && process.ExitCode != 0)
                throw new IOException($"logcat 退出码 {process.ExitCode}：{errorTail.ToString().Trim()}");
            // Flush a final unmatched surrogate before reporting byte statistics.
            writer.Dispose();
            return new(session, writer.BytesWritten, writer.FileCount, token.IsCancellationRequested);
        }
        finally
        {
            KillClient(process);
            // A timed out inherited handle must not keep a pump alive after its
            // destination writer has been disposed.
            if (!pipes.IsCompleted)
            {
                process.StandardOutput.Close(); process.StandardError.Close();
                try { await pipes.ConfigureAwait(false); } catch (Exception) { }
            }
        }

        async Task PumpAsync(StreamReader reader, Action<string> consume)
        {
            try
            {
                var buffer = new char[4096];
                while (await reader.ReadAsync(buffer).ConfigureAwait(false) is var count && count > 0)
                    consume(new string(buffer, 0, count));
            }
            catch { KillClient(process); throw; }
        }
    }

    private async Task<string> QueryAsync(ConsoleCommand command, CancellationToken token)
    {
        var text = new StringBuilder();
        var overflow = false;
        var sync = new object();
        var code = await new ConsoleCommandRunner(paths).RunAsync(command, (chunk, _) =>
        {
            lock (sync)
            {
                if (text.Length + chunk.Length > 1024 * 1024) { overflow = true; return; }
                text.Append(chunk);
            }
        }, TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        if (overflow) throw new InvalidDataException("UID 查询输出过大，未开始采集。");
        if (code != 0) throw new IOException($"日志能力或 UID 查询失败（退出码 {code}）：{text.ToString()[..Math.Min(text.Length, 1500)]}");
        return text.ToString();
    }

    private static void KillClient(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: false); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
