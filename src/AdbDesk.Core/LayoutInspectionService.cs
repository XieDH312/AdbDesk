using System.Text;

namespace AdbDesk.Core;

public sealed record LayoutSnapshot(string Serial, string? Xml, LayoutHierarchy? Hierarchy, byte[]? Png,
    DateTimeOffset StartedAt, DateTimeOffset FinishedAt, IReadOnlyList<string> Warnings);

public sealed class LayoutInspectionService
{
    private readonly Func<ConsoleCommand, CancellationToken, Task<string>> _run;
    private readonly Func<string, CancellationToken, Task<byte[]>> _capture;

    public LayoutInspectionService(ToolPaths paths)
    {
        _capture = new ScreenshotService(paths).CaptureAsync;
        _run = async (command, token) =>
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var sync = new object();
            var overflow = false;
            var code = await new ConsoleCommandRunner(paths).RunAsync(command, (text, error) =>
            {
                lock (sync)
                {
                    var buffer = error ? stderr : stdout;
                    if (buffer.Length + text.Length > LayoutHierarchy.MaximumCharacters) { overflow = true; return; }
                    buffer.Append(text);
                }
            }, TimeSpan.FromSeconds(25), token).ConfigureAwait(false);
            if (overflow) throw new InvalidDataException("布局命令输出超过 4 MB 字符限制。");
            if (code != 0 || stderr.ToString().Contains("ERROR:", StringComparison.OrdinalIgnoreCase))
            {
                var error = stderr.Length > 0 ? stderr.ToString() : stdout.ToString();
                if (error.Length > 1500) error = error[..1500];
                throw new IOException($"ADB 退出码 {code}：{error.Trim()}");
            }
            return stdout.ToString();
        };
    }

    internal LayoutInspectionService(Func<ConsoleCommand, CancellationToken, Task<string>> run,
        Func<string, CancellationToken, Task<byte[]>> capture) { _run = run; _capture = capture; }

    public async Task<LayoutSnapshot> CaptureAsync(string serial, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        token.ThrowIfCancellationRequested();
        var started = DateTimeOffset.Now;
        var warnings = new List<string>();
        var path = $"/data/local/tmp/adbdesk-layout-{Guid.NewGuid():N}.xml";
        string? xml = null;
        LayoutHierarchy? hierarchy = null;
        byte[]? png = null;
        try
        {
            await Run($"shell uiautomator dump {path}", token).ConfigureAwait(false);
            xml = await Run($"shell cat {path}", token).ConfigureAwait(false);
            hierarchy = LayoutHierarchy.Parse(xml);
            if (hierarchy.Nodes.Count == 0) warnings.Add("布局读取成功，但当前窗口没有可访问的控件节点。");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { xml = null; warnings.Add("控件层级读取失败：" + ex.Message); }
        finally
        {
            // This generated path is unique to this capture. Cleanup is bounded
            // independently so cancellation does not leave a normal dump behind.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await Run($"shell rm -f {path}", cleanup.Token).ConfigureAwait(false); }
            catch { warnings.Add($"手机临时文件未能清理：{path}"); }
        }
        token.ThrowIfCancellationRequested();
        try { png = await _capture(serial, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { warnings.Add("截图读取失败：" + ex.Message); }
        token.ThrowIfCancellationRequested();
        if (hierarchy is null && png is null) throw new IOException(string.Join("\r\n", warnings));
        return new(serial, xml, hierarchy, png, started, DateTimeOffset.Now, warnings);

        Task<string> Run(string command, CancellationToken cancellation) => _run(ConsoleCommand.Parse(command, serial), cancellation);
    }
}
