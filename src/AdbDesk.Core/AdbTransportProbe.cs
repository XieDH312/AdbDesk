using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AdbDesk.Core;

internal static class AdbTransportProbe
{
    internal static async Task<AdbTransportSnapshot> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var endpoint = ResolveEndpoint(Environment.GetEnvironmentVariable("ADB_SERVER_SOCKET"),
                Environment.GetEnvironmentVariable("ANDROID_ADB_SERVER_ADDRESS"), Environment.GetEnvironmentVariable("ANDROID_ADB_SERVER_PORT"));
            // devices -l has already started/verified the selected adb's server.
            // Use its smart socket directly: Windows CRT text-mode stdout would
            // expand binary LF bytes and corrupt the CLI's protobuf framing.
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.Host, endpoint.Port, linked.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();
            const string service = "host:track-devices-proto-binary";
            var request = Encoding.ASCII.GetBytes(service.Length.ToString("X4", CultureInfo.InvariantCulture) + service);
            await stream.WriteAsync(request, linked.Token).ConfigureAwait(false);
            return await ReadResponseAsync(stream, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return AdbTransportSnapshot.Unavailable("ADB 连接类型查询超时。");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or NotSupportedException or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) return AdbTransportSnapshot.Unavailable("ADB 连接类型查询超时。");
            return AdbTransportSnapshot.Unavailable(ex.Message);
        }
    }

    internal static (string Host, int Port) ResolveEndpoint(string? socket, string? address, string? port)
    {
        // Presence, not nonempty content, gives ADB_SERVER_SOCKET precedence.
        // Unsupported overrides must never silently query a different server.
        if (socket is not null)
        {
            if (!socket.StartsWith("tcp:", StringComparison.Ordinal)) throw UnsupportedEndpoint();
            var target = socket[4..];
            if (target.Length > 0 && target.All(char.IsAsciiDigit)) return ("127.0.0.1", ParsePort(target));
            if (target.StartsWith('['))
            {
                var end = target.IndexOf(']');
                if (end < 2 || end + 1 >= target.Length || target[end + 1] != ':') throw UnsupportedEndpoint();
                var host = target[1..end];
                if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6) throw UnsupportedEndpoint();
                return (host, ParsePort(target[(end + 2)..]));
            }
            var separator = target.IndexOf(':');
            if (separator <= 0 || separator != target.LastIndexOf(':')) throw UnsupportedEndpoint();
            return (ValidateHost(target[..separator]), ParsePort(target[(separator + 1)..]));
        }
        var serverPort = string.IsNullOrEmpty(port) ? 5037 : ParsePort(port);
        return address is null ? ("127.0.0.1", serverPort) : ResolveEndpoint($"tcp:{address}:{serverPort}", null, null);
    }

    private static string ValidateHost(string host)
    {
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        if (host.Length == 0 || host.Length > 253 || host.Any(char.IsWhiteSpace) || host.Any(char.IsControl) ||
            (!IPAddress.TryParse(host, out _) && (Uri.CheckHostName(host) != UriHostNameType.Dns ||
                host.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not '.' and not '-')))) throw UnsupportedEndpoint();
        return host == "localhost" ? "127.0.0.1" : host;
    }

    private static int ParsePort(string text)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 1 or > 65535)
            throw UnsupportedEndpoint();
        return value;
    }

    private static NotSupportedException UnsupportedEndpoint() => new("ADB 服务地址不受支持；连接类型查询仅支持带明确端口的 TCP 地址。");

    internal static async Task<AdbTransportSnapshot> ReadResponseAsync(Stream input, CancellationToken cancellationToken)
    {
        var status = new byte[4];
        await input.ReadExactlyAsync(status, cancellationToken).ConfigureAwait(false);
        if (status.AsSpan().SequenceEqual("FAIL"u8))
        {
            var error = Encoding.UTF8.GetString(await ReadFrameAsync(input, cancellationToken).ConfigureAwait(false));
            throw new InvalidDataException("ADB 连接类型服务不可用：" + error[..Math.Min(error.Length, 1024)]);
        }
        if (!status.AsSpan().SequenceEqual("OKAY"u8)) throw new InvalidDataException("ADB 服务应答无效。");
        var payload = await ReadFrameAsync(input, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // Closing this socket after the initial snapshot leaves the shared server alive.
        return AdbTransportSnapshot.Parse(payload);
    }

    internal static async Task<byte[]> ReadFrameAsync(Stream input, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await input.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!ushort.TryParse(Encoding.ASCII.GetString(header), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size))
            throw new InvalidDataException("ADB 连接列表长度无效。");
        var payload = new byte[size];
        await input.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

}
