using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AdbDesk.Core;

if (Environment.GetEnvironmentVariable("ADBDESK_TEST_FAKE_TOOL") == "1")
{
    await FakeTool.RunAsync(args);
    return;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("wireless discovery parses TLS legacy and pairing endpoints", WirelessDiscoveryTests.ParsingAsync),
    ("wireless discovery requires exact network transport matches", WirelessDiscoveryTests.MatchingAsync),
    ("wireless discovery names survive port changes without IP-only identity", WirelessDiscoveryTests.NamesAsync),
    ("wireless discovery name cache is bounded and recoverable", WirelessDiscoveryTests.CacheBoundsAsync),
    ("wireless discovery reports discovery and partial name errors", WirelessDiscoveryTests.FailureAsync),
    ("wireless discovery cancellation and timeout stop bounded client reads", WirelessDiscoveryTests.CancellationAsync),
    ("mirror toolbar docks outside the phone frame or uses hidden edge reveal", MirrorToolbarPlacementTests.PlacementAsync),
    ("mirror toolbar auto-hide preserves hover grace and owned help without first-frame overlays", MirrorToolbarPlacementTests.VisibilityAsync),
    ("mirror screen buttons target one owned window and release modifiers on failed submission", MirrorScreenControlTests.CommandsAsync),
    ("mirror height width and custom display preserve aspect offsets and reject stale selections", MirrorDisplayTests.SettingsGeometryAsync),
    ("mirror display preferences preserve independent device overrides across restart and deletion", MirrorDisplayTests.SettingsStoreAsync),
    ("mirror fill preserves aspect ratio and converts current orientation into natural crop coordinates", MirrorDisplayTests.GeometryAsync),
    ("mirror fullscreen and crop arguments preserve device scope and cancel geometry reads", MirrorDisplayTests.ArgumentsAsync),
    ("phone Wi-Fi addresses remain separate from VPN interfaces", DeviceNetworkTests.AddressesAsync),
    ("Windows hotspot and same-device mDNS candidates match the LAN", DeviceNetworkTests.HotspotAsync),
    ("wireless mode reads fresh physical Wi-Fi addresses rather than VPN", DeviceNetworkTests.ServiceAsync),
    ("explicit Wi-Fi correction preserves pairing and connection ports", DeviceNetworkTests.OverrideAsync),
    ("TCP diagnostics distinguish port reachability from ADB readiness", DeviceNetworkTests.DiagnosticsAsync),
    ("ADB transport snapshot identifies multiple USB devices by serial and transport ID", AdbTransportTests.ParseAsync),
    ("ADB transport framing reads one bounded binary snapshot", AdbTransportTests.FrameAsync),
    ("ADB transport probe honors server endpoint overrides without silent fallback", AdbTransportTests.EndpointAsync),
    ("multiple Windows USB transports remain distinct from socket connections during wireless actions", AdbTransportTests.MultipleUsbAsync),
    ("ADB transport refresh bypasses stale device property cache", AdbTransportTests.RefreshAsync),
    ("wireless confirmation rechecks authoritative per-device USB evidence", AdbTransportTests.ConfirmationAsync),
    ("ADB transport probe stops its own client on timeout and cancellation", AdbTransportTests.CancellationAsync),
    ("command library presets preserve device scope and searchable metadata", ConsoleLibraryTests.CatalogAsync),
    ("command library validates parameters quotes once and confirms mutations", ConsoleLibraryTests.ParametersAsync),
    ("command library persists bounded ID-only favorites and recent selections", ConsoleLibraryTests.PreferencesAsync),
    ("installed apps preserve current Android user and numeric version details", InstalledAppsTests.SnapshotAsync),
    ("installed app actions validate package user and launch component", InstalledAppsTests.ActionsAsync),
    ("installed apps reject false success and stop canceled operations", InstalledAppsTests.FailureAndCancellationAsync),
    ("installed app details distinguish package versions and user scope", InstalledAppsTests.DetailsAsync),
    ("installed app details keep requested install and selected-user runtime permissions distinct", InstalledAppsTests.DetailsUserScopeAsync),
    ("installed app details exclude hidden duplicates and preserve missing values", InstalledAppsTests.DetailsMissingAndDuplicateBlocksAsync),
    ("installed app settings use the selected serial user and package without a launcher", InstalledAppsTests.SettingsAsync),
    ("installed app details reject failures and canceled reads", InstalledAppsTests.DetailsFailureAndCancellationAsync),
    ("installed app names parse Chinese whitespace and scrcpy long-name continuations", InstalledAppNamesTests.ParsingAsync),
    ("installed app names reject malformed incomplete and conflicting lists", InstalledAppNamesTests.InvalidParsingAsync),
    ("installed app names target bundled tools and verify owner-user scope before publishing", InstalledAppNamesTests.TargetingAndUserScopeAsync),
    ("installed app names reject failed commands and canceled reads without partial results", InstalledAppNamesTests.FailureAndCancellationAsync),
    ("installed app name capture preserves UTF8 and bounds output", InstalledAppNamesTests.BoundedUtf8Async),
    ("slow update metadata retains a valid signed higher code", UpdateVersionCodeTests.SlowNetworkAsync),
    ("update request diagnostics identify failing steps and preserve caller cancellation", UpdateVersionCodeTests.RequestDiagnosticsAsync),
    ("numeric updates order signed version codes after channel filtering", UpdateVersionCodeTests.DiscoveryAsync),
    ("numeric update metadata is authenticated and strictly parsed", UpdateVersionCodeTests.MetadataAsync),
    ("numeric update downloads recheck retained version identity", UpdateVersionCodeTests.DownloadAsync),
    ("numeric update installation rejects downgrade and altered metadata", UpdateVersionCodeTests.InstallerPolicyAsync),
    ("update versions follow SemVer including numeric prereleases", UpdateDownloadTests.VersionsAsync),
    ("update manifests require valid signatures and safe complete file lists", UpdateDownloadTests.ManifestAsync),
    ("update discovery validates repository channels versions and redirects", UpdateDownloadTests.DiscoveryAsync),
    ("update downloads enforce sizes hashes cancellation and owned cleanup", UpdateDownloadTests.DownloadAsync),
    ("update installer preserves unknown files and skips unchanged locked tools", UpdateInstallTests.TransactionAsync),
    ("update installer rolls back replacements and newly created files", UpdateInstallTests.RollbackAsync),
    ("update installer never relaunches an incompletely restored installation", UpdateInstallTests.IncompleteRollbackAsync),
    ("update installer rejects locked changed files and corrupt payloads", UpdateInstallTests.LockedAndCorruptAsync),
    ("update installer rejects unsafe or inconsistent ZIP entries", UpdateInstallTests.ZipValidationAsync),
    ("log capture validates targets and parses UID and preview lines", LogCaptureTests.ParametersAndParsingAsync),
    ("log capture rotates UTF8 files without discarding complete logs", LogCaptureTests.RotationAsync),
    ("network history pages bounded metadata and validates body paths", NetworkCaptureHistoryTests.RunAsync),
    ("capture curl rejects incomplete and unsafe requests", NetworkCaptureExportTests.CurlSafetyAsync),
    ("capture HAR preserves complete data and marks missing fields", NetworkCaptureExportTests.HarFidelityAsync),
    ("capture bodies decode within bounds and filters match precisely", NetworkCaptureExportTests.BodyAndFiltersAsync),
    ("network proxy restores exact baseline and preserves external changes", NetworkCaptureTests.ProxyRecoveryAsync),
    ("network protocol bounds previews and ignores invalid events", NetworkCaptureTests.ProtocolAsync),
    ("network capture rolls back startup and owns only its reverse channel", NetworkCaptureTests.LifecycleAsync),
    ("layout hierarchy preserves attributes and rejects unsafe or oversized nesting", LayoutTests.ParserAsync),
    ("layout capture targets device and cleans temporary files on failure and cancellation", LayoutTests.CaptureAsync),
    ("toolbox parses foreground recent tasks and process ownership separately", ToolboxTests.ParseAsync),
    ("toolbox keeps device targeting partial failures and ordered server restart", ToolboxTests.ServiceAsync),
    ("console commands preserve Android quoting and require the selected target", ConsoleParsingAsync),
    ("console pairing and conservative history never retain credentials", ConsolePrivacyAsync),
    ("console history persists bounded MRU and discards stale unsafe entries", ConsoleHistoryAsync),
    ("console runner streams bounded chunks from both pipes and preserves nonzero exit", ConsoleStreamingAsync),
    ("console runner distinguishes timeout and cancellation and kills its own child", ConsoleCancellationAsync),
    ("console cancellation preserves a shared server spawned by its client", () => SharedServerSurvivalAsync(false)),
    ("screenshot cancellation preserves a shared server spawned by its client", () => SharedServerSurvivalAsync(true)),
    ("connection history persists recent unique endpoints and clears across restarts", ConnectionHistoryAsync),
    ("connection history recovers corrupt data and rejects invalid new endpoints", ConnectionHistoryRecoveryAsync),
    ("connection history removes only the chosen endpoint and persists the remaining order", ConnectionHistoryRemovalAsync),
    ("screenshot preserves PNG bytes, drains stderr and selects exactly one device", ScreenshotBytesAsync),
    ("screenshot rejects command errors empty output and non-PNG bytes", ScreenshotFailuresAsync),
    ("screenshot timeout and cancellation terminate their own child", ScreenshotCancellationAsync),
    ("LAN preflight compares actual masks and all physical adapters", LocalSubnetAsync),
    ("declined or canceled endpoint confirmation never submits network mutations", NetworkPreflightAsync),
    ("wireless disconnect targets one transport and verifies removal without affecting other devices", WirelessDisconnectAsync),
    ("device enumeration parses states and routes through explicit adb arguments", DeviceEnumerationAsync),
    ("device enumeration enriches names versions and authoritative transports", DeviceDetailsAsync),
    ("USB-only selector confirms Windows devices with unknown devpath and rechecks before mutation", UnknownUsbPathAsync),
    ("device detail probes are cached bounded and preserve partial devices", DeviceDetailCacheAsync),
    ("wireless enable requires a freshly authorized physical USB transport and Wi-Fi IPv4", WirelessPreconditionsAsync),
    ("wireless enable retries connect and verifies endpoint readiness", WirelessEnableAsync),
    ("wireless never reports connected from connect exit code alone", WirelessReadinessFailureAsync),
    ("wireless disable targets the selected USB device and restarts adbd on USB", WirelessDisableAsync),
    ("QR pairing sessions are unique short lived and redact credentials", QrSessionAsync),
    ("QR pairing selects only its exact service and GUID matched connect endpoint", QrPairingAsync),
    ("pairing code validates inputs and keeps pairing and connection ports distinct", CodePairingAsync),
    ("pair success remains distinct from verified connection", PairingPartialAsync),
    ("pair failure cancellation and progress never expose credentials", PairingFailureAndCancellationAsync),
    ("process runner forwards output and bounds a hung child", ProcessRunnerAsync),
    ("ADB client cancellation preserves its shared server child", ProcessRunnerClientOnlyAsync),
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

static Task ConsoleParsingAsync()
{
    foreach (var input in new[] { "devices -l", "help", "version", "start-server", "kill-server", "server-status", "mdns services", "connect 192.0.2.3:5555", "disconnect", "track-devices -l", "keygen adbkey" })
    {
        var global = ConsoleCommand.Parse("adb " + input, "chosen");
        True(global.IsGlobal, input + " is global");
        False(global.Arguments.Contains("-s"), "global commands never get serial");
    }
    var command = ConsoleCommand.Parse("adb.exe -s chosen shell echo 'hello world' | grep hello", "chosen");
    True(command.Arguments.SequenceEqual(new[] { "-s", "chosen", "shell", "echo 'hello world' | grep hello" }), "shell tail preserves Android quotes and pipe as one argument");
    Equal("shell echo 'hello world' | grep hello", command.HistoryText, "history has no target prefix");
    command = ConsoleCommand.Parse("shell \"echo 'hello world' | grep hello\"", "chosen");
    Equal("echo 'hello world' | grep hello", command.Arguments[^1], "one outer shell wrapper removed");
    command = ConsoleCommand.Parse("install \"C:\\Phone apps\\test.apk\"", "chosen");
    Equal("C:\\Phone apps\\test.apk", command.Arguments[^1], "Windows paths keep backslashes");
    foreach (var path in new[] { "C:\\Phone apps\\", "C:\\Phone apps\\test.apk", "name \"quoted\".apk", "name;with&symbols.apk" })
        Equal(path, ConsoleCommand.Parse("install " + ConsoleCommand.QuoteArgument(path), "chosen").Arguments[^1], "quoted paths roundtrip without mangling: " + path);
    foreach (var input in new[] { "", "adb", "-s other shell id", "-d shell id", "-e shell id", "-t 1 shell id", "-H remote devices", "-P 5038 devices", "devices && whoami", "devices | findstr usb", "devices; version", "devices\nversion", "shell", "shell -t", "exec-out screencap -p" })
        Throws<ArgumentException>(() => ConsoleCommand.Parse(input, "chosen"));
    Throws<ArgumentException>(() => ConsoleCommand.Parse("shell id", null));
    Throws<ArgumentException>(() => ConsoleCommand.Parse("-s chosen devices", "chosen"));
    foreach (var input in new[] { "forward --list", "forward --remove-all", "reconnect offline" })
    {
        var global = ConsoleCommand.Parse(input, null);
        True(global.IsGlobal, "global subform requires no selected phone: " + input);
        False(global.Arguments.Contains("-s"), "global subform has no injected serial: " + input);
        Throws<ArgumentException>(() => ConsoleCommand.Parse("-s chosen " + input, "chosen"));
    }
    False(ConsoleCommand.Parse("reverse --remove-all", "chosen").IsGlobal, "reverse cleanup stays device scoped");
    False(ConsoleCommand.Parse("forward --remove tcp:8080", "chosen").IsGlobal, "individual forward removal stays device scoped");
    foreach (var input in new[] { "wait-for-device pair 192.0.2.1:37001 123456", "wait-for-device kill-server", "wait-for-device disconnect", "wait-for-usb-device shell echo hello" })
        Throws<ArgumentException>(() => ConsoleCommand.Parse(input, "chosen"));
    True(ConsoleCommand.Parse("wait-for-device", "chosen").Arguments.SequenceEqual(new[] { "-s", "chosen", "wait-for-device" }), "standalone wait remains available");
    foreach (var input in new[] { "raw host:kill", "server nodaemon", "attach other", "track-devices --proto-binary" })
        Throws<ArgumentException>(() => ConsoleCommand.Parse(input, "chosen"));
    return Task.CompletedTask;
}

static Task ConsolePrivacyAsync()
{
    var pair = ConsoleCommand.Parse("adb pair 192.0.2.3:37001 123456", null);
    True(pair.IsGlobal, "pair is global");
    Equal("123456\n", pair.StandardInput, "code only travels on stdin");
    True(pair.Arguments.SequenceEqual(new[] { "pair", "192.0.2.3:37001" }), "no code in process argument list");
    False(pair.Preview.Contains("123456"), "preview redacted");
    False(pair.HistoryText.Contains("123456"), "history text redacted");
    False(pair.ToString().Contains("123456"), "record diagnostic representation redacted");
    Equal("code [已隐藏] echoed", pair.Redact("code 123456 echoed"), "echoed credentials sanitized");
    False(pair.CanRemember, "pair never persisted");
    Throws<ArgumentException>(() => ConsoleCommand.Parse("pair 192.0.2.3:37001", null));
    foreach (var input in new[] { "shell getprop", "shell getprop ro.product.model", "shell wm size", "shell pm list packages", "shell dumpsys battery", "logcat -d", "logcat -v time", "logcat -d -v time", "devices -l" })
        True(ConsoleCommand.Parse(input, "chosen").CanRemember, "safe query remembered: " + input);
    foreach (var input in new[] { "shell input text my-password", "shell echo secret", "shell getprop; echo secret", "shell getprop | grep secret", "logcat -f secret.txt", "install --unexpected secret", "pull /sdcard/private.txt", "uninstall secret-token", "push --sync secret" })
        False(ConsoleCommand.Parse(input, "chosen").CanRemember, "arbitrary data not remembered: " + input);
    foreach (var input in new[] { "install \"C:\\Phone apps\\test.apk\"", "install -r test.apk", "push local.txt /sdcard/file.txt", "pull /sdcard/file.txt local.txt", "bugreport report.zip", "uninstall com.example.app" })
        True(ConsoleCommand.Parse(input, "chosen").CanRemember, "known path/package form retained: " + input);
    return Task.CompletedTask;
}

static Task ConsoleHistoryAsync()
{
    using var directory = new TempDirectory();
    var path = Path.Combine(directory.Path, "console.json");
    var store = new ConsoleCommandHistoryStore(path);
    Equal(0, store.Load().Count, "empty initial history");
    for (var i = 0; i < 35; i++) store.Remember($"shell getprop ro.test.property{i}");
    var entries = new ConsoleCommandHistoryStore(path).Load();
    Equal(30, entries.Count, "bounded MRU");
    Equal("shell getprop ro.test.property34", entries[0], "newest first");
    store.Remember("adb shell getprop ro.test.property10");
    Equal("shell getprop ro.test.property10", store.Load()[0], "canonical deduplication moves to front");
    Equal(29, store.Remove("shell getprop ro.test.property10").Count, "remove exact record");
    Throws<ArgumentException>(() => store.Remember("shell input text password"));
    File.WriteAllText(path, "[\"pair 192.0.2.1:37001 123456\",\"shell echo password\",null,\"adb devices -l\",\"devices -l\",\"-s stale shell getprop\"]");
    True(store.Load().SequenceEqual(new[] { "devices -l" }), "unsafe and explicitly targeted legacy records removed");
    False(File.ReadAllText(path).Contains("123456"), "stale secret physically removed from disk");
    File.WriteAllText(path, "broken JSON");
    Equal(0, store.Load().Count, "malformed recovery");
    store.Remember("version");
    store.Clear();
    Equal(0, new ConsoleCommandHistoryStore(path).Load().Count, "clear persists across restart");
    return Task.CompletedTask;
}

static async Task ConsoleStreamingAsync()
{
    using var fixture = new ToolFixture();
    var runner = new ConsoleCommandRunner(fixture.Paths);
    long stdout = 0, stderr = 0;
    var chunks = new ConcurrentQueue<int>();
    var firstOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var pending = runner.RunAsync(ConsoleCommand.Parse("shell console-flood", "console"), (text, isError) =>
    {
        chunks.Enqueue(text.Length);
        if (isError) Interlocked.Add(ref stderr, text.Length); else Interlocked.Add(ref stdout, text.Length);
        firstOutput.TrySetResult();
    }, TimeSpan.FromSeconds(10), CancellationToken.None);
    await firstOutput.Task.WaitAsync(TimeSpan.FromSeconds(5));
    False(pending.IsCompleted, "unterminated output delivered before process exits");
    Equal(7, await pending, "nonzero exit retained");
    Equal(1024L * 1024, stdout, "all stdout drained");
    Equal(1024L * 1024, stderr, "all stderr drained concurrently");
    True(chunks.All(length => length <= 4096), "bounded callback chunks with no newlines");
    True(fixture.HasInvocation("-s", "console", "shell", "console-flood"), "selected target only");
    fixture.SetPairResult("code 123456 echoed");
    var output = new ConcurrentQueue<string>();
    await runner.RunAsync(ConsoleCommand.Parse("pair 192.0.2.3:37001 123456", null), (value, _) => output.Enqueue(value), TimeSpan.FromSeconds(5), CancellationToken.None);
    False(string.Concat(output).Contains("123456"), "runner sanitizes echoed code");
    True(fixture.HasInvocation("pair", "192.0.2.3:37001"), "pair argv never contains secret");
    output.Clear();
    await runner.RunAsync(ConsoleCommand.Parse("pair split-secret:37001 123456", null), (value, _) => output.Enqueue(value), TimeSpan.FromSeconds(5), CancellationToken.None);
    False(string.Concat(output).Contains("123456"), "pair secret split across reads is redacted");
}

static async Task ConsoleCancellationAsync()
{
    using var fixture = new ToolFixture();
    var runner = new ConsoleCommandRunner(fixture.Paths);
    var command = ConsoleCommand.Parse("shell console-hang", "console");
    using var cancel = new CancellationTokenSource();
    var pending = runner.RunAsync(command, (_, _) => { }, null, cancel.Token);
    var pidPath = Path.Combine(fixture.DirectoryPath, "console-pid.txt");
    await TestWait.UntilAsync(() => File.Exists(pidPath), TimeSpan.FromSeconds(5));
    var pid = int.Parse(await File.ReadAllTextAsync(pidPath));
    cancel.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    await AssertConsoleProcessExitedAsync(pid);
    await ThrowsAsync<TimeoutException>(() => runner.RunAsync(command, (_, _) => { }, TimeSpan.FromSeconds(2), CancellationToken.None));
    pid = int.Parse(await File.ReadAllTextAsync(pidPath));
    await AssertConsoleProcessExitedAsync(pid);
    False(fixture.HasInvocation("kill-server"), "cancel does not stop shared adb server");
}

static async Task AssertConsoleProcessExitedAsync(int pid)
{
    await TestWait.UntilAsync(() =>
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }, TimeSpan.FromSeconds(3));
}

