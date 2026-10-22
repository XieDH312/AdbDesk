using System.Text;
using AdbDesk.Core;

internal static class InstalledAppNamesTests
{
    private static readonly ToolPaths Paths = new(
        Path.GetFullPath(Path.Combine("name-test-tools", "adb.exe")),
        Path.GetFullPath(Path.Combine("name-test-tools", "scrcpy.exe")));

    // Fixed fixtures catch parsing by the last space or by rendered display width.
    public static Task ParsingAsync()
    {
        const string fixture = "scrcpy 4.1 <https://github.com/Genymobile/scrcpy>\r\n" +
            "INFO: ADB device found:\r\n" +
            "[server] INFO: List of apps:\r\n" +
            " * 设置                             com.android.settings\r\n" +
            " - My  App                        com.example.spaces\r\n" +
            " - ERROR: Reader                  com.example.error\r\n" +
            " - 一个非常长的应用名称用于验证三十字符边界和包名换行解析行为测试\r\n" +
            "                                  com.example.long\r\n" +
            " - 123456789012345678901234567890\r\n" +
            "                                  com.example.thirty\r\n" +
            " - 12345678901234567890123456789  com.looks.like.package\r\n" +
            "                                  com.example.ambiguous\r\n" +
            " - 🎮 游戏                          com.example.emoji\r\n";
        var names = InstalledAppNamesParser.Parse(fixture);
        Check(names.Count == 7 && names["com.android.settings"] == "设置", "Chinese names retain their package association");
        Check(names["com.example.spaces"] == "My  App", "internal whitespace is preserved");
        Check(names["com.example.error"] == "ERROR: Reader", "label text is not a failure diagnostic");
        Check(names["com.example.thirty"] == "123456789012345678901234567890", "exactly thirty UTF-16 units require continuation");
        Check(names["com.example.long"] == "一个非常长的应用名称用于验证三十字符边界和包名换行解析行为测试", "long Chinese names remain complete");
        Check(names["com.example.ambiguous"] == "12345678901234567890123456789  com.looks.like.package", "a package-looking label suffix is not consumed as the package");
        Check(names["com.example.emoji"] == "🎮 游戏", "surrogate pairs count as two units like Java String.length");
        Check(InstalledAppNamesParser.Parse("[server] INFO: List of apps:\n").Count == 0, "an explicitly empty list is valid");
        var app = new InstalledApp("com.example.spaces", 0, 42, false, true) { Name = "中文 名称" };
        var summary = new InstalledAppDetails("1", 42, [], "").ToSummary(app);
        Check(summary.Contains("应用名称：中文 名称") && summary.Contains("包名：com.example.spaces"), "details keep the human name and stable package identity distinct");
        return Task.CompletedTask;
    }

    public static Task InvalidParsingAsync()
    {
        const string row = " - Example                        com.example\n";
        foreach (var malformed in new[]
        {
            "", row, "INFO: List of apps:\n" + row, "[server] INFO: List of apps: unexpected\n" + row,
            "[server] INFO: List of apps:\n - short com.example\n",
            "[server] INFO: List of apps:\n" + row + "unrecognized trailing output\n",
            "[server] INFO: List of apps:\n                                  com.orphan\n",
            "[server] INFO: List of apps:\n - 123456789012345678901234567890\n",
            "[server] INFO: List of apps:\n - 123456789012345678901234567890\n   com.badindent\n",
            "[server] INFO: List of apps:\n - Example                        com.example;reboot\n",
            "[server] INFO: List of apps:\n -                                com.blank\n",
            "[server] INFO: List of apps:\n" + row + " - Different                      com.example\n",
            "[server] INFO: List of apps:\n" + row + "[server] INFO: List of apps:\n",
            "[server] INFO: List of apps:\n" + row + "[server] ERROR: enumeration failed\n"
        }) Throws<IOException>(() => InstalledAppNamesParser.Parse(malformed));
        var duplicate = InstalledAppNamesParser.Parse("[server] INFO: List of apps:\n" + row + row);
        Check(duplicate.Count == 1 && duplicate["com.example"] == "Example", "identical duplicate entries are harmless but conflicts fail");
        return Task.CompletedTask;
    }

