using System.Net;
using System.Text.RegularExpressions;

namespace AdbDesk.Core;

internal sealed record DeviceDetails(
    string DeviceName,
    string Model,
    string AndroidVersion,
    DeviceTransport Transport,
    int? WirelessPort,
    string Error);

internal static partial class DeviceDetailsParser
{
    public static DeviceDetails Parse(
        DeviceInfo device,
        string configuredName,
        string properties,
        string devPath,
        IEnumerable<string> errors)
    {
        var values = ParseProperties(properties);
        var transport = ClassifyTransport(device.Serial, devPath);
        var wirelessPort = ReadPositivePort(values, "service.adb.tcp.port");
        if (IsTlsMdnsSerial(device.Serial)) wirelessPort = null;

        var model = FirstValue(values, "ro.product.model", "ro.product.vendor.model");
        var deviceName = NormalizeConfiguredName(configuredName);
        if (deviceName.Length == 0)
            deviceName = FirstValue(values, "ro.product.marketname", "ro.product.vendor.marketname");
        if (deviceName.Length == 0) deviceName = model.Length == 0 ? device.Model : model;

        return new DeviceDetails(
            deviceName,
            model,
            FirstValue(values, "ro.build.version.release"),
            transport,
            wirelessPort,
            string.Join("；", errors.Where(error => !string.IsNullOrWhiteSpace(error)).Distinct()));
    }

    public static DeviceTransport ClassifyTransport(string serial, string devPath)
    {
        if (serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase)) return DeviceTransport.Emulator;
        if (LooksLikeNetworkSerial(serial)) return DeviceTransport.Network;
        return IsPhysicalDevicePath(devPath) ? DeviceTransport.Usb : DeviceTransport.Unknown;
    }

    public static string FindWifiIpv4(string output)
    {
        foreach (Match match in Ipv4AddressRegex().Matches(output ?? string.Empty))
        {
            if (!IPAddress.TryParse(match.Groups[1].Value, out var address)) continue;
            var bytes = address.GetAddressBytes();
            if (bytes[0] is 0 or 127 || (bytes[0] == 169 && bytes[1] == 254) || bytes[0] >= 224) continue;
            return address.ToString();
        }
        return string.Empty;
    }

    private static Dictionary<string, string> ParseProperties(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in PropertyRegex().Matches(output ?? string.Empty))
            values[match.Groups[1].Value] = match.Groups[2].Value.Trim();
        return values;
    }

    private static string FirstValue(IReadOnlyDictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) && value.Length > 0) return value;
        }
        return string.Empty;
    }

    private static string NormalizeConfiguredName(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.Equals(normalized, "null", StringComparison.OrdinalIgnoreCase) ? string.Empty : normalized;
    }

    private static int? ReadPositivePort(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && int.TryParse(value, out var port) && port is > 0 and <= 65535 ? port : null;

    private static bool IsPhysicalDevicePath(string devPath) =>
        !string.IsNullOrWhiteSpace(devPath) && !string.Equals(devPath.Trim(), "unknown", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeNetworkSerial(string serial)
    {
        if (IsTlsMdnsSerial(serial) || serial.Contains("._adb._tcp", StringComparison.OrdinalIgnoreCase)) return true;
        var separator = serial.LastIndexOf(':');
        return separator > 0 && separator < serial.Length - 1 &&
            int.TryParse(serial[(separator + 1)..], out var port) && port is > 0 and <= 65535;
    }

    private static bool IsTlsMdnsSerial(string serial) =>
        serial.Contains("._adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\[([^]]+)\]:\s*\[(.*)\]\s*$", RegexOptions.Multiline)]
    private static partial Regex PropertyRegex();

    [GeneratedRegex(@"\binet\s+(\d{1,3}(?:\.\d{1,3}){3})/", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4AddressRegex();
}

internal sealed class DeviceDetailsCache(TimeSpan lifetime)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string serial, out DeviceDetails details)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(serial, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
            {
                details = entry.Details;
                return true;
            }
            _entries.Remove(serial);
        }
        details = null!;
        return false;
    }

    public void Set(string serial, DeviceDetails details)
    {
        lock (_sync) _entries[serial] = new Entry(details, DateTimeOffset.UtcNow + lifetime);
    }

    public void Clear()
    {
        lock (_sync) _entries.Clear();
    }

    private sealed record Entry(DeviceDetails Details, DateTimeOffset ExpiresAt);
}
