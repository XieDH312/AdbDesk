using System.Collections.Concurrent;
using System.Diagnostics;
using AdbDesk.Core;

if (Environment.GetEnvironmentVariable("ADBDESK_TEST_FAKE_TOOL") == "1")
{
    await FakeTool.RunAsync(args);
    return;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("device enumeration parses states and routes through explicit adb arguments", DeviceEnumerationAsync),
    ("process runner forwards output and bounds a hung child", ProcessRunnerAsync),
    ("install queue serializes each serial and permits only two global installs", InstallConcurrencyAsync),
    ("install queue cancels queued and running work", InstallCancellationAsync),
    ("install queue requires Success and exit zero", InstallResultAsync),
    ("mirror lifecycle deduplicates, routes serial, forwards logs, and cleans failed starts", MirrorLifecycleAsync),
    ("tool discovery honors override and validates both executables", ToolDiscoveryAsync),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {test.Name}{Environment.NewLine}{exception}");
        Console.Error.WriteLine(failures[^1]);
    }
}

Console.WriteLine($"RESULT {tests.Length - failures.Count}/{tests.Length} passed");
Environment.ExitCode = failures.Count == 0 ? 0 : 1;

static async Task DeviceEnumerationAsync()
{
    using var fixture = new ToolFixture();
    Environment.SetEnvironmentVariable("ADBDESK_TEST_DEVICES", """
        * daemon not running; starting now at tcp:5037
        * daemon started successfully
        List of devices attached
        serial-A device product:husky model:Pixel_9 device:husky transport_id:1
        serial-B unauthorized usb:2-1 transport_id:2
        serial-C offline transport_id:3

        """);

    var service = new AdbService(fixture.Paths);
    var devices = await service.GetDevicesAsync();

    Equal(3, devices.Count, "device count");
    Equal(1, devices.Count(device => device.IsReady), "ready count");
    Equal("Pixel 9", devices.Single(device => device.Serial == "serial-A").Model, "decoded model");
    Equal("unauthorized", devices.Single(device => device.Serial == "serial-B").State, "unauthorized state");
    Equal("offline", devices.Single(device => device.Serial == "serial-C").State, "offline state");
    Equal("devices|-l", await fixture.ReadInvocationAsync("adb"), "adb enumeration arguments");
}

static async Task ProcessRunnerAsync()
{
    using var fixture = new ToolFixture();
    var lines = new ConcurrentQueue<string>();
    var result = await ProcessRunner.RunAsync(
        fixture.Paths.Adb,
        ["emit"],
        TimeSpan.FromSeconds(2),
        line => lines.Enqueue($"out:{line}"),
        line => lines.Enqueue($"err:{line}"));
    Equal(0, result.ExitCode, "emit exit code");
    True(lines.Contains("out:hello stdout"), "stdout callback");
    True(lines.Contains("err:hello stderr"), "stderr callback");

    using var alreadyCanceled = new CancellationTokenSource();
    alreadyCanceled.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(
        Path.Combine(fixture.DirectoryPath, "must-not-start.exe"),
        ["emit"],
        TimeSpan.FromSeconds(2),
        cancellationToken: alreadyCanceled.Token));

    var stopwatch = Stopwatch.StartNew();
    await ThrowsAsync<TimeoutException>(() => ProcessRunner.RunAsync(
        fixture.Paths.Adb,
        ["hang"],
        TimeSpan.FromMilliseconds(250)));
    True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), "timeout kills owned child promptly");
}

