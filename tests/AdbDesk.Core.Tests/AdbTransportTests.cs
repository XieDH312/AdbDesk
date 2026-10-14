using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AdbDesk.Core;

internal static class AdbTransportTests
{
    // Hand-encoded AOSP adb_host.proto: usb-A/id 1 and usb-B/id 2 are DEVICE/USB;
    // alias/id 3 is DEVICE/SOCKET. An empty bus_address is intentional on Windows.
    internal static byte[] TwoUsbAndSocket => Convert.FromHexString(
        "0A0D0A057573622D41100838015001" +
        "0A0D0A057573622D42100838015002" +
        "0A0D0A05616C696173100838025003");

    internal static Task ParseAsync()
    {
        var snapshot = AdbTransportSnapshot.Parse(TwoUsbAndSocket);
        Equal(DeviceTransport.Usb, snapshot.Classify(Device("usb-A", 1)), "first Windows USB");
        Equal(DeviceTransport.Usb, snapshot.Classify(Device("usb-B", 2)), "second Windows USB");
        Equal(DeviceTransport.Network, snapshot.Classify(Device("alias", 3)), "socket with a non-IP serial is never USB");
        Equal(DeviceTransport.Unknown, snapshot.Classify(Device("usb-A", 99)), "replugged transport ID cannot inherit classification");
        Equal(DeviceTransport.Unknown, snapshot.Classify(Device("other", 1)), "ID alone cannot identify a device");
        Equal(DeviceTransport.Unknown, snapshot.Classify(new("usb-A", "Phone", "device")), "missing ID is not guessed");
        Equal(DeviceTransport.Unknown, snapshot.Classify(Device("usb-A", 1) with { State = "offline" }), "racing state cannot authorize a USB operation");
        Equal(DeviceTransport.Unknown, AdbTransportSnapshot.Parse([]).Classify(Device("usb-A", 1)), "empty snapshot");
        var duplicate = AdbTransportSnapshot.Parse([.. TwoUsbAndSocket, .. TwoUsbAndSocket]);
        Equal(DeviceTransport.Unknown, duplicate.Classify(Device("usb-A", 1)), "duplicate matches are ambiguous");
        var future = AdbTransportSnapshot.Parse([.. TwoUsbAndSocket, 0x18, 0x01]);
        Equal(DeviceTransport.Usb, future.Classify(Device("usb-B", 2)), "unknown valid protobuf field is skipped");
        foreach (var invalid in new byte[][] { [0x0A, 0xFF], [0], [0x0A, 0x02, 0x0A, 0x7F], [0x0B], [0x0A, 0x03, 0x0A, 0x01, 0xFF] })
            Throws<InvalidDataException>(() => AdbTransportSnapshot.Parse(invalid));
        return Task.CompletedTask;
    }

    internal static async Task FrameAsync()
    {
        using var framed = new MemoryStream([.. Encoding.ASCII.GetBytes("002D"), .. TwoUsbAndSocket, 0xFF]);
        var bytes = await AdbTransportProbe.ReadFrameAsync(framed, CancellationToken.None);
        if (!bytes.SequenceEqual(TwoUsbAndSocket)) throw new Exception("ADB frame must preserve binary payload");
        Equal(49L, framed.Position, "only the initial snapshot is read");
        using var invalidHeader = new MemoryStream(Encoding.ASCII.GetBytes("ZZZZ"));
        await ThrowsAsync<InvalidDataException>(() => AdbTransportProbe.ReadFrameAsync(invalidHeader, CancellationToken.None));
        foreach (var invalid in new byte[][] { Encoding.ASCII.GetBytes("0003x"), [0x30] })
        {
            using var input = new MemoryStream(invalid);
            await ThrowsAsync<EndOfStreamException>(() => AdbTransportProbe.ReadFrameAsync(input, CancellationToken.None));
        }
        using var empty = new MemoryStream(Encoding.ASCII.GetBytes("0000"));
        Equal(0, (await AdbTransportProbe.ReadFrameAsync(empty, CancellationToken.None)).Length, "no devices is a valid frame");
        // Raw smart-socket bytes must survive Windows without CRT newline translation,
        // including a trailing CR (transport ID 13) and CRLF inside the protobuf.
        var controlBytes = Convert.FromHexString("0A0D0A057573622D4110083801500D");
        using var response = new MemoryStream([.. Encoding.ASCII.GetBytes("OKAY000F"), .. controlBytes, 0xFF]);
        var snapshot = await AdbTransportProbe.ReadResponseAsync(response, CancellationToken.None);
        Equal(DeviceTransport.Usb, snapshot.Classify(Device("usb-A", 13)), "raw binary CR byte is preserved");
        Equal(23L, response.Position, "no next-frame lookahead for trailing CR");
        using var refused = new MemoryStream(Encoding.ASCII.GetBytes("FAIL000Bunsupported"));
        await ThrowsAsync<InvalidDataException>(() => AdbTransportProbe.ReadResponseAsync(refused, CancellationToken.None));
        using var badStatus = new MemoryStream(Encoding.ASCII.GetBytes("NOPE"));
        await ThrowsAsync<InvalidDataException>(() => AdbTransportProbe.ReadResponseAsync(badStatus, CancellationToken.None));
    }

