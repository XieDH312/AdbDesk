using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace AdbDesk.Core;

/// <summary>Owns one loopback-only proxy and its explicit-device ADB reverse mapping.</summary>
public sealed class NetworkCaptureSession : IAsyncDisposable
{
    private readonly string _mitmdump, _addonPath, _stateRoot;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<string>> _adb;
    private readonly Func<ProcessStartInfo, INetworkCaptureEngine> _engineFactory;
    private readonly NetworkCaptureProxyState _proxy;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _starting;
    private INetworkCaptureEngine? _engine;
    private string? _serial;
    private int _port;
    private bool _reverseOwned;
    private volatile bool _running;
    private bool _disposed;
    private Action<string>? _status;
    private Channel<NetworkCaptureEntry>? _entries;
    private Task? _dispatch;
    private long _lastDropNotice;
    private readonly object _diagnosticsLock = new();
    private NetworkCaptureDiagnostics _diagnostics = new("未启动", "未建立", "未设置", "尚未观察到流量", "");
    public bool IsRunning => _running;
    public string? CaptureDirectory { get; private set; }
    public NetworkCaptureDiagnostics Diagnostics { get { lock (_diagnosticsLock) return _diagnostics; } }
    public string CertificatePath => Path.Combine(_stateRoot, "network-ca", "mitmproxy-ca-cert.cer");
    public bool HasPendingRecovery(string serial) => _proxy.HasPendingRecovery(serial) || File.Exists(ReversePath(serial));

    public NetworkCaptureSession(ToolPaths paths, string mitmdump, string addonPath, string stateRoot)
        : this(mitmdump, addonPath, stateRoot, async (serial, args, token) =>
        {
            return await NetworkCaptureAdb.RunAsync(paths.Adb, new[] { "-s", serial }.Concat(args).ToArray(), token).ConfigureAwait(false);
        }, info => new NetworkCaptureProcess(info)) { }

    internal NetworkCaptureSession(string mitmdump, string addonPath, string stateRoot,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<string>> adb,
        Func<ProcessStartInfo, INetworkCaptureEngine> engineFactory)
    {
        _mitmdump = mitmdump; _addonPath = addonPath; _stateRoot = stateRoot; _adb = adb; _engineFactory = engineFactory;
        _proxy = new NetworkCaptureProxyState(stateRoot, adb);
    }