static async Task InstallConcurrencyAsync()
{
    using var fixture = new ToolFixture();
    fixture.SetDelay(450);
    using var queue = new InstallQueue(fixture.Paths);
    var updates = new UpdateCollector(queue);
    var apk1 = fixture.CreateApk("one.apk");
    var apk2 = fixture.CreateApk("two with spaces.apk");
    var apk3 = fixture.CreateApk("three.apk");
    var apk4 = fixture.CreateApk("four.apk");
    var ids = new[]
    {
        queue.Enqueue(Ready("serial-A"), apk1),
        queue.Enqueue(Ready("serial-A"), apk2),
        queue.Enqueue(Ready("serial-B"), apk3),
        queue.Enqueue(Ready("serial-C"), apk4),
    };

    await updates.WaitForTerminalAsync(ids, TimeSpan.FromSeconds(10));
    True(ids.All(id => updates.Last(id).Status == "成功"), "all concurrent installs succeed");

    var runs = fixture.ReadRuns("install");
    Equal(4, runs.Count, "install child count");
    True(MaximumConcurrency(runs) <= 2, "global install concurrency");
    var sameSerial = runs.Where(run => run.Serial == "serial-A").OrderBy(run => run.Start).ToArray();
    Equal(2, sameSerial.Length, "same serial child count");
    True(sameSerial[0].End <= sameSerial[1].Start, "same serial does not overlap");
    var spaced = await fixture.ReadInvocationContainingAsync("two with spaces.apk");
    True(spaced.Contains("-s|serial-A|install|-r|"), "install uses explicit serial and preserve-data reinstall arguments");
    True(spaced.EndsWith("two with spaces.apk", StringComparison.Ordinal), "apk path remains one argument");
    var serialAOrder = runs.Where(run => run.Serial == "serial-A").OrderBy(run => run.Start).Select(run => run.ApkName).ToArray();
    Equal("one.apk|two with spaces.apk", string.Join('|', serialAOrder), "same serial preserves enqueue FIFO");
}

static async Task InstallCancellationAsync()
{
    using var fixture = new ToolFixture();
    fixture.SetDelay(3000);
    using var queue = new InstallQueue(fixture.Paths);
    var updates = new UpdateCollector(queue);
    var first = queue.Enqueue(Ready("serial-A"), fixture.CreateApk("first.apk"));
    var second = queue.Enqueue(Ready("serial-B"), fixture.CreateApk("second.apk"));
    var queued = queue.Enqueue(Ready("serial-C"), fixture.CreateApk("queued.apk"));

    await updates.WaitForStatusAsync(first, "安装中", TimeSpan.FromSeconds(5));
    await updates.WaitForStatusAsync(second, "安装中", TimeSpan.FromSeconds(5));
    queue.Cancel(queued);
    await updates.WaitForStatusAsync(queued, "已取消", TimeSpan.FromSeconds(5));
    False(fixture.HasRun("install", "serial-C"), "queued cancellation never starts process");

    queue.Cancel(first);
    await updates.WaitForStatusAsync(first, "已取消", TimeSpan.FromSeconds(5));
    await fixture.WaitForProcessesToExitAsync(TimeSpan.FromSeconds(5));
    False(fixture.HasLiveProcesses(), "running cancellation kills owned child");
    queue.Cancel(second);
}

static async Task InstallResultAsync()
{
    using var fixture = new ToolFixture();
    fixture.SetDelay(50);
    using var queue = new InstallQueue(fixture.Paths);
    var updates = new UpdateCollector(queue);
    var good = queue.Enqueue(Ready("serial-A"), fixture.CreateApk("good.apk"));
    var missingSuccess = queue.Enqueue(Ready("serial-B"), fixture.CreateApk("no-success.apk"));
    var nonzero = queue.Enqueue(Ready("serial-C"), fixture.CreateApk("nonzero.apk"));
    await updates.WaitForTerminalAsync([good, missingSuccess, nonzero], TimeSpan.FromSeconds(8));
    Equal("成功", updates.Last(good).Status, "success result");
    Equal("失败", updates.Last(missingSuccess).Status, "exit zero without Success");
    Equal("失败", updates.Last(nonzero).Status, "nonzero with Success");
}

