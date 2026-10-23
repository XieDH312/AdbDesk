using System.Diagnostics;
using System.Text;

namespace AdbDesk.Core;

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        Action<string>? standardOutput = null,
        Action<string>? standardError = null,
        CancellationToken cancellationToken = default,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? standardInput = null,
        bool killEntireProcessTree = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(Path.GetFullPath(executable)) ?? Environment.CurrentDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException($"无法启动进程：{executable}");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var stdoutTask = ReadLinesAsync(process.StandardOutput, stdout, standardOutput, linkedSource.Token);
        var stderrTask = ReadLinesAsync(process.StandardError, stderr, standardError, linkedSource.Token);
        var stdinTask = standardInput is null
            ? Task.CompletedTask
            : WriteInputAsync(process.StandardInput, standardInput, linkedSource.Token);
        var pipes = Task.WhenAll(stdoutTask, stderrTask, stdinTask);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
            await pipes.WaitAsync(linkedSource.Token).ConfigureAwait(false);
            linkedSource.Token.ThrowIfCancellationRequested();
            return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (OperationCanceledException)
        {
            // ADB clients may start a shared server. Those callers opt out of tree termination.
            TryKill(process, killEntireProcessTree);
            await FinishAfterStopAsync(process, pipes).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            throw new TimeoutException($"进程在 {timeout.TotalSeconds:0.#} 秒内未完成：{Path.GetFileName(executable)}");
        }
        catch
        {
            TryKill(process, killEntireProcessTree);
            linkedSource.Cancel();
            await FinishAfterStopAsync(process, pipes).ConfigureAwait(false);
            throw;
        }
    }

    internal static void TryKill(Process process, bool entireProcessTree = true)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static async Task ReadLinesAsync(StreamReader reader, StringBuilder destination, Action<string>? callback,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            destination.AppendLine(line);
            if (callback is null) continue;
            try { callback(line); }
            catch { }
        }
    }

    private static async Task WriteInputAsync(StreamWriter writer, string input, CancellationToken cancellationToken)
    {
        try
        {
            await writer.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writer.Close();
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
            // A surviving shared server can retain inherited output handles. Close only our readers.
            process.StandardOutput.Close();
            process.StandardError.Close();
        }
        _ = pipes.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
