using System.Text.Json;
using AdbDesk.Core;

internal static class WirelessDiscoveryTests
{
    internal static Task ParsingAsync()
    {
        var entries = MdnsServices.Parse("""
            List of discovered mdns services
            phone. _adb-tls-connect._tcp. 192.168.001.20:041133
            phone _adb-tls-connect._tcp 192.168.1.20:41133
            phone _adb-tls-connect._tcp 192.168.1.20:41134
            phone _adb._tcp 192.168.1.20:5555
            phone _adb-tls-pairing._tcp 192.168.1.20:31234
            other _adb._tcp 192.168.1.20:5555
            bad _adb._tcp 192.168.1.20:0
            bad _adb._tcp 192.168.1.20:65536
            bad _adb._tcp 192.168.1.20:+5555
            bad _adb._tcp 192.168.1.999:5555
            bad _printer._tcp 192.168.1.20:4444
            . _adb._tcp 192.168.1.20:5555
            """);
        Equal(5, entries.Count, "only identical name/type/normalized endpoint tuples are deduplicated");
        Equal("192.168.1.20:41133", entries[0].Endpoint, "fresh endpoints are normalized");
        Equal(2, entries.Count(entry => entry.Type == "_adb._tcp"), "legacy ADB advertisements remain separate by identity");
        Equal(1, MdnsServices.FindCandidates(string.Join('\n', entries.Select(e => $"{e.Name} {e.Type} {e.Endpoint}")),
            "phone", "_adb-tls-pairing._tcp", []).Count, "pairing callers still select their exact type");
        return Task.CompletedTask;
    }

