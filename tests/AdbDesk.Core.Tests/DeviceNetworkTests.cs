using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AdbDesk.Core;

internal static class DeviceNetworkTests
{
    internal static Task AddressesAsync()
    {
        var network = DeviceNetworkInfo.Parse("""
            1: lo inet 127.0.0.1/8 scope host lo
            4: tun0 inet 172.19.0.1/30 scope global tun0
            7: wlan0 inet 192.168.137.208/24 brd 192.168.137.255 scope global wlan0
            8: rmnet_data0 inet 10.1.2.3/30 scope global rmnet_data0
            9: wlan1@if12 inet 172.20.1.20/24 scope global wlan1
            10: wifi0 inet 169.254.3.4/16 scope link wifi0
            """
        );
        Equal("192.168.137.208", network.WifiAddress, "Wi-Fi is selected by interface, not the first IPv4 or default VPN route");
        Equal("172.19.0.1", network.VpnAddresses.Single().Address, "VPN address is separate");
        Equal(2, network.WifiAddresses.Count, "link-local Wi-Fi is excluded");
        Equal("172.20.1.20", network.SelectWifiAddress([new("LAN", "172.20.1.1", "255.255.255.0", "", "Wi-Fi", true)]), "172-range Wi-Fi remains valid and subnet can select secondary interface");
        Equal("", DeviceNetworkInfo.Parse("4: tun0 inet 172.19.0.1/30 scope global tun0").WifiAddress, "VPN alone never becomes Wi-Fi");
        Equal("", DeviceNetworkInfo.Parse("7: wlan0 inet 999.1.1.1/24 scope global wlan0").WifiAddress, "invalid IP rejected");
        Equal("", DeviceNetworkInfo.Parse("7: wlan0 inet 192.168.1.2/99 scope global wlan0").WifiAddress, "invalid prefix rejected");
        return Task.CompletedTask;
    }

    internal static Task HotspotAsync()
    {
        var kind = LocalNetwork.ClassifyAdapter("本地连接* 12", "Microsoft Wi-Fi Direct Virtual Adapter #2", NetworkInterfaceType.Wireless80211);
        Equal(("电脑热点", true), kind, "Windows Wi-Fi Direct virtual adapter supports hotspot LAN");
        Equal(("其他网卡", false), LocalNetwork.ClassifyAdapter("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet), "WSL stays excluded");
        Equal(("其他网卡", false), LocalNetwork.ClassifyAdapter("Clash", "Mihomo TUN", NetworkInterfaceType.Ethernet), "VPN stays excluded");
        Equal(SubnetMatch.SameSubnet, LocalNetwork.Compare("192.168.137.208:41133", [new("Hotspot", "192.168.137.1", "255.255.255.0", "", kind.Item1, kind.Item2)]), "hotspot without default gateway still matches phone");
        var services = "phone _adb-tls-connect._tcp 172.19.0.1:41133\nother _adb-tls-connect._tcp 192.168.137.30:44444\nphone _adb-tls-connect._tcp 192.168.137.208:41133";
        var choices = MdnsServices.FindCandidates(services, "phone", MdnsServices.ConnectType, [new("Hotspot", "192.168.137.1", "255.255.255.0", "", "电脑热点", true)]);
        Equal("192.168.137.208:41133", choices[0], "same-service hotspot address is preferred");
        Equal(2, choices.Count, "another phone is never a fallback candidate");
        return Task.CompletedTask;
    }

    internal static async Task ServiceAsync()
    {
        using var fixture = new ToolFixture();
        fixture.SetDevices("List of devices attached\nphone device usb:1-1 transport_id:5\n");
        fixture.SetResponse("phone", "network", "4: tun0 inet 172.19.0.1/30 scope global tun0\n7: wlan1 inet 192.168.137.208/24 scope global wlan1");
        fixture.SetResponse("phone", "tcpip", "restarting in TCP mode port: 5555");
        fixture.SetResponse("192.168.137.208:5555", "state", "device");
        var service = new AdbService(fixture.Paths);
        var first = (await service.GetDevicesAsync()).Single();
        Equal("192.168.137.208", first.Network.WifiAddress, "card has actual Wi-Fi IP while VPN is enabled");
        await service.GetDevicesAsync();
        Equal(1, fixture.CountInvocation("phone", "ip"), "device polling does not query network IP every two seconds");
        var result = await service.SetWirelessAsync(first, true);
        Equal(true, result.Connected, "wireless enables through a secondary Wi-Fi interface");
        Equal("192.168.137.208:5555", result.Endpoint, "USB mode uses real Wi-Fi and configured TCP port");
        Equal(false, fixture.HasInvocation("connect", "172.19.0.1:5555"), "VPN endpoint is not connected");
        fixture.SetResponse("phone", "network", "4: tun0 inet 172.19.0.1/30 scope global tun0\n7: wlan1 inet 192.168.137.209/24 scope global wlan1");
        Equal("192.168.137.209", (await service.GetNetworkAsync(first)).WifiAddress, "explicit refresh bypasses cached network addresses");
        fixture.SetResponse("phone", "network", "4: tun0 inet 172.19.0.1/30 scope global tun0");
        var before = fixture.CountCommand("tcpip");
        Equal(false, (await service.SetWirelessAsync(first, true)).Succeeded, "VPN-only device cannot enable with a guessed IP");
        Equal(before, fixture.CountCommand("tcpip"), "no daemon mutation without Wi-Fi evidence");
    }

    internal static async Task OverrideAsync()
    {
        Equal("192.168.137.208:41133", NetworkConnectionDiagnostics.ApplyWifiHost("172.19.0.1:41133", "192.168.137.208"), "replace host only, preserve connection port");
        Throws<ArgumentException>(() => NetworkConnectionDiagnostics.ApplyWifiHost("172.19.0.1:41133", "192.168.137.208:5555"));
        using var fixture = new ToolFixture();
        fixture.SetPairResult("Successfully paired to 192.168.137.208:31234 [guid=TEST-GUID]");
        fixture.SetResponse("192.168.137.208:41133", "state", "device");
        var checkedEndpoints = new List<string>();
        var service = new PairingService(fixture.Paths) { ConfirmEndpointAsync = (endpoint, _) => { checkedEndpoints.Add(endpoint); return Task.FromResult(true); } };
        var result = await service.PairWithCodeAsync("172.19.0.1:31234", "123456", "172.19.0.1:41133", wifiHostOverride: "192.168.137.208");
        Equal(true, result.Connected, "manual correction pairs and connects");
        Equal(true, fixture.HasInvocation("pair", "192.168.137.208:31234"), "pairing port preserved");
        Equal(true, fixture.HasInvocation("connect", "192.168.137.208:41133"), "connection port preserved");
        Equal(false, fixture.AllInvocationText().Contains("172.19.0.1"), "incorrect address is not submitted");
        Equal("192.168.137.208:31234", checkedEndpoints[0], "network preflight sees corrected address");
        var qr = PairingService.CreateQrSession();
        using var qrFixture = new ToolFixture();
        qrFixture.SetPairResult("Successfully paired to 192.168.137.208:31234 [guid=TEST-GUID]");
        qrFixture.SetResponse("192.168.137.208:41133", "state", "device");
        qrFixture.SetMdnsBefore($"{qr.ServiceName} _adb-tls-pairing._tcp 172.19.0.1:31234");
        qrFixture.SetMdnsAfter("TEST-GUID _adb-tls-connect._tcp 172.19.0.1:41133");
        Equal(true, (await qrFixture.PairingService().PairWithQrAsync(qr, wifiHostOverride: "192.168.137.208")).Connected, "QR correction applies to both discovered service ports");
    }

    internal static async Task DiagnosticsAsync()
    {
        var text = NetworkConnectionDiagnostics.DescribeFailure("192.168.137.208:41133", "connection refused");
        Equal(true, text.Contains("拒绝"), "refused is distinct from unreachable");
        Equal(true, text.Contains("41133"), "actual target is visible");
        Equal(true, NetworkConnectionDiagnostics.DescribeFailure("192.168.137.208:41133", "failed to authenticate").Contains("配对"), "authentication is distinct from TCP failure");
        Equal(true, NetworkConnectionDiagnostics.DescribeFailure("192.168.137.208:41133", "operation timed out").Contains("VPN"), "timeout suggests VPN as a possibility");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var probe = await NetworkConnectionDiagnostics.ProbeTcpAsync($"127.0.0.1:{port}", CancellationToken.None);
            Equal(true, probe.Reachable, "port check succeeds without pretending ADB authenticated");
            Equal(true, probe.Message.Contains("不代表"), "TCP success is not ADB readiness");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => NetworkConnectionDiagnostics.ProbeTcpAsync($"127.0.0.1:{port}", canceled.Token));
        }
        finally { listener.Stop(); }
    }

    private static void Equal<T>(T expected, T actual, string message) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{message}: {expected} != {actual}"); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
}
