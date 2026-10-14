namespace AdbDesk.Core;

public sealed class AdbService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DetailTimeout = TimeSpan.FromSeconds(6);
    private static readonly HashSet<string> KnownStates = new(StringComparer.Ordinal)
    {
        "device", "offline", "unauthorized", "recovery", "sideload", "bootloader", "host"
    };

    private readonly ToolPaths _paths;
    private readonly SemaphoreSlim _detailGate = new(4, 4);
    private readonly DeviceDetailsCache _detailsCache;
    private readonly DeviceNetworkCache _networkCache = new();
    public Func<string, CancellationToken, Task<bool>>? ConfirmEndpointAsync { get; init; }

    public AdbService(ToolPaths paths) : this(paths, TimeSpan.FromSeconds(30)) { }

    internal AdbService(ToolPaths paths, TimeSpan cacheLifetime)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _detailsCache = new DeviceDetailsCache(cacheLifetime);
    }

    public async Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = await GetRawDevicesAsync(cancellationToken).ConfigureAwait(false);
        var enriched = await Task.WhenAll(devices.Select(device => EnrichAsync(device, cancellationToken))).ConfigureAwait(false);
        return enriched;
    }

    public void InvalidateNetworkCache() => _networkCache.Clear();

    public async Task<MirrorDisplaySize> GetDisplaySizeAsync(DeviceInfo device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        cancellationToken.ThrowIfCancellationRequested();
        if (!device.IsReady || string.IsNullOrWhiteSpace(device.Serial)) throw new InvalidOperationException("请先连接并授权目标设备。");
        var output = new System.Text.StringBuilder();
        var error = new System.Text.StringBuilder();
        var truncated = false;
        // This runner cancels only its own adb client, preserving the shared server.
        var exitCode = await new ConsoleCommandRunner(_paths).RunAsync(ConsoleCommand.Parse("shell wm size", device.Serial),
            (text, isError) =>
            {
                var destination = isError ? error : output;
                var remaining = 8192 - destination.Length;
                if (text.Length > remaining) truncated = true;
                if (remaining > 0) destination.Append(text.AsSpan(0, Math.Min(text.Length, remaining)));
            }, DetailTimeout, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (exitCode != 0) throw new IOException($"无法读取手机分辨率：{(error.Length > 0 ? error : output)}");
        if (truncated) throw new InvalidDataException("手机分辨率查询返回了过多数据。");
        return MirrorDisplayGeometry.ParseNaturalSize(output.ToString());
    }

    public async Task<DeviceNetworkInfo> GetNetworkAsync(DeviceInfo device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        cancellationToken.ThrowIfCancellationRequested();
        if (!device.IsReady || device.Transport == DeviceTransport.Emulator) return DeviceNetworkInfo.Empty;
        var result = await RunTargetedAsync(device.Serial, ["shell", "ip", "-o", "-4", "addr", "show"], DetailTimeout, cancellationToken).ConfigureAwait(false);
        var network = result.Succeeded ? DeviceNetworkInfo.Parse(result.Output) : DeviceNetworkInfo.Empty with { Error = result.ErrorOrOutput };
        _networkCache.Set($"{device.Serial}\0{device.TransportId}", network);
        return network;
    }

    public async Task<DisconnectResult> DisconnectWirelessAsync(DeviceInfo device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(device.Serial) || !device.IsNetworkConnection)
            throw new InvalidOperationException("仅可断开指定的无线 ADB 连接。");

        var current = await GetRawDevicesAsync(cancellationToken).ConfigureAwait(false);
        if (!current.Any(candidate => candidate.Serial == device.Serial))
            return new DisconnectResult(true, "该无线连接已不在设备列表中。");

        // Never omit the serial: a bare 'adb disconnect' disconnects every TCP device.
        // ADB also accepts an exact mDNS transport serial here, including offline transports.
        var result = await RunAsync(["disconnect", device.Serial], CommandTimeout, cancellationToken).ConfigureAwait(false);
        _detailsCache.Clear();
        if (!result.Succeeded || !result.Output.Split('\n').Any(line => line.Trim() == $"disconnected {device.Serial}"))
            return new DisconnectResult(false, $"断开失败：{result.ErrorOrOutput}");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0) await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            current = await GetRawDevicesAsync(cancellationToken).ConfigureAwait(false);
            if (!current.Any(candidate => candidate.Serial == device.Serial))
                return new DisconnectResult(true, "已断开当前无线连接；手机无线调试开关和配对记录保持不变。");
        }
        return new DisconnectResult(false, "ADB 已处理断开请求，但设备仍在列表中，可能正在重新连接。请检查手机或其他连接工具。");
    }

    public async Task<WirelessResult> SetWirelessAsync(
        DeviceInfo device,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        cancellationToken.ThrowIfCancellationRequested();

        var current = (await GetRawDevicesAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => candidate.Serial == device.Serial);
        if (current is null)
            return Failure(device.WirelessPort is not null, "设备已断开，未执行无线调试切换。");
        if (!current.IsReady)
            return Failure(device.WirelessPort is not null, $"设备当前状态为 {current.State}，请在手机上重新授权后再试。");

        var devPath = await RunTargetedAsync(current.Serial, ["get-devpath"], DetailTimeout, cancellationToken).ConfigureAwait(false);
        var transport = await ResolveTransportAsync(current, devPath, cancellationToken).ConfigureAwait(false);
        if (transport != DeviceTransport.Usb)
            return Failure(device.WirelessPort is not null, "无法确认当前设备为 USB 连接，未执行切换。若插有多台手机，请暂时只保留目标手机的 USB 数据线后重试。");

        if (!enabled) return await DisableWirelessAsync(device, cancellationToken).ConfigureAwait(false);

        var wifi = await GetNetworkAsync(current, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<LocalNetworkAddress> local;
        try { local = LocalNetwork.ReadAddresses(); } catch { local = []; }
        var address = wifi.SelectWifiAddress(local);
        if (address.Length == 0)
        {
            var detail = wifi.Error.Length == 0 ? "手机 Wi-Fi 未取得可用 IPv4 地址；VPN 虚拟网卡地址不能作为 Wi-Fi 地址。" : $"无法读取手机 Wi-Fi 地址：{wifi.Error}";
            return Failure(device.WirelessPort is not null, $"{detail} 未开启 TCP 调试。");
        }

        var endpoint = $"{address}:5555";
        if (ConfirmEndpointAsync is { } confirm)
        {
            var accepted = await confirm(endpoint, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!accepted) return Failure(device.WirelessPort is not null, "已取消开启无线，未切换手机 TCP/IP 模式。");
            // Confirmation can remain open while the user changes cables or authorizations.
            var refreshed = (await GetRawDevicesAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(candidate => candidate.Serial == current.Serial);
            if (refreshed?.IsReady != true) return Failure(false, "设备连接状态已变化，请重试。");
            var refreshedPath = await RunTargetedAsync(current.Serial, ["get-devpath"], DetailTimeout, cancellationToken).ConfigureAwait(false);
            if (await ResolveTransportAsync(refreshed, refreshedPath, cancellationToken).ConfigureAwait(false) != DeviceTransport.Usb)
                return Failure(false, "无法重新确认 USB 目标，未切换无线。");
            var refreshedWifi = await GetNetworkAsync(refreshed, cancellationToken).ConfigureAwait(false);
            if (refreshedWifi.Error.Length > 0 || !refreshedWifi.WifiAddresses.Any(candidate => candidate.Address == address))
                return Failure(false, "手机 Wi-Fi 地址已变化，请重新开启无线。");
        }
        var tcpMode = await RunTargetedAsync(current.Serial, ["tcpip", "5555"], CommandTimeout, cancellationToken).ConfigureAwait(false);
        _detailsCache.Clear();
        _networkCache.Clear();
        if (!tcpMode.Succeeded || !Contains(tcpMode.Output, "restarting in TCP mode"))
            return Failure(false, $"开启 TCP 调试失败：{tcpMode.ErrorOrOutput}");

        var lastError = string.Empty;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            var connect = await RunAsync(["connect", endpoint], CommandTimeout, cancellationToken).ConfigureAwait(false);
            var connectAccepted = connect.Succeeded &&
                (Contains(connect.Output, $"connected to {endpoint}") || Contains(connect.Output, $"already connected to {endpoint}"));
            if (!connectAccepted) lastError = connect.ErrorOrOutput;

            var state = await RunTargetedAsync(endpoint, ["get-state"], DetailTimeout, cancellationToken).ConfigureAwait(false);
            if (connectAccepted && state.Succeeded && string.Equals(state.Output.Trim(), "device", StringComparison.Ordinal))
            {
                _detailsCache.Clear();
                return new WirelessResult(true, true, endpoint, $"已开启 TCP 调试并连接 {endpoint}。");
            }
            if (!state.Succeeded || state.Output.Trim() != "device") lastError = state.ErrorOrOutput;
        }

        _detailsCache.Clear();
        return new WirelessResult(
            true,
            false,
            endpoint,
            $"TCP 调试已开启，但主机未连接到 {endpoint}。请保持 USB 连接后重试。{AppendDetail(lastError)}\n{NetworkConnectionDiagnostics.DescribeFailure(endpoint, lastError)}")
        { Succeeded = false };
    }

    internal static IReadOnlyList<DeviceInfo> ParseDevices(string output)
    {
        var devices = new List<DeviceInfo>();
        using var reader = new StringReader(output ?? string.Empty);
        while (reader.ReadLine() is { } rawLine)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices attached", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith('*') || line.StartsWith("adb ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2) continue;
            var state = fields[1];
            if (state == "no" && fields.Length > 2 && fields[2] == "permissions") state = "no permissions";
            if (!KnownStates.Contains(state) && state != "no permissions") continue;

            var modelField = fields.FirstOrDefault(field => field.StartsWith("model:", StringComparison.Ordinal));
            var model = modelField is null ? string.Empty : modelField["model:".Length..].Replace('_', ' ');
            var path = fields.FirstOrDefault(field => field.StartsWith("usb:", StringComparison.Ordinal)) ?? "";
            var id = fields.FirstOrDefault(field => field.StartsWith("transport_id:", StringComparison.Ordinal));
            devices.Add(new DeviceInfo(fields[0], model, state)
            {
                Transport = DeviceDetailsParser.ClassifyTransport(fields[0], path.Length > 4 ? path : ""),
                TransportId = id is not null && long.TryParse(id["transport_id:".Length..], out var number) && number > 0 ? number : null
            });
        }
        return devices;
    }

    private async Task<IReadOnlyList<DeviceInfo>> GetRawDevicesAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            _paths.Adb,
            ["devices", "-l"],
            CommandTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"ADB 设备查询失败（退出代码 {result.ExitCode}）：{FirstUsefulText(result.StandardError, result.StandardOutput)}");
        var devices = ParseDevices(result.StandardOutput);
        if (!devices.Any(device => device.Transport == DeviceTransport.Unknown)) return devices;
        var snapshot = await AdbTransportProbe.ReadAsync(DetailTimeout, cancellationToken).ConfigureAwait(false);
        return devices.Select(device =>
        {
            // Known emulator/network serials keep their special UI semantics.
            if (device.Transport != DeviceTransport.Unknown) return device;
            var transport = snapshot.Classify(device);
            return device with
            {
                Transport = transport,
                DetailsError = transport == DeviceTransport.Unknown
                    ? "无法确认连接类型：" + (snapshot.Error.Length > 0 ? snapshot.Error : "ADB 未返回匹配当前连接的信息。") : ""
            };
        }).ToArray();
    }

    private async Task<DeviceInfo> EnrichAsync(DeviceInfo device, CancellationToken cancellationToken)
    {
        if (!device.IsReady) return device;
        var cacheKey = $"{device.Serial}\0{device.TransportId}";
        if (!_detailsCache.TryGet(cacheKey, out var details))
        {
            await _detailGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_detailsCache.TryGet(cacheKey, out details))
                {
                    details = await ProbeDetailsAsync(device, cancellationToken).ConfigureAwait(false);
                    _detailsCache.Set(cacheKey, details);
                }
            }
            finally
            {
                _detailGate.Release();
            }
        }

        var network = DeviceNetworkInfo.Empty;
        if (device.Transport != DeviceTransport.Emulator)
        {
            await _detailGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_networkCache.TryGet(cacheKey, out network)) network = await GetNetworkAsync(device, cancellationToken).ConfigureAwait(false);
            }
            finally { _detailGate.Release(); }
        }
        return device with
        {
            DeviceName = details.DeviceName,
            Model = details.Model.Length == 0 ? device.Model : details.Model,
            AndroidVersion = details.AndroidVersion,
            Transport = device.Transport != DeviceTransport.Unknown ? device.Transport : details.Transport,
            WirelessPort = details.WirelessPort,
            Network = network,
            DetailsError = string.Join("；", new[] { details.Error,
                device.Transport == DeviceTransport.Unknown && details.Transport == DeviceTransport.Unknown ? device.DetailsError : "" }
                .Where(error => error.Length > 0).Distinct()),
        };
    }

    private async Task<DeviceDetails> ProbeDetailsAsync(DeviceInfo device, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var devPath = await RunTargetedAsync(device.Serial, ["get-devpath"], DetailTimeout, cancellationToken).ConfigureAwait(false);
        if (!devPath.Succeeded) errors.Add(devPath.Error);
        var configuredName = await RunTargetedAsync(
            device.Serial,
            ["shell", "settings", "get", "global", "device_name"],
            DetailTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!configuredName.Succeeded) errors.Add(configuredName.Error);
        var properties = await RunTargetedAsync(device.Serial, ["shell", "getprop"], DetailTimeout, cancellationToken).ConfigureAwait(false);
        if (!properties.Succeeded) errors.Add(properties.Error);
        // Live server metadata belongs to this refresh, never to the property cache.
        // Only legacy path/USB-selector evidence is retained for old ADB backends.
        var transport = device.Transport == DeviceTransport.Unknown
            ? await ResolveTransportAsync(device, devPath, cancellationToken).ConfigureAwait(false)
            : DeviceDetailsParser.ClassifyTransport(device.Serial, devPath.Succeeded ? devPath.Output : "");
        return DeviceDetailsParser.Parse(device, configuredName.Output, properties.Output, devPath.Output, errors)
            with { Transport = transport };
    }

    private async Task<DeviceTransport> ResolveTransportAsync(
        DeviceInfo device, CommandResult devPath, CancellationToken cancellationToken)
    {
        if (device.Transport != DeviceTransport.Unknown) return device.Transport;
        var serial = device.Serial;
        var transport = DeviceDetailsParser.ClassifyTransport(serial, devPath.Succeeded ? devPath.Output : string.Empty);
        if (transport != DeviceTransport.Unknown) return transport;

        // Some Windows ADB backends omit the physical path. The USB-only selector
        // confirms the transport without inferring it from a serial or phone property.
        var usb = await RunAsync(["-d", "get-serialno"], DetailTimeout, cancellationToken).ConfigureAwait(false);
        return usb.Succeeded && usb.Output.Length > 0 && usb.Output != "unknown" &&
            string.Equals(usb.Output, serial, StringComparison.Ordinal)
            ? DeviceTransport.Usb
            : DeviceTransport.Unknown;
    }

    private async Task<WirelessResult> DisableWirelessAsync(DeviceInfo device, CancellationToken cancellationToken)
    {
        var usb = await RunTargetedAsync(device.Serial, ["usb"], CommandTimeout, cancellationToken).ConfigureAwait(false);
        _detailsCache.Clear();
        _networkCache.Clear();
        if (usb.Succeeded && Contains(usb.Output, "restarting in USB mode"))
            return new WirelessResult(false, false, string.Empty, "已让设备端 ADB 返回 USB 模式，连接可能短暂中断。");
        return Failure(device.WirelessPort is not null, $"返回 USB 模式失败：{usb.ErrorOrOutput}");
    }

    private Task<CommandResult> RunTargetedAsync(
        string serial,
        IReadOnlyList<string> command,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        RunAsync(["-s", serial, .. command], timeout, cancellationToken);

    private async Task<CommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(
                _paths.Adb,
                arguments,
                timeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new CommandResult(result.ExitCode == 0, result.StandardOutput.Trim(), result.StandardError.Trim());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return new CommandResult(false, string.Empty, exception.Message);
        }
    }

    private static WirelessResult Failure(bool modeEnabled, string message) =>
        new(modeEnabled, false, string.Empty, message) { Succeeded = false };

    private static bool Contains(string value, string expected) =>
        value.Contains(expected, StringComparison.OrdinalIgnoreCase);

    private static string AppendDetail(string detail) => detail.Length == 0 ? string.Empty : $" 详情：{detail}";

    private static string FirstUsefulText(params string[] values) => values
        .Select(value => value.Trim())
        .FirstOrDefault(value => value.Length > 0) ?? "未知错误";

    private sealed record CommandResult(bool Succeeded, string Output, string Error)
    {
        public string ErrorOrOutput => FirstUsefulText(Error, Output);
    }
}