    internal static async Task MatchingAsync()
    {
        using var directory = new TempDirectory();
        var output = """
            ready _adb-tls-connect._tcp 192.168.1.20:41133
            alias _adb-tls-connect._tcp 192.168.1.21:41133
            legacy _adb._tcp 192.168.1.22:5555
            usb _adb-tls-connect._tcp 192.168.1.23:41133
            offline _adb-tls-connect._tcp 192.168.1.24:41133
            unauthorized _adb-tls-connect._tcp 192.168.1.25:41133
            stale _adb-tls-connect._tcp 192.168.1.26:41133
            pairing _adb-tls-pairing._tcp 192.168.1.27:31234
            substring _adb-tls-connect._tcp 192.168.1.28:41133
            sameip _adb-tls-connect._tcp 192.168.1.20:41134
            """;
        var devices = new[]
        {
            Network("192.168.001.20:041133", name: "My phone"),
            Network("alias._adb-tls-connect._tcp."),
            Network("legacy._adb._tcp"),
            Network("192.168.1.23:41133") with { Transport = DeviceTransport.Usb },
            Network("192.168.1.24:41133", "offline"),
            Network("192.168.1.25:41133", "unauthorized"),
            Network("192.168.1.26:41132"),
            Network("pairing._adb-tls-pairing._tcp"),
            Network("prefix-substring._adb-tls-connect._tcp")
        };
        var snapshot = await Service(directory.Path, output, devices).DiscoverAsync();
        Equal(10, snapshot.Services.Count, "every advertised service remains visible");
        Equal("ready,alias,legacy", string.Join(',', snapshot.Services.Where(e => e.IsConnected).Select(e => e.Name)),
            "only precise current wireless endpoint or complete mDNS serial matches are connected");
        Equal(true, snapshot.Services.Single(e => e.Name == "pairing").IsPairing, "pairing row is classified");
        Equal<DeviceInfo?>(null, snapshot.Services.Single(e => e.Name == "pairing").Device, "pairing never borrows an ADB connection");
        Equal("offline", snapshot.Services.Single(e => e.Name == "offline").Device?.State, "offline evidence remains visible without readiness");
        Equal("My phone", snapshot.Services[0].DisplayName, "live name wins over model");
        Equal(false, snapshot.Services[0].IsHistoricalName, "live names are not marked historical");
        using var rawDirectory = new TempDirectory();
        var rawDevices = WirelessDiscoveryService.ParseDeviceSnapshot("""
            List of devices attached
            phone._adb-tls-connect._tcp device usb:1-1 model:USB_Alias transport_id:1
            192.168.1.31:41133 device usb:1-2 model:USB_Address transport_id:2
            wireless._adb-tls-connect._tcp device model:Wireless_Alias transport_id:3
            192.168.1.33:41133 device usb:unknown model:Wireless_IP transport_id:4
            emulator-5554 device usb:1-9 model:Emulator transport_id:5
            repeated._adb._tcp device usb:1-3 model:USB_Twin transport_id:6
            repeated._adb._tcp device model:Wireless_Twin transport_id:7
            """);
        Equal(DeviceTransport.Usb, rawDevices.Single(device => device.TransportId == 1).Transport,
            "physical USB evidence overrides an mDNS-shaped serial in the production parser");
        Equal(DeviceTransport.Usb, rawDevices.Single(device => device.TransportId == 2).Transport,
            "physical USB evidence overrides an IPv4-shaped serial in the production parser");
        Equal(DeviceTransport.Emulator, rawDevices.Single(device => device.TransportId == 5).Transport,
            "emulator classification remains explicit even with a USB-shaped path");
        Equal(DeviceTransport.Network, rawDevices.Single(device => device.TransportId == 4).Transport,
            "usb:unknown is not evidence of a physical USB connection");
        Equal(DeviceTransport.Network, rawDevices.Single(device => device.TransportId == 7).Transport,
            "a separate USB transport with the same serial cannot overwrite the wireless raw entry");
        var rawServices = """
            phone _adb-tls-connect._tcp 192.168.1.30:41133
            ipusb _adb-tls-connect._tcp 192.168.1.31:41133
            wireless _adb-tls-connect._tcp 192.168.1.32:41133
            ipready _adb-tls-connect._tcp 192.168.1.33:41133
            repeated _adb._tcp 192.168.1.34:5555
            """;
        var parsedSnapshot = await Service(rawDirectory.Path, rawServices, rawDevices).DiscoverAsync();
        Equal("wireless,ipready,repeated", string.Join(',', parsedSnapshot.Services.Where(entry => entry.IsConnected).Select(entry => entry.Name)),
            "production parsing preserves wireless aliases and IP matches while excluding USB-shaped serials");
        Equal("phone", parsedSnapshot.Services[0].DisplayName, "USB model is not a live wireless name");
        Equal("ipusb", parsedSnapshot.Services[1].DisplayName, "USB address does not lend its model to an advertisement");
        var afterDisconnect = await Service(rawDirectory.Path, rawServices, []).DiscoverAsync();
        Equal(false, afterDisconnect.Services[0].IsHistoricalName, "mDNS-shaped USB names are never cached");
        Equal(false, afterDisconnect.Services[1].IsHistoricalName, "IPv4-shaped USB names are never cached");
        Equal("Wireless Alias", afterDisconnect.Services[2].DisplayName, "verified wireless names still persist");
        Equal(true, afterDisconnect.Services[2].IsHistoricalName, "persisted wireless model is marked historical");
    }

