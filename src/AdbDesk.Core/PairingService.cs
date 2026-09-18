using System.Text.RegularExpressions;

namespace AdbDesk.Core;

public sealed class PairingService
{
    private static readonly TimeSpan PairTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private readonly ToolPaths _paths;
    private readonly TimeSpan _discoveryInterval;
    private readonly TimeSpan _connectDiscoveryTimeout;
    public Func<string, CancellationToken, Task<bool>>? ConfirmEndpointAsync { get; init; }

    public PairingService(ToolPaths paths) : this(paths, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(10)) { }

    internal PairingService(ToolPaths paths, TimeSpan discoveryInterval, TimeSpan connectDiscoveryTimeout)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        if (discoveryInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(discoveryInterval));
        if (connectDiscoveryTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(connectDiscoveryTimeout));
        _discoveryInterval = discoveryInterval;
        _connectDiscoveryTimeout = connectDiscoveryTimeout;
    }

    public static QrPairingSession CreateQrSession()
    {
        var serviceName = "studio-" + QrPairingSession.CreateRandomText(10);
        var secret = QrPairingSession.CreateRandomText(16);
        return new QrPairingSession(serviceName, secret, DateTimeOffset.UtcNow.AddMinutes(2));
    }

    public async Task<PairingResult> PairWithQrAsync(
        QrPairingSession session,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
            return new PairingResult(false, false, string.Empty, "二维码已过期，请刷新后重试。");

        Report(progress, "等待手机扫描二维码并发现配对服务…");
        var pairingEndpoint = await FindServiceAsync(
            session.ServiceName,
            MdnsServices.PairingType,
            session.ExpiresAt,
            cancellationToken).ConfigureAwait(false);
        if (pairingEndpoint is null)
            return new PairingResult(false, false, string.Empty, "二维码已过期，未发现对应的手机配对服务。");
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
            return new PairingResult(false, false, string.Empty, "二维码已过期，未提交配对请求。");

        return await PairAndConnectAsync(
            pairingEndpoint,
            session.PairingSecret,
            connectionEndpoint: null,
            progress,
            cancellationToken,
            session.ExpiresAt).ConfigureAwait(false);
    }

    public Task<PairingResult> PairWithCodeAsync(
        string pairingEndpoint,
        string code,
        string? connectionEndpoint = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!MdnsServices.TryNormalizeEndpoint(pairingEndpoint, out var normalizedPairingEndpoint))
            throw new ArgumentException("配对地址必须是有效的 IPv4:端口。", nameof(pairingEndpoint));
        if (code is null || code.Length != 6 || code.Any(character => character is < '0' or > '9'))
            throw new ArgumentException("配对码必须是六位数字。", nameof(code));

        string? normalizedConnectionEndpoint = null;
        if (!string.IsNullOrWhiteSpace(connectionEndpoint) &&
            !MdnsServices.TryNormalizeEndpoint(connectionEndpoint, out normalizedConnectionEndpoint))
        {
            throw new ArgumentException("连接地址必须是有效的 IPv4:端口。", nameof(connectionEndpoint));
        }
        if (normalizedConnectionEndpoint is not null &&
            !string.Equals(
                MdnsServices.GetHost(normalizedPairingEndpoint),
                MdnsServices.GetHost(normalizedConnectionEndpoint),
                StringComparison.Ordinal))
        {
            throw new ArgumentException("配对地址和连接地址必须属于同一台手机（IPv4 地址需一致）。", nameof(connectionEndpoint));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return PairAndConnectAsync(
            normalizedPairingEndpoint,
            code,
            normalizedConnectionEndpoint,
            progress,
            cancellationToken);
    }

    public async Task<PairingResult> ConnectAsync(
        string connectionEndpoint,
        CancellationToken cancellationToken = default)
    {
        if (!MdnsServices.TryNormalizeEndpoint(connectionEndpoint, out var endpoint))
            throw new ArgumentException("连接地址必须是有效的 IPv4:端口。", nameof(connectionEndpoint));
        cancellationToken.ThrowIfCancellationRequested();

        if (!await ConfirmAsync(endpoint, cancellationToken).ConfigureAwait(false))
            return new PairingResult(false, false, endpoint, "已取消连接，未提交 ADB 连接请求。");
        return await ConnectCoreAsync(endpoint, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PairingResult> ConnectCoreAsync(string endpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connect = await RunAsync(["connect", endpoint], CommandTimeout, cancellationToken).ConfigureAwait(false);
        var accepted = connect.Succeeded &&
            (Contains(connect.Output, $"connected to {endpoint}") || Contains(connect.Output, $"already connected to {endpoint}"));
        if (!accepted)
            return new PairingResult(false, false, endpoint, $"连接失败：{SafeDetail(connect.ErrorOrOutput)}");

        var state = await RunAsync(["-s", endpoint, "get-state"], CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (state.Succeeded && string.Equals(state.Output.Trim(), "device", StringComparison.Ordinal))
            return new PairingResult(false, true, endpoint, $"已连接并确认设备在线：{endpoint}。");
        return new PairingResult(false, false, endpoint, $"ADB 已接受连接请求，但设备尚未在线。{AppendDetail(state.ErrorOrOutput)}");
    }

    private async Task<PairingResult> PairAndConnectAsync(
        string pairingEndpoint,
        string secret,
        string? connectionEndpoint,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        DateTimeOffset? expiresAt = null)
    {
        if (!await ConfirmAsync(pairingEndpoint, cancellationToken).ConfigureAwait(false))
            return new PairingResult(false, false, pairingEndpoint, "已取消配对，未提交 ADB 配对请求。");
        if (expiresAt is { } expiry && DateTimeOffset.UtcNow >= expiry)
            return new PairingResult(false, false, pairingEndpoint, "二维码已过期，请刷新后重试。");
        Report(progress, "正在安全提交配对请求…");
        var pair = await RunAsync(
            ["pair", pairingEndpoint],
            PairTimeout,
            cancellationToken,
            secret + "\n").ConfigureAwait(false);
        var combinedOutput = string.Join(Environment.NewLine, pair.Output, pair.Error);
        var match = PairSuccessRegex(pairingEndpoint).Match(combinedOutput);
        if (!pair.Succeeded || !match.Success)
            return new PairingResult(false, false, string.Empty, $"配对失败：{SafeDetail(pair.ErrorOrOutput, secret)}");

        var guid = match.Groups[1].Value;
        Report(progress, "配对成功，正在查找该设备的连接服务…");
        var endpoint = connectionEndpoint;
        if (endpoint is null)
        {
            endpoint = await FindServiceAsync(
                guid,
                MdnsServices.ConnectType,
                DateTimeOffset.UtcNow + _connectDiscoveryTimeout,
                cancellationToken).ConfigureAwait(false);
        }

        if (endpoint is null)
        {
            return new PairingResult(
                true,
                false,
                string.Empty,
                "配对成功，但未发现该设备的连接地址。请填写手机无线调试主页显示的 IP 地址和端口后连接。");
        }

        Report(progress, "正在连接并确认设备在线…");
        // The same host has already been approved before pairing. A newly discovered
        // different address must be checked independently before connecting.
        var connection = MdnsServices.GetHost(endpoint) == MdnsServices.GetHost(pairingEndpoint)
            ? await ConnectCoreAsync(endpoint, cancellationToken).ConfigureAwait(false)
            : await ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        return connection.Connected
            ? new PairingResult(true, true, endpoint, $"配对成功，设备已连接：{endpoint}。")
            : new PairingResult(true, false, endpoint, $"配对成功，但设备尚未连接。{AppendDetail(connection.Message)}");
    }

    private async Task<bool> ConfirmAsync(string endpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var accepted = ConfirmEndpointAsync is null || await ConfirmEndpointAsync(endpoint, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return accepted;
    }

    private async Task<string?> FindServiceAsync(
        string exactName,
        string exactType,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var services = await RunAsync(["mdns", "services"], CommandTimeout, cancellationToken).ConfigureAwait(false);
            if (services.Succeeded)
            {
                var match = MdnsServices.Parse(services.Output)
                    .FirstOrDefault(service => service.Name == exactName && service.Type == exactType);
                if (match is not null) return match.Endpoint;
            }

            var remaining = Smaller(deadline - DateTimeOffset.UtcNow, _discoveryInterval);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private async Task<CommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? standardInput = null)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(
                _paths.Adb,
                arguments,
                timeout,
                cancellationToken: cancellationToken,
                standardInput: standardInput).ConfigureAwait(false);
            return new CommandResult(result.ExitCode == 0, result.StandardOutput.Trim(), result.StandardError.Trim());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return new CommandResult(false, string.Empty, exception.Message);
        }
    }

    private static Regex PairSuccessRegex(string endpoint) => new(
        $@"Successfully paired to\s+{Regex.Escape(endpoint)}\s+\[guid=([^\]\s]+)\]",
        RegexOptions.CultureInvariant);

    private static TimeSpan Smaller(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private static void Report(IProgress<string>? progress, string message) => progress?.Report(message);

    private static bool Contains(string value, string expected) =>
        value.Contains(expected, StringComparison.OrdinalIgnoreCase);

    private static string SafeDetail(string detail, string? secret = null)
    {
        var value = string.IsNullOrWhiteSpace(detail) ? "ADB 未返回成功信息。" : detail.Trim();
        return string.IsNullOrEmpty(secret) ? value : value.Replace(secret, "******", StringComparison.Ordinal);
    }

    private static string AppendDetail(string detail) => string.IsNullOrWhiteSpace(detail) ? string.Empty : $" 详情：{detail}";

    private sealed record CommandResult(bool Succeeded, string Output, string Error)
    {
        public string ErrorOrOutput => !string.IsNullOrWhiteSpace(Error) ? Error : Output;
    }
}