static async Task SharedServerSurvivalAsync(bool screenshot)
{
    using var fixture = new ToolFixture();
    using var cancel = new CancellationTokenSource();
    var processIdsPath = Path.Combine(fixture.DirectoryPath, "console-server-processes.txt");
    Process? sharedServer = null;
    Task pending = screenshot ? new ScreenshotService(fixture.Paths).CaptureAsync("capture-shared-server", cancel.Token) : new ConsoleCommandRunner(fixture.Paths).RunAsync(
        ConsoleCommand.Parse("shell console-start-shared-server", "console"), (_, _) => { }, null, cancel.Token);
    try
    {
        await TestWait.UntilAsync(() => File.Exists(processIdsPath), TimeSpan.FromSeconds(5));
        var ids = (await File.ReadAllTextAsync(processIdsPath)).Split('|').Select(int.Parse).ToArray();
        // Retain the exact process handle before canceling so cleanup never selects
        // an unrelated process if Windows later reuses this PID.
        sharedServer = Process.GetProcessById(ids[1]);
        _ = sharedServer.Handle;
        False(sharedServer.HasExited, "fake shared server has started");
        cancel.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await AssertConsoleProcessExitedAsync(ids[0]);
        False(sharedServer.HasExited, "cancel stops only the adb client, preserving its shared-server descendant");
        False(fixture.HasInvocation("kill-server"), "no global server-stop command submitted");
    }
    finally
    {
        cancel.Cancel();
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }
        finally
        {
            if (sharedServer is not null)
            {
                using (sharedServer)
                {
                    if (!sharedServer.HasExited) sharedServer.Kill(entireProcessTree: false);
                    await sharedServer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
        }
    }
}

static Task ConnectionHistoryAsync()
{
    using var directory = new TempDirectory();
    var path = Path.Combine(directory.Path, "settings", "connections.json");
    var history = new ConnectionHistoryStore(path);
    Equal(0, history.Load().Count, "first launch has no history");
    for (var index = 1; index <= 12; index++) history.Remember($"192.0.2.{index}:5555");
    var restored = new ConnectionHistoryStore(path).Load();
    Equal(10, restored.Count, "bounded to ten recent endpoints");
    Equal("192.0.2.12:5555", restored[0], "newest first");
    Equal("192.0.2.3:5555", restored[^1], "oldest records evicted");
    restored = history.Remember("192.000.2.5:05555");
    Equal("192.0.2.5:5555", restored[0], "normalize and move duplicate to front");
    Equal(1, restored.Count(endpoint => endpoint == "192.0.2.5:5555"), "no duplicate");
    restored = history.Remember("192.0.2.5:41487");
    True(restored.Contains("192.0.2.5:5555"), "same IP with another port retained separately");
    Equal("192.0.2.5:41487", new ConnectionHistoryStore(path).Load()[0], "IP and port survive restart together");
    history.Clear();
    Equal(0, new ConnectionHistoryStore(path).Load().Count, "clear persists");
    return Task.CompletedTask;
}

static Task ConnectionHistoryRemovalAsync()
{
    using var directory = new TempDirectory();
    var path = Path.Combine(directory.Path, "connections.json");
    var history = new ConnectionHistoryStore(path);
    history.Remember("192.0.2.1:5555");
    history.Remember("192.0.2.2:5555");
    history.Remember("192.0.2.1:41487");
    var remaining = history.Remove("192.000.2.1:05555");
    True(remaining.SequenceEqual(new[] { "192.0.2.1:41487", "192.0.2.2:5555" }), "only exact IP and port removed, order preserved");
    True(new ConnectionHistoryStore(path).Load().SequenceEqual(remaining), "deletion survives restart");
    True(history.Remove("192.0.2.9:5555").SequenceEqual(remaining), "missing entry does not delete others");
    Throws<ArgumentException>(() => history.Remove("bad address"));
    True(history.Load().SequenceEqual(remaining), "invalid deletion leaves data intact");
    history.Remove("192.0.2.1:41487");
    history.Remove("192.0.2.2:5555");
    Equal(0, new ConnectionHistoryStore(path).Load().Count, "last deletion persists empty list");
    Equal("192.0.2.1:5555", history.Remember("192.0.2.1:5555").Single(), "future successful connection may record address again");
    return Task.CompletedTask;
}

static Task ConnectionHistoryRecoveryAsync()
{
    using var directory = new TempDirectory();
    var path = Path.Combine(directory.Path, "connections.json");
    var history = new ConnectionHistoryStore(path);
    File.WriteAllText(path, "broken JSON");
    Equal(0, history.Load().Count, "corrupt file does not block startup");
    history.Remember("192.0.2.1:5555");
    Throws<ArgumentException>(() => history.Remember("192.0.2.1:99999"));
    Equal("192.0.2.1:5555", history.Load().Single(), "invalid input does not overwrite history");
    File.WriteAllText(path, "[null,\"bad\",\"192.0.2.7:5555\",\"192.000.2.7:05555\",\"192.0.2.8:41487\"]");
    Equal(2, history.Load().Count, "ignore invalid entries and normalize duplicates on load");
    File.WriteAllText(path, "null");
    Equal(0, history.Load().Count, "null JSON tolerated");
    return Task.CompletedTask;
}

static async Task ScreenshotBytesAsync()
{
    using var fixture = new ToolFixture();
    var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
    fixture.SetResponse("capture-target", "screenshot", Convert.ToBase64String(png));
    var actual = await new ScreenshotService(fixture.Paths).CaptureAsync("capture-target");
    True(png.SequenceEqual(actual), "all binary bytes including CR LF and high bytes survive");
    True(fixture.HasInvocation("-s", "capture-target", "exec-out", "screencap", "-p"), "exact serial and exec-out PNG arguments");
    Equal(1, Directory.GetFiles(fixture.DirectoryPath, "call-*.txt").Length, "no mirror or other-device commands");
}

static async Task ScreenshotFailuresAsync()
{
    using var fixture = new ToolFixture();
    var service = new ScreenshotService(fixture.Paths);
    fixture.SetFailure("capture-error", "screenshot", "device offline");
    await ThrowsAsync<InvalidOperationException>(() => service.CaptureAsync("capture-error"));
    fixture.SetResponse("capture-empty", "screenshot", "");
    await ThrowsAsync<InvalidDataException>(() => service.CaptureAsync("capture-empty"));
    fixture.SetResponse("capture-text", "screenshot", Convert.ToBase64String(Encoding.UTF8.GetBytes("not a screenshot")));
    await ThrowsAsync<InvalidDataException>(() => service.CaptureAsync("capture-text"));
    await ThrowsAsync<InvalidDataException>(() => service.CaptureAsync("capture-oversize"));
}

static async Task ScreenshotCancellationAsync()
{
    using var fixture = new ToolFixture();
    await ThrowsAsync<TimeoutException>(() => new ScreenshotService(fixture.Paths, TimeSpan.FromSeconds(2)).CaptureAsync("capture-hang"));
    await AssertCaptureExitedAsync(fixture);
    File.Delete(Path.Combine(fixture.DirectoryPath, "capture-pid.txt"));
    using var cancellation = new CancellationTokenSource();
    var pending = new ScreenshotService(fixture.Paths).CaptureAsync("capture-hang", cancellation.Token);
    await TestWait.UntilAsync(() => File.Exists(Path.Combine(fixture.DirectoryPath, "capture-pid.txt")), TimeSpan.FromSeconds(5));
    cancellation.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => pending);
    await AssertCaptureExitedAsync(fixture);
}

