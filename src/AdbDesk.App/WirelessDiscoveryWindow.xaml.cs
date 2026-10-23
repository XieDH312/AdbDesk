using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using AdbDesk.Core;

namespace AdbDesk.App;

public partial class WirelessDiscoveryWindow : Window
{
    private readonly WirelessDiscoveryService _discovery;
    private readonly PairingService _pairing;
    private readonly AdbService _adb;
    private readonly Func<string, Task> _connected;
    private readonly Action<string> _openPairing;
    private CancellationTokenSource? _operation;
    private bool _closed;
    private bool _hasSnapshot;
    private bool _refreshPending;
    private bool _statusIsWarning;

    public WirelessDiscoveryWindow(WirelessDiscoveryService discovery, PairingService pairing, AdbService adb,
        Func<string, Task> connected, Action<string> openPairing)
    {
        _discovery = discovery;
        _pairing = pairing;
        _adb = adb;
        _connected = connected;
        _openPairing = openPairing;
        InitializeComponent();
    }

    public bool IsBusy => _operation is not null;

    public Func<DeviceInfo, CancellationToken, Task<DisconnectResult>>? DisconnectAsync { get; set; }

    public Task RefreshAfterPairingAsync()
    {
        if (_closed) return Task.CompletedTask;
        if (IsBusy)
        {
            _refreshPending = true;
            return Task.CompletedTask;
        }
        return RefreshAsync();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        FitToWorkArea();
        await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private Task RefreshAsync() => RunOperationAsync(async token =>
    {
        ShowStatus("正在发现无线 ADB 服务…", false);
        var snapshot = await RefreshSnapshotAsync(token);
        if (snapshot is null)
        {
            ShowStatus("本次刷新未完成，请查看下方发现提示。", true);
            return;
        }
        ShowStatus(snapshot.Services.Count == 0
            ? "本次未发现服务。请检查手机无线调试与局域网连接，或使用手动配对 / 连接。"
            : $"发现 {snapshot.Services.Count} 个服务。请选择目标设备进行连接或配对。", false);
    });

    private async Task<WirelessDiscoverySnapshot?> RefreshSnapshotAsync(CancellationToken token)
    {
        _refreshPending = false;
        try
        {
            var snapshot = await _discovery.DiscoverAsync(token);
            token.ThrowIfCancellationRequested();
            if (_closed) return null;
            var rows = snapshot.Services.Select(entry => new ServiceRow(entry)).ToArray();
            ServicesList.ItemsSource = rows;
            ServiceCountText.Text = $"{rows.Length} 个";
            EmptyState.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _hasSnapshot = true;
            ShowDiscoveryWarning(snapshot.Warning);
            return snapshot;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            token.ThrowIfCancellationRequested();
            ShowDiscoveryWarning($"发现失败：{exception.Message}" + (_hasSnapshot
                ? "\n当前保留上次列表；操作前会重新确认服务。"
                : "\n请检查 ADB、手机无线调试及局域网连接，也可使用手动配对 / 连接。"));
            return null;
        }
    }

    private async Task<WirelessServiceEntry?> RevalidateAsync(WirelessServiceEntry selected, CancellationToken token)
    {
        var snapshot = await RefreshSnapshotAsync(token);
        if (snapshot is null)
        {
            ShowStatus("无法确认当前服务，未发送操作请求。请刷新后重试。", true);
            return null;
        }

        var sameService = snapshot.Services.Where(candidate =>
            string.Equals(candidate.Name, selected.Name, StringComparison.Ordinal) &&
            string.Equals(candidate.Type, selected.Type, StringComparison.Ordinal)).ToArray();
        var exact = sameService.Where(candidate =>
            string.Equals(candidate.Endpoint, selected.Endpoint, StringComparison.Ordinal)).ToArray();
        // A service can announce a new port. Only a unique, fresh identity may replace the selected endpoint.
        var current = exact.Length == 1 ? exact[0] : exact.Length == 0 && sameService.Length == 1 ? sameService[0] : null;
        if (current is null)
            ShowStatus(sameService.Length == 0
                ? "该服务已消失，未发送操作请求。列表已刷新，请重新选择。"
                : "该服务存在多个地址，无法确认目标，未发送操作请求。列表已刷新，请重新选择。", true);
        return current;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ServiceRow row } || row.Entry.IsPairing) return;
        await RunOperationAsync(async token =>
        {
            ShowStatus("正在重新确认连接服务…", false);
            var current = await RevalidateAsync(row.Entry, token);
            if (current is null) return;
            if (current.IsPairing)
            {
                ShowStatus("这是配对服务，请使用“配对”按钮。配对端口不能用于连接。", true);
                return;
            }
            if (current.IsConnected)
            {
                ShowStatus($"{current.DisplayName} 已连接，无需重复连接。", false);
                return;
            }

            ShowStatus($"正在连接并确认设备在线：{current.Endpoint}…", false);
            try
            {
                var result = await _pairing.ConnectAsync(current.Endpoint, token);
                token.ThrowIfCancellationRequested();
                if (_closed) return;
                ShowStatus(result.Connected ? $"设备已连接。\n{result.Message}" : result.Message, !result.Connected);
                if (result.Connected)
                {
                    try
                    {
                        await _connected(string.IsNullOrWhiteSpace(result.Endpoint) ? current.Endpoint : result.Endpoint);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception)
                    {
                        ShowStatus($"设备已连接，但更新主窗口或连接记录失败：{exception.Message}", true);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { ShowStatus($"连接失败：{exception.Message}", true); }
            await RefreshAfterActionAsync(token);
        });
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ServiceRow row } || row.Entry.IsPairing || row.Entry.Device is null) return;
        await RunOperationAsync(async token =>
        {
            ShowStatus("正在重新确认要断开的无线连接…", false);
            var current = await RevalidateAsync(row.Entry, token);
            if (current is null) return;
            var selectedDevice = row.Entry.Device;
            var exactDevice = current.Device;
            if (current.IsPairing || exactDevice is null || !exactDevice.IsNetworkConnection ||
                !string.Equals(exactDevice.Serial, selectedDevice.Serial, StringComparison.Ordinal) ||
                (selectedDevice.TransportId.HasValue && selectedDevice.TransportId != exactDevice.TransportId))
            {
                ShowStatus("此服务匹配的无线连接已变化或已断开，未发送断开请求。列表已刷新，请重新选择。", true);
                return;
            }

            ShowStatus($"正在断开当前无线连接：{exactDevice.Serial}…", false);
            try
            {
                var result = DisconnectAsync is { } disconnect
                    ? await disconnect(exactDevice, token)
                    : await _adb.DisconnectWirelessAsync(exactDevice, token);
                token.ThrowIfCancellationRequested();
                ShowStatus(result.Message, !result.Disconnected);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { ShowStatus($"断开失败：{exception.Message}", true); }
            await RefreshAfterActionAsync(token);
        });
    }

    private async Task RefreshAfterActionAsync(CancellationToken token)
    {
        // Restore the completed result if Cancel temporarily replaces it while the follow-up refresh unwinds.
        var outcome = StatusText.Text;
        var warning = _statusIsWarning;
        try { await RefreshSnapshotAsync(token); }
        catch (OperationCanceledException)
        {
            ShowDiscoveryWarning("操作后的发现刷新已取消；当前列表可能不是最新状态，请手动刷新确认。");
        }
        finally { ShowStatus(outcome, warning); }
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ServiceRow row } || !row.Entry.IsPairing) return;
        string? endpoint = null;
        await RunOperationAsync(async token =>
        {
            ShowStatus("正在重新确认配对服务…", false);
            var current = await RevalidateAsync(row.Entry, token);
            if (current is null) return;
            if (!current.IsPairing)
            {
                ShowStatus("服务类型已变化，请重新选择配对服务。", true);
                return;
            }
            token.ThrowIfCancellationRequested();
            endpoint = current.Endpoint;
            ShowStatus($"请在配对窗口输入手机显示的配对码：{endpoint}。", false);
        });
        // The parent may show a modal pairing window; release our busy state before dispatching it.
        if (!_closed && !IsBusy && endpoint is not null) OpenPairing(endpoint);
    }

    private void ManualPairing_Click(object sender, RoutedEventArgs e)
    {
        if (!_closed && !IsBusy) OpenPairing(string.Empty);
    }

    private void OpenPairing(string endpoint)
    {
        try { _openPairing(endpoint); }
        catch (Exception exception) { ShowStatus($"无法打开配对窗口：{exception.Message}", true); }
    }

    private void CopyEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || IsBusy || sender is not FrameworkElement { DataContext: ServiceRow row }) return;
        try
        {
            Clipboard.SetText(row.Entry.Endpoint);
            ShowStatus($"已复制{(row.Entry.IsPairing ? "配对" : "连接")}地址：{row.Entry.Endpoint}", false);
        }
        catch (Exception exception) { ShowStatus($"复制地址失败：{exception.Message}", true); }
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> action)
    {
        if (_closed || IsBusy) return;
        using var source = new CancellationTokenSource();
        _operation = source;
        SetBusy(true);
        try { await action(source.Token); }
        catch (OperationCanceledException)
        {
            ShowStatus("已取消等待。已提交给 ADB 的操作仍可能完成，请刷新确认设备状态。", true);
        }
        catch (Exception exception) { ShowStatus($"操作失败：{exception.Message}", true); }
        finally
        {
            if (ReferenceEquals(_operation, source))
            {
                _operation = null;
                if (!_closed)
                {
                    SetBusy(false);
                    if (_refreshPending)
                    {
                        _refreshPending = false;
                        await RefreshAsync();
                    }
                }
            }
        }
    }

    private void SetBusy(bool busy)
    {
        if (_closed) return;
        RefreshButton.IsEnabled = !busy;
        ManualPairingButton.IsEnabled = !busy;
        ServicesList.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
    }

    private void ShowStatus(string message, bool warning)
    {
        if (_closed) return;
        _statusIsWarning = warning;
        StatusText.Text = message;
        StatusText.Foreground = warning ? new SolidColorBrush(Color.FromRgb(180, 83, 9)) : (Brush)FindResource("AccentBrush");
    }

    private void ShowDiscoveryWarning(string message)
    {
        if (_closed) return;
        DiscoveryWarningText.Text = message;
        DiscoveryWarning.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _operation is null) return;
        CancelButton.IsEnabled = false;
        _refreshPending = false;
        ShowStatus("正在取消等待…已提交给 ADB 的操作仍可能完成。", true);
        _operation.Cancel();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        _operation?.Cancel();
        // Each operation owns its source until its asynchronous work has unwound.
    }

    private void FitToWorkArea()
    {
        var work = SystemParameters.WorkArea;
        var monitor = MonitorFromWindow(new WindowInteropHelper(this).Handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var origin = transform.Transform(new Point(info.Work.Left, info.Work.Top));
            var end = transform.Transform(new Point(info.Work.Right, info.Work.Bottom));
            work = new Rect(origin, end);
        }
        var availableWidth = Math.Max(1, work.Width - 16);
        var availableHeight = Math.Max(1, work.Height - 16);
        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
        Left = Math.Clamp(Left, work.Left + 8, work.Right - Width - 8);
        Top = Math.Clamp(Top, work.Top + 8, work.Bottom - Height - 8);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private sealed class ServiceRow(WirelessServiceEntry entry)
    {
        public WirelessServiceEntry Entry { get; } = entry;
        public string DisplayName => string.IsNullOrWhiteSpace(Entry.DisplayName) ? "无线 Android 设备" : Entry.DisplayName;
        public string ServiceTypeText => Entry.Type switch
        {
            WirelessServiceEntry.ConnectType => "无线调试 · TLS",
            WirelessServiceEntry.LegacyType => "传统 TCP 调试",
            WirelessServiceEntry.PairingType => "无线配对",
            _ => Entry.Type
        };
        public string ServiceDescription => $"{ServiceTypeText}  ·  {Entry.Name}";
        public bool ShowConnect => !Entry.IsPairing && !Entry.IsConnected;
        public bool ShowDisconnect => !Entry.IsPairing && Entry.Device?.IsNetworkConnection == true;
        public string StateText => Entry.IsPairing ? "等待配对" : Entry.Device?.State switch
        {
            "device" => "已连接",
            "unauthorized" => "等待授权",
            "offline" => "设备离线",
            null => "已发现 · 未连接",
            _ => $"状态：{Entry.Device.State}"
        };
        public Brush StateForeground => Entry.IsPairing ? Brushes.Purple : Entry.IsConnected ? Brushes.ForestGreen :
            Entry.Device is null ? new SolidColorBrush(Color.FromRgb(71, 85, 105)) : new SolidColorBrush(Color.FromRgb(180, 83, 9));
        public Brush StateBackground => new SolidColorBrush(Entry.IsPairing ? Color.FromRgb(250, 245, 255) :
            Entry.IsConnected ? Color.FromRgb(236, 253, 245) : Entry.Device is null ? Color.FromRgb(241, 245, 249) : Color.FromRgb(255, 247, 237));
    }
}