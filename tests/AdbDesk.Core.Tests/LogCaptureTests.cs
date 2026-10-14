using AdbDesk.Core;

internal static class LogCaptureTests
{
    public static Task ParametersAndParsingAsync()
    {
        var command = LogCaptureOptions.BuildCommand("device-1", true, 1010023);
        Check(command.Arguments.SequenceEqual(new[] { "-s", "device-1", "logcat", "-v", "threadtime", "-b", "crash", "--uid=1010023", "*:V" }), "exact device UID crash command");
        Check(!LogCaptureOptions.BuildCommand("device-1", false, null).Arguments.Contains("-c"), "never clear log buffers");
        Check(LogCaptureOptions.ParseUid("package:com.example.other uid:10023\npackage:com.example uid:1010023", "com.example", 10) == 1010023, "exact package and Android user");
        Reject(() => LogCaptureOptions.ParseUid("package:com.example uid:10023", "com.example", 10));
        Reject(() => LogCaptureOptions.ParseUid("package:com.example uid:1010023\npackage:com.example uid:1010044", "com.example", 10));
        Reject(() => new LogCaptureOptions("x", "folder", false, "com.example;reboot", 0).Validate());
        Reject(() => LogCaptureOptions.BuildCommand("x", false, -1));
        var line = LogCaptureLine.Parse("09-28 13:20:01.123  1234  1245 E AndroidRuntime: FATAL EXCEPTION: main");
        Check(line.Level == 'E' && line.Tag == "AndroidRuntime" && line.Pid == 1234, "threadtime metadata");
        Check(line.Matches('W', "runtime", "fatal") && !line.Matches('F', "", ""), "combined level Tag keyword filters");
        var parser = new LogCaptureLineDecoder();
        var lines = new List<LogCaptureLine>();
        parser.Append("09-28 13:20:01.123  1234  1245 E Android", lines.Add);
        parser.Append("Runtime: crash\r\n    at java.example", lines.Add);
        parser.Complete(lines.Add);
        Check(lines.Count == 2 && lines[1].Level == 'E' && lines[1].Tag == "AndroidRuntime", "split chunks and stack continuation metadata");
        parser.Append(new string('x', 40000), lines.Add);
        parser.Complete(lines.Add);
        Check(lines.Skip(2).All(l => l.Text.Length <= 16384), "long newline-free output is bounded in preview decoder");
        return Task.CompletedTask;
    }

    public static Task RotationAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "AdbDesk-logs-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var expected = string.Concat(Enumerable.Repeat("日志 😀\r\n", 40));
            using (var writer = new LogCaptureFileWriter(folder, 64))
            {
                // An emoji split across pipe reads must remain valid UTF-8.
                var split = expected.IndexOf("😀", StringComparison.Ordinal);
                writer.Write(expected[..(split + 1)]);
                writer.Write(expected[(split + 1)..]);
            }
            var files = Directory.GetFiles(folder, "logcat-*.txt").OrderBy(p => p, StringComparer.Ordinal).ToArray();
            Check(files.Length > 1, "rotates without overwriting earlier logs");
            Check(string.Concat(files.Select(File.ReadAllText)) == expected, "all early and late logs preserved in valid UTF-8 segments");
            Check(files.All(p => new FileInfo(p).Length <= 68), "rotation is bounded with allowance for UTF-8 codepoint");
            try { using var duplicate = new LogCaptureFileWriter(folder); throw new Exception("Existing capture was overwritten."); }
            catch (IOException) { }
            Check(string.Concat(files.Select(File.ReadAllText)) == expected, "existing capture remains untouched on filename collision");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (ArgumentException) { return; } catch (InvalidDataException) { return; }
        throw new Exception("Expected invalid capture parameters to be rejected.");
    }
}