static async Task AssertCaptureExitedAsync(ToolFixture fixture)
{
    var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.DirectoryPath, "capture-pid.txt")));
    await TestWait.UntilAsync(() =>
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }, TimeSpan.FromSeconds(3));
}

static Task LocalSubnetAsync()
{
    var wifi = new LocalNetworkAddress("Wi-Fi", "192.168.101.230", "255.255.255.0", "192.168.101.1", "Wi-Fi", true);
    Equal(SubnetMatch.SameSubnet, LocalNetwork.Compare("192.168.101.243:41487", [wifi]), "/24 match");
    Equal(SubnetMatch.DifferentSubnet, LocalNetwork.Compare("192.168.110.42:5555", [wifi]), "/24 mismatch");
    Equal(SubnetMatch.SameSubnet, LocalNetwork.Compare("192.168.110.42:5555", [wifi with { Mask = "255.255.0.0" }]), "/16 spans third octet");
    Equal(SubnetMatch.SameSubnet, LocalNetwork.Compare("192.168.100.42:5555", [wifi with { Mask = "255.255.254.0" }]), "/23 matches adjacent range");
    Equal(SubnetMatch.DifferentSubnet, LocalNetwork.Compare("192.168.102.42:5555", [wifi with { Mask = "255.255.254.0" }]), "/23 respects bit boundary");
    Equal(SubnetMatch.SameSubnet, LocalNetwork.Compare("10.10.0.22:5555", [wifi, wifi with { Name = "Ethernet", Address = "10.10.0.2" }]), "secondary LAN can match");
    Equal(SubnetMatch.DifferentSubnet, LocalNetwork.Compare("10.10.0.22:5555", [wifi, wifi with { Address = "10.10.0.2", IsLan = false }]), "virtual adapter does not impersonate LAN");
    Equal(SubnetMatch.Unknown, LocalNetwork.Compare("192.168.101.243:5555", []), "no network stays unknown");
    Equal(SubnetMatch.Unknown, LocalNetwork.Compare("192.168.101.243:5555", [wifi with { Mask = "255.0.255.0" }]), "invalid mask");
    Equal(SubnetMatch.Unknown, LocalNetwork.Compare("192.168.101.243:5555", [wifi with { Address = "169.254.1.2" }]), "APIPA cannot confirm LAN");
    Equal(SubnetMatch.Unknown, LocalNetwork.Compare("not-an-address", [wifi]), "invalid endpoint");
    return Task.CompletedTask;
}