static async Task MirrorLifecycleAsync()
{
    using var fixture = new ToolFixture();
    using var mirrors = new MirrorService(fixture.Paths);
    var events = new ConcurrentQueue<MirrorEvent>();
    mirrors.Changed += events.Enqueue;

    mirrors.Start(Ready("serial-A"), new MirrorOptions(1920, 60, true));
    mirrors.Start(Ready("serial-A"), new MirrorOptions());
    await TestWait.UntilAsync(() => mirrors.IsRunning("serial-A"), TimeSpan.FromSeconds(3));
    await TestWait.UntilAsync(() => events.Any(item => item.Message.Contains("mirror stdout", StringComparison.Ordinal)), TimeSpan.FromSeconds(3));
    await TestWait.UntilAsync(() => events.Any(item => item.Message.Contains("mirror stderr", StringComparison.Ordinal)), TimeSpan.FromSeconds(3));
    Equal(1, fixture.CountStarts("mirror", "serial-A"), "deduplicated mirror process");
    var invocation = await fixture.ReadInvocationAsync("scrcpy");
    Equal("--serial|serial-A|--window-title|Test phone (serial-A)|--max-size|1920|--max-fps|60", invocation, "scrcpy arguments and identifiable title");
    Equal(Path.GetFullPath(fixture.Paths.Adb), await fixture.ReadValueAsync("mirror-adb"), "scrcpy receives selected adb path");

    mirrors.Stop("serial-A");
    await TestWait.UntilAsync(() => !mirrors.IsRunning("serial-A"), TimeSpan.FromSeconds(3));
    True(events.Any(item => item.Serial == "serial-A" && !item.IsRunning), "stop event");

    mirrors.Start(Ready("fail"), new MirrorOptions());
    await TestWait.UntilAsync(() => events.Any(item => item.Serial == "fail" && !item.IsRunning), TimeSpan.FromSeconds(4));
    var failedEvents = events.Where(item => item.Serial == "fail").ToArray();
    True(failedEvents.Any(item => item.Message.Contains("startup failed tail", StringComparison.Ordinal)), "failed startup preserves stderr tail");
    True(Array.FindIndex(failedEvents, item => item.Message.Contains("startup failed tail", StringComparison.Ordinal)) <
         Array.FindLastIndex(failedEvents, item => !item.IsRunning), "stderr tail arrives before terminal event");
    False(mirrors.IsRunning("fail"), "failed startup is removed");
    mirrors.Start(Ready("fail"), new MirrorOptions());
    await TestWait.UntilAsync(() => fixture.CountStarts("mirror", "fail") == 2, TimeSpan.FromSeconds(4));
}

static Task ToolDiscoveryAsync()
{
    using var fixture = new TempDirectory();
    var overrideDirectory = Directory.CreateDirectory(Path.Combine(fixture.Path, "override")).FullName;
    File.WriteAllText(Path.Combine(overrideDirectory, "adb.exe"), string.Empty);
    File.WriteAllText(Path.Combine(overrideDirectory, "scrcpy.exe"), string.Empty);
    var previous = Environment.GetEnvironmentVariable("ADBDESK_TOOLS");
    try
    {
        Environment.SetEnvironmentVariable("ADBDESK_TOOLS", overrideDirectory);
        var paths = ToolPaths.Discover(fixture.Path);
        Equal(Path.Combine(overrideDirectory, "adb.exe"), paths.Adb, "override adb path");
        Equal(Path.Combine(overrideDirectory, "scrcpy.exe"), paths.Scrcpy, "override scrcpy path");
        File.Delete(paths.Scrcpy);
        Throws<FileNotFoundException>(() => ToolPaths.Discover(fixture.Path));
    }
    finally
    {
        Environment.SetEnvironmentVariable("ADBDESK_TOOLS", previous);
    }
    return Task.CompletedTask;
}

static DeviceInfo Ready(string serial) => new(serial, "Test phone", "device");

static int MaximumConcurrency(IReadOnlyList<ToolRun> runs)
{
    return runs.SelectMany(run => new[] { (run.Start, 1), (run.End, -1) })
        .OrderBy(point => point.Item1)
        .ThenBy(point => point.Item2)
        .Aggregate((Current: 0, Maximum: 0), (state, point) =>
        {
            var current = state.Current + point.Item2;
            return (current, Math.Max(state.Maximum, current));
        }).Maximum;
}

static void True(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException($"Expected true: {description}");
}

static void False(bool condition, string description) => True(!condition, description);