    public static async Task TargetingAndUserScopeAsync()
    {
        var calls = new List<InstalledAppNamesCommand>();
        var service = new InstalledAppNamesService(Paths, (command, _) =>
        {
            calls.Add(command);
            return Task.FromResult(new ProcessResult(0, IsList(command) ? ListOutput : "0\n",
                IsList(command) ? "INFO: device ready\nWARN: optional capability unavailable\nscrcpy-server: 1 file pushed, 0 skipped.\n" : ""));
        });
        var result = await service.ReadAsync("phone-A; still-one-argument", 0, default);
        Check(result["com.example"] == "Example" && calls.Count == 3, "complete names publish after both user checks");
        Check(calls[0].Arguments.SequenceEqual(new[] { "-s", "phone-A; still-one-argument", "shell", "am", "get-current-user" }), "before query targets the selected serial through individual arguments");
        Check(calls[2].Arguments.SequenceEqual(calls[0].Arguments), "after query checks the same device");
        var list = calls[1];
        Check(list.Executable == Paths.Scrcpy && list.Arguments.SequenceEqual(new[] { "-s", "phone-A; still-one-argument", "--list-apps", "--verbosity=info" }), "only the list command is sent to the selected device");
        Check(list.WorkingDirectory == Path.GetDirectoryName(Paths.Scrcpy) && list.Environment["ADB"] == Paths.Adb, "scrcpy uses the bundled directory and configured ADB");
        Check(list.Environment["SCRCPY_SERVER_PATH"] == Path.Combine(list.WorkingDirectory, "scrcpy-server"), "ambient server path cannot replace the matching bundled server");
        var before = calls.Count;
        foreach (var unsupported in new[] { -1, 1, 10 })
            await ThrowsAsync<NotSupportedException>(() => service.ReadAsync("phone-A", unsupported, default));
        Check(calls.Count == before, "secondary users are rejected before all external I/O");
        await ThrowsAsync<ArgumentException>(() => service.ReadAsync("phone\nother", 0, default));
        Check(calls.Count == before, "invalid serial is rejected before I/O");

        foreach (var values in new[] { new[] { "10", "0" }, new[] { "0", "10" }, new[] { "0", "current user: 0" }, new[] { "0\n10", "0" } })
        {
            var userCalls = 0;
            var listed = false;
            var switched = new InstalledAppNamesService(Paths, (command, _) =>
            {
                if (IsList(command)) { listed = true; return Task.FromResult(new ProcessResult(0, ListOutput, "")); }
                return Task.FromResult(new ProcessResult(0, values[userCalls++], ""));
            });
            await ThrowsAsync<IOException>(() => switched.ReadAsync("phone-B", 0, default));
            if (values[0] != "0") Check(!listed, "wrong user prevents scrcpy from starting");
        }
    }

    public static async Task FailureAndCancellationAsync()
    {
        foreach (var failed in new[]
        {
            new ProcessResult(7, ListOutput, ""),
            new ProcessResult(0, ListOutput, "[server] ERROR: package manager unavailable"),
            new ProcessResult(0, ListOutput, "adb: error: device offline"),
            new ProcessResult(0, ListOutput, "Failure [permission denied]"),
            new ProcessResult(0, ListOutput + "ERROR: connection lost\n", ""),
            new ProcessResult(0, "INFO: list requested but not returned\n", "")
        })
        {
            var calls = 0;
            var service = new InstalledAppNamesService(Paths, (command, _) =>
            {
                calls++;
                return Task.FromResult(IsList(command) ? failed : new ProcessResult(0, "0", ""));
            });
            await ThrowsAsync<IOException>(() => service.ReadAsync("serial", 0, default));
            Check(calls == 2, "failed list does not publish a partial mapping or continue querying");
        }
        for (var cancelOnCall = 0; cancelOnCall <= 3; cancelOnCall++)
        {
            using var cancellation = new CancellationTokenSource();
            var calls = 0;
            var target = cancelOnCall;
            var service = new InstalledAppNamesService(Paths, (command, token) =>
            {
                Check(token.CanBeCanceled, "all child commands receive cancellation");
                if (++calls == target) cancellation.Cancel();
                return Task.FromResult(new ProcessResult(0, IsList(command) ? ListOutput : "0", ""));
            });
            if (cancelOnCall == 0) cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => service.ReadAsync("serial", 0, cancellation.Token));
            Check(calls == cancelOnCall, "cancellation prevents later I/O and name publication");
        }
        var timeout = new InstalledAppNamesService(Paths, (_, _) => Task.FromException<ProcessResult>(new TimeoutException("owned client timed out")));
        await ThrowsAsync<TimeoutException>(() => timeout.ReadAsync("serial", 0, default));
        using var activeCancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeCalls = 0;
        var active = new InstalledAppNamesService(Paths, async (command, token) =>
        {
            activeCalls++;
            if (!IsList(command)) return new ProcessResult(0, "0", "");
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new Exception("Canceled query must not finish normally");
        });
        var reading = active.ReadAsync("serial", 0, activeCancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        activeCancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(2)));
        Check(activeCalls == 2, "cancellation reaches a running query and prevents the final user check");
    }

    public static async Task BoundedUtf8Async()
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("中文 🎮\n"));
        using var reader = new StreamReader(source, new UTF8Encoding(false, true));
        Check(await InstalledAppNamesProcess.ReadBoundedAsync(reader, 100, default) == "中文 🎮\n", "pipe capture preserves UTF-8 text");
        using var overflow = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 100))));
        await ThrowsAsync<IOException>(() => InstalledAppNamesProcess.ReadBoundedAsync(overflow, 64, default));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using var unused = new StreamReader(new MemoryStream());
        await ThrowsAsync<OperationCanceledException>(() => InstalledAppNamesProcess.ReadBoundedAsync(unused, 64, canceled.Token));
    }

    private const string ListOutput = "[server] INFO: List of apps:\n - Example                        com.example\n";
    private static bool IsList(InstalledAppNamesCommand command) => command.Arguments.Contains("--list-apps");
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