static async Task NetworkPreflightAsync()
{
    using var fixture = ReadyUsbWirelessFixture("guard-usb");
    var checkedEndpoints = new List<string>();
    Task<bool> Decline(string endpoint, CancellationToken token)
    {
        checkedEndpoints.Add(endpoint);
        return Task.FromResult(false);
    }
    var pairing = new PairingService(fixture.Paths) { ConfirmEndpointAsync = Decline };
    False((await pairing.ConnectAsync("192.0.2.42:5555")).Connected, "declined direct connection");
    False((await pairing.PairWithCodeAsync("192.0.2.42:37001", "123456")).Paired, "declined code pairing");
    var qr = PairingService.CreateQrSession();
    fixture.SetMdnsBefore($"{qr.ServiceName} _adb-tls-pairing._tcp 192.0.2.42:37002");
    False((await pairing.PairWithQrAsync(qr)).Paired, "QR checks discovered address before pairing");
    Equal(0, fixture.CountCommand("pair"), "no pair command before approval");
    Equal(0, fixture.CountCommand("connect"), "no connect command before approval");
    True(checkedEndpoints.Contains("192.0.2.42:37002"), "exact QR endpoint checked");

    fixture.SetResponse("guard-usb", "wifi", "7: wlan0 inet 192.0.2.42/24 scope global wlan0");
    var adb = new AdbService(fixture.Paths) { ConfirmEndpointAsync = Decline };
    False((await adb.SetWirelessAsync(new DeviceInfo("guard-usb", "Phone", "device"), true)).Succeeded, "declined USB enable");
    Equal(0, fixture.CountCommand("tcpip"), "no mode switch before approval");

    using var cancel = new CancellationTokenSource();
    var canceling = new PairingService(fixture.Paths)
    {
        ConfirmEndpointAsync = (_, _) => { cancel.Cancel(); return Task.FromResult(true); }
    };
    await ThrowsAsync<OperationCanceledException>(() => canceling.ConnectAsync("192.0.2.42:5555", cancel.Token));
    Equal(0, fixture.CountCommand("connect"), "cancellation after callback prevents command");

    fixture.SetResponse("192.0.2.42:5555", "state", "device");
    var accepted = new PairingService(fixture.Paths) { ConfirmEndpointAsync = (_, _) => Task.FromResult(true) };
    True((await accepted.ConnectAsync("192.0.2.42:5555")).Connected, "user can still connect across subnet");
}

static async Task WirelessDisconnectAsync()
{
    using var fixture = new ToolFixture();
    const string target = "192.0.2.40:6666";
    const string other = "192.0.2.41:5555";
    const string tls = "adb-Test._adb-tls-connect._tcp";
    fixture.SetDevices($"List of devices attached\n{target} device model:One\n{other} device model:Two\n{tls} offline\nphysical-usb device model:USB\n");
    var service = new AdbService(fixture.Paths);
    fixture.SetResponse(target, "disconnect", $"disconnected {target}");
    var disconnected = await service.DisconnectWirelessAsync(new DeviceInfo(target, "One", "device"));
    True(disconnected.Disconnected, "selected network transport removed");
    True(fixture.HasInvocation("disconnect", target), "exact selected serial including custom port");
    False(fixture.HasInvocation("disconnect"), "never disconnect all devices");
    False(fixture.HasInvocation("disconnect", other), "other network transport untouched");
    False(fixture.HasInvocation("kill-server"), "shared server remains running");

    fixture.SetResponse(tls, "disconnect", $"disconnected {tls}");
    True((await service.DisconnectWirelessAsync(new DeviceInfo(tls, "TLS", "offline"))).Disconnected,
        "offline mDNS transport can be disconnected by exact serial without shell probes");
    await ThrowsAsync<InvalidOperationException>(() => service.DisconnectWirelessAsync(
        new DeviceInfo("physical-usb", "USB", "device") { Transport = DeviceTransport.Network }));
    False(fixture.HasInvocation("disconnect", "physical-usb"), "USB blocked even with incorrect transport metadata");
    var calls = fixture.CountCommand("disconnect");
    True((await service.DisconnectWirelessAsync(new DeviceInfo(target, "One", "device"))).Disconnected, "already absent is harmless");
    Equal(calls, fixture.CountCommand("disconnect"), "no disconnect for absent target");

    fixture.SetResponse(other, "disconnect", "unexpected response");
    False((await service.DisconnectWirelessAsync(new DeviceInfo(other, "Two", "device"))).Disconnected,
        "exit zero without success is not disconnection");
    fixture.SetResponse(other, "disconnect", $"disconnected {other}");
    fixture.SetResponse(other, "keep-after-disconnect", "1");
    False((await service.DisconnectWirelessAsync(new DeviceInfo(other, "Two", "device"))).Disconnected,
        "still-present or immediately reconnected target is not reported removed");
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    calls = fixture.CountCommand("disconnect");
    await ThrowsAsync<OperationCanceledException>(() => service.DisconnectWirelessAsync(new DeviceInfo(other, "Two", "device"), canceled.Token));
    Equal(calls, fixture.CountCommand("disconnect"), "pre-cancel does not submit disconnection");
}

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