    internal static Task EndpointAsync()
    {
        Equal(("127.0.0.1", 5037), AdbTransportProbe.ResolveEndpoint(null, null, null), "default ADB server");
        Equal(("127.0.0.1", 5040), AdbTransportProbe.ResolveEndpoint("tcp:5040", "other", "5041"), "socket override wins");
        Equal(("remote.example", 5042), AdbTransportProbe.ResolveEndpoint("tcp:remote.example:5042", null, null), "explicit remote endpoint");
        Equal(("::1", 5042), AdbTransportProbe.ResolveEndpoint("tcp:[::1]:5042", null, null), "IPv6 TCP endpoint");
        Equal(("127.0.0.1", 5037), AdbTransportProbe.ResolveEndpoint("tcp:localhost:5037", null, null), "ADB localhost selects IPv4 loopback");
        Equal(("remote.example", 5043), AdbTransportProbe.ResolveEndpoint(null, "remote.example", "5043"), "legacy host and port");
        Equal(("::1", 5043), AdbTransportProbe.ResolveEndpoint(null, "[::1]", "5043"), "bracketed legacy IPv6 address");
        Equal(("127.0.0.1", 5037), AdbTransportProbe.ResolveEndpoint(null, null, ""), "empty legacy port uses default");
        foreach (var socket in new[] { "", "localfilesystem:/tmp/adb", "tcp:", "tcp:0", "tcp:65536", "tcp:host", "tcp:host:", "tcp:host:abc", "tcp:::1:5037", "tcp:name._adb-tls-connect._tcp:5037", "tcp: localhost:5037" })
            Throws<NotSupportedException>(() => AdbTransportProbe.ResolveEndpoint(socket, "fallback", "5037"));
        foreach (var port in new[] { "0", "65536", "bad" })
            Throws<NotSupportedException>(() => AdbTransportProbe.ResolveEndpoint(null, null, port));
        Throws<NotSupportedException>(() => AdbTransportProbe.ResolveEndpoint(null, "::1", "5037"));
        Throws<NotSupportedException>(() => AdbTransportProbe.ResolveEndpoint(null, "", "5037"));
        return Task.CompletedTask;
    }