    internal static async Task NamesAsync()
    {
        using var directory = new TempDirectory();
        var first = await Service(directory.Path, "phone _adb-tls-connect._tcp 192.168.1.20:41133",
            [Network("192.168.1.20:41133", name: "我的手机")]).DiscoverAsync();
        Equal("我的手机", first.Services.Single().DisplayName, "verified live names are displayed");
        var history = await Service(directory.Path, """
            phone _adb-tls-connect._tcp 192.168.1.30:42000
            stranger _adb-tls-connect._tcp 192.168.1.20:41133
            phone _adb._tcp 192.168.1.30:5555
            phone _adb-tls-pairing._tcp 192.168.1.30:32000
            """, []).DiscoverAsync();
        Equal("我的手机", history.Services[0].DisplayName, "service identity survives a changed port and IP after restart");
        Equal(true, history.Services[0].IsHistoricalName, "cached name is explicitly historical");
        Equal(false, history.Services[0].IsConnected, "cached name never grants connection status");
        Equal("stranger", history.Services[1].DisplayName, "same old IP and port do not transfer a name");
        Equal(false, history.Services.Skip(1).Any(e => e.IsHistoricalName), "type and pairing identity never borrow names");

        using var offlineDirectory = new TempDirectory();
        var offline = await Service(offlineDirectory.Path, "phone _adb-tls-connect._tcp 192.168.1.20:41133",
            [Network("192.168.1.20:41133", "offline", "Unverified")]).DiscoverAsync();
        Equal("phone", offline.Services.Single().DisplayName, "offline property data is not a live name");
        var disconnected = await Service(offlineDirectory.Path, "phone _adb-tls-connect._tcp 192.168.1.20:41133", []).DiscoverAsync();
        Equal(false, disconnected.Services.Single().IsHistoricalName, "offline names are never persisted");

        var invalid = await Service(offlineDirectory.Path, "phone _adb-tls-connect._tcp 192.168.1.20:41133",
            [Network("192.168.1.20:41133", name: "null") with { Model = "unknown" }]).DiscoverAsync();
        Equal("phone", invalid.Services.Single().DisplayName, "invalid name and model fall back to service identity");
        var model = await Service(offlineDirectory.Path, "phone _adb-tls-connect._tcp 192.168.1.20:41133",
            [Network("192.168.1.20:41133", name: "") with { Model = "Pixel 9" }]).DiscoverAsync();
        Equal("Pixel 9", model.Services.Single().DisplayName, "valid live model is a useful fallback");
    }