static async Task DeviceDetailsAsync()
{
    using var fixture = new ToolFixture();
    fixture.SetDevices("""
        List of devices attached
        physical-one device product:husky model:Raw_Model transport_id:1
        libusb-one device product:oriole model:Libusb_Model transport_id:2
        192.0.2.20:5555 device product:husky model:Network_Model transport_id:3
        adb-TLS._adb-tls-connect._tcp device product:husky model:Tls_Model transport_id:4
        emulator-5554 device product:sdk_gphone model:Emulator_Model transport_id:5
        locked-one unauthorized usb:2-1 transport_id:6
        """);
    fixture.SetResponse("physical-one", "devpath", "usb:4.0.1");
    fixture.SetResponse("physical-one", "device-name", "Owner Pixel");
    fixture.SetResponse("physical-one", "getprop", Properties("Retail Pixel", "Pixel 9 Pro", "16", "5555"));
    fixture.SetResponse("libusb-one", "devpath", "4.0.2");
    fixture.SetResponse("libusb-one", "getprop", Properties("", "Pixel 8", "15", "-1"));
    fixture.SetResponse("192.0.2.20:5555", "devpath", "unknown");
    fixture.SetResponse("192.0.2.20:5555", "getprop", Properties("Living Room", "Pixel Fold", "15", "5555"));
    fixture.SetResponse("adb-TLS._adb-tls-connect._tcp", "devpath", "unknown");
    fixture.SetResponse("adb-TLS._adb-tls-connect._tcp", "getprop", Properties("TLS Phone", "Pixel 8a", "15", "-1"));
    fixture.SetResponse("emulator-5554", "devpath", "unknown");
    fixture.SetResponse("emulator-5554", "getprop", Properties("", "Virtual Phone", "16", "5555"));

    var devices = await new AdbService(fixture.Paths).GetDevicesAsync();

    var physical = devices.Single(device => device.Serial == "physical-one");
    Equal("Owner Pixel", physical.DeviceName, "user configured device name takes priority");
    Equal("Pixel 9 Pro", physical.Model, "property model replaces list fallback");
    Equal("16", physical.AndroidVersion, "Android version");
    Equal(DeviceTransport.Usb, physical.Transport, "native USB devpath");
    Equal(5555, physical.WirelessPort, "plain TCP daemon port");

    Equal(DeviceTransport.Usb, devices.Single(device => device.Serial == "libusb-one").Transport,
        "libusb path without usb prefix remains physical USB");
    Equal(DeviceTransport.Network, devices.Single(device => device.Serial == "192.0.2.20:5555").Transport,
        "plain TCP endpoint transport");
    var tls = devices.Single(device => device.Serial == "adb-TLS._adb-tls-connect._tcp");
    Equal(DeviceTransport.Network, tls.Transport, "TLS mDNS endpoint transport");
    Equal(null, tls.WirelessPort, "TLS transport is distinct from plain TCP daemon mode");
    Equal(DeviceTransport.Emulator, devices.Single(device => device.Serial == "emulator-5554").Transport,
        "emulator transport");

    var locked = devices.Single(device => device.Serial == "locked-one");
    Equal(DeviceTransport.Usb, locked.Transport, "explicit usb path identifies transport even before authorization");
    Equal(0, fixture.InvocationsForSerial("locked-one").Count, "unauthorized device has no probes");
}

static async Task UnknownUsbPathAsync()
{
    using var fixture = ReadyUsbWirelessFixture("windows-usb");
    fixture.SetResponse("windows-usb", "devpath", "unknown");
    fixture.SetResponse("usb-selector", "serial", "windows-usb");
    var service = new AdbService(fixture.Paths);
    var device = (await service.GetDevicesAsync()).Single();
    Equal(DeviceTransport.Usb, device.Transport, "USB-only selector is authoritative despite unknown path");
    True(fixture.HasInvocation("-d", "get-serialno"), "query must restrict transport to USB");

    fixture.SetResponse("windows-usb", "wifi", "7: wlan0 inet 192.0.2.44/24 scope global wlan0");
    fixture.SetResponse("192.0.2.44:5555", "state", "device");
    True((await service.SetWirelessAsync(device, true)).Connected, "confirmed USB can enable wireless");
    var calls = fixture.CountCommand("tcpip");
    fixture.SetResponse("usb-selector", "serial", "another-usb");
    False((await service.SetWirelessAsync(device, true)).Succeeded, "stale cached USB classification cannot authorize mutation");
    Equal(calls, fixture.CountCommand("tcpip"), "different USB selection never switches target");

    fixture.SetFailure("usb-selector", "serial", "error: more than one device/emulator");
    var ambiguous = (await new AdbService(fixture.Paths).GetDevicesAsync()).Single();
    Equal(DeviceTransport.Unknown, ambiguous.Transport, "ambiguous USB selection is not guessed");
    False((await service.SetWirelessAsync(device, false)).Succeeded, "ambiguous transport cannot disable wireless");
    Equal(0, fixture.CountCommand("usb"), "no USB mode mutation when ambiguous");
}

static async Task DeviceDetailCacheAsync()
{
    using var fixture = new ToolFixture();
    fixture.SetDevices("List of devices attached\ncache-one device model:List_Model transport_id:1\n");
    fixture.SetResponse("cache-one", "devpath", "usb:9.1");
    fixture.SetFailure("cache-one", "getprop", "property service unavailable");
    var service = new AdbService(fixture.Paths);

    var first = (await service.GetDevicesAsync()).Single();
    var second = (await service.GetDevicesAsync()).Single();

    Equal("List Model", first.Model, "list model survives failed detail probe");
    Equal(DeviceTransport.Usb, first.Transport, "successful transport probe survives property failure");
    True(first.DetailsError.Contains("property service unavailable", StringComparison.Ordinal), "probe error is exposed");
    Equal(first, second, "cached detail result");
    Equal(1, fixture.CountInvocation("cache-one", "getprop"), "property probe cache");
    Equal(1, fixture.CountInvocation("cache-one", "get-devpath"), "devpath probe cache");

    fixture.SetDevices("""
        List of devices attached
        probe-1 device model:One
        probe-2 device model:Two
        probe-3 device model:Three
        probe-4 device model:Four
        probe-5 device model:Five
        probe-6 device model:Six
        """);
    fixture.SetProbeDelay(200);
    foreach (var serial in Enumerable.Range(1, 6).Select(number => $"probe-{number}"))
    {
        fixture.SetResponse(serial, "devpath", $"usb:{serial}");
        fixture.SetResponse(serial, "getprop", Properties("", serial, "15", "-1"));
    }
    await new AdbService(fixture.Paths).GetDevicesAsync();
    True(fixture.MaximumProbeConcurrency() <= 4, "detail probe concurrency is bounded to four devices");
    True(fixture.MaximumProbeConcurrency() >= 2, "independent device probes overlap");
}

static async Task WirelessPreconditionsAsync()
{
    using var unauthorizedFixture = new ToolFixture();
    unauthorizedFixture.SetDevices("List of devices attached\nwire-one unauthorized usb:1-1\n");
    var unauthorized = await new AdbService(unauthorizedFixture.Paths).SetWirelessAsync(
        new DeviceInfo("wire-one", "Phone", "device") { Transport = DeviceTransport.Usb }, true);
    False(unauthorized.ModeEnabled, "authorization loss blocks mutation");
    False(unauthorized.Succeeded, "authorization loss result is explicit failure");
    Equal(0, unauthorizedFixture.CountCommand("tcpip"), "no tcpip after authorization loss");

    using var networkFixture = new ToolFixture();
    networkFixture.SetDevices("List of devices attached\nnetwork-alias device model:Phone\n");
    networkFixture.SetResponse("network-alias", "devpath", "unknown");
    var network = await new AdbService(networkFixture.Paths).SetWirelessAsync(
        new DeviceInfo("network-alias", "Phone", "device") { Transport = DeviceTransport.Usb }, true);
    False(network.ModeEnabled, "missing physical devpath blocks mutation even without colon in serial");
    Equal(0, networkFixture.CountCommand("tcpip"), "no tcpip for non-USB transport");

    using var noWifiFixture = ReadyUsbWirelessFixture("wire-two");
    noWifiFixture.SetResponse("wire-two", "wifi", "");
    var noWifi = await new AdbService(noWifiFixture.Paths).SetWirelessAsync(
        new DeviceInfo("wire-two", "Phone", "device") { Transport = DeviceTransport.Usb }, true);
    False(noWifi.ModeEnabled, "missing Wi-Fi IPv4 blocks TCP mode");
    Equal(0, noWifiFixture.CountCommand("tcpip"), "Wi-Fi discovery happens before tcpip mutation");
}

static async Task WirelessEnableAsync()
{
    using var fixture = ReadyUsbWirelessFixture("wire-three");
    fixture.SetResponse("wire-three", "wifi", "7: wlan0    inet 192.0.2.44/24 brd 192.0.2.255 scope global wlan0");
    fixture.SetConnectFailures(1);
    fixture.SetResponse("192.0.2.44:5555", "state", "device");
    var service = new AdbService(fixture.Paths);
    await service.GetDevicesAsync();

    var result = await service.SetWirelessAsync(
        new DeviceInfo("wire-three", "Phone", "device") { Transport = DeviceTransport.Usb }, true);

    True(result.ModeEnabled, "TCP daemon mode enabled");
    True(result.Connected, "endpoint verified ready");
    True(result.Succeeded, "fully verified enable succeeds");
    Equal("192.0.2.44:5555", result.Endpoint, "wireless endpoint");
    Equal(2, fixture.CountCommand("connect"), "connect retries after daemon restart");
    True(fixture.HasInvocation("-s", "wire-three", "tcpip", "5555"), "tcpip targets selected USB serial");
    True(fixture.HasInvocation("connect", "192.0.2.44:5555"), "connect targets discovered endpoint");
    True(fixture.HasInvocation("-s", "192.0.2.44:5555", "get-state"), "readiness checks exact endpoint");

    await service.GetDevicesAsync();
    Equal(2, fixture.CountInvocation("wire-three", "getprop"), "state mutation invalidates detail cache");
}

static async Task WirelessReadinessFailureAsync()
{
    using var fixture = ReadyUsbWirelessFixture("wire-four");
    fixture.SetResponse("wire-four", "wifi", "7: wlan0 inet 198.51.100.8/24 scope global wlan0");
    fixture.SetResponse("198.51.100.8:5555", "state", "offline");
    var result = await new AdbService(fixture.Paths).SetWirelessAsync(
        new DeviceInfo("wire-four", "Phone", "device") { Transport = DeviceTransport.Usb }, true);
    True(result.ModeEnabled, "TCP mode remains enabled when host readiness fails");
    False(result.Connected, "connect exit zero alone is insufficient");
    False(result.Succeeded, "partial enable is explicit failure");
    True(result.Message.Contains("未连接", StringComparison.Ordinal), "partial result is explicit");
}

