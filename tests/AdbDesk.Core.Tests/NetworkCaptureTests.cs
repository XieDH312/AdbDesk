using AdbDesk.Core;
using System.Text.Json;

internal static class NetworkCaptureTests
{
    public static Task ProtocolAsync()
    {
        var line = "ADBDESK_CAPTURE " + JsonSerializer.Serialize(new { type = "entry", Id = "abc", Time = "2026-09-28T00:00:00Z", Method = "POST", Url = "https://example.test/a", StatusCode = 201, DurationMs = 12.5, RequestHeaders = "Content-Type: application/json", RequestBody = "{\"ok\":true}", ResponseHeaders = "", ResponseBody = "done", Error = "" });
        Check(NetworkCaptureProtocol.TryParse(line, out var entry) && entry!.StatusCode == 201 && entry.RequestBody == "{\"ok\":true}", "wire entry preserves response and request fields");
        Check(!NetworkCaptureProtocol.TryParse("ADBDESK_CAPTURE {invalid}", out _), "malformed event ignored");
        Check(!NetworkCaptureProtocol.TryParse("ADBDESK_CAPTURE " + new string('x', 600_000), out _), "oversized event ignored");
        Check(!NetworkCaptureProtocol.TryParse(line.Replace("done", new string('z', 100_000)), out _), "valid JSON cannot bypass per-body preview bounds");
        Check(!NetworkCaptureProtocol.IsReady("ADBDESK_CAPTURE " + new string(' ', 1_200_000) + "{\"type\":\"ready\"}"), "oversized readiness event is rejected");
        return Task.CompletedTask;
    }

