using System.Diagnostics;

namespace AdbDesk.Core;

public sealed class MirrorService : IDisposable
{
    private readonly ToolPaths _paths;
    private readonly object _sync = new();
    private readonly Dictionary<string, MirrorSession> _sessions = new(StringComparer.Ordinal);
    private bool _disposed;

    public MirrorService(ToolPaths paths) => _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public event Action<MirrorEvent>? Changed;

    public bool IsRunning(string serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return false;
        lock (_sync)
        {
            return _sessions.TryGetValue(serial, out var session) && session.IsRunning;
        }
    }

    // Resolve only the owned scrcpy process, never another window with a matching title.
    public IntPtr GetWindowHandle(string serial)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(serial, out var session) || !session.IsRunning) return IntPtr.Zero;
            try { session.Process.Refresh(); return session.Process.MainWindowHandle; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return IntPtr.Zero; }
        }
    }

    public void Start(DeviceInfo device, MirrorOptions options)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);
        if (!device.IsReady) throw new InvalidOperationException($"设备 {device.Serial} 当前不可投屏：{device.State}");
        if (string.IsNullOrWhiteSpace(device.Serial)) throw new ArgumentException("设备标识不能为空。", nameof(device));
        if (options.MaxSize <= 0) throw new ArgumentOutOfRangeException(nameof(options), "最大尺寸必须大于零。");
        if (options.MaxFps <= 0) throw new ArgumentOutOfRangeException(nameof(options), "最大帧率必须大于零。");
        options.Crop?.Validate();
        if (options.WindowWidth is < 2 or > 32768 || options.WindowHeight is < 2 or > 32768)
            throw new ArgumentOutOfRangeException(nameof(options), "窗口初始尺寸无效。");

        MirrorSession session;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessions.TryGetValue(device.Serial, out var existing) && Volatile.Read(ref existing.Completed) == 0) return;
            if (existing is not null)
            {
                _sessions.Remove(device.Serial);
                existing.Dispose();
            }

            session = new MirrorSession(device.Serial, CreateProcess(device, options));
            try
            {
                if (!session.Process.Start()) throw new InvalidOperationException("scrcpy 进程未能启动。");
                session.Started = true;
                _sessions.Add(device.Serial, session);
                session.OutputTask = PumpAsync(session, session.Process.StandardOutput);
                session.ErrorTask = PumpAsync(session, session.Process.StandardError);
            }
            catch (Exception exception)
            {
                _sessions.Remove(device.Serial);
                ProcessRunner.TryKill(session.Process);
                session.Dispose();
                Publish(new MirrorEvent(device.Serial, $"投屏启动失败：{exception.Message}", false));
                return;
            }
        }

        Publish(new MirrorEvent(device.Serial, "投屏已启动", true));
        session.MonitorTask = MonitorAsync(session);
    }

    public void Stop(string serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return;
        MirrorSession? session;
        lock (_sync)
        {
            if (!_sessions.Remove(serial, out session)) return;
            session.StopRequested = true;
        }
        ProcessRunner.TryKill(session.Process);
    }

    public void Dispose()
    {
        MirrorSession[] sessions;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
            foreach (var session in sessions) session.StopRequested = true;
        }
        foreach (var session in sessions) ProcessRunner.TryKill(session.Process);
        var monitors = sessions.Select(session => session.MonitorTask).Where(task => task is not null).Cast<Task>().ToArray();
        if (monitors.Length > 0)
        {
            try { Task.WaitAll(monitors, TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { }
        }
    }

    private Process CreateProcess(DeviceInfo device, MirrorOptions options)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _paths.Scrcpy,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(_paths.Scrcpy)) ?? Environment.CurrentDirectory,
        };
        var titleModel = string.IsNullOrWhiteSpace(device.Model) ? "Android" : device.Model;
        AddArguments(startInfo,
            "--serial", device.Serial,
            "--window-title", $"{titleModel} ({device.Serial})",
            "--max-size", options.MaxSize.ToString(),
            "--max-fps", options.MaxFps.ToString());
        if (!options.Audio) startInfo.ArgumentList.Add("--no-audio");
        if (options.FullScreen) startInfo.ArgumentList.Add("--fullscreen");
        if (options.Crop is { } crop) AddArguments(startInfo, "--crop", crop.ToScrcpyArgument());
        if (options.WindowX is int x) AddArguments(startInfo, "--window-x", x.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (options.WindowY is int y) AddArguments(startInfo, "--window-y", y.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (options.WindowWidth is int width) AddArguments(startInfo, "--window-width", width.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (options.WindowHeight is int height) AddArguments(startInfo, "--window-height", height.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.Environment["ADB"] = Path.GetFullPath(_paths.Adb);
        return new Process { StartInfo = startInfo };
    }

    private static void AddArguments(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
    }

    private async Task PumpAsync(MirrorSession session, StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length > 0) Publish(new MirrorEvent(session.Serial, line, true));
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task MonitorAsync(MirrorSession session)
    {
        try
        {
            await session.Process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(session.OutputTask!, session.ErrorTask!).ConfigureAwait(false);
            var message = session.StopRequested
                ? "投屏已停止"
                : session.Process.ExitCode == 0 ? "投屏已结束" : $"投屏进程退出（代码 {session.Process.ExitCode}）";
            Complete(session, message);
        }
        catch (Exception exception)
        {
            Complete(session, $"投屏会话异常：{exception.Message}");
        }
    }

    private void Complete(MirrorSession session, string message)
    {
        if (Interlocked.Exchange(ref session.Completed, 1) != 0) return;
        var replaced = false;
        lock (_sync)
        {
            if (_sessions.TryGetValue(session.Serial, out var current) && ReferenceEquals(current, session))
                _sessions.Remove(session.Serial);
            else if (current is not null)
                replaced = true;
        }
        session.Dispose();
        if (!replaced) Publish(new MirrorEvent(session.Serial, message, false));
    }

    private void Publish(MirrorEvent mirrorEvent)
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (Action<MirrorEvent> handler in handlers.GetInvocationList())
        {
            try { handler(mirrorEvent); }
            catch { }
        }
    }

    private sealed class MirrorSession(string serial, Process process) : IDisposable
    {
        public string Serial { get; } = serial;
        public Process Process { get; } = process;
        public bool Started { get; set; }
        public bool StopRequested { get; set; }
        public int Completed;
        public Task? OutputTask { get; set; }
        public Task? ErrorTask { get; set; }
        public Task? MonitorTask { get; set; }
        public bool IsRunning
        {
            get
            {
                if (!Started || Volatile.Read(ref Completed) != 0) return false;
                try { return !Process.HasExited; }
                catch (InvalidOperationException) { return false; }
            }
        }
        public void Dispose() => Process.Dispose();
    }
}