    internal static async Task CacheBoundsAsync()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "names.json");
        foreach (var badContent in new[] { "{broken", new string('x', 1024 * 1024 + 1), "[null,{}]" })
        {
            File.WriteAllText(path, badContent);
            var result = await Service(directory.Path, "phone _adb-tls-connect._tcp 192.168.1.20:41133", []).DiscoverAsync();
            Equal("phone", result.Services.Single().DisplayName, "corrupt or oversized cache cannot hide a fresh endpoint");
            Equal(false, result.Services.Single().IsHistoricalName, "malformed cache is not trusted");
            Equal(true, result.Warning.Length > 0, "invalid cache is reported");
        }
        File.Delete(path);
        var output = string.Join('\n', Enumerable.Range(0, 205).Select(i => $"phone{i} _adb._tcp 192.168.1.20:{5000 + i}"));
        var devices = Enumerable.Range(0, 205).Select(i => Network($"192.168.1.20:{5000 + i}", name: $"Phone {i}")).ToArray();
        await Service(directory.Path, output, devices).DiscoverAsync();
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Equal(200, json.RootElement.GetArrayLength(), "persistent names retain at most 200 identities");
        Equal(true, new FileInfo(path).Length <= 1024 * 1024, "cache file remains bounded");
        Equal(0, Directory.GetFiles(directory.Path, "*.tmp").Length, "atomic writes leave no temporary files");

        var block = Path.Combine(directory.Path, "block");
        File.WriteAllText(block, "not a directory");
        var failedWrite = await Service(block, "phone _adb._tcp 192.168.1.20:5555", [Network("192.168.1.20:5555")]).DiscoverAsync();
        Equal(true, failedWrite.Services.Single().IsConnected, "cache IO failure cannot discard connection evidence");
        Equal(true, failedWrite.Warning.Length > 0, "cache IO failure is visible");
    }

    internal static async Task FailureAsync()
    {
        using var directory = new TempDirectory();
        var mdnsFailure = new WirelessDiscoveryService(_ => Task.FromException<string>(new IOException("mdns unavailable")),
            _ => Task.FromResult<IReadOnlyList<DeviceInfo>>([]), Path.Combine(directory.Path, "names.json"));
        var empty = await mdnsFailure.DiscoverAsync();
        Equal(0, empty.Services.Count, "failed discovery never returns stale previous endpoints");
        Equal(true, empty.Warning.Length > 0, "mDNS failure is visible");
        var deviceFailure = new WirelessDiscoveryService(_ => Task.FromResult("phone _adb._tcp 192.168.1.20:5555"),
            _ => Task.FromException<IReadOnlyList<DeviceInfo>>(new IOException("devices unavailable")), Path.Combine(directory.Path, "names.json"));
        var unverified = await deviceFailure.DiscoverAsync();
        Equal(1, unverified.Services.Count, "device read failure does not hide advertised endpoint");
        Equal(false, unverified.Services.Single().IsConnected, "device read failure never grants connected status");
        Equal(true, unverified.Warning.Length > 0, "device read failure is visible");

        var oversized = await Service(directory.Path, "phone _adb._tcp 192.168.1.20:5555\n" + new string('x', 128 * 1024), []).DiscoverAsync();
        Equal(0, oversized.Services.Count, "oversized mDNS output does not publish a partial endpoint set");
        Equal(true, oversized.Warning.Length > 0, "oversized discovery output is reported");

        var nameReads = new List<string>();
        var names = new WirelessDiscoveryService(_ => Task.FromResult("phone _adb._tcp 192.168.1.20:5555\npair _adb-tls-pairing._tcp 192.168.1.21:32000"),
            _ => Task.FromResult<IReadOnlyList<DeviceInfo>>([Network("192.168.1.20:5555"), Network("192.168.1.22:5555")]),
            Path.Combine(directory.Path, "names.json"), readName: (device, _) =>
            {
                nameReads.Add(device.Serial);
                return Task.FromException<DeviceInfo>(new IOException("name unavailable"));
            });
        var fallback = await names.DiscoverAsync();
        Equal("192.168.1.20:5555", string.Join(',', nameReads), "name queries target only matched ready wireless transports");
        Equal("Pixel", fallback.Services[0].DisplayName, "name query failure falls back to the live model");
        Equal(true, fallback.Services[0].IsConnected, "optional name failure preserves live status");
        Equal(true, fallback.Warning.Length > 0, "optional name failure is visible");
    }

    internal static async Task CancellationAsync()
    {
        using var directory = new TempDirectory();
        var never = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new WirelessDiscoveryService(_ => never.Task, _ => Task.FromResult<IReadOnlyList<DeviceInfo>>([]),
            Path.Combine(directory.Path, "names.json"), TimeSpan.FromMilliseconds(30));
        var timed = await service.DiscoverAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Equal(0, timed.Services.Count, "deadline returns no invented endpoints");
        Equal(true, timed.Warning.Length > 0, "bounded discovery reports timeout");
        var nameNever = new TaskCompletionSource<DeviceInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowName = new WirelessDiscoveryService(_ => Task.FromResult("phone _adb._tcp 192.168.1.20:5555"),
            _ => Task.FromResult<IReadOnlyList<DeviceInfo>>([Network("192.168.1.20:5555")]),
            Path.Combine(directory.Path, "slow-name.json"), TimeSpan.FromMilliseconds(30), (_, _) => nameNever.Task);
        var named = await slowName.DiscoverAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Equal(true, named.Services.Single().IsConnected, "slow name lookup retains fresh connection evidence");
        Equal("Pixel", named.Services.Single().DisplayName, "slow name lookup falls back to live model");
        Equal(true, named.Warning.Length > 0, "slow optional lookup is reported");
        nameNever.SetResult(Network("192.168.1.20:5555", name: "Late name"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => service.DiscoverAsync(cancellation.Token));
        using var activeCancellation = new CancellationTokenSource();
        var waiting = new WirelessDiscoveryService(async token => { await Task.Delay(Timeout.Infinite, token); return ""; },
            _ => Task.FromResult<IReadOnlyList<DeviceInfo>>([]), Path.Combine(directory.Path, "names.json"));
        var active = waiting.DiscoverAsync(activeCancellation.Token);
        activeCancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => active.WaitAsync(TimeSpan.FromSeconds(2)));
        Equal(false, File.Exists(Path.Combine(directory.Path, "names.json")), "canceled discovery does not persist unverified names");
    }

    private static WirelessDiscoveryService Service(string directory, string output, IReadOnlyList<DeviceInfo> devices) =>
        new(_ => Task.FromResult(output), _ => Task.FromResult(devices), Path.Combine(directory, "names.json"));
    private static DeviceInfo Network(string serial, string state = "device", string name = "") =>
        new(serial, "Pixel", state) { Transport = DeviceTransport.Network, DeviceName = name };
    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{message}: {expected} != {actual}");
    }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
}