using System.Net.Sockets;

namespace AdbDesk.Core;

public sealed record TcpEndpointDiagnostic(bool Reachable, string Message);

public static class NetworkConnectionDiagnostics
{
    public static string ApplyWifiHost(string endpoint, string? wifiHost)
    {
        if (!MdnsServices.TryNormalizeEndpoint(endpoint, out var normalized)) throw new ArgumentException("地址必须是有效的 IPv4:端口。");
        if (wifiHost is null) return normalized;
        wifiHost = wifiHost.Trim();
        if (!DeviceNetworkInfo.IsUsableIpv4(wifiHost) || !MdnsServices.TryNormalizeEndpoint(wifiHost + ":1", out var host))
            throw new ArgumentException("请填写手机 Wi-Fi 的 IPv4 地址，例如 192.168.137.208；这里不填写端口。");
        return MdnsServices.GetHost(host) + normalized[normalized.LastIndexOf(':')..];
    }

    public static string DescribeFailure(string endpoint, string error, bool pairing = false)
    {
        var reason = error.Contains("refused", StringComparison.OrdinalIgnoreCase) || error.Contains("拒绝", StringComparison.Ordinal)
            ? pairing ? "配对服务拒绝连接：请保持手机配对弹窗打开，并确认配对端口仍有效；防火墙也可能主动拒绝连接。"
                : "连接被拒绝：请确认无线调试已开启、连接端口仍有效；配对端口不能用于连接。防火墙也可能主动拒绝连接。"
            : error.Contains("auth", StringComparison.OrdinalIgnoreCase) || error.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
                ? pairing ? "配对认证失败：请确认手机 Wi-Fi IP、配对端口和配对码／二维码仍有效。"
                    : "ADB 认证失败：请重新配对或在手机上确认授权；端口能访问不代表已经配对。"
                : error.Contains("timed out", StringComparison.OrdinalIgnoreCase) || error.Contains("timeout", StringComparison.OrdinalIgnoreCase) || error.Contains("超时", StringComparison.Ordinal)
                    ? "请求超时：请检查实际 Wi-Fi IP、端口、手机 VPN 的局域网直连设置以及电脑热点／防火墙。超时不能单独证明是 VPN 导致。"
                    : error.Contains("unreachable", StringComparison.OrdinalIgnoreCase) || error.Contains("no route", StringComparison.OrdinalIgnoreCase)
                        ? "网络不可达：请检查手机实际 Wi-Fi 地址和电脑热点、路由及 VPN 设置。"
                        : "请确认使用手机 Wi-Fi 详情里的实际 IP，以及无线调试主页的连接端口；VPN 地址可能无法从电脑访问。可点击“检测端口”进一步检查。";
        return $"目标：{endpoint}\n{reason}";
    }

    public static async Task<TcpEndpointDiagnostic> ProbeTcpAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        endpoint = ApplyWifiHost(endpoint, null);
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        using var client = new TcpClient();
        var separator = endpoint.LastIndexOf(':');
        try
        {
            await client.ConnectAsync(endpoint[..separator], int.Parse(endpoint[(separator + 1)..]), linked.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new(true, $"目标：{endpoint}\nTCP 端口可达；这不代表 ADB 已配对或已连接，请继续执行 ADB 连接。");
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(false, DescribeFailure(endpoint, "连接超时"));
        }
        catch (SocketException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detail = ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "connection refused",
                SocketError.TimedOut => "timed out",
                SocketError.NetworkUnreachable or SocketError.HostUnreachable => "network unreachable",
                _ => ex.Message
            };
            return new(false, DescribeFailure(endpoint, detail) + $"\n系统信息：{ex.Message}");
        }
    }
}