static async Task WirelessDisableAsync()
{
    using var fixture = ReadyUsbWirelessFixture("wire-five");
    var result = await new AdbService(fixture.Paths).SetWirelessAsync(
        new DeviceInfo("wire-five", "Phone", "device") { Transport = DeviceTransport.Usb }, false);
    False(result.ModeEnabled, "USB mode disables plain TCP mode");
    False(result.Connected, "disable result has no wireless connection");
    True(result.Succeeded, "verified USB restart succeeds");
    True(fixture.HasInvocation("-s", "wire-five", "usb"), "adb usb targets selected device");
    Equal(0, fixture.CountCommand("disconnect"), "disable never uses host-only disconnect");
}

static ToolFixture ReadyUsbWirelessFixture(string serial)
{
    var fixture = new ToolFixture();
    fixture.SetDevices($"List of devices attached{Environment.NewLine}{serial} device model:Phone transport_id:1{Environment.NewLine}");
    fixture.SetResponse(serial, "devpath", "usb:1.2");
    fixture.SetResponse(serial, "getprop", Properties("Phone", "Phone", "15", "-1"));
    fixture.SetResponse(serial, "tcpip", "restarting in TCP mode port: 5555");
    fixture.SetResponse(serial, "usb", "restarting in USB mode");
    return fixture;
}

static string Properties(string marketName, string model, string androidVersion, string tcpPort) => $"""
    [ro.product.marketname]: [{marketName}]
    [ro.product.model]: [{model}]
    [ro.build.version.release]: [{androidVersion}]
    [service.adb.tcp.port]: [{tcpPort}]
    """;

static async Task QrSessionAsync()
{
    var before = DateTimeOffset.UtcNow;
    var first = PairingService.CreateQrSession();
    var second = PairingService.CreateQrSession();
    var after = DateTimeOffset.UtcNow;

    True(first.ServiceName.StartsWith("studio-", StringComparison.Ordinal), "QR service prefix");
    Equal(17, first.ServiceName.Length, "studio prefix plus random ten characters");
    True(first.ServiceName.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'), "QR-safe service name");
    True(first.Payload.StartsWith($"WIFI:T:ADB;S:{first.ServiceName};P:", StringComparison.Ordinal), "AOSP QR payload prefix");
    True(first.Payload.EndsWith(";;", StringComparison.Ordinal), "AOSP QR payload terminator");
    False(first.ServiceName == second.ServiceName, "independent service names");
    False(first.Payload == second.Payload, "independent secrets");
    True(first.ExpiresAt >= before.AddMinutes(2) && first.ExpiresAt <= after.AddMinutes(2), "two minute expiry");
    False(first.ToString().Contains(first.ServiceName, StringComparison.Ordinal), "ToString redacts service name");
    False(first.ToString().Contains(first.PairingSecret, StringComparison.Ordinal), "ToString redacts secret");

    using var fixture = new ToolFixture();
    var expired = new QrPairingSession(
        "studio-Expired01",
        "Secret1234567890",
        DateTimeOffset.UtcNow.AddSeconds(-1));
    var expiredResult = await fixture.PairingService().PairWithQrAsync(expired);
    False(expiredResult.Paired, "expired QR is rejected");
    Equal(0, fixture.CountCommand("mdns"), "expired QR starts no discovery");
    Equal(0, fixture.CountCommand("pair"), "expired QR starts no pair process");
}

static async Task QrPairingAsync()
{
    using var fixture = new ToolFixture();
    var session = PairingService.CreateQrSession();
    fixture.SetMdnsBefore($"""
        List of discovered mdns services
        studio-unrelated _adb-tls-pairing._tcp 192.0.2.9:30001
        {session.ServiceName} _adb-tls-pairing._tcp 192.0.2.10:37123
        """);
    fixture.SetPairResult("Successfully paired to 192.0.2.10:37123 [guid=adb-DEVICE-GUID]");
    fixture.SetMdnsAfter("""
        List of discovered mdns services
        adb-OTHER-GUID _adb-tls-connect._tcp 192.0.2.99:49999
        adb-DEVICE-GUID _adb-tls-connect._tcp 192.0.2.10:41567
        """);
    fixture.SetResponse("192.0.2.10:41567", "state", "device");
    var progress = new ProgressCollector();

    var result = await fixture.PairingService().PairWithQrAsync(session, progress);

    True(result.Paired, "QR pair success parsed");
    True(result.Connected, "QR connection readiness verified");
    Equal("192.0.2.10:41567", result.Endpoint, "connect endpoint uses discovered dynamic port");
    True(fixture.HasInvocation("pair", "192.0.2.10:37123"), "pair targets exact QR service endpoint");
    True(fixture.HasInvocation("connect", "192.0.2.10:41567"), "connect targets GUID matched service endpoint");
    False(fixture.HasInvocation("pair", "192.0.2.9:30001"), "unrelated QR service ignored");
    False(fixture.HasInvocation("connect", "192.0.2.99:49999"), "unrelated connect service ignored");
    False(fixture.AllInvocationText().Contains(session.PairingSecret, StringComparison.Ordinal), "QR secret absent from arguments and command log");
    False(progress.Messages.Any(message => message.Contains(session.PairingSecret, StringComparison.Ordinal)), "QR secret absent from progress");
}

static async Task CodePairingAsync()
{
    using var fixture = new ToolFixture();
    var service = fixture.PairingService();
    await ThrowsAsync<ArgumentException>(() => service.PairWithCodeAsync("not-an-endpoint", "123456"));
    await ThrowsAsync<ArgumentException>(() => service.PairWithCodeAsync("127.1:30123", "123456"));
    await ThrowsAsync<ArgumentException>(() => service.PairWithCodeAsync("192.0.2.5:30123", "12345"));
    await ThrowsAsync<ArgumentException>(() => service.PairWithCodeAsync("192.0.2.5:30123", "123456", "192.0.2.5"));
    await ThrowsAsync<ArgumentException>(() => service.PairWithCodeAsync("192.0.2.5:30123", "123456", "192.0.2.6:42678"));
    Equal(0, fixture.CountCommand("pair"), "invalid input starts no pair process");

    fixture.SetPairResult("Successfully paired to 192.0.2.5:30123 [guid=adb-CODE-GUID]");
    fixture.SetResponse("192.0.2.5:42678", "state", "device");
    var progress = new ProgressCollector();
    var result = await service.PairWithCodeAsync("192.0.2.5:30123", "123456", "192.0.2.5:42678", progress);

    True(result.Paired && result.Connected, "code pairing and connection succeed");
    Equal("192.0.2.5:42678", result.Endpoint, "explicit connection endpoint is preserved");
    False(result.Endpoint.EndsWith(":30123", StringComparison.Ordinal), "pairing port is never reused as connection port");
    False(fixture.AllInvocationText().Contains("123456", StringComparison.Ordinal), "pairing code absent from arguments and command log");
    False(progress.Messages.Any(message => message.Contains("123456", StringComparison.Ordinal)), "pairing code absent from progress");
}

static async Task PairingPartialAsync()
{
    using var fixture = new ToolFixture();
    fixture.SetPairResult("Enter pairing code: Successfully paired to 192.0.2.20:31234 [guid=adb-PARTIAL-GUID]");
    fixture.SetMdnsAfter("adb-PARTIAL-GUID _adb-tls-connect._tcp 192.0.2.20:45678");
    fixture.SetResponse("192.0.2.20:45678", "state", "offline");

    var result = await fixture.PairingService().PairWithCodeAsync("192.0.2.20:31234", "654321");

    True(result.Paired, "pair success is retained");
    False(result.Connected, "connect exit zero alone is insufficient");
    Equal("192.0.2.20:45678", result.Endpoint, "partial result exposes manual connection endpoint");
    True(result.Message.Contains("配对成功", StringComparison.Ordinal), "partial message distinguishes paired state");
    False(fixture.AllInvocationText().Contains(":5555", StringComparison.Ordinal), "TLS pairing never falls back to 5555");
}

static async Task PairingFailureAndCancellationAsync()
{
    using var failedFixture = new ToolFixture();
    failedFixture.SetPairResult("Failed: pairing code 112233 rejected", exitCode: 0);
    var progress = new ProgressCollector();
    var failed = await failedFixture.PairingService().PairWithCodeAsync("192.0.2.30:32345", "112233", progress: progress);
    False(failed.Paired, "exit zero without success text does not establish pairing");
    False(failed.Connected, "failed pair does not connect");
    Equal(0, failedFixture.CountCommand("connect"), "failed pair starts no connect");
    False(failed.Message.Contains("112233", StringComparison.Ordinal), "failure message redacts code");

    using var discoveryFixture = new ToolFixture();
    var discoverySession = PairingService.CreateQrSession();
    using var discoveryCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    await ThrowsAsync<OperationCanceledException>(() => discoveryFixture.PairingService().PairWithQrAsync(
        discoverySession,
        cancellationToken: discoveryCancellation.Token));
    Equal(0, discoveryFixture.CountCommand("pair"), "cancelled discovery submits no pair request");

    using var canceledFixture = new ToolFixture();
    canceledFixture.SetPairHang();
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
    await ThrowsAsync<OperationCanceledException>(() => canceledFixture.PairingService().PairWithCodeAsync(
        "192.0.2.31:32346", "445566", cancellationToken: cancellation.Token));
    await canceledFixture.WaitForProcessesToExitAsync(TimeSpan.FromSeconds(4));
    False(canceledFixture.HasLiveProcesses(), "pair cancellation kills owned child");
    False(canceledFixture.AllInvocationText().Contains("445566", StringComparison.Ordinal), "canceled code absent from logs");
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

static async Task ProcessRunnerClientOnlyAsync()
{
    using var fixture = new ToolFixture();
    using var cancellation = new CancellationTokenSource();
    var childPath = Path.Combine(fixture.DirectoryPath, "shared-child.pid");
    var running = ProcessRunner.RunAsync(fixture.Paths.Adb, ["hang-with-shared-child"],
        TimeSpan.FromSeconds(15), cancellationToken: cancellation.Token, killEntireProcessTree: false);
    Process? child = null;
    try
    {
        await TestWait.UntilAsync(() => File.Exists(childPath), TimeSpan.FromSeconds(5));
        child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(childPath)));
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(4)));
        False(child.HasExited, "canceling a CLI client must preserve the shared daemon it started");
    }
    finally
    {
        cancellation.Cancel();
        try { await running.WaitAsync(TimeSpan.FromSeconds(4)); } catch { }
        if (child is null && File.Exists(childPath))
        {
            try { child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(childPath))); }
            catch (ArgumentException) { }
        }
        if (child is not null)
        {
            try
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));
            }
            finally { child.Dispose(); }
        }
    }
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

    mirrors.Start(Ready("serial-A"), new MirrorOptions(Audio: true));
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

