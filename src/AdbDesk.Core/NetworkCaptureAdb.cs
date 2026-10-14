using System.Diagnostics;
using System.Text;

namespace AdbDesk.Core;

/// <summary>Bounded ADB client operation. Never kills a process tree or shared ADB server.</summary>
internal static class NetworkCaptureAdb
{
    internal static async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo { FileName = executable, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new IOException("无法启动 ADB 客户端。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0 || error.Contains("Exception", StringComparison.OrdinalIgnoreCase) || error.Contains("Permission denial", StringComparison.OrdinalIgnoreCase))
                throw new IOException("ADB 命令失败：" + error[..Math.Min(error.Length, 2048)].Trim() + " " + output[..Math.Min(output.Length, 2048)].Trim());
            return output;
        }
        catch
        {
            var timedOut = timeout.IsCancellationRequested && !token.IsCancellationRequested;
            // An adb client may have started the shared server. Kill only this client.
            ProcessRunner.TryKill(process, entireProcessTree: false);
            timeout.Cancel();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            if (timedOut) throw new IOException("ADB 命令未完成；设备可能离线，恢复记录将保留。");
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var output = new StringBuilder();
        var chunk = new char[4096];
        bool exceeded = false;
        int read;
        while ((read = await reader.ReadAsync(chunk.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > 1_048_576) exceeded = true;
            else if (!exceeded) output.Append(chunk, 0, read);
        }
        if (exceeded) throw new IOException("ADB 输出超出 1 MiB 上限，已停止解析。");
        return output.ToString();
    }
}