static void Equal<T>(T expected, T actual, string description)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{description}: expected <{expected}>, actual <{actual}>");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class UpdateCollector
{
    private readonly ConcurrentDictionary<Guid, InstallUpdate> _last = new();

    public UpdateCollector(InstallQueue queue) => queue.Changed += update => _last[update.Id] = update;
    public InstallUpdate Last(Guid id) => _last[id];
    public Task WaitForStatusAsync(Guid id, string status, TimeSpan timeout) =>
        TestWait.UntilAsync(() => _last.TryGetValue(id, out var update) && update.Status == status, timeout);
    public Task WaitForTerminalAsync(IEnumerable<Guid> ids, TimeSpan timeout)
    {
        var expected = ids.ToArray();
        return TestWait.UntilAsync(() => expected.All(id => _last.TryGetValue(id, out var update) &&
            update.Status is "成功" or "失败" or "已取消"), timeout);
    }
}

sealed record ToolRun(string Serial, string ApkName, DateTime Start, DateTime End);

sealed class ToolFixture : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly string? _previousMode = Environment.GetEnvironmentVariable("ADBDESK_TEST_FAKE_TOOL");
    private readonly string? _previousDirectory = Environment.GetEnvironmentVariable("ADBDESK_TEST_DIRECTORY");
    private readonly string? _previousDelay = Environment.GetEnvironmentVariable("ADBDESK_TEST_DELAY_MS");
    public ToolPaths Paths { get; }
    public string DirectoryPath => _directory.Path;

    public ToolFixture()
    {
        Environment.SetEnvironmentVariable("ADBDESK_TEST_FAKE_TOOL", "1");
        Environment.SetEnvironmentVariable("ADBDESK_TEST_DIRECTORY", _directory.Path);
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test executable path unavailable.");
        Paths = new ToolPaths(executable, executable);
    }

    public void SetDelay(int milliseconds) => Environment.SetEnvironmentVariable("ADBDESK_TEST_DELAY_MS", milliseconds.ToString());
    public string CreateApk(string name)
    {
        var path = Path.Combine(_directory.Path, name);
        File.WriteAllText(path, "fake apk");
        return path;
    }

    public async Task<string> ReadInvocationAsync(string name)
    {
        var path = Path.Combine(_directory.Path, $"{name}-invocation.txt");
        await TestWait.UntilAsync(() => File.Exists(path), TimeSpan.FromSeconds(3));
        return (await File.ReadAllTextAsync(path)).Trim();
    }

    public async Task<string> ReadInvocationContainingAsync(string value)
    {
        string? match = null;
        await TestWait.UntilAsync(() =>
        {
            match = Directory.GetFiles(_directory.Path, "invocation-*.txt")
                .Select(File.ReadAllText).FirstOrDefault(text => text.Contains(value, StringComparison.Ordinal));
            return match is not null;
        }, TimeSpan.FromSeconds(3));
        return match!.Trim();
    }

    public async Task<string> ReadValueAsync(string name)
    {
        var path = Path.Combine(_directory.Path, $"{name}.txt");
        await TestWait.UntilAsync(() => File.Exists(path), TimeSpan.FromSeconds(3));
        return (await File.ReadAllTextAsync(path)).Trim();
    }

    public IReadOnlyList<ToolRun> ReadRuns(string kind)
    {
        var starts = Directory.GetFiles(_directory.Path, $"{kind}-*.start");
        return starts.Select(start =>
        {
            var key = Path.GetFileNameWithoutExtension(start);
            var startParts = File.ReadAllText(start).Split('|');
            var end = Path.Combine(_directory.Path, $"{key}.end");
            return new ToolRun(startParts[0], startParts[1], new DateTime(long.Parse(startParts[2]), DateTimeKind.Utc),
                new DateTime(long.Parse(File.ReadAllText(end)), DateTimeKind.Utc));
        }).ToArray();
    }

    public bool HasRun(string kind, string serial) => Directory.GetFiles(_directory.Path, $"{kind}-*.start")
        .Any(path => File.ReadAllText(path).StartsWith(serial + "|", StringComparison.Ordinal));
    public int CountStarts(string kind, string serial) => Directory.GetFiles(_directory.Path, $"{kind}-*.start")
        .Count(path => File.ReadAllText(path).StartsWith(serial + "|", StringComparison.Ordinal));
    public bool HasLiveProcesses() => Directory.GetFiles(_directory.Path, "*.start")
        .Select(path => int.Parse(Path.GetFileNameWithoutExtension(path).Split('-')[^1]))
        .Any(pid => { try { return !Process.GetProcessById(pid).HasExited; } catch { return false; } });
    public Task WaitForProcessesToExitAsync(TimeSpan timeout) => TestWait.UntilAsync(() => !HasLiveProcesses(), timeout);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ADBDESK_TEST_FAKE_TOOL", _previousMode);
        Environment.SetEnvironmentVariable("ADBDESK_TEST_DIRECTORY", _previousDirectory);
        Environment.SetEnvironmentVariable("ADBDESK_TEST_DELAY_MS", _previousDelay);
        Environment.SetEnvironmentVariable("ADBDESK_TEST_DEVICES", null);
        _directory.Dispose();
    }
}

sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AdbDesk-tests-" + Guid.NewGuid().ToString("N"));
    public TempDirectory() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}

static class TestWait
{
    public static async Task UntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Condition was not met before the test deadline.");
            await Task.Delay(20);
        }
    }
}

static class FakeTool
{
    public static async Task RunAsync(string[] arguments)
    {
        var directory = Environment.GetEnvironmentVariable("ADBDESK_TEST_DIRECTORY")!;
        if (arguments.SequenceEqual(["devices", "-l"]))
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "adb-invocation.txt"), string.Join('|', arguments));
            Console.Write(Environment.GetEnvironmentVariable("ADBDESK_TEST_DEVICES"));
            return;
        }
        if (arguments.SequenceEqual(["emit"]))
        {
            Console.WriteLine("hello stdout");
            Console.Error.WriteLine("hello stderr");
            return;
        }
        if (arguments.SequenceEqual(["hang"]))
        {
            await Task.Delay(TimeSpan.FromMinutes(2));
            return;
        }
        if (arguments.Contains("install", StringComparer.Ordinal))
        {
            await RunInstallAsync(directory, arguments);
            return;
        }
        await RunMirrorAsync(directory, arguments);
    }

    private static async Task RunInstallAsync(string directory, string[] arguments)
    {
        var serial = arguments[Array.IndexOf(arguments, "-s") + 1];
        var apk = arguments[^1];
        var key = $"install-{Environment.ProcessId}";
        await File.WriteAllTextAsync(Path.Combine(directory, $"invocation-{Environment.ProcessId}.txt"), string.Join('|', arguments));
        await File.WriteAllTextAsync(Path.Combine(directory, $"{key}.start"), $"{serial}|{Path.GetFileName(apk)}|{DateTime.UtcNow.Ticks}");
        await Task.Delay(int.TryParse(Environment.GetEnvironmentVariable("ADBDESK_TEST_DELAY_MS"), out var delay) ? delay : 100);
        if (apk.Contains("no-success", StringComparison.Ordinal)) Console.WriteLine("Finished");
        else Console.WriteLine("Success");
        await File.WriteAllTextAsync(Path.Combine(directory, $"{key}.end"), DateTime.UtcNow.Ticks.ToString());
        if (apk.Contains("nonzero", StringComparison.Ordinal)) Environment.ExitCode = 7;
    }

    private static async Task RunMirrorAsync(string directory, string[] arguments)
    {
        var serial = arguments[Array.IndexOf(arguments, "--serial") + 1];
        var key = $"mirror-{Environment.ProcessId}";
        await File.WriteAllTextAsync(Path.Combine(directory, "scrcpy-invocation.txt"), string.Join('|', arguments));
        await File.WriteAllTextAsync(Path.Combine(directory, $"invocation-{Environment.ProcessId}.txt"), string.Join('|', arguments));
        await File.WriteAllTextAsync(Path.Combine(directory, "mirror-adb.txt"), Environment.GetEnvironmentVariable("ADB") ?? string.Empty);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{key}.start"), $"{serial}|{DateTime.UtcNow.Ticks}");
        Console.WriteLine("mirror stdout");
        Console.Error.WriteLine("mirror stderr");
        if (serial == "fail")
        {
            Console.Error.WriteLine("startup failed tail");
            Environment.ExitCode = 7;
            return;
        }
        await Task.Delay(TimeSpan.FromMinutes(2));
    }
}