    internal static async Task MultipleUsbAsync()
    {
        using var fixture = new ToolFixture();
        fixture.SetDevices("List of devices attached\nusb-A device model:One transport_id:1\nusb-B device model:Two transport_id:2\nalias device model:Remote transport_id:3\nemulator-5554 device model:Virtual transport_id:4\n");
        foreach (var serial in new[] { "usb-A", "usb-B", "alias" }) fixture.SetResponse(serial, "devpath", "unknown");
        fixture.SetFailure("usb-selector", "serial", "error: more than one device");
        fixture.SetResponse("transport", "snapshot", Convert.ToBase64String(TwoUsbAndSocket));
        var service = new AdbService(fixture.Paths);
        var devices = await service.GetDevicesAsync();
        Equal(DeviceTransport.Usb, devices.Single(d => d.Serial == "usb-A").Transport, "first USB shown despite missing devpath");
        Equal(DeviceTransport.Usb, devices.Single(d => d.Serial == "usb-B").Transport, "second USB shown despite missing devpath");
        Equal(DeviceTransport.Network, devices.Single(d => d.Serial == "alias").Transport, "socket kept separate");
        Equal(DeviceTransport.Emulator, devices.Single(d => d.Serial == "emulator-5554").Transport, "emulator is never reclassified as USB");
        fixture.SetResponse("usb-A", "wifi", "7: wlan0 inet 192.0.2.44/24 scope global wlan0");
        fixture.SetResponse("usb-A", "tcpip", "restarting in TCP mode port: 5555");
        fixture.SetResponse("192.0.2.44:5555", "state", "device");
        if (!(await service.SetWirelessAsync(devices.Single(d => d.Serial == "usb-A"), true)).Connected)
            throw new Exception("Multiple USB devices should allow wireless enable on the selected device");
        if (!fixture.HasInvocation("-s", "usb-A", "tcpip", "5555") || fixture.HasInvocation("-s", "usb-B", "tcpip", "5555"))
            throw new Exception("Wireless enable must not switch the other USB phone");
        fixture.SetResponse("usb-B", "usb", "restarting in USB mode");
        if (!(await service.SetWirelessAsync(devices.Single(d => d.Serial == "usb-B"), false)).Succeeded)
            throw new Exception("Fresh per-device USB evidence should allow selected-device wireless action");
        if (!fixture.HasInvocation("-s", "usb-B", "usb") || fixture.HasInvocation("-s", "usb-A", "usb"))
            throw new Exception("Wireless action must target only selected USB serial");
        var mutations = fixture.CountCommand("usb");
        // Same serial, same ID, fresh server evidence now says SOCKET; cached USB must not authorize it.
        fixture.SetResponse("transport", "snapshot", Convert.ToBase64String(Convert.FromHexString("0A0D0A057573622D42100838025002")));
        if ((await service.SetWirelessAsync(devices.Single(d => d.Serial == "usb-B"), false)).Succeeded)
            throw new Exception("Cached USB must not override fresh SOCKET evidence");
        Equal(mutations, fixture.CountCommand("usb"), "no mutation on a socket transport");
        await fixture.WaitForProcessesToExitAsync(TimeSpan.FromSeconds(3));
        await TestWait.UntilAsync(() => fixture.TransportServer.ActiveConnections == 0, TimeSpan.FromSeconds(3));
    }

    internal static async Task RefreshAsync()
    {
        using var fixture = new ToolFixture();
        fixture.SetDevices("List of devices attached\nusb-A device model:One transport_id:1\n");
        fixture.SetResponse("usb-A", "devpath", "unknown");
        var service = new AdbService(fixture.Paths);
        Equal(DeviceTransport.Unknown, (await service.GetDevicesAsync()).Single().Transport, "unsupported initial probe remains unknown");
        fixture.SetResponse("transport", "snapshot", Convert.ToBase64String(TwoUsbAndSocket));
        Equal(DeviceTransport.Usb, (await service.GetDevicesAsync()).Single().Transport, "fresh snapshot fixes classification before property cache expires");
        Equal(1, fixture.CountInvocation("usb-A", "getprop"), "model property caching is preserved");
        fixture.SetResponse("transport", "snapshot", "");
        Equal(DeviceTransport.Unknown, (await service.GetDevicesAsync()).Single().Transport, "vanished live evidence is never cached as USB");
        fixture.SetDevices("List of devices attached\nusb-A device model:One transport_id:7\n");
        fixture.SetResponse("transport", "snapshot", Convert.ToBase64String(Convert.FromHexString("0A0D0A057573622D41100838015007")));
        Equal(DeviceTransport.Usb, (await service.GetDevicesAsync()).Single().Transport, "replugged transport gets fresh evidence");
        Equal(2, fixture.CountInvocation("usb-A", "getprop"), "transport replacement invalidates old property cache");
        await fixture.WaitForProcessesToExitAsync(TimeSpan.FromSeconds(3));
    }

    internal static async Task ConfirmationAsync()
    {
        using var fixture = new ToolFixture();
        fixture.SetDevices("List of devices attached\nusb-A device model:One transport_id:1\nusb-B device model:Two transport_id:2\n");
        fixture.SetResponse("transport", "snapshot", Convert.ToBase64String(TwoUsbAndSocket));
        fixture.SetResponse("usb-B", "wifi", "7: wlan0 inet 192.0.2.44/24 scope global wlan0");
        var service = new AdbService(fixture.Paths)
        {
            ConfirmEndpointAsync = (_, _) =>
            {
                fixture.SetResponse("transport", "snapshot", Convert.ToBase64String(Convert.FromHexString("0A0D0A057573622D42100838025002")));
                return Task.FromResult(true);
            }
        };
        var device = (await service.GetDevicesAsync()).Single(d => d.Serial == "usb-B");
        if ((await service.SetWirelessAsync(device, true)).Succeeded)
            throw new Exception("USB evidence must be rechecked after the network confirmation dialog");
        Equal(0, fixture.CountCommand("tcpip"), "no TCP mutation after USB evidence changes during confirmation");
        await fixture.WaitForProcessesToExitAsync(TimeSpan.FromSeconds(3));
    }

