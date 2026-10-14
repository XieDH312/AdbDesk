using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AdbDesk.Core;

public sealed record LocalNetworkAddress(string Name, string Address, string Mask, string Gateway, string Kind, bool IsLan);

public enum SubnetMatch { Unknown, SameSubnet, DifferentSubnet }

public static class LocalNetwork
{
    public static IReadOnlyList<LocalNetworkAddress> ReadAddresses()
    {
        var result = new List<LocalNetworkAddress>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                var properties = adapter.GetIPProperties();
                var gateway = properties.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))?.Address.ToString() ?? "";
                var kind = ClassifyAdapter(adapter.Name, adapter.Description, adapter.NetworkInterfaceType);
                foreach (var address in properties.UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address.Address) || address.Address.Equals(IPAddress.Any)) continue;
                    result.Add(new(adapter.Name, address.Address.ToString(), address.IPv4Mask.ToString(), gateway,
                        kind.Kind, kind.IsLan));
                }
            }
            catch (NetworkInformationException) { /* Adapter may disappear during a Wi-Fi switch. */ }
        }
        return result.OrderByDescending(a => a.IsLan).ThenByDescending(a => a.Gateway.Length > 0)
            .ThenBy(a => a.Name, StringComparer.Ordinal).ThenBy(a => a.Address, StringComparer.Ordinal).ToArray();
    }

    internal static (string Kind, bool IsLan) ClassifyAdapter(string name, string description, NetworkInterfaceType type)
    {
        // Wi-Fi Direct/Hosted Network adapters carry Windows mobile hotspot LAN
        // traffic despite being virtual. Do not grant this exception to WSL/VPN.
        var hotspot = description.Contains("Microsoft Wi-Fi Direct Virtual Adapter", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("Microsoft Hosted Network Virtual Adapter", StringComparison.OrdinalIgnoreCase);
        if (hotspot) return ("电脑热点", true);
        var wifi = type == NetworkInterfaceType.Wireless80211;
        var ethernet = type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT;
        var label = name + " " + description;
        var virtualAdapter = new[] { "virtual", "vethernet", "hyper-v", "wsl", "vpn", "tun", "tap-", "wireguard", "tailscale", "mihomo", "clash", "vmware", "virtualbox" }
            .Any(marker => label.Contains(marker, StringComparison.OrdinalIgnoreCase));
        return !virtualAdapter && (wifi || ethernet) ? (wifi ? "Wi-Fi" : "有线", true) : ("其他网卡", false);
    }

    public static SubnetMatch Compare(string endpoint, IReadOnlyList<LocalNetworkAddress> addresses)
    {
        if (!MdnsServices.TryNormalizeEndpoint(endpoint, out var normalized)) return SubnetMatch.Unknown;
        var remote = IPAddress.Parse(MdnsServices.GetHost(normalized)).GetAddressBytes();
        var validLan = false;
        foreach (var local in addresses.Where(a => a.IsLan))
        {
            if (!IPAddress.TryParse(local.Address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
                !IPAddress.TryParse(local.Mask, out var mask) || mask.AddressFamily != AddressFamily.InterNetwork) continue;
            var ipBytes = ip.GetAddressBytes();
            var maskBytes = mask.GetAddressBytes();
            // Unspecified, APIPA and invalid masks cannot establish useful LAN reachability.
            if (ipBytes[0] is 0 or 127 || (ipBytes[0] == 169 && ipBytes[1] == 254)) continue;
            var maskValue = ((uint)maskBytes[0] << 24) | ((uint)maskBytes[1] << 16) | ((uint)maskBytes[2] << 8) | maskBytes[3];
            var inverse = ~maskValue;
            if (maskValue == 0 || (inverse & (inverse + 1)) != 0) continue;
            validLan = true;
            if (Enumerable.Range(0, 4).All(i => (ipBytes[i] & maskBytes[i]) == (remote[i] & maskBytes[i]))) return SubnetMatch.SameSubnet;
        }
        return validLan ? SubnetMatch.DifferentSubnet : SubnetMatch.Unknown;
    }
}
