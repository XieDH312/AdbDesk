using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AdbDesk.Core;

namespace AdbDesk.App;

public partial class MainWindow
{
    // One-shot debounce for bursts of Windows network notifications, not polling.
    private readonly DispatcherTimer _networkRefreshDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private int _networkRefreshQueued;
    private int _networkEventsStopped;
    private readonly SemaphoreSlim _networkConfirmationGate = new(1, 1);
    private IReadOnlyList<LocalNetworkAddress> _networkAddresses = [];

    private void InitializeNetworkStatus()
    {
        _networkRefreshDelay.Tick += NetworkRefreshDelay_Tick;
        NetworkChange.NetworkAddressChanged += NetworkAddress_Changed;
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailability_Changed;
        Closed += NetworkStatus_Closed;
        RefreshNetworkStatus();
    }

    private void NetworkAddress_Changed(object? sender, EventArgs e) => QueueNetworkRefresh();

    private void NetworkAvailability_Changed(object? sender, NetworkAvailabilityEventArgs e) => QueueNetworkRefresh();

    private void QueueNetworkRefresh()
    {
        // Windows invokes these events off the UI thread. Coalesce queued callbacks
        // and unsubscribe on close so static NetworkChange events cannot retain us.
        if (Volatile.Read(ref _networkEventsStopped) != 0 || Dispatcher.HasShutdownStarted ||
            Interlocked.Exchange(ref _networkRefreshQueued, 1) != 0) return;
        Dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _networkRefreshQueued, 0);
            if (_isClosing || Volatile.Read(ref _networkEventsStopped) != 0) return;
            _networkRefreshDelay.Stop();
            _networkRefreshDelay.Start();
        }, DispatcherPriority.Background);
    }

    private void NetworkRefreshDelay_Tick(object? sender, EventArgs e)
    {
        _networkRefreshDelay.Stop();
        RefreshNetworkStatus();
    }

    private void NetworkStatus_Closed(object? sender, EventArgs e)
    {
        Interlocked.Exchange(ref _networkEventsStopped, 1);
        NetworkChange.NetworkAddressChanged -= NetworkAddress_Changed;
        NetworkChange.NetworkAvailabilityChanged -= NetworkAvailability_Changed;
        _networkRefreshDelay.Stop();
        _networkRefreshDelay.Tick -= NetworkRefreshDelay_Tick;
        Closed -= NetworkStatus_Closed;
    }

    private void RefreshNetworkStatus()
    {
        if (_isClosing) return;
        _networkRefreshDelay.Stop();
        try { _networkAddresses = LocalNetwork.ReadAddresses(); }
        catch { _networkAddresses = []; }
        var primary = _networkAddresses.FirstOrDefault(a => a.IsLan);
        LocalIpText.Text = primary is null ? "电脑 IP：未获取到局域网地址" : $"电脑 IP：{primary.Address} · {primary.Kind}";
        LocalIpButton.ToolTip = _networkAddresses.Count == 0 ? "暂无可用 IPv4 地址，请检查 Wi-Fi 或网线。" :
            "点击复制首选局域网 IP；右侧箭头查看全部地址。\n\n" + string.Join("\n\n", _networkAddresses.Select(DescribeAddress));
    }

    private static string DescribeAddress(LocalNetworkAddress address) =>
        $"{address.Name}（{address.Kind}）\nIP：{address.Address}\n掩码：{address.Mask}\n网关：{(address.Gateway.Length == 0 ? "无" : address.Gateway)}";

    private void CopyLocalIp_Click(object sender, RoutedEventArgs e)
    {
        RefreshNetworkStatus();
        if (_networkAddresses.FirstOrDefault(a => a.IsLan) is { } primary) CopyNetworkAddress(primary.Address);
    }

    private void CopyNetworkAddress(string address)
    {
        try { Clipboard.SetText(address); SetStatus($"已复制电脑 IP：{address}", StatusKind.Ready); }
        catch (Exception ex) { SetStatus($"复制失败：{ex.Message}", StatusKind.Warning); }
    }

    private void NetworkMenu_Click(object sender, RoutedEventArgs e)
    {
        RefreshNetworkStatus();
        var menu = new ContextMenu { PlacementTarget = (Button)sender };
        foreach (var address in _networkAddresses)
        {
            var item = new MenuItem { Header = $"{address.Address} · {address.Name}{(address.IsLan ? "" : "（其他网卡）")}", ToolTip = DescribeAddress(address) };
            item.Click += (_, _) => CopyNetworkAddress(address.Address);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "暂无可用 IPv4 地址", IsEnabled = false });
        menu.IsOpen = true;
    }

    private string GetNetworkHint()
    {
        RefreshNetworkStatus();
        var addresses = _networkAddresses.Where(a => a.IsLan).Select(a => a.Address).ToArray();
        return (addresses.Length == 0 ? "电脑暂未获取到局域网 IP。" : $"电脑 IP：{string.Join("、", addresses)}。") +
            " 若一直发现不到手机，请检查双方是否连接同一局域网、访客网络隔离及 VPN 设置。";
    }

    private async Task<bool> ConfirmNetworkEndpointAsync(string endpoint, CancellationToken cancellationToken)
    {
        await _networkConfirmationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Dispatcher.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_isClosing) return false;
                RefreshNetworkStatus();
                var comparison = LocalNetwork.Compare(endpoint, _networkAddresses);
                if (comparison == SubnetMatch.SameSubnet) return true;
                var addresses = _networkAddresses.Where(a => a.IsLan).ToArray();
                var computer = addresses.Length == 0 ? "未获取到局域网 IPv4 地址" :
                    string.Join("\n", addresses.Select(a => $"{a.Address}（{a.Name}，掩码 {a.Mask}）"));
                var explanation = comparison == SubnetMatch.DifferentSubnet
                    ? "电脑与手机不在同一局域网子网，可能无法连接。请检查 Wi-Fi、访客网络或 VPN 设置。部分网络支持跨子网路由，也可以仍然连接。"
                    : "暂时无法判断电脑与手机是否处于同一子网。请检查电脑网络；也可以仍然尝试连接。";
                var owner = _pairingWindow is { IsVisible: true } pairing ? (Window)pairing : this;
                return NetworkWarningWindow.Confirm(owner, $"{explanation}\n\n电脑：\n{computer}\n\n手机：{endpoint}", cancellationToken);
            });
        }
        finally { _networkConfirmationGate.Release(); }
    }
}
