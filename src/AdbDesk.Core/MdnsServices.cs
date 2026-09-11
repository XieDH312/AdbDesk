using System.Globalization;

namespace AdbDesk.Core;

internal sealed record MdnsService(string Name, string Type, string Endpoint);

internal static class MdnsServices
{
    public const string PairingType = "_adb-tls-pairing._tcp";
    public const string ConnectType = "_adb-tls-connect._tcp";

    public static IReadOnlyList<MdnsService> Parse(string output)
    {
        var services = new List<MdnsService>();
        using var reader = new StringReader(output ?? string.Empty);
        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || !TryNormalizeEndpoint(fields[^1], out var endpoint)) continue;
            var type = fields[^2].TrimEnd('.');
            if (type is not PairingType and not ConnectType) continue;
            services.Add(new MdnsService(fields[0].TrimEnd('.'), type, endpoint));
        }
        return services;
    }

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
        if (!int.TryParse(trimmed[(separator + 1)..], out var port) || port is <= 0 or > 65535) return false;
        endpoint = $"{string.Join('.', normalizedAddressParts)}:{port}";
        return true;
    }

    public static string GetHost(string normalizedEndpoint) =>
        normalizedEndpoint[..normalizedEndpoint.LastIndexOf(':')];
}
