using AdbDesk.Core;
using System.Text.Json;

internal static class NetworkCaptureHistoryTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "adbdesk-history-" + Guid.NewGuid());
        var directory = Path.Combine(root, "session");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "session.json"), JsonSerializer.Serialize(new { Serial = "device", StartedAt = DateTimeOffset.UtcNow }));
            for (var i = 0; i < 3; i++)
            {
                var entry = new NetworkCaptureEntry(i.ToString(), DateTimeOffset.UtcNow, "POST", "https://example.test/", 200, 1, "", "", "", "", "")
                {
                    RequestHeaderItems = [new("X-Test", "one"), new("X-Test", "two")], RequestHeadersComplete = true,
                    RequestCapture = new CapturedBody("test.body", 3, 3, true, ""), Finished = true
                };
                File.WriteAllText(Path.Combine(directory, i + ".json"), JsonSerializer.Serialize(entry));
            }
            File.WriteAllText(Path.Combine(directory, "broken.json"), "{bad");
            File.WriteAllText(Path.Combine(directory, "storage-warning.txt"), "Session storage limit reached; some requests were not saved.");
            File.WriteAllBytes(Path.Combine(directory, "test.body"), [0, 128, 255]);
            Check(NetworkCaptureHistory.List(root).Single().Serial == "device", "session survives restart");
            var page = NetworkCaptureHistory.ReadPage(directory, 1, 2);
            Check(page.Entries.Count == 1 && page.TotalCount == 3 && page.PageCount == 2 && page.Warning.Length > 0, "pagination skips invalid records with warning");
            Check(page.Warning.Contains("some requests were not saved"), "capture loss diagnostics survive history reopen");
            Reject(() => NetworkCaptureHistory.ReadEntries(directory).ToArray());
            File.Delete(Path.Combine(directory, "broken.json"));
            Check(NetworkCaptureHistory.ReadEntries(directory).Count() == 3, "lazy export sees every valid entry");
            Check(NetworkCaptureHistory.ReadBody(directory, page.Entries[0].RequestCapture!).SequenceEqual(new byte[] { 0, 128, 255 }), "raw binary survives read");
            Check(NetworkCaptureHistory.ReadBody(directory, new(null, 0, 0, true, "")).Length == 0, "empty completed bodies need no physical file");
            foreach (var name in new[] { "../outside.body", "C:\\outside.body", "sub/file.body", "test.body:stream" })
                Reject(() => NetworkCaptureHistory.ReadBody(directory, new(name, 3, 3, true, "")));
            Reject(() => NetworkCaptureHistory.ReadBody(directory, new("test.body", 2, 2, true, "")));
            Reject(() => NetworkCaptureHistory.ReadBody(directory, new("test.body", 5_000_000, 5_000_000, true, "")));
            var legacy = new NetworkCaptureEntry("legacy", DateTimeOffset.UtcNow, "GET", "https://example.test/", null, null, "", "", "", "", "");
            Check(!legacy.RequestHeadersComplete && legacy.RequestCapture is null && !legacy.Finished, "legacy completeness stays unknown");
            var malformed = legacy with { RequestHeadersComplete = true, RequestHeaderItems = null };
            Check(!NetworkCaptureProtocol.IsValid(malformed), "complete headers require structured items");
            malformed = legacy with { RequestCapture = new("test.body", 2, 3, true, "") };
            Check(!NetworkCaptureProtocol.IsValid(malformed), "saved bytes cannot exceed observed bytes");
            malformed = legacy with { RequestCapture = new("../outside.body", 3, 3, true, "") };
            Check(!NetworkCaptureProtocol.IsValid(malformed), "unsafe body references rejected at protocol boundary");
            malformed = legacy with { ResponseHttpVersion = new string('x', 33) };
            Check(!NetworkCaptureProtocol.IsValid(malformed), "response protocol field is bounded");
            var overflowDirectory = Path.Combine(root, "overflow");
            Directory.CreateDirectory(overflowDirectory);
            File.Copy(Path.Combine(directory, "session.json"), Path.Combine(overflowDirectory, "session.json"));
            var json = JsonSerializer.Serialize(legacy);
            for (var i = 0; i < 5001; i++) File.WriteAllText(Path.Combine(overflowDirectory, i + ".json"), json);
            Reject(() => NetworkCaptureHistory.ReadEntries(overflowDirectory).ToArray());
            await SessionDiagnosticsAsync(root);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task SessionDiagnosticsAsync(string root)
    {
        var engine = new FakeEngine();
        var mapping = false;
        string? storedDirectory = null;
        Task<string> Adb(string serial, IReadOnlyList<string> args, CancellationToken token)
        {
            Check(serial == "diagnostic-device", "session always uses explicit serial");
            if (args.Contains("--list")) return Task.FromResult(mapping ? "diagnostic-device tcp:18888 tcp:18888" : "");
            if (args.Contains("--no-rebind")) mapping = true;
            if (args.Contains("--remove")) mapping = false;
            return Task.FromResult("");
        }
        await using var session = new NetworkCaptureSession("fake-engine", "fake-addon", root, Adb, start =>
        {
            storedDirectory = start.Environment["ADBDESK_CAPTURE_DIRECTORY"];
            Check(storedDirectory is not null && File.Exists(Path.Combine(storedDirectory, "session.json")), "session metadata exists before engine starts");
            return engine;
        });
        await session.StartAsync("diagnostic-device", 18888, false, _ => { }, _ => { });
        Check(session.CaptureDirectory == storedDirectory && session.Diagnostics.Engine.Contains("已就绪") && session.Diagnostics.Tunnel.Contains("已建立"), "engine and tunnel phases become ready independently");
        Check(session.Diagnostics.Proxy.Contains("手动") && session.Diagnostics.Traffic.Contains("未验证"), "manual proxy and TLS trust remain unverified");
        engine.Emit("ADBDESK_CAPTURE {\"type\":\"diagnostic\",\"Error\":\"TLS handshake failed\"}");
        Check(session.Diagnostics.LastError == "TLS handshake failed", "engine diagnostics remain visible in immutable snapshot");
        await session.StopAsync();
        Check(session.Diagnostics.Engine == "已停止" && !mapping && NetworkCaptureHistory.List(Path.Combine(root, "NetworkCaptures")).Count == 1, "stop restores tunnel and preserves local session history");
    }

    private sealed class FakeEngine : INetworkCaptureEngine
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action<string>? _line;
        public Task Completion => _completion.Task;
        public Task StartAsync(Action<string> line, CancellationToken token) { _line = line; Emit("ADBDESK_CAPTURE {\"type\":\"ready\"}"); return Task.CompletedTask; }
        public void Emit(string line) => _line?.Invoke(line);
        public Task StopAsync() { _completion.TrySetResult(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException) { return; }
        throw new Exception("Unsafe or inconsistent body reference was accepted.");
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
}