    internal static async Task CancellationAsync()
    {
        using var fixture = new ToolFixture();
        fixture.SetResponse("transport", "hang", "1");
        using var cancellation = new CancellationTokenSource();
        var pending = AdbTransportProbe.ReadAsync(TimeSpan.FromSeconds(10), cancellation.Token);
        await TestWait.UntilAsync(() => fixture.TransportServer.Requests > 0, TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(async () => await pending);
        await fixture.WaitForProcessesToExitAsync(TimeSpan.FromSeconds(3));
        await TestWait.UntilAsync(() => fixture.TransportServer.ActiveConnections == 0, TimeSpan.FromSeconds(3));
        var timeout = await AdbTransportProbe.ReadAsync(TimeSpan.FromMilliseconds(500), CancellationToken.None);
        Equal(DeviceTransport.Unknown, timeout.Classify(Device("usb-A", 1)), "timeout cannot become USB evidence");
        await fixture.WaitForProcessesToExitAsync(TimeSpan.FromSeconds(3));
        await TestWait.UntilAsync(() => fixture.TransportServer.ActiveConnections == 0, TimeSpan.FromSeconds(3));
    }

    private static DeviceInfo Device(string serial, long id) => new(serial, "Phone", "device") { TransportId = id };
    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{message}: expected {expected}, actual {actual}");
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
}

// Every ToolFixture points at its own mock server, including old tests whose
// metadata service is unsupported. Tests can never query a developer's ADB server.
internal sealed class TestAdbTransportServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly ConcurrentBag<Task> _handlers = new();
    private readonly Task _loop;
    private readonly string _directory;
    private int _requests;
    public int Requests => Volatile.Read(ref _requests);
    public int ActiveConnections => _clients.Count;
    public string SocketSpec { get; }

    public TestAdbTransportServer(string directory)
    {
        _directory = directory;
        _listener.Start();
        SocketSpec = $"tcp:127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _loop = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _clients.TryAdd(client, 0);
                _handlers.Add(HandleAsync(client));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var header = new byte[4];
                await stream.ReadExactlyAsync(header, _stop.Token);
                var request = new byte[int.Parse(Encoding.ASCII.GetString(header), NumberStyles.HexNumber, CultureInfo.InvariantCulture)];
                await stream.ReadExactlyAsync(request, _stop.Token);
                if (Encoding.ASCII.GetString(request) != "host:track-devices-proto-binary")
                    throw new InvalidDataException("Unexpected mock ADB service request");
                Interlocked.Increment(ref _requests);
                if (!File.Exists(FakeTool.ResponsePath(_directory, "transport", "hang")))
                {
                    var path = FakeTool.ResponsePath(_directory, "transport", "snapshot");
                    var exists = File.Exists(path);
                    var bytes = exists ? Convert.FromBase64String(await File.ReadAllTextAsync(path, _stop.Token)) : Encoding.UTF8.GetBytes("unsupported service");
                    var prefix = Encoding.ASCII.GetBytes((exists ? "OKAY" : "FAIL") + bytes.Length.ToString("X4"));
                    // Deliberately fragment status/length and payload, like a TCP stream.
                    foreach (var value in prefix) await stream.WriteAsync(new byte[] { value }, _stop.Token);
                    await stream.WriteAsync(bytes, _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                }
                // Keep the mock server alive until the probe closes just its connection.
                var oneByte = new byte[1];
                if (await stream.ReadAsync(oneByte, _stop.Token) != 0)
                    throw new InvalidDataException("Unexpected request after initial snapshot");
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        finally { _clients.TryRemove(client, out _); }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _loop.GetAwaiter().GetResult();
        foreach (var client in _clients.Keys) client.Dispose();
        Task.WhenAll(_handlers).GetAwaiter().GetResult();
        _stop.Dispose();
    }
}