    public static async Task ProxyRecoveryAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "adbdesk-network-test-" + Guid.NewGuid());
        var settings = new Dictionary<string, string> { ["http_proxy"] = ":0", ["global_http_proxy_host"] = "", ["other_setting"] = "unchanged" };
        var calls = new List<string[]>();
        bool offline = false;
        Task<string> Adb(string serial, IReadOnlyList<string> args, CancellationToken token)
        {
            Check(serial == "device-A", "every command uses selected serial");
            calls.Add(args.ToArray());
            if (offline) throw new IOException("device offline");
            if (args.SequenceEqual(new[] { "shell", "settings", "list", "global" }))
                return Task.FromResult(string.Join("\n", settings.Select(x => x.Key + "=" + x.Value)));
            if (args[2] == "put") settings[args[4]] = args[5].Trim('\'');
            else if (args[2] == "delete") settings.Remove(args[4]);
            return Task.FromResult("");
        }
        try
        {
            var proxy = new NetworkCaptureProxyState(root, Adb);
            await proxy.ConfigureAsync("device-A", 18889, default);
            Check(settings["http_proxy"] == "127.0.0.1:18889" && proxy.HasPendingRecovery("device-A"), "journal exists before managing proxy");
            offline = true;
            try { await proxy.RestoreAsync("device-A", default); throw new Exception("expected offline failure"); } catch (IOException) { }
            Check(proxy.HasPendingRecovery("device-A"), "failed restore retains recovery journal");
            offline = false;
            await new NetworkCaptureProxyState(root, Adb).RestoreAsync("device-A", default);
            Check(settings.Count == 3 && settings["http_proxy"] == ":0" && settings["global_http_proxy_host"] == "" && settings["other_setting"] == "unchanged", "restart recovery restores exact absent and empty keys");
            Check(!proxy.HasPendingRecovery("device-A"), "verified recovery removes journal");
            await proxy.ConfigureAsync("device-A", 18889, default);
            settings["http_proxy"] = "192.168.1.5:9999";
            var before = calls.Count;
            try { await proxy.RestoreAsync("device-A", default); throw new Exception("expected conflict"); } catch (InvalidOperationException) { }
            Check(calls.Skip(before).All(a => a[2] == "list") && settings["http_proxy"] == "192.168.1.5:9999", "external edits are not overwritten");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public static async Task LifecycleAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "adbdesk-network-lifecycle-" + Guid.NewGuid());
        var calls = new List<string>();
        var engine = new FakeEngine();
        bool reverseExists = false;
        Task<string> Adb(string serial, IReadOnlyList<string> args, CancellationToken token)
        {
            calls.Add(serial + " " + string.Join(" ", args));
            if (args.Contains("--list")) return Task.FromResult(reverseExists ? "device-B tcp:18889 tcp:18889\n" : "");
            if (args.Contains("--no-rebind")) reverseExists = true;
            if (args.Contains("--remove")) reverseExists = false;
            return Task.FromResult("");
        }
        try
        {
            await using var session = new NetworkCaptureSession("mitmdump.exe", "addon.py", root, Adb, _ => engine);
            await session.StartAsync("device-B", 18889, false, _ => { }, _ => { }, default);
            Check(session.IsRunning && calls.Contains("device-B reverse --no-rebind tcp:18889 tcp:18889"), "ready process installs only explicit device reverse without replacing it");
            await session.StopAsync();
            Check(engine.Stopped && !session.IsRunning && calls.Last() == "device-B reverse --remove tcp:18889", "stop kills owned engine and removes only owned reverse");
            var refused = new NetworkCaptureSession("engine", "addon", root, (s,a,t) => throw new IOException("reverse exists"), _ => new FakeEngine());
            await using (refused)
            {
                try { await refused.StartAsync("device-B", 18889, false, _ => { }, _ => { }, default); throw new Exception("expected refused reverse"); } catch (IOException) { }
                Check(!refused.IsRunning, "failed startup cannot remain running");
            }
            var changedMapping = new List<string>();
            bool conflictCreated = false;
            await using var conflict = new NetworkCaptureSession("engine", "addon", root, (s, a, t) =>
            {
                changedMapping.Add(string.Join(" ", a));
                if (a.Contains("--no-rebind")) conflictCreated = true;
                return Task.FromResult(a.Contains("--list") && conflictCreated ? "device-C tcp:18889 tcp:9999\n" : "");
            }, _ => new FakeEngine());
            await conflict.StartAsync("device-C", 18889, false, _ => { }, _ => { }, default);
            try { await conflict.StopAsync(); throw new Exception("expected mapping conflict"); } catch (AggregateException) { }
            Check(!changedMapping.Any(c => c.Contains("--remove")) && conflict.HasPendingRecovery("device-C"), "cleanup preserves externally rebound reverse mapping and recovery evidence");

            var stoppedStatus = false;
            var exitedEngine = new FakeEngine();
            await using var exits = new NetworkCaptureSession("engine", "addon", root, Adb, _ => exitedEngine);
            await exits.StartAsync("device-D", 18889, false, _ => { }, status => { if (status.Contains("停止")) stoppedStatus = !exits.IsRunning; }, default);
            await exitedEngine.StopAsync();
            await exits.StopAsync();
            Check(stoppedStatus && !exits.IsRunning, "completion callbacks observe stopped state after engine exit and cleanup");

            foreach (var cancelled in new[] { false, true })
            {
                var ambiguousSerial = cancelled ? "ambiguous-cancel" : "ambiguous-io";
                bool mappingCreated = false;
                bool removed = false;
                await using var ambiguous = new NetworkCaptureSession("engine", "addon", root, (s, a, t) =>
                {
                    if (a.Contains("--list")) return Task.FromResult(mappingCreated ? s + " tcp:18889 tcp:18889\n" : "");
                    if (a.Contains("--no-rebind"))
                    {
                        var journal = Path.Combine(root, "network-reverse-" + NetworkCaptureProxyState.DeviceKey(s) + ".json");
                        using var pending = JsonDocument.Parse(File.ReadAllText(journal));
                        Check(!pending.RootElement.GetProperty("Owned").GetBoolean(), "attempt journal must reach disk before device mutation");
                        mappingCreated = true;
                        if (cancelled) throw new OperationCanceledException("client cancelled after device mutation");
                        throw new IOException("client disconnected after device mutation");
                    }
                    if (a.Contains("--remove")) removed = true;
                    return Task.FromResult("");
                }, _ => new FakeEngine());
                try { await ambiguous.StartAsync(ambiguousSerial, 18889, false, _ => { }, _ => { }, default); throw new Exception("expected ambiguous creation"); }
                catch (AggregateException) { }
                Check(!removed && ambiguous.HasPendingRecovery(ambiguousSerial), "ambiguous creation preserves mapping and durable recovery evidence");
                try { await ambiguous.RecoverAsync(ambiguousSerial); throw new Exception("expected ownership ambiguity"); }
                catch (InvalidOperationException) { }
                Check(!removed && ambiguous.HasPendingRecovery(ambiguousSerial), "restart recovery cannot delete unacknowledged mapping");
                mappingCreated = false; // Explicit operator cleanup, outside this session.
                await ambiguous.RecoverAsync(ambiguousSerial);
                Check(!removed && !ambiguous.HasPendingRecovery(ambiguousSerial), "absent ambiguous mapping allows journal-only recovery");
            }

            bool triedRebind = false;
            await using var occupied = new NetworkCaptureSession("engine", "addon", root, (s, a, t) =>
            {
                if (a.Contains("--no-rebind")) triedRebind = true;
                return Task.FromResult("device-E tcp:18889 tcp:7777\n");
            }, _ => new FakeEngine());
            try { await occupied.StartAsync("device-E", 18889, false, _ => { }, _ => { }, default); throw new Exception("expected occupied port"); }
            catch (InvalidOperationException) { }
            Check(!triedRebind && !occupied.HasPendingRecovery("device-E"), "preexisting mapping is rejected before mutation or journal creation");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class FakeEngine : INetworkCaptureEngine
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Stopped { get; private set; }
        public Task Completion => _exit.Task;
        public Task StartAsync(Action<string> line, CancellationToken token) { line("ADBDESK_CAPTURE {\"type\":\"ready\"}"); return Task.CompletedTask; }
        public Task StopAsync() { Stopped = true; _exit.TrySetResult(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
}
