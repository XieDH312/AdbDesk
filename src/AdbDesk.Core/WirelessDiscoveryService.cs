using System.ComponentModel;
using System.Text;

namespace AdbDesk.Core;

public sealed record WirelessServiceEntry(
    string Name, string Type, string Endpoint, DeviceInfo? Device, string DisplayName, bool IsHistoricalName)
{
    public const string ConnectType = MdnsServices.ConnectType;
    public const string LegacyType = MdnsServices.LegacyType;
    public const string PairingType = MdnsServices.PairingType;
    public bool IsPairing => Type == PairingType;
    public bool IsConnected => !IsPairing && Device?.IsReady == true;
}

public sealed record WirelessDiscoverySnapshot(IReadOnlyList<WirelessServiceEntry> Services, string Warning);

/// <summary>One bounded refresh. Advertisements are never interpreted as authorization or connectivity.</summary>
public sealed class WirelessDiscoveryService
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(10);
    private readonly Func<CancellationToken, Task<string>> _readServices;
    private readonly Func<CancellationToken, Task<IReadOnlyList<DeviceInfo>>> _readDevices;
    private readonly Func<DeviceInfo, CancellationToken, Task<DeviceInfo>>? _readName;
    private readonly WirelessDeviceNameStore _names;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public WirelessDiscoveryService(ToolPaths paths, string nameCachePath)
        : this(token => ReadTextAsync(paths, "mdns services", null, 128 * 1024, token),
            async token => ParseDeviceSnapshot(await ReadTextAsync(paths, "devices -l", null, 256 * 1024, token).ConfigureAwait(false)),
            nameCachePath, readName: (device, token) => ReadNameAsync(paths, device, token))
    {
        ArgumentNullException.ThrowIfNull(paths);
    }

    internal WirelessDiscoveryService(
        Func<CancellationToken, Task<string>> readServices,
        Func<CancellationToken, Task<IReadOnlyList<DeviceInfo>>> readDevices,
        string nameCachePath,
        TimeSpan? timeout = null,
        Func<DeviceInfo, CancellationToken, Task<DeviceInfo>>? readName = null)
    {
        _readServices = readServices ?? throw new ArgumentNullException(nameof(readServices));
        _readDevices = readDevices ?? throw new ArgumentNullException(nameof(readDevices));
        _readName = readName;
        _names = new WirelessDeviceNameStore(nameCachePath);
        _timeout = timeout ?? DiscoveryTimeout;
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<WirelessDiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Serialize refreshes so an older snapshot cannot overwrite newer verified names.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try { await _refreshGate.WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new([], "无线服务刷新仍在进行，本次等待已超时。");
        }
        try
        {
            var serviceTask = ReadSafelyAsync(_readServices, "", "无线服务发现", deadline.Token, cancellationToken);
            var deviceTask = ReadSafelyAsync(_readDevices, (IReadOnlyList<DeviceInfo>)[], "ADB 连接状态读取", deadline.Token, cancellationToken);
            await Task.WhenAll(serviceTask, deviceTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var serviceResult = await serviceTask.ConfigureAwait(false);
            var deviceResult = await deviceTask.ConfigureAwait(false);
            if (serviceResult.Value.Length > 128 * 1024)
                serviceResult = new("", "无线服务发现返回的数据超过大小限制，已忽略。");
            var services = MdnsServices.Parse(serviceResult.Value);
            var devices = deviceResult.Value;
            var warnings = new List<string> { serviceResult.Warning, deviceResult.Warning };

            if (_readName is not null)
            {
                // Never probe USB devices, unmatched IPs, pairing endpoints, or non-ready transports.
                var matched = services.Select(service => MatchDevice(service, devices))
                    .OfType<DeviceInfo>().Where(device => device.IsReady).DistinctBy(device => device.Serial).ToArray();
                // A canceled runner may still release this managed gate during its bounded cleanup.
                var nameGate = new SemaphoreSlim(4, 4);
                var reads = matched.Select(async device =>
                {
                    var result = await ReadSafelyAsync(async token =>
                    {
                        await nameGate.WaitAsync(token).ConfigureAwait(false);
                        try { return await _readName(device, token).ConfigureAwait(false); }
                        finally { nameGate.Release(); }
                    }, device, "设备名称读取", deadline.Token, cancellationToken).ConfigureAwait(false);
                    // Name lookup must not change the transport identity or its readiness evidence.
                    return (device.Serial, Device: device with { DeviceName = result.Value.DeviceName, Model = result.Value.Model }, result.Warning);
                }).ToArray();
                var enriched = await Task.WhenAll(reads).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var bySerial = enriched.ToDictionary(result => result.Serial, result => result.Device, StringComparer.Ordinal);
                devices = devices.Select(device => device.Transport == DeviceTransport.Network && bySerial.TryGetValue(device.Serial, out var named)
                    ? device with { DeviceName = named.DeviceName, Model = named.Model }
                    : device).ToArray();
                warnings.AddRange(enriched.Select(result => result.Warning));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var names = _names.Load();
            warnings.Add(names.Warning);
            var entries = BuildEntries(services, devices, names.Entries);
            cancellationToken.ThrowIfCancellationRequested();
            warnings.Add(_names.Remember(entries, names.Entries));
            return new(entries, string.Join("；", warnings.Where(warning => warning.Length > 0).Distinct(StringComparer.Ordinal)));
        }
        finally { _refreshGate.Release(); }
    }

    internal static IReadOnlyList<DeviceInfo> ParseDeviceSnapshot(string output)
    {
        var devices = new List<DeviceInfo>();
        using var reader = new StringReader(output ?? "");
        while (reader.ReadLine() is { } line)
        {
            var parsed = AdbService.ParseDevices(line);
            if (parsed.Count == 0) continue;
            var usbField = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(field => field.StartsWith("usb:", StringComparison.Ordinal));
            var physicalUsb = usbField is not null &&
                DeviceDetailsParser.ClassifyTransport("", usbField["usb:".Length..]) == DeviceTransport.Usb;
            // Use evidence from this exact row. The same serial can have several transports.
            devices.AddRange(parsed.Select(device => physicalUsb && device.Transport != DeviceTransport.Emulator
                ? device with { Transport = DeviceTransport.Usb }
                : device));
        }
        return devices;
    }
    private static IReadOnlyList<WirelessServiceEntry> BuildEntries(
        IReadOnlyList<MdnsService> services, IReadOnlyList<DeviceInfo> devices, IReadOnlyList<WirelessDeviceName> history)
    {
        return services.Select(service =>
        {
            var device = MatchDevice(service, devices);
            var displayName = device?.IsReady == true ? WirelessDeviceNameStore.NormalizeDisplayName(device.DeviceName) : "";
            if (displayName.Length == 0 && device?.IsReady == true)
                displayName = WirelessDeviceNameStore.NormalizeDisplayName(device.Model);
            var historical = false;
            if (displayName.Length == 0 && service.Type != MdnsServices.PairingType)
            {
                var remembered = history.FirstOrDefault(entry => entry.Name == service.Name && entry.Type == service.Type);
                if (remembered is not null)
                {
                    displayName = remembered.DisplayName;
                    historical = true;
                }
            }
            return new WirelessServiceEntry(service.Name, service.Type, service.Endpoint, device,
                displayName.Length > 0 ? displayName : service.Name, historical);
        }).ToArray();
    }

    private static DeviceInfo? MatchDevice(MdnsService service, IReadOnlyList<DeviceInfo> devices)
    {
        if (service.Type == MdnsServices.PairingType) return null;
        var alias = service.Name + "." + service.Type;
        return devices.Where(device => device.Transport == DeviceTransport.Network &&
                (string.Equals(device.Serial.Trim().TrimEnd('.'), alias, StringComparison.Ordinal) ||
                 MdnsServices.TryNormalizeEndpoint(device.Serial, out var endpoint) && endpoint == service.Endpoint))
            .OrderByDescending(device => device.IsReady).FirstOrDefault();
    }

    private sealed record ReadResult<T>(T Value, string Warning);

    private static async Task<ReadResult<T>> ReadSafelyAsync<T>(
        Func<CancellationToken, Task<T>> read, T fallback, string operation, CancellationToken deadline, CancellationToken caller)
    {
        try
        {
            deadline.ThrowIfCancellationRequested();
            var task = read(deadline);
            // Observe a late fault even if a supplied reader does not honor cancellation.
            _ = task.ContinueWith(completed => _ = completed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return new(await task.WaitAsync(deadline).ConfigureAwait(false), "");
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            return new(fallback, operation + "超时，未确认的设备不会标为已连接。");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or Win32Exception or TimeoutException or UnauthorizedAccessException)
        {
            var message = exception.Message;
            return new(fallback, operation + "失败：" + message[..Math.Min(message.Length, 1000)]);
        }
    }

    private static async Task<DeviceInfo> ReadNameAsync(ToolPaths paths, DeviceInfo device, CancellationToken token)
    {
        // Both commands are reads against a transport already matched in this refresh.
        var name = await ReadTextAsync(paths, "shell settings get global device_name", device.Serial, 8192, token).ConfigureAwait(false);
        var normalized = WirelessDeviceNameStore.NormalizeDisplayName(name);
        if (normalized.Length > 0) return device with { DeviceName = normalized };
        var properties = await ReadTextAsync(paths, "shell getprop", device.Serial, 64 * 1024, token).ConfigureAwait(false);
        var details = DeviceDetailsParser.Parse(device, "", properties, "", []);
        return device with { DeviceName = details.DeviceName, Model = details.Model.Length > 0 ? details.Model : device.Model };
    }

    private static async Task<string> ReadTextAsync(
        ToolPaths paths, string command, string? serial, int maximumCharacters, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var error = new StringBuilder();
        var gate = new object();
        var truncated = false;
        // ConsoleCommandRunner stops only its own adb client, never the shared server tree.
        var exitCode = await new ConsoleCommandRunner(paths).RunAsync(ConsoleCommand.Parse(command, serial), (text, isError) =>
        {
            lock (gate)
            {
                var destination = isError ? error : output;
                var remaining = (isError ? 8192 : maximumCharacters) - destination.Length;
                if (text.Length > remaining) truncated = true;
                if (remaining > 0) destination.Append(text.AsSpan(0, Math.Min(text.Length, remaining)));
            }
        }, TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (exitCode != 0)
        {
            var detail = error.Length > 0 ? error.ToString() : output.ToString();
            throw new IOException($"ADB 返回 {exitCode}：{detail[..Math.Min(detail.Length, 1000)].Trim()}");
        }
        if (truncated) throw new InvalidDataException("ADB 返回的数据超过大小限制。");
        return output.ToString();
    }
}