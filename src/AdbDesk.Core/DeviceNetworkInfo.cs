using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace AdbDesk.Core;

public sealed record DeviceNetworkAddress(string Interface, string Address, int PrefixLength);

public sealed partial record DeviceNetworkInfo(IReadOnlyList<DeviceNetworkAddress> WifiAddresses, IReadOnlyList<DeviceNetworkAddress> VpnAddresses, string Error = "")
{
    public static DeviceNetworkInfo Empty { get; } = new([], []);
    public string WifiAddress => WifiAddresses.FirstOrDefault()?.Address ?? "";

    public string SelectWifiAddress(IReadOnlyList<LocalNetworkAddress> localAddresses) =>
        WifiAddresses.FirstOrDefault(address => LocalNetwork.Compare(address.Address + ":1", localAddresses) == SubnetMatch.SameSubnet)?.Address ?? WifiAddress;

    internal static DeviceNetworkInfo Parse(string output)
    {
        var wifi = new List<DeviceNetworkAddress>();
        var vpn = new List<DeviceNetworkAddress>();
        foreach (Match match in AddressRegex().Matches(output ?? ""))
        {
            var name = match.Groups[1].Value.Split('@')[0];
            var addressText = match.Groups[2].Value;
            if (!IsUsableIpv4(addressText) || !int.TryParse(match.Groups[3].Value, out var prefix) || prefix is < 1 or > 32) continue;
            var address = new DeviceNetworkAddress(name, IPAddress.Parse(addressText).ToString(), prefix);
            if (WifiInterfaceRegex().IsMatch(name)) wifi.Add(address);
            else if (VpnInterfaceRegex().IsMatch(name)) vpn.Add(address);
        }
        return new(wifi.Distinct().OrderBy(address => address.Interface == "wlan0" ? 0 : 1)
            .ThenBy(address => address.Interface, StringComparer.Ordinal).ToArray(), vpn.Distinct().ToArray());
    }

    internal static bool IsUsableIpv4(string text)
    {
        var parts = text.Split('.');
        if (parts.Length != 4 || parts.Any(part => !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))) return false;
        var bytes = parts.Select(part => byte.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        return bytes[0] is not (0 or 127) && bytes[0] < 224 && !(bytes[0] == 169 && bytes[1] == 254);
    }

    [GeneratedRegex(@"^\s*\d+:\s+(\S+)\s+inet\s+(\d{1,3}(?:\.\d{1,3}){3})/(\d+)\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex AddressRegex();
    [GeneratedRegex(@"^(?:wlan|swlan|wifi|ath)\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WifiInterfaceRegex();
    [GeneratedRegex(@"^(?:tun|utun|tap|wg|ppp|ipsec)\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VpnInterfaceRegex();
}

internal sealed class DeviceNetworkCache
{
    private readonly ConcurrentDictionary<string, (DeviceNetworkInfo Network, DateTimeOffset Expires)> _entries = new();
    internal bool TryGet(string key, out DeviceNetworkInfo network)
    {
        if (_entries.TryGetValue(key, out var entry) && entry.Expires > DateTimeOffset.UtcNow) { network = entry.Network; return true; }
        _entries.TryRemove(key, out _);
        network = DeviceNetworkInfo.Empty;
        return false;
    }
    internal void Set(string key, DeviceNetworkInfo network)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _entries) if (entry.Value.Expires <= now) _entries.TryRemove(entry.Key, out _);
        _entries[key] = (network, now.AddSeconds(10));
    }
    internal void Clear() => _entries.Clear();
}