    public async Task StartAsync(string serial, int port, bool configureProxy, Action<NetworkCaptureEntry> onEntry,
        Action<string> onStatus, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentNullException.ThrowIfNull(onEntry);
        ArgumentNullException.ThrowIfNull(onStatus);
        if (serial.Length > 1024 || serial.Any(char.IsControl)) throw new ArgumentException("设备序列号无效。", nameof(serial));
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "端口须为 1024–65535。");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running) throw new InvalidOperationException("抓包会话已在运行。");
            if (HasPendingRecovery(serial)) throw new InvalidOperationException("该设备有未完成的恢复记录，请先恢复代理设置。");
            _starting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _starting.Token;
            _serial = serial; _port = port; _running = true; _status = onStatus;
            UpdateDiagnostics(_ => new("启动中", "等待引擎就绪", configureProxy ? "待设置" : "手动代理模式", "尚未观察到流量；未验证 HTTPS 信任", ""));
            try
            {
                Directory.CreateDirectory(Path.Combine(_stateRoot, "network-ca"));
                CaptureDirectory = Path.Combine(_stateRoot, "NetworkCaptures", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(CaptureDirectory);
                await NetworkCaptureProxyState.WriteDurableAsync(Path.Combine(CaptureDirectory, "session.json"), new { Serial = serial, StartedAt = DateTimeOffset.UtcNow }, token).ConfigureAwait(false);
                _entries = Channel.CreateBounded<NetworkCaptureEntry>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
                var entries = _entries;
                _dispatch = DispatchAsync(entries.Reader, onEntry);
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var errors = new StringBuilder();
                string EngineErrors() { lock (errors) return errors.ToString(); }
                _engine = _engineFactory(CreateStartInfo(port));
                await _engine.StartAsync(line =>
                {
                    if (NetworkCaptureProtocol.IsReady(line))
                    {
                        UpdateDiagnostics(d => d with { Engine = "已就绪：127.0.0.1:" + port });
                        ready.TrySetResult();
                    }
                    else if (NetworkCaptureProtocol.TryDiagnostic(line, out var diagnostic))
                    {
                        UpdateDiagnostics(d => d with { LastError = diagnostic });
                        Report(diagnostic);
                    }
                    else if (NetworkCaptureProtocol.TryParse(line, out var entry))
                    {
                        UpdateDiagnostics(d => d with { Traffic = entry!.Error.Length > 0 ? "已观察到请求失败" : entry.Finished ? "已观察到完成的 HTTP 请求" : "已观察到 HTTP 请求", LastError = entry.Error.Length > 0 ? entry.Error : d.LastError });
                        if (!entries.Writer.TryWrite(entry!))
                        {
                            var now = Environment.TickCount64;
                            if (now - Interlocked.Read(ref _lastDropNotice) > 3000)
                            {
                                Interlocked.Exchange(ref _lastDropNotice, now);
                                Report("抓包显示队列已满，部分显示事件已丢弃；独立历史存储与网络转发仍继续运行，请查看存储诊断。");
                            }
                        }
                    }
                    else
                    {
                        lock (errors) { if (errors.Length < 4096) errors.AppendLine(line[..Math.Min(line.Length, 512)]); }
                        if (!string.IsNullOrWhiteSpace(line)) UpdateDiagnostics(d => d with { LastError = line[..Math.Min(line.Length, 4096)] });
                    }
                }, token).ConfigureAwait(false);
                using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                startupTimeout.CancelAfter(TimeSpan.FromSeconds(25));
                var completed = await Task.WhenAny(ready.Task, _engine.Completion).WaitAsync(startupTimeout.Token).ConfigureAwait(false);
                if (completed != ready.Task || _engine.Completion.IsCompleted)
                    throw new IOException("抓包引擎在就绪前退出：" + EngineErrors());
                UpdateDiagnostics(d => d with { Tunnel = "建立 ADB 反向通道中" });
                if (await FindReverseTargetAsync(serial, port, token).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("所选反向端口已被占用；未修改原映射，请选择其他端口。");
                // The client can lose its response after the device has created the mapping.
                // Persist intent first, and never infer ownership from a matching port alone.
                await NetworkCaptureProxyState.WriteDurableAsync(ReversePath(serial), new ReverseJournal(serial, port, false), token).ConfigureAwait(false);
                await _adb(serial, ["reverse", "--no-rebind", "tcp:" + port, "tcp:" + port], token).ConfigureAwait(false);
                _reverseOwned = true;
                await NetworkCaptureProxyState.WriteDurableAsync(ReversePath(serial), new ReverseJournal(serial, port, true), token, overwrite: true).ConfigureAwait(false);
                UpdateDiagnostics(d => d with { Tunnel = "已建立：设备 tcp:" + port + " → 本机 tcp:" + port });
                if (configureProxy) await _proxy.ConfigureAsync(serial, port, token).ConfigureAwait(false);
                UpdateDiagnostics(d => d with { Proxy = configureProxy ? "设备全局代理已配置" : "手动代理模式；尚未验证应用代理设置" });
                token.ThrowIfCancellationRequested();
                if (_engine.Completion.IsCompleted) throw new IOException("抓包引擎意外退出：" + EngineErrors());
                Report("抓包已启动；代理仅监听本机 127.0.0.1:" + port + "。");
                var ownedEngine = _engine;
                _ = MonitorExitAsync(ownedEngine);
            }
            catch (Exception startError)
            {
                UpdateDiagnostics(d => d with { LastError = startError.Message });
                var cleanupError = await CleanupAsync().ConfigureAwait(false);
                if (cleanupError is not null) throw new AggregateException("抓包启动失败，部分设置仍待恢复。", startError, cleanupError);
                throw;
            }
            finally { _starting.Dispose(); _starting = null; }
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        try { _starting?.Cancel(); } catch (ObjectDisposedException) { }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var failure = await CleanupAsync().ConfigureAwait(false);
            if (failure is not null) throw failure;
        }
        finally { _gate.Release(); }
    }

    public async Task RecoverAsync(string serial, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_running) throw new InvalidOperationException("请先停止当前抓包再恢复设备设置。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            await _proxy.RestoreAsync(serial, timeout.Token).ConfigureAwait(false);
            await RecoverReverseAsync(serial, timeout.Token).ConfigureAwait(false);
            UpdateDiagnostics(d => d with { Tunnel = "已恢复或无需恢复", Proxy = "已恢复或无需恢复" });
        }
        finally { _gate.Release(); }
    }

    private async Task<Exception?> CleanupAsync()
    {
        if (!_running && _engine is null) return null;
        var failures = new List<Exception>();
        UpdateDiagnostics(d => d with { Engine = "停止中", Tunnel = "恢复中", Proxy = "恢复中" });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        // Restore while the proxy is still alive, so normal stop does not strand applications.
        if (_serial is { } serial)
        {
            try
            {
                await _proxy.RestoreAsync(serial, timeout.Token).ConfigureAwait(false);
                UpdateDiagnostics(d => d with { Proxy = "自动设置已恢复或无需恢复" });
            }
            catch (Exception ex) { failures.Add(ex); UpdateDiagnostics(d => d with { Proxy = "恢复失败；待人工重试", LastError = ex.Message }); }
            if (_reverseOwned)
            {
                try
                {
                    await RemoveReverseAsync(serial, _port, timeout.Token).ConfigureAwait(false);
                    UpdateDiagnostics(d => d with { Tunnel = "已移除本会话反向通道" });
                }
                catch (Exception ex) { failures.Add(ex); UpdateDiagnostics(d => d with { Tunnel = "恢复失败；保留恢复记录", LastError = ex.Message }); }
                _reverseOwned = false;
            }
            else if (File.Exists(ReversePath(serial)))
            {
                try { await RecoverReverseAsync(serial, timeout.Token).ConfigureAwait(false); UpdateDiagnostics(d => d with { Tunnel = "已恢复" }); }
                catch (Exception ex) { failures.Add(ex); UpdateDiagnostics(d => d with { Tunnel = "恢复失败；保留恢复记录", LastError = ex.Message }); }
            }
            else UpdateDiagnostics(d => d with { Tunnel = "未建立反向通道" });
        }
        if (_engine is { } engine)
        {
            try { await engine.StopAsync().ConfigureAwait(false); } catch (Exception ex) { failures.Add(ex); }
            try { await engine.DisposeAsync().ConfigureAwait(false); _engine = null; } catch (Exception ex) { failures.Add(ex); }
        }
        _entries?.Writer.TryComplete();
        if (_dispatch is not null)
        {
            try { await _dispatch.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (TimeoutException) { }
        }
        _entries = null; _dispatch = null; _running = _engine is not null; _serial = null;
        UpdateDiagnostics(d => d with { Engine = _running ? "停止失败；请重试" : "已停止", LastError = failures.Count > 0 ? failures[0].Message : d.LastError });
        Report(_running ? "抓包引擎未能退出，请重试停止。" : failures.Count == 0 ? "抓包已停止，自动设置已恢复。" : "抓包已停止，但设备设置尚未完全恢复；请连接同一设备后点恢复。" + failures[0].Message);
        return failures.Count == 0 ? null : new AggregateException("抓包清理未全部完成，已保留恢复记录。", failures);
    }

    private async Task MonitorExitAsync(INetworkCaptureEngine engine)
    {
        try { await engine.Completion.ConfigureAwait(false); } catch { }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_engine, engine)) return;
            UpdateDiagnostics(d => d with { LastError = "抓包引擎意外退出。" + d.LastError });
            var failure = await CleanupAsync().ConfigureAwait(false);
            Report("抓包引擎意外退出。" + (failure is null ? "设备设置已恢复。" : "请恢复设备代理设置。"));
        }
        finally { _gate.Release(); }
    }

    private static async Task DispatchAsync(ChannelReader<NetworkCaptureEntry> reader, Action<NetworkCaptureEntry> callback)
    {
        await foreach (var entry in reader.ReadAllAsync().ConfigureAwait(false))
            try { callback(entry); } catch { /* A UI callback cannot break forwarding. */ }
    }
    private void Report(string message) { try { _status?.Invoke(message); } catch { } }
    private void UpdateDiagnostics(Func<NetworkCaptureDiagnostics, NetworkCaptureDiagnostics> update)
    {
        lock (_diagnosticsLock) _diagnostics = update(_diagnostics);
    }
    private async Task RecoverReverseAsync(string serial, CancellationToken token)
    {
        var path = ReversePath(serial);
        if (!File.Exists(path)) return;
        var reverse = JsonSerializer.Deserialize<ReverseJournal>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
        if (reverse is null || reverse.Serial != serial || reverse.Port is < 1024 or > 65535) throw new InvalidDataException("反向端口恢复记录无效。");
        await RemoveReverseAsync(serial, reverse.Port, token, reverse.Owned).ConfigureAwait(false);
    }
    private async Task<string?> FindReverseTargetAsync(string serial, int port, CancellationToken token)
    {
        var mappings = await _adb(serial, ["reverse", "--list"], token).ConfigureAwait(false);
        var match = mappings.Split('\n').Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(p => p.Length >= 3 && p[^2] == "tcp:" + port);
        return match?[^1];
    }
    private async Task RemoveReverseAsync(string serial, int port, CancellationToken token, bool owned = true)
    {
        var target = await FindReverseTargetAsync(serial, port, token).ConfigureAwait(false);
        if (target is not null)
        {
            if (!owned) throw new InvalidOperationException($"反向端口 tcp:{port} 的创建未收到确认，无法确定归属；未自动删除。请人工核实并清除该设备上的映射后，再点击恢复。恢复记录已保留。");
            if (target != "tcp:" + port) throw new InvalidOperationException("反向端口已被其他程序修改，未覆盖它；恢复记录已保留。");
            await _adb(serial, ["reverse", "--remove", "tcp:" + port], token).ConfigureAwait(false);
        }
        File.Delete(ReversePath(serial));
    }
    private string ReversePath(string serial) => Path.Combine(_stateRoot, "network-reverse-" + NetworkCaptureProxyState.DeviceKey(serial) + ".json");
    private sealed record ReverseJournal(string Serial, int Port, bool Owned);
    private ProcessStartInfo CreateStartInfo(int port)
    {
        var info = new ProcessStartInfo { FileName = _mitmdump, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        if (CaptureDirectory is { } directory) info.Environment["ADBDESK_CAPTURE_DIRECTORY"] = directory;
        foreach (var arg in new[] { "--mode", "regular", "--listen-host", "127.0.0.1", "--listen-port", port.ToString(), "--set", "confdir=" + Path.Combine(_stateRoot, "network-ca"), "--set", "flow_detail=0", "--set", "termlog_verbosity=error", "--set", "connection_strategy=lazy", "--set", "store_streamed_bodies=false", "-s", _addonPath }) info.ArgumentList.Add(arg);
        return info;
    }
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        // The exit monitor may still be waiting on the gate. Do not dispose it underneath it.
    }
}

