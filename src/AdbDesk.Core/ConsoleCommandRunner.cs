using System.Diagnostics;

namespace AdbDesk.Core;

/// <summary>Streams text without accumulating lines or the entire command output.</summary>
public sealed class ConsoleCommandRunner(ToolPaths paths)
{
    public async Task<int> RunAsync(ConsoleCommand command, Action<string, bool> onOutput,
        TimeSpan? timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(onOutput);
        if (timeout is { } duration && duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = paths.Adb,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Close stdin immediately for ordinary commands so adb cannot inherit
            // a console or stall waiting for interactive input.
            RedirectStandardInput = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(paths.Adb))!
        };
        foreach (var argument in command.Arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        using var timeoutSource = new CancellationTokenSource();
        if (timeout is { } limit) timeoutSource.CancelAfter(limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        if (!process.Start()) throw new InvalidOperationException("无法启动 ADB 控制台命令。");
        // Cancellation kills synchronously, including when the WPF dispatcher is
        // already shutting down. Never send a separate adb kill-server command.
        using var registration = linked.Token.Register(() => KillClient(process));
        var stdout = PumpAsync(process.StandardOutput, false, command, onOutput, linked.Token);
        var stderr = PumpAsync(process.StandardError, true, command, onOutput, linked.Token);
        var stdin = WriteInputAsync(process.StandardInput, command.StandardInput, linked.Token);
        var pipes = Task.WhenAll(stdout, stderr, stdin);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await pipes.WaitAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
        catch (Exception) when (linked.IsCancellationRequested)
        {
            KillClient(process);
            await FinishAfterStopAsync(process, pipes).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            throw new TimeoutException($"ADB 命令超过 {timeout!.Value.TotalSeconds:0.#} 秒，已停止。");
        }
        catch
        {
            KillClient(process);
            linked.Cancel();
            await FinishAfterStopAsync(process, pipes).ConfigureAwait(false);
            throw;
        }
    }

    private static void KillClient(Process process)
    {
        // adb may have started the shared server as a descendant. Only stop this
        // client; killing the whole tree could disrupt every connected tool.
        try { if (!process.HasExited) process.Kill(entireProcessTree: false); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static async Task WriteInputAsync(StreamWriter writer, string? input, CancellationToken token)
    {
        try
        {
            if (input is null) return;
            await writer.WriteAsync(input.AsMemory(), token).ConfigureAwait(false);
            await writer.FlushAsync(token).ConfigureAwait(false);
        }
        finally { writer.Close(); }
    }

    private static async Task PumpAsync(StreamReader reader, bool isError, ConsoleCommand command,
        Action<string, bool> callback, CancellationToken token)
    {
        var buffer = new char[4096];
        var secret = command.StandardInput?.TrimEnd('\n');
        var pending = "";
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) break;
            var text = pending + new string(buffer, 0, count);
            pending = "";
            if (secret is not null)
            {
                text = command.Redact(text);
                // Retain only a possible partial secret at the end of a read.
                // This prevents credentials split across arbitrary pipe chunks.
                for (var length = Math.Min(secret.Length - 1, text.Length); length > 0; length--)
                {
                    if (!text.EndsWith(secret[..length], StringComparison.Ordinal)) continue;
                    pending = text[^length..];
                    text = text[..^length];
                    break;
                }
            }
            Emit(text);
        }
        Emit(pending);

        void Emit(string text)
        {
            for (var offset = 0; offset < text.Length; offset += 4096)
            {
                try { callback(text.Substring(offset, Math.Min(4096, text.Length - offset)), isError); }
                catch { /* A display failure must not stop draining the child pipes. */ }
            }
        }
    }

    private static async Task FinishAfterStopAsync(Process process, Task pipes)
    {
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(), pipes).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException or InvalidOperationException)
        {
            // Closing streams after the bounded wait releases any outstanding IO.
            process.StandardOutput.Close();
            process.StandardError.Close();
        }
        // Observe a late pipe failure if a descendant retained an inherited handle.
        _ = pipes.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