sealed class ProgressCollector : IProgress<string>
{
    public ConcurrentQueue<string> Messages { get; } = new();
    public void Report(string value) => Messages.Enqueue(value);
}

sealed record ToolRun(string Serial, string ApkName, DateTime Start, DateTime End);

sealed class ToolFixture : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly string? _previousMode = Environment.GetEnvironmentVariable("ADBDESK_TEST_FAKE_TOOL");
    private readonly string? _previousDirectory = Environment.GetEnvironmentVariable("ADBDESK_TEST_DIRECTORY");
    private readonly string? _previousDelay = Environment.GetEnvironmentVariable("ADBDESK_TEST_DELAY_MS");
    private readonly string? _previousProbeDelay = Environment.GetEnvironmentVariable("ADBDESK_TEST_PROBE_DELAY_MS");
    private readonly string? _previousConnectFailures = Environment.GetEnvironmentVariable("ADBDESK_TEST_CONNECT_FAILURES");
    private readonly string? _previousMdnsBefore = Environment.GetEnvironmentVariable("ADBDESK_TEST_MDNS_BEFORE");
    private readonly string? _previousMdnsAfter = Environment.GetEnvironmentVariable("ADBDESK_TEST_MDNS_AFTER");
    private readonly string? _previousPairResult = Environment.GetEnvironmentVariable("ADBDESK_TEST_PAIR_RESULT");
    private readonly string? _previousPairExit = Environment.GetEnvironmentVariable("ADBDESK_TEST_PAIR_EXIT");
    private readonly string? _previousPairHang = Environment.GetEnvironmentVariable("ADBDESK_TEST_PAIR_HANG");
    private readonly string? _previousAdbSocket = Environment.GetEnvironmentVariable("ADB_SERVER_SOCKET");
    public ToolPaths Paths { get; }
    public TestAdbTransportServer TransportServer { get; }
    public string DirectoryPath => _directory.Path;

    public ToolFixture()
    {
        TransportServer = new TestAdbTransportServer(_directory.Path);
        Environment.SetEnvironmentVariable("ADB_SERVER_SOCKET", TransportServer.SocketSpec);
        Environment.SetEnvironmentVariable("ADBDESK_TEST_FAKE_TOOL", "1");
        Environment.SetEnvironmentVariable("ADBDESK_TEST_DIRECTORY", _directory.Path);
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test executable path unavailable.");
        Paths = new ToolPaths(executable, executable);
    }

    public void SetDelay(int milliseconds) => Environment.SetEnvironmentVariable("ADBDESK_TEST_DELAY_MS", milliseconds.ToString());
    public void SetProbeDelay(int milliseconds) => Environment.SetEnvironmentVariable("ADBDESK_TEST_PROBE_DELAY_MS", milliseconds.ToString());
    public void SetConnectFailures(int count) => Environment.SetEnvironmentVariable("ADBDESK_TEST_CONNECT_FAILURES", count.ToString());
    public void SetDevices(string output) => Environment.SetEnvironmentVariable("ADBDESK_TEST_DEVICES", output);
    public void SetResponse(string serial, string kind, string output) =>
        File.WriteAllText(FakeTool.ResponsePath(_directory.Path, serial, kind), output);
    public void SetFailure(string serial, string kind, string error) =>
        File.WriteAllText(FakeTool.ResponsePath(_directory.Path, serial, kind) + ".failure", error);
    public void SetMdnsBefore(string output) => Environment.SetEnvironmentVariable("ADBDESK_TEST_MDNS_BEFORE", output);
    public void SetMdnsAfter(string output) => Environment.SetEnvironmentVariable("ADBDESK_TEST_MDNS_AFTER", output);
    public void SetPairResult(string output, int exitCode = 0)
    {
        Environment.SetEnvironmentVariable("ADBDESK_TEST_PAIR_RESULT", output);
        Environment.SetEnvironmentVariable("ADBDESK_TEST_PAIR_EXIT", exitCode.ToString());
    }
    public void SetPairHang() => Environment.SetEnvironmentVariable("ADBDESK_TEST_PAIR_HANG", "1");
    public PairingService PairingService() => new(Paths, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(250));
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

    public IReadOnlyList<string[]> InvocationsForSerial(string serial) => ReadInvocations()
        .Where(arguments => Array.IndexOf(arguments, "-s") is var index && index >= 0 && index + 1 < arguments.Length && arguments[index + 1] == serial)
        .ToArray();
    public int CountInvocation(string serial, string command) => InvocationsForSerial(serial).Count(arguments =>
        command == "getprop" ? arguments.AsSpan().EndsWith(["shell", "getprop"], StringComparer.Ordinal) : arguments.Contains(command, StringComparer.Ordinal));
    public int CountCommand(string command) => ReadInvocations().Count(arguments => arguments.Contains(command, StringComparer.Ordinal));
    public bool HasInvocation(params string[] expected) => ReadInvocations().Any(arguments => arguments.SequenceEqual(expected));
    public string AllInvocationText() => string.Join(Environment.NewLine, ReadInvocations().Select(arguments => string.Join('|', arguments)));
    public int MaximumProbeConcurrency()
    {
        var runs = Directory.GetFiles(_directory.Path, "probe-*.start").Select(start =>
        {
            var end = Path.ChangeExtension(start, ".end");
            return (Start: long.Parse(File.ReadAllText(start)), End: long.Parse(File.ReadAllText(end)));
        }).ToArray();
        return runs.SelectMany(run => new[] { (run.Start, 1), (run.End, -1) })
            .OrderBy(point => point.Item1).ThenBy(point => point.Item2)
            .Aggregate((Current: 0, Maximum: 0), (state, point) =>
            {
                var current = state.Current + point.Item2;
                return (current, Math.Max(current, state.Maximum));
            }).Maximum;
    }

    private IReadOnlyList<string[]> ReadInvocations() => Directory.GetFiles(_directory.Path, "call-*.txt")
        .Select(path => File.ReadAllText(path).Split('|')).ToArray();

    public void Dispose()
    {
        TransportServer.Dispose();
        Environment.SetEnvironmentVariable("ADB_SERVER_SOCKET", _previousAdbSocket);
        Environment.SetEnvironmentVariable("ADBDESK_TEST_FAKE_TOOL", _previousMode);
        Environment.SetEnvironmentVariable("ADBDESK_TEST_DIRECTORY", _previousDirectory);
        Environment.SetEnvironmentVariable("ADBDESK_TEST_DELAY_MS", _previousDelay);
        Environment.SetEnvironmentVariable("ADBDESK_TEST_PROBE_DELAY_MS", _previousProbeDelay);
        Environment.SetEnvironmentVariable("ADBDESK_TEST_CONNECT_FAILURES", _previousConnectFailures);
        Environment.SetEnvironmentVariable("ADBDESK_TEST_MDNS_BEFORE", _previousMdnsBefore);
        Environment.SetEnvironmentVariable("ADBDESK_TEST_MDNS_AFTER", _previousMdnsAfter);
        Environment.SetEnvironmentVariable("ADBDESK_TEST_PAIR_RESULT", _previousPairResult);
        Environment.SetEnvironmentVariable("ADBDESK_TEST_PAIR_EXIT", _previousPairExit);
        Environment.SetEnvironmentVariable("ADBDESK_TEST_PAIR_HANG", _previousPairHang);
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
        await File.WriteAllTextAsync(Path.Combine(directory, $"call-{DateTime.UtcNow.Ticks}-{Environment.ProcessId}.txt"), string.Join('|', arguments));
        if (arguments is ["--console-shared-server"])
        {
            await Task.Delay(TimeSpan.FromMinutes(2));
            return;
        }
        if (arguments is ["-s", "console", "shell", "console-start-shared-server"] or ["-s", "capture-shared-server", "exec-out", "screencap", "-p"])
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--console-shared-server");
            using var sharedServer = Process.Start(startInfo) ?? throw new InvalidOperationException("Fake shared server failed to start.");
            var processIdsPath = Path.Combine(directory, "console-server-processes.txt");
            await File.WriteAllTextAsync(processIdsPath + ".tmp", $"{Environment.ProcessId}|{sharedServer.Id}");
            File.Move(processIdsPath + ".tmp", processIdsPath);
            await Task.Delay(TimeSpan.FromMinutes(2));
            return;
        }
        if (arguments is ["-s", "console", "shell", "console-flood"])
        {
            await Task.WhenAll(Console.Out.WriteAsync(new string('o', 1024 * 1024)), Console.Error.WriteAsync(new string('e', 1024 * 1024)));
            await Task.Delay(500);
            Environment.ExitCode = 7;
            return;
        }
        if (arguments is ["-s", "console", "shell", "console-hang"])
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "console-pid.txt"), Environment.ProcessId.ToString());
            await Task.Delay(TimeSpan.FromMinutes(2));
            return;
        }
        if (arguments is ["pair", "split-secret:37001"])
        {
            var secret = (await Console.In.ReadToEndAsync()).Trim();
            await Console.Out.WriteAsync(secret[..3]);
            await Console.Out.FlushAsync();
            await Task.Delay(150);
            await Console.Out.WriteAsync(secret[3..]);
            return;
        }
        if (arguments is ["-s", var captureSerial, "exec-out", "screencap", "-p"])
        {
            if (captureSerial == "capture-hang")
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "capture-pid.txt"), Environment.ProcessId.ToString());
                await Task.Delay(TimeSpan.FromMinutes(2));
                return;
            }
            if (captureSerial == "capture-oversize")
            {
                await Console.OpenStandardOutput().WriteAsync(new byte[65 * 1024 * 1024]);
                return;
            }
            var path = ResponsePath(directory, captureSerial, "screenshot");
            if (File.Exists(path + ".failure"))
            {
                Console.Error.WriteLine(await File.ReadAllTextAsync(path + ".failure"));
                Environment.ExitCode = 7;
                return;
            }
            await Console.Error.WriteAsync(new string('x', 128 * 1024));
            var bytes = Convert.FromBase64String(await File.ReadAllTextAsync(path));
            await Console.OpenStandardOutput().WriteAsync(bytes);
            return;
        }
        if (arguments.SequenceEqual(["devices", "-l"]))
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "adb-invocation.txt"), string.Join('|', arguments));
            var removedPath = Path.Combine(directory, "disconnected.txt");
            var removed = File.Exists(removedPath) ? await File.ReadAllLinesAsync(removedPath) : [];
            var rows = (Environment.GetEnvironmentVariable("ADBDESK_TEST_DEVICES") ?? "").Split('\n');
            Console.Write(string.Join('\n', rows.Where(row => !removed.Contains(row.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()))));
            return;
        }
        if (arguments is ["disconnect", var disconnectSerial])
        {
            await RespondAsync(directory, disconnectSerial, "disconnect");
            var responsePath = ResponsePath(directory, disconnectSerial, "disconnect");
            if (Environment.ExitCode == 0 && File.Exists(responsePath) &&
                (await File.ReadAllTextAsync(responsePath)).Trim() == $"disconnected {disconnectSerial}" &&
                !File.Exists(ResponsePath(directory, disconnectSerial, "keep-after-disconnect")))
                await File.AppendAllTextAsync(Path.Combine(directory, "disconnected.txt"), disconnectSerial + "\n");
            return;
        }
        if (arguments.SequenceEqual(["emit"]))
        {
            Console.WriteLine("hello stdout");
            Console.Error.WriteLine("hello stderr");
            return;
        }
        if (arguments.SequenceEqual(["hang-with-shared-child"]))
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };
            start.ArgumentList.Add("hang");
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Fake daemon failed to start");
            var pidPath = Path.Combine(directory, "shared-child.pid");
            await File.WriteAllTextAsync(pidPath + ".tmp", child.Id.ToString());
            File.Move(pidPath + ".tmp", pidPath);
            await Task.Delay(TimeSpan.FromMinutes(2));
            return;
        }
        if (arguments.SequenceEqual(["hang"]))
        {
            await Task.Delay(TimeSpan.FromMinutes(2));
            return;
        }
        if (arguments.SequenceEqual(["mdns", "services"]))
        {
            var paired = File.Exists(Path.Combine(directory, "pair-complete.txt"));
            Console.Write(paired
                ? Environment.GetEnvironmentVariable("ADBDESK_TEST_MDNS_AFTER")
                : Environment.GetEnvironmentVariable("ADBDESK_TEST_MDNS_BEFORE"));
            return;
        }
        if (arguments is ["pair", _])
        {
            var secret = (await Console.In.ReadToEndAsync()).Trim();
            if (secret.Length == 0)
            {
                Console.Error.WriteLine("pairing secret missing from stdin");
                Environment.ExitCode = 8;
                return;
            }
            if (Environment.GetEnvironmentVariable("ADBDESK_TEST_PAIR_HANG") == "1")
            {
                await File.WriteAllTextAsync(Path.Combine(directory, $"pair-{Environment.ProcessId}.start"), DateTime.UtcNow.Ticks.ToString());
                await Task.Delay(TimeSpan.FromMinutes(2));
                return;
            }
            var result = Environment.GetEnvironmentVariable("ADBDESK_TEST_PAIR_RESULT") ?? "Failed: no configured pair result";
            Console.WriteLine(result);
            if (result.Contains("Successfully paired to", StringComparison.Ordinal))
                await File.WriteAllTextAsync(Path.Combine(directory, "pair-complete.txt"), "done");
            Environment.ExitCode = int.TryParse(Environment.GetEnvironmentVariable("ADBDESK_TEST_PAIR_EXIT"), out var pairExit) ? pairExit : 0;
            return;
        }
        if (arguments.Contains("install", StringComparer.Ordinal))
        {
            await RunInstallAsync(directory, arguments);
            return;
        }
        if (arguments is ["-s", var serial, "get-devpath"])
        {
            await RespondAsync(directory, serial, "devpath");
            return;
        }
        if (arguments is ["-d", "get-serialno"])
        {
            await RespondAsync(directory, "usb-selector", "serial");
            return;
        }
        if (arguments is ["-s", var propertySerial, "shell", "getprop"])
        {
            var key = $"probe-{Environment.ProcessId}";
            await File.WriteAllTextAsync(Path.Combine(directory, $"{key}.start"), DateTime.UtcNow.Ticks.ToString());
            if (int.TryParse(Environment.GetEnvironmentVariable("ADBDESK_TEST_PROBE_DELAY_MS"), out var delay))
                await Task.Delay(delay);
            await RespondAsync(directory, propertySerial, "getprop");
            await File.WriteAllTextAsync(Path.Combine(directory, $"{key}.end"), DateTime.UtcNow.Ticks.ToString());
            return;
        }
        if (arguments is ["-s", var nameSerial, "shell", "settings", "get", "global", "device_name"])
        {
            await RespondAsync(directory, nameSerial, "device-name");
            return;
        }
        if (arguments is ["-s", _, "shell", "wm", "size"] or ["-s", _, "shell", "wm size"])
        {
            await RespondAsync(directory, arguments[1], "display-size");
            return;
        }
        if (arguments is ["-s", var networkSerial, "shell", "ip", "-o", "-4", "addr", "show"])
        {
            var kind = File.Exists(ResponsePath(directory, networkSerial, "network")) ? "network" : "wifi";
            await RespondAsync(directory, networkSerial, kind);
            return;
        }
        if (arguments is ["-s", var wifiSerial, "shell", "ip", "-o", "-4", "addr", "show", "wlan0"])
        {
            await RespondAsync(directory, wifiSerial, "wifi");
            return;
        }
        if (arguments is ["-s", var modeSerial, "tcpip", "5555"])
        {
            await RespondAsync(directory, modeSerial, "tcpip");
            return;
        }
        if (arguments is ["-s", var usbSerial, "usb"])
        {
            await RespondAsync(directory, usbSerial, "usb");
            return;
        }
        if (arguments is ["connect", var endpoint])
        {
            var counterPath = Path.Combine(directory, "connect-count.txt");
            var attempt = File.Exists(counterPath) ? int.Parse(await File.ReadAllTextAsync(counterPath)) + 1 : 1;
            await File.WriteAllTextAsync(counterPath, attempt.ToString());
            var failures = int.TryParse(Environment.GetEnvironmentVariable("ADBDESK_TEST_CONNECT_FAILURES"), out var count) ? count : 0;
            if (attempt <= failures)
            {
                Console.Error.WriteLine($"cannot connect to {endpoint}");
                Environment.ExitCode = 1;
            }
            else
            {
                Console.WriteLine($"connected to {endpoint}");
            }
            return;
        }
        if (arguments is ["-s", var stateSerial, "get-state"])
        {
            await RespondAsync(directory, stateSerial, "state");
            return;
        }
        await RunMirrorAsync(directory, arguments);
    }

    public static string ResponsePath(string directory, string serial, string kind)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serial)));
        return Path.Combine(directory, $"response-{key}-{kind}.txt");
    }

    private static async Task RespondAsync(string directory, string serial, string kind)
    {
        var path = ResponsePath(directory, serial, kind);
        if (File.Exists(path + ".failure"))
        {
            Console.Error.WriteLine(await File.ReadAllTextAsync(path + ".failure"));
            Environment.ExitCode = 7;
            return;
        }
        if (File.Exists(path)) Console.Write(await File.ReadAllTextAsync(path));
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