internal interface INetworkCaptureEngine : IAsyncDisposable
{
    Task Completion { get; }
    Task StartAsync(Action<string> line, CancellationToken cancellationToken);
    Task StopAsync();
}

internal sealed class NetworkCaptureProcess(ProcessStartInfo info) : INetworkCaptureEngine
{
    private Process? _process;
    private Task _completion = Task.CompletedTask;
    public Task Completion => _completion;
    public Task StartAsync(Action<string> line, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _process = new Process { StartInfo = info };
        if (!_process.Start()) throw new IOException("无法启动抓包引擎。");
        var stdout = ReadBoundedAsync(_process.StandardOutput, line);
        var stderr = ReadBoundedAsync(_process.StandardError, line);
        _completion = Task.WhenAll(_process.WaitForExitAsync(), stdout, stderr);
        return Task.CompletedTask;
    }
    private static async Task ReadBoundedAsync(StreamReader reader, Action<string> line)
    {
        var chunk = new char[4096];
        var current = new StringBuilder();
        var discard = false;
        int length;
        while ((length = await reader.ReadAsync(chunk).ConfigureAwait(false)) != 0)
        {
            for (var i = 0; i < length; i++)
            {
                if (chunk[i] == '\n')
                {
                    if (!discard) line(current.ToString().TrimEnd('\r'));
                    current.Clear(); discard = false;
                }
                else if (!discard)
                {
                    if (current.Length >= NetworkCaptureProtocol.MaximumLineLength) { current.Clear(); discard = true; }
                    else current.Append(chunk[i]);
                }
            }
        }
        if (!discard && current.Length > 0) line(current.ToString());
    }
    public async Task StopAsync()
    {
        if (_process is null) return;
        ProcessRunner.TryKill(_process);
        try { await _completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException ex) { throw new IOException("抓包引擎在停止后 5 秒仍未退出。", ex); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _process?.Dispose(); _process = null;
    }
}
