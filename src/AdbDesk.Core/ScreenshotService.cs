using System.Diagnostics;
using System.Text;

namespace AdbDesk.Core;

public sealed class ScreenshotService
{
    private readonly ToolPaths _paths;
    private readonly TimeSpan _timeout;
    private const int MaximumBytes = 64 * 1024 * 1024;

    public ScreenshotService(ToolPaths paths) : this(paths, TimeSpan.FromSeconds(20)) { }

    internal ScreenshotService(ToolPaths paths, TimeSpan timeout)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeout = timeout;
    }

    public async Task<byte[]> CaptureAsync(string serial, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(_paths.Adb)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(_paths.Adb))!
        };
        foreach (var argument in new[] { "-s", serial, "exec-out", "screencap", "-p" })
            info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("无法启动 ADB 截图进程。");
        using var deadline = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        // Cancellation must terminate the child even while the WPF dispatcher is shutting down.
        using var killOnCancellation = linked.Token.Register(() => ProcessRunner.TryKill(process));
        var errorTask = ReadErrorAsync(process.StandardError, linked.Token);
        try
        {
            // PNG must bypass StreamReader: text decoding corrupts its binary bytes.
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > MaximumBytes)
                    throw new InvalidDataException("截图超过 64 MB，请降低手机分辨率后重试。");
                output.Write(buffer, 0, count);
            }
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var error = (await errorTask.ConfigureAwait(false)).Trim();
            linked.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrEmpty(error) ? "截图失败，请确认设备已连接并允许 USB 调试。" : $"截图失败：{error}");
            var bytes = output.ToArray();
            if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new InvalidDataException("手机没有返回有效的 PNG 截图，请确认设备连接后重试。");
            return bytes;
        }
        catch (Exception ex)
        {
            var wasCanceled = linked.IsCancellationRequested;
            ProcessRunner.TryKill(process);
            linked.Cancel();
            try { await errorTask.ConfigureAwait(false); } catch { }
            if (wasCanceled || ex is OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException($"截图超过 {_timeout.TotalSeconds:0.#} 秒，请检查手机连接后重试。");
            }
            throw;
        }
    }

    private static async Task<string> ReadErrorAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            // Continue draining even after the message limit, so the child cannot block on stderr.
            var keep = Math.Min(count, 16384 - text.Length);
            if (keep > 0) text.Append(buffer, 0, keep);
        }
        return text.ToString();
    }
}
