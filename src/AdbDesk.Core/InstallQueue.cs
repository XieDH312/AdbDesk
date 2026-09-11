using System.Collections.Concurrent;

namespace AdbDesk.Core;

public sealed class InstallQueue : IDisposable
{
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
    private readonly ToolPaths _paths;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _globalGate = new(2, 2);
    private readonly Dictionary<string, SerialQueue> _serialQueues = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, InstallWork> _work = new();
    private readonly CancellationTokenSource _disposeSource = new();
    private bool _disposed;

    public InstallQueue(ToolPaths paths) => _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public event Action<InstallUpdate>? Changed;

    public Guid Enqueue(DeviceInfo device, string apkPath)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!device.IsReady) throw new InvalidOperationException($"设备 {device.Serial} 当前不可安装：{device.State}");
        if (string.IsNullOrWhiteSpace(device.Serial)) throw new ArgumentException("设备标识不能为空。", nameof(device));
        ArgumentException.ThrowIfNullOrWhiteSpace(apkPath);
        var fullPath = Path.GetFullPath(apkPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".apk", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("仅支持普通 APK 文件。", nameof(apkPath));
        if (!File.Exists(fullPath)) throw new FileNotFoundException("APK 文件不存在。", fullPath);

        var item = new InstallWork(Guid.NewGuid(), device.Serial, fullPath);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var serialQueue = GetOrCreateSerialQueue(device.Serial);
            serialQueue.Items.Enqueue(item);
            _work[item.Id] = item;
            Publish(item, "排队中", "等待安装");
            if (serialQueue.Worker is null)
                serialQueue.Worker = Task.Run(() => RunSerialQueueAsync(serialQueue));
        }
        return item.Id;
    }

    public void Cancel(Guid id)
    {
        if (!_work.TryGetValue(id, out var item)) return;
        try { item.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { return; }
        if (Volatile.Read(ref item.Started) == 0) CompleteCanceled(item);
    }

    public void Dispose()
    {
        Task[] workers;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _disposeSource.Cancel();
            foreach (var item in _work.Values)
            {
                try { item.Cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
                if (Volatile.Read(ref item.Started) == 0) CompleteCanceled(item);
            }
            workers = _serialQueues.Values.Select(queue => queue.Worker).Where(task => task is not null).Cast<Task>().ToArray();
        }

        var workersStopped = workers.Length == 0 || Task.WaitAll(workers, TimeSpan.FromSeconds(5));
        if (workersStopped)
        {
            _globalGate.Dispose();
            _disposeSource.Dispose();
        }
    }

    private SerialQueue GetOrCreateSerialQueue(string serial)
    {
        if (_serialQueues.TryGetValue(serial, out var queue)) return queue;
        queue = new SerialQueue(serial);
        _serialQueues.Add(serial, queue);
        return queue;
    }

    private async Task RunSerialQueueAsync(SerialQueue serialQueue)
    {
        while (true)
        {
            InstallWork? item;
            lock (_sync)
            {
                if (!serialQueue.Items.TryDequeue(out item))
                {
                    serialQueue.Worker = null;
                    if (serialQueue.Items.Count == 0) _serialQueues.Remove(serialQueue.Serial);
                    return;
                }
                Volatile.Write(ref item.Started, 1);
            }

            if (item.Cancellation.IsCancellationRequested || _disposeSource.IsCancellationRequested)
            {
                CompleteCanceled(item);
                Cleanup(item);
                continue;
            }

            await RunInstallAsync(item).ConfigureAwait(false);
            Cleanup(item);
        }
    }

    private async Task RunInstallAsync(InstallWork item)
    {
        var globalEntered = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(item.Cancellation.Token, _disposeSource.Token);
        try
        {
            await _globalGate.WaitAsync(linked.Token).ConfigureAwait(false);
            globalEntered = true;
            linked.Token.ThrowIfCancellationRequested();
            Publish(item, "安装中", "正在调用 ADB 安装");

            var result = await ProcessRunner.RunAsync(
                _paths.Adb,
                ["-s", item.Serial, "install", "-r", item.ApkPath],
                InstallTimeout,
                line => PublishProgress(item, line),
                line => PublishProgress(item, line),
                linked.Token).ConfigureAwait(false);

            var hasSuccess = result.StandardOutput.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Concat(result.StandardError.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                .Any(line => line == "Success");
            if (result.ExitCode == 0 && hasSuccess)
            {
                Complete(item, "成功", "APK 安装成功");
            }
            else
            {
                var detail = string.Join(Environment.NewLine, new[] { result.StandardError.Trim(), result.StandardOutput.Trim() }
                    .Where(value => value.Length > 0));
                Complete(item, "失败", detail.Length == 0 ? $"ADB 退出代码：{result.ExitCode}" : detail);
            }
        }
        catch (OperationCanceledException)
        {
            CompleteCanceled(item);
        }
        catch (TimeoutException exception)
        {
            Complete(item, "失败", $"安装超时：{exception.Message}");
        }
        catch (Exception exception)
        {
            Complete(item, "失败", $"安装失败：{exception.Message}");
        }
        finally
        {
            if (globalEntered) _globalGate.Release();
        }
    }

    private void PublishProgress(InstallWork item, string detail)
    {
        if (Volatile.Read(ref item.Terminal) == 0) Publish(item, "安装中", detail);
    }

    private void CompleteCanceled(InstallWork item) => Complete(
        item,
        "已取消",
        "安装任务已取消；若设备已开始处理，设备端操作可能仍会完成");

    private void Complete(InstallWork item, string status, string detail)
    {
        if (Interlocked.Exchange(ref item.Terminal, 1) == 0) Publish(item, status, detail);
    }

    private void Cleanup(InstallWork item)
    {
        _work.TryRemove(item.Id, out _);
        item.Cancellation.Dispose();
    }

    private void Publish(InstallWork item, string status, string detail)
    {
        var update = new InstallUpdate(item.Id, item.Serial, item.ApkPath, status, detail);
        var handlers = Changed;
        if (handlers is null) return;
        foreach (Action<InstallUpdate> handler in handlers.GetInvocationList())
        {
            try { handler(update); }
            catch { }
        }
    }

    private sealed class SerialQueue(string serial)
    {
        public string Serial { get; } = serial;
        public Queue<InstallWork> Items { get; } = new();
        public Task? Worker { get; set; }
    }

    private sealed class InstallWork(Guid id, string serial, string apkPath)
    {
        public Guid Id { get; } = id;
        public string Serial { get; } = serial;
        public string ApkPath { get; } = apkPath;
        public CancellationTokenSource Cancellation { get; } = new();
        public int Started;
        public int Terminal;
    }
}
