using System.Globalization;

namespace AdbDesk.Core;

internal sealed record MdnsService(string Name, string Type, string Endpoint);

internal static class MdnsServices
{
    public const string PairingType = "_adb-tls-pairing._tcp";
    public const string ConnectType = "_adb-tls-connect._tcp";
    public const string LegacyType = "_adb._tcp";

    public static IReadOnlyList<MdnsService> Parse(string output)
    {
        var services = new List<MdnsService>();
        var seen = new HashSet<MdnsService>();
        using var reader = new StringReader(output ?? string.Empty);
        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || !TryNormalizeEndpoint(fields[^1], out var endpoint)) continue;
            var type = fields[^2].TrimEnd('.');
            if (type is not PairingType and not ConnectType and not LegacyType) continue;
            var name = fields[0].TrimEnd('.');
            if (name.Length is 0 or > 255 || name.Any(char.IsControl)) continue;
            var service = new MdnsService(name, type, endpoint);
            if (seen.Add(service)) services.Add(service);
        }
        return services;
    }

    internal static IReadOnlyList<string> FindCandidates(string output, string name, string type, IReadOnlyList<LocalNetworkAddress> localAddresses) =>
        Parse(output).Where(service => service.Name == name && service.Type == type)
            .Select(service => service.Endpoint).Distinct(StringComparer.Ordinal)
            .OrderByDescending(endpoint => LocalNetwork.Compare(endpoint, localAddresses) == SubnetMatch.SameSubnet).ToArray();

    public static bool TryNormalizeEndpoint(string value, out string endpoint)
    {
        endpoint = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        var separator = trimmed.LastIndexOf(':');
        if (separator <= 0 || separator == trimmed.Length - 1 || trimmed[..separator].Contains(':')) return false;
        var addressParts = trimmed[..separator].Split('.');
        if (addressParts.Length != 4) return false;
        var normalizedAddressParts = new string[4];
        for (var index = 0; index < addressParts.Length; index++)
        {
            if (!byte.TryParse(addressParts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var addressPart))
                return false;
            normalizedAddressParts[index] = addressPart.ToString(CultureInfo.InvariantCulture);
        }
        if (!int.TryParse(trimmed[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is <= 0 or > 65535) return false;
        endpoint = $"{string.Join('.', normalizedAddressParts)}:{port}";
        return true;
    }

    public static string GetHost(string normalizedEndpoint) =>
        normalizedEndpoint[..normalizedEndpoint.LastIndexOf(':')];
}
