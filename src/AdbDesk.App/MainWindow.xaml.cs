using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AdbDesk.Core;
using Microsoft.Win32;

namespace AdbDesk.App;

public partial class MainWindow : Window
{
    private static readonly string[] ActiveInstallStates = ["排队中", "安装中"];

    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _deviceTimer;
    private AdbService? _adbService;
    private MirrorService? _mirrorService;
    private InstallQueue? _installQueue;
    private PairingService? _pairingService;
    private PairingWindow? _pairingWindow;
    private int _refreshing;
    private int _deviceRevision;
    private bool _servicesReady;
    private bool _isClosing;
    private bool _closePending, _closeComplete;
    private bool _quickConnecting;
    private bool _adbRestarting;
    private ToolPaths? _toolPaths;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        InitializeMirrorPreferences();
        InitializeNetworkStatus();
        InitializeConnectionHistory();
        AdbConsole.RefreshRequested += async (_, _) => await RefreshDevicesAsync();
        AppToolbox.RestartRequested = RestartAdbServerAsync;
        _deviceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _deviceTimer.Tick += DeviceTimer_Tick;
    }

    public ObservableCollection<DeviceRow> Devices { get; } = [];
    public ObservableCollection<InstallRow> InstallTasks { get; } = [];
    public ObservableCollection<string> MirrorLogs { get; } = [];

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeUpdates();
        await InitializeServicesAsync();
    }

    private async Task InitializeServicesAsync()
    {
        RetryButton.IsEnabled = false;
        SetStatus("正在检测 ADB 与 scrcpy 工具…", StatusKind.Working);

        try
        {
            await AppToolbox.StopCapturesAsync();
            DisposeServices();
            var paths = ToolPaths.Discover(AppContext.BaseDirectory);
            _toolPaths = paths;
            AdbConsole.Configure(paths);
            AppToolbox.Configure(paths);
            _screenshotService = new ScreenshotService(paths);
            _adbService = new AdbService(paths) { ConfirmEndpointAsync = ConfirmNetworkEndpointAsync };
            _mirrorService = new MirrorService(paths);
            _installQueue = new InstallQueue(paths);
            _pairingService = new PairingService(paths) { ConfirmEndpointAsync = ConfirmNetworkEndpointAsync };
            _mirrorService.Changed += MirrorService_Changed;
            _installQueue.Changed += InstallQueue_Changed;
            _servicesReady = true;
            RetryButton.Visibility = Visibility.Collapsed;
            _deviceTimer.Start();
            SetStatus("工具已就绪，正在查找设备", StatusKind.Ready);
            await RefreshDevicesAsync();
        }
        catch (Exception ex)
        {
            _servicesReady = false;
            _deviceTimer.Stop();
            RetryButton.Visibility = Visibility.Visible;
            SetStatus($"工具不可用：{ex.Message}。请将完整 scrcpy 工具包放到 tools\\scrcpy 后重试。", StatusKind.Error);
        }
        finally
        {
            RetryButton.IsEnabled = true;
        }
    }

    private async void DeviceTimer_Tick(object? sender, EventArgs e) => await RefreshDevicesAsync();

    private async Task RefreshDevicesAsync()
    {
        if (_adbRestarting || _adbService is null || Interlocked.Exchange(ref _refreshing, 1) != 0)
        {
            return;
        }

        var revision = _deviceRevision;
        try
        {
            var discovered = await _adbService.GetDevicesAsync(_lifetime.Token);
            if (_isClosing || revision != _deviceRevision) return;
            var serials = discovered.Select(device => device.Serial).ToHashSet(StringComparer.Ordinal);

            for (var i = Devices.Count - 1; i >= 0; i--)
            {
                if (!serials.Contains(Devices[i].Serial))
                {
                    if (_mirrorService?.IsRunning(Devices[i].Serial) == true || Devices[i].IsConnectionBusy)
                    {
                        Devices[i].Update(Devices[i].Device with { State = "stale" });
                        Devices[i].IsSelected = false;
                    }
                    else
                    {
                        Devices[i].PropertyChanged -= DeviceRow_PropertyChanged;
                        Devices.RemoveAt(i);
                    }
                }
            }

            foreach (var device in discovered.OrderByDescending(device => device.IsReady).ThenBy(device => device.Model))
            {
                var row = Devices.FirstOrDefault(item => string.Equals(item.Serial, device.Serial, StringComparison.Ordinal));
                if (row is null)
                {
                    row = new DeviceRow(device);
                    row.PropertyChanged += DeviceRow_PropertyChanged;
                    Devices.Add(row);
                }
                else
                {
                    row.Update(device);
                    if (!row.IsReady)
                    {
                        row.IsSelected = false;
                    }
                }

                row.IsMirroring = _mirrorService?.IsRunning(device.Serial) == true;
            }

            UpdateDeviceSummary();
            AdbConsole.UpdateDevices(discovered);
            AppToolbox.UpdateDevices(discovered);
            SetStatus(
                Devices.Count == 0 ? "未发现设备；请连接 USB 并在手机上确认调试授权" : $"已发现 {Devices.Count} 台设备",
                Devices.Count == 0 ? StatusKind.Neutral : StatusKind.Ready);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_isClosing || revision != _deviceRevision) return;
            foreach (var row in Devices)
            {
                row.Update(row.Device with { State = "stale" });
                row.IsSelected = false;
            }

            UpdateDeviceSummary();
            AdbConsole.UpdateDevices(Devices.Select(row => row.Device));
            AppToolbox.UpdateDevices(Devices.Select(row => row.Device));
            SetStatus($"刷新设备失败：{ex.Message}", StatusKind.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private void DeviceRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceRow.IsSelected))
        {
            UpdateDeviceSummary();
        }
    }

    private void UpdateDeviceSummary()
    {
        var ready = Devices.Count(device => device.IsReady);
        var unauthorized = Devices.Count(device => device.State == "unauthorized");
        var offline = Devices.Count(device => device.State == "offline");
        var mirroring = Devices.Count(device => device.IsMirroring);
        DeviceCountText.Text = $"共 {Devices.Count} · 可用 {ready} · 待授权 {unauthorized} · 离线 {offline} · 投屏 {mirroring}";
        SelectedCountText.Text = $"已选 {Devices.Count(device => device.IsSelected && device.IsReady)} 台";
        EmptyDevicePanel.Visibility = Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _adbService?.InvalidateNetworkCache();
        await RefreshDevicesAsync();
    }

    private async void DeviceWifiIp_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing || _adbService is null || (sender as FrameworkElement)?.DataContext is not DeviceRow { IsReady: true } row) return;
        try
        {
            var network = await _adbService.GetNetworkAsync(row.Device, _lifetime.Token);
            if (_isClosing || !Devices.Contains(row)) return;
            row.Update(row.Device with { Network = network });
            if (network.WifiAddress.Length == 0) { SetStatus("未取得手机 Wi-Fi IP；请检查 Wi-Fi 或 USB 授权。VPN 地址不会作为备用。", StatusKind.Warning); return; }
            Clipboard.SetText(network.WifiAddress);
            SetStatus($"已复制 {row.DisplayName} 的 Wi-Fi IP：{network.WifiAddress}", StatusKind.Ready);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus($"读取／复制手机 Wi-Fi IP 失败：{ex.Message}", StatusKind.Warning); }
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e) => await InitializeServicesAsync();

    private void WirelessConnectionButton_Click(object sender, RoutedEventArgs e) => OpenPairingWindow(PairingTab.Qr);

    private async void QuickConnectButton_Click(object sender, RoutedEventArgs e) => await QuickConnectAsync();

    private async void QuickConnectInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        await QuickConnectAsync();
    }

    private async Task QuickConnectAsync()
    {
        if (_isClosing || _quickConnecting) return;
        if (!_servicesReady || _pairingService is not { } service)
        {
            ShowQuickConnectResult("连接服务未就绪，请先检查 ADB 工具。", true);
            return;
        }

        var ip = QuickConnectIp.Text.Trim();
        var port = QuickConnectPort.Text.Trim();
        if (ip.Length == 0 || ip.Any(c => c != '.' && (c < '0' || c > '9')))
        {
            ShowQuickConnectResult("请填写手机 IPv4 地址，例如 192.168.1.100；无需输入冒号。", true);
            QuickConnectIp.Focus();
            return;
        }
        if (port.Length == 0 || port.Any(c => c < '0' || c > '9') ||
            !int.TryParse(port, out var portNumber) || portNumber is < 1 or > 65535)
        {
            ShowQuickConnectResult("连接端口必须是 1–65535 的数字，无需输入冒号。", true);
            QuickConnectPort.Focus();
            return;
        }

        _quickConnecting = true;
        QuickConnectButton.IsEnabled = QuickConnectIp.IsEnabled = QuickConnectPort.IsEnabled = false;
        ConnectionHistoryButton.IsEnabled = false;
        QuickConnectButton.Content = "连接中…";
        ShowQuickConnectResult($"正在连接 {ip}:{port}…", false);
        try
        {
            var result = await service.ConnectAsync($"{ip}:{port}", _lifetime.Token);
            if (_isClosing) return;
            ShowQuickConnectResult(result.Message, !result.Connected);
            AppendMirrorLog(result.Endpoint, result.Message);
            if (result.Connected)
            {
                RememberConnection(result.Endpoint, result.Message);
                await RefreshDevicesAsync();
            }
        }
        catch (OperationCanceledException) when (_isClosing) { }
        catch (Exception ex)
        {
            if (!_isClosing) ShowQuickConnectResult($"连接失败：{ex.Message}", true);
        }
        finally
        {
            _quickConnecting = false;
            if (!_isClosing)
            {
                QuickConnectButton.IsEnabled = QuickConnectIp.IsEnabled = QuickConnectPort.IsEnabled = true;
                ConnectionHistoryButton.IsEnabled = true;
                QuickConnectButton.Content = "连接";
            }
        }
    }

    private void ShowQuickConnectResult(string message, bool warning)
    {
        QuickConnectResult.Text = message;
        QuickConnectResult.ToolTip = message;
        QuickConnectResult.Foreground = warning
            ? new SolidColorBrush(Color.FromRgb(180, 83, 9))
            : (Brush)FindResource("MutedBrush");
        QuickConnectResult.Visibility = Visibility.Visible;
    }

    private void OpenPairingWindow(PairingTab initialTab)
    {
        if (_isClosing)
        {
            return;
        }

        if (_pairingService is null)
        {
            SetStatus("无线配对服务未就绪，请检查工具后重试", StatusKind.Warning);
            return;
        }

        if (_pairingWindow is { IsLoaded: true })
        {
            if (_pairingWindow.WindowState == WindowState.Minimized) _pairingWindow.WindowState = WindowState.Normal;
            _pairingWindow.Activate();
            return;
        }

        var window = new PairingWindow(_pairingService, initialTab, RefreshDevicesAsync, GetNetworkHint,
            () => Devices.Where(row => row.IsReady && row.Device.Transport == DeviceTransport.Usb)
                .SelectMany(row => row.Device.Network.WifiAddresses.Select(address => address.Address)).Distinct().ToArray())
        {
            Owner = this
        };
        _pairingWindow = window;
        window.Closed += async (_, _) =>
        {
            if (ReferenceEquals(_pairingWindow, window))
            {
                _pairingWindow = null;
            }
            if (!_isClosing) await RefreshDevicesAsync();
        };
        window.Show();
    }

    private void StartSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var targets = Devices.Where(device => device.IsSelected && device.IsReady).ToArray();
        if (targets.Length == 0)
        {
            SetStatus("请先勾选至少一台已授权设备", StatusKind.Warning);
            return;
        }

        foreach (var target in targets)
        {
            StartMirror(target);
        }
    }

    private void StopSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var targets = Devices.Where(device => device.IsSelected && (device.IsMirroring || device.IsMirrorStarting)).ToArray();
        if (targets.Length == 0)
        {
            SetStatus("所选设备当前没有正在运行的投屏", StatusKind.Warning);
            return;
        }

        foreach (var target in targets)
        {
            try
            {
                StopMirror(target);
            }
            catch (Exception ex)
            {
                AppendMirrorLog(target.Serial, $"停止失败：{ex.Message}");
            }
        }

        SetStatus($"正在停止 {targets.Length} 个投屏或启动任务", StatusKind.Working);
    }

    private void MirrorButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DeviceRow row)
        {
            return;
        }

        if (row.IsMirroring || row.IsMirrorStarting)
        {
            try
            {
                var starting = row.IsMirrorStarting;
                StopMirror(row);
                SetStatus(starting ? $"已取消 {row.DisplayName} 的投屏启动" : $"正在停止 {row.DisplayName} 的投屏", StatusKind.Working);
            }
            catch (Exception ex)
            {
                SetStatus($"停止投屏失败：{ex.Message}", StatusKind.Error);
            }
        }
        else
        {
            StartMirror(row);
        }
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing || _adbService is not { } service ||
            (sender as FrameworkElement)?.DataContext is not DeviceRow row || !row.CanDisconnectAction) return;
        if (InstallTasks.Any(task => task.Serial == row.Serial && ActiveInstallStates.Contains(task.Status)))
        {
            SetStatus("这台设备有安装任务，请等待完成或取消安装后再断开连接", StatusKind.Warning);
            return;
        }

        row.IsDisconnecting = true;
        row.IsSelected = false;
        row.SetWirelessStatus(string.Empty);
        _deviceRevision++;
        try
        {
            var result = await service.DisconnectWirelessAsync(row.Device, _lifetime.Token);
            if (_isClosing) return;
            row.SetWirelessStatus(result.Message);
            AppendMirrorLog(row.Serial, result.Message);
            if (result.Disconnected)
            {
                _mirrorService?.Stop(row.Serial);
                row.IsMirroring = false;
                row.Update(row.Device with { State = "offline" });
                row.PropertyChanged -= DeviceRow_PropertyChanged;
                Devices.Remove(row);
                UpdateDeviceSummary();
            }
            SetStatus(result.Message, result.Disconnected ? StatusKind.Ready : StatusKind.Warning);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_isClosing)
            {
                var message = $"无法确认断开结果：{exception.Message}";
                row.SetWirelessStatus(message);
                AppendMirrorLog(row.Serial, message);
                SetStatus(message, StatusKind.Error);
            }
        }
        finally
        {
            row.IsDisconnecting = false;
            _deviceRevision++;
        }
        if (!_isClosing) await RefreshDevicesAsync();
    }

    private async void WirelessButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing || _adbService is null || (sender as FrameworkElement)?.DataContext is not DeviceRow row)
        {
            return;
        }

        if (!row.CanWirelessAction)
        {
            SetStatus("无线切换仅支持当前已授权的 USB 设备，请等待当前操作完成", StatusKind.Warning);
            return;
        }

        if (HasActiveInstallTasks())
        {
            MessageBox.Show(
                this,
                "当前有安装任务正在排队或执行。请等待安装完成或取消任务后，再切换设备连接方式。",
                "暂时无法切换无线连接",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var enabled = string.Equals((sender as Button)?.Tag?.ToString(), "enable", StringComparison.Ordinal);
        var actionText = enabled ? "开启无线" : "关闭无线";
        var explanation = enabled
            ? "手机和电脑需要位于同一局域网（手机连接 Wi-Fi，电脑可以使用网线）。操作会重启手机的 ADB 服务并切换到 TCP/IP 5555 模式，当前投屏可能短暂断开。随后 AdbDesk 会尝试连接手机的局域网地址。"
            : "操作会把手机 ADB 切回 USB 模式，当前投屏可能短暂断开。请保持 USB 数据线连接。";
        var confirmation = MessageBox.Show(
            this,
            $"确定要为 {row.DisplayName} {actionText}吗？\n\n{explanation}",
            $"确认{actionText}",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (confirmation != MessageBoxResult.OK || _isClosing)
        {
            return;
        }

        if (!row.CanWirelessAction || HasActiveInstallTasks())
        {
            SetStatus("设备或安装任务状态已变化，请确认当前状态后重试无线切换", StatusKind.Warning);
            return;
        }

        var target = row.Device;
        row.IsWirelessBusy = true;
        row.SetWirelessStatus(string.Empty);
        try
        {
            var result = await _adbService.SetWirelessAsync(target, enabled, _lifetime.Token);
            var outcome = FormatWirelessResult(result, enabled);
            row.SetWirelessStatus(outcome.Summary);
            SetStatus(outcome.Summary, outcome.IsWarning ? StatusKind.Warning : StatusKind.Ready);
            if (!_isClosing)
            {
                MessageBox.Show(
                    this,
                    outcome.Detail,
                    enabled ? "开启无线结果" : "关闭无线结果",
                    MessageBoxButton.OK,
                    outcome.IsWarning ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }

            await RefreshDevicesAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var detail = $"{actionText}失败：{ex.Message}";
            row.SetWirelessStatus(detail);
            SetStatus(detail, StatusKind.Error);
            if (!_isClosing)
            {
                MessageBox.Show(this, detail, $"{actionText}失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            row.IsWirelessBusy = false;
        }
    }

    private static (string Summary, string Detail, bool IsWarning) FormatWirelessResult(WirelessResult result, bool enabled)
    {
        if (enabled && result.ModeEnabled && !result.Connected)
        {
            const string summary = "TCP/IP 模式已开启，但电脑尚未连接";
            var endpoint = string.IsNullOrWhiteSpace(result.Endpoint) ? "未获取到可连接地址" : result.Endpoint;
            return (
                summary,
                $"{summary}。\n\n目标：{endpoint}\n\n{result.Message}\n\n请保持 USB 连接，确认手机和电脑位于同一局域网后重试；也可以点击“关闭无线”恢复 USB 模式。",
                true);
        }

        if (!result.Succeeded)
        {
            var summary = enabled ? "无线 ADB 切换尚未确认成功" : "恢复 USB 模式尚未确认成功";
            var endpoint = string.IsNullOrWhiteSpace(result.Endpoint) ? string.Empty : $"\n\n目标：{result.Endpoint}";
            var detail = string.IsNullOrWhiteSpace(result.Message) ? "设备未返回可确认的结果。" : result.Message;
            return (
                summary,
                $"{summary}。{endpoint}\n\n{detail}\n\n请保持 USB 连接，刷新设备状态后重试。",
                true);
        }

        var fallback = enabled ? "无线连接操作已完成" : "已请求恢复 USB 模式";
        var summaryText = string.IsNullOrWhiteSpace(result.Message) ? fallback : result.Message;
        var endpointText = string.IsNullOrWhiteSpace(result.Endpoint) ? string.Empty : $"\n\n连接地址：{result.Endpoint}";
        return (summaryText, $"{summaryText}{endpointText}", false);
    }

    private async void StartMirror(DeviceRow row, MirrorTargetDisplay? targetOverride = null, bool replaceExisting = false)
    {
        if (_isClosing || !_servicesReady || _mirrorService is not { } mirrors || !row.IsReady)
        {
            SetStatus("投屏服务未就绪，或设备尚未授权", StatusKind.Warning);
            return;
        }

        if (row.IsConnectionBusy)
        {
            SetStatus("该设备正在处理连接操作，请等待完成后再启动投屏", StatusKind.Warning);
            return;
        }

        if (_mirrorStarting.ContainsKey(row.Serial) || (mirrors.IsRunning(row.Serial) && !replaceExisting)) return;
        using var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var device = row.Device;
        _mirrorStarting.Add(row.Serial, source);
        row.IsMirrorStarting = true;
        bool IsCurrentStart() => !_isClosing && _mirrorStarting.TryGetValue(row.Serial, out var pending) && ReferenceEquals(pending, source);
        try
        {
            var options = GetMirrorOptions();
            var displaySettings = GetDisplaySettings(row);
            options = options with { FullScreen = displaySettings.FullScreen };
            var cropToFill = displaySettings.Mode != MirrorScaleMode.Complete;
            if (cropToFill || options.FullScreen || targetOverride is not null)
            {
                var display = targetOverride ?? GetMirrorTargetDisplay();
                options = options with
                {
                    WindowX = display.Left + display.Width / 8,
                    WindowY = display.Top + display.Height / 8,
                    WindowWidth = display.Width * 3 / 4,
                    WindowHeight = display.Height * 3 / 4
                };
                if (cropToFill)
                {
                    var adb = _adbService ?? throw new InvalidOperationException("ADB 服务未就绪。");
                    var screenshots = _screenshotService ?? throw new InvalidOperationException("截图服务未就绪。");
                    SetStatus($"正在读取 {row.DisplayName} 的屏幕尺寸并计算居中裁剪…", StatusKind.Working);
                    var natural = await adb.GetDisplaySizeAsync(device, source.Token);
                    var png = await screenshots.CaptureAsync(device.Serial, source.Token);
                    var current = MirrorDisplayGeometry.ReadScreenshotSize(png);
                    options = options with { Crop = MirrorDisplayGeometry.CreateCrop(natural, current, display.Width, display.Height, displaySettings) };
                }
            }
            source.Token.ThrowIfCancellationRequested();
            if (_isClosing || !ReferenceEquals(_mirrorService, mirrors) || !Devices.Contains(row) || !row.IsReady ||
                row.Device.TransportId != device.TransportId) return;
            // Keep the old mirror alive until all new geometry and device checks succeed.
            if (replaceExisting) { CloseMirrorToolbar(row.Serial); mirrors.Stop(row.Serial); }
            mirrors.Start(row.Device, options);
            if (options.Crop is { } crop) AppendMirrorLog(row.Serial, $"保持比例裁剪：{crop.ToScrcpyArgument()}；改变手机方向后请停止并重开投屏。");
            SetStatus($"正在启动 {row.DisplayName} 的独立投屏窗口", StatusKind.Working);
        }
        catch (OperationCanceledException) { if (IsCurrentStart()) SetStatus("已取消投屏启动", StatusKind.Warning); }
        catch (Exception ex)
        {
            if (!IsCurrentStart()) return;
            SetStatus($"启动投屏失败：{ex.Message}", StatusKind.Error);
            AppendMirrorLog(row.Serial, ex.Message);
        }
        finally
        {
            if (_mirrorStarting.TryGetValue(row.Serial, out var pending) && ReferenceEquals(pending, source))
            {
                _mirrorStarting.Remove(row.Serial);
                row.IsMirrorStarting = false;
            }
        }
    }

    private MirrorOptions GetMirrorOptions()
    {
        var defaults = new MirrorOptions();
        var tag = (QualityComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        var values = tag.Split(',');
        var maxSize = int.TryParse(values.ElementAtOrDefault(0), out var size) && size > 0 ? size : defaults.MaxSize;
        var maxFps = int.TryParse(values.ElementAtOrDefault(1), out var fps) && fps > 0 ? fps : defaults.MaxFps;
        return new MirrorOptions(maxSize, maxFps, AudioCheckBox.IsChecked == true, FullScreen: MirrorFullScreenCheckBox.IsChecked == true);
    }

    private void MirrorService_Changed(MirrorEvent update)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var row = Devices.FirstOrDefault(device => string.Equals(device.Serial, update.Serial, StringComparison.Ordinal));
            if (row is not null)
            {
                row.IsMirroring = _mirrorService?.IsRunning(update.Serial) == true;
                if (row.IsMirroring) EnsureMirrorToolbar(row);
                else CloseMirrorToolbar(update.Serial);
            }

            AppendMirrorLog(update.Serial, update.Message);
            UpdateDeviceSummary();
        }));
    }

    private void ChooseApkButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要安装的 APK",
            Filter = "Android 安装包 (*.apk)|*.apk",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            EnqueueForSelectedDevices(dialog.FileName);
        }
    }

    private void PublicDrop_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetSingleApk(e.Data, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void PublicDrop_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (TryGetSingleApk(e.Data, out var apkPath))
        {
            EnqueueForSelectedDevices(apkPath);
        }
        else
        {
            SetStatus("仅支持一次拖入一个普通 .apk 文件", StatusKind.Warning);
        }
    }

    private void DeviceRow_DragOver(object sender, DragEventArgs e)
    {
        var ready = (sender as FrameworkElement)?.DataContext is DeviceRow { IsReady: true };
        e.Effects = ready && TryGetSingleApk(e.Data, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DeviceRow_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not DeviceRow { IsReady: true } row)
        {
            SetStatus("该设备尚未连接或授权，无法安装", StatusKind.Warning);
            return;
        }

        if (!TryGetSingleApk(e.Data, out var apkPath))
        {
            SetStatus("仅支持一次拖入一个普通 .apk 文件", StatusKind.Warning);
            return;
        }

        EnqueueInstall(row, apkPath);
    }

    private void EnqueueForSelectedDevices(string apkPath)
    {
        var targets = Devices.Where(device => device.IsSelected && device.IsReady).ToArray();
        if (targets.Length == 0)
        {
            SetStatus("请先勾选要安装 APK 的已授权设备", StatusKind.Warning);
            return;
        }

        var targetList = string.Join(Environment.NewLine, targets.Select(target => $"• {target.DisplayName}  ({target.Serial})"));
        var answer = MessageBox.Show(
            this,
            $"将 {Path.GetFileName(apkPath)} 加入以下 {targets.Length} 台设备的安装队列：{Environment.NewLine}{Environment.NewLine}{targetList}",
            "确认安装目标",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        if (Devices.Any(device => device.IsWirelessBusy) || targets.Any(device => device.IsDisconnecting))
        {
            SetStatus("设备正在切换无线连接，暂时不能提交新的安装任务", StatusKind.Warning);
            return;
        }

        foreach (var target in targets)
        {
            EnqueueInstall(target, apkPath);
        }
    }

    private void EnqueueInstall(DeviceRow target, string apkPath)
    {
        if (_isClosing || target.IsDisconnecting || Devices.Any(device => device.IsWirelessBusy))
        {
            SetStatus("设备正在切换无线连接，暂时不能提交新的安装任务", StatusKind.Warning);
            return;
        }

        if (_installQueue is null)
        {
            SetStatus("安装服务未就绪，请检查工具后重试", StatusKind.Error);
            return;
        }

        try
        {
            var id = _installQueue.Enqueue(target.Device, apkPath);
            InstallTasks.Insert(0, new InstallRow(id, target.Serial, apkPath));
            TrimInstallHistory();
            UpdateInstallSummary();
            SetStatus($"已将 {Path.GetFileName(apkPath)} 加入 {target.DisplayName} 的安装队列", StatusKind.Ready);
        }
        catch (Exception ex)
        {
            SetStatus($"无法加入安装队列：{ex.Message}", StatusKind.Error);
        }
    }

    private void InstallQueue_Changed(InstallUpdate update)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var row = InstallTasks.FirstOrDefault(task => task.Id == update.Id);
            if (row is null)
            {
                row = new InstallRow(update.Id, update.Serial, update.ApkPath, update.Status, update.Detail);
                InstallTasks.Insert(0, row);
            }
            else
            {
                row.Apply(update);
            }

            TrimInstallHistory();
            UpdateInstallSummary();
            AppendMirrorLog(update.Serial, $"[APK {update.Status}] {Path.GetFileName(update.ApkPath)}：{update.Detail}");
            if (update.Status is "失败" or "已取消")
            {
                SetStatus($"{Path.GetFileName(update.ApkPath)}：{update.Detail}",
                    update.Status == "失败" ? StatusKind.Error : StatusKind.Neutral);
            }
        }));
    }

    private void CancelInstallButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not InstallRow row || !row.CanCancel)
        {
            return;
        }

        try
        {
            _installQueue?.Cancel(row.Id);
            SetStatus("已请求取消；若设备已接收安装，结果仍以设备返回为准", StatusKind.Warning);
        }
        catch (Exception ex)
        {
            SetStatus($"取消任务失败：{ex.Message}", StatusKind.Error);
        }
    }

    private void CancelInstallMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = CancelInstallMenuButton };
        foreach (var row in InstallTasks.Where(task => task.CanCancel))
        {
            var item = new MenuItem
            {
                Header = $"{row.FileName} · {row.Serial} · {row.Status}",
                DataContext = row,
                ToolTip = "取消这台设备上的本次 APK 安装"
            };
            item.Click += CancelInstallButton_Click;
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.IsOpen = true;
    }

    private void TrimInstallHistory()
    {
        while (InstallTasks.Count > 200)
        {
            var removable = InstallTasks.LastOrDefault(task => !ActiveInstallStates.Contains(task.Status));
            if (removable is null)
            {
                break;
            }

            InstallTasks.Remove(removable);
        }
    }

    private void UpdateInstallSummary()
    {
        var active = InstallTasks.Count(task => ActiveInstallStates.Contains(task.Status));
        CancelInstallMenuButton.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
        CancelInstallMenuButton.Content = $"取消安装（{active}） ▾";
    }

    private bool HasActiveInstallTasks() =>
        InstallTasks.Any(task => ActiveInstallStates.Contains(task.Status));

    private void AppendMirrorLog(string serial, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        MirrorLogs.Add($"{DateTime.Now:HH:mm:ss}  [{serial}]  {message.Trim()}");
        while (MirrorLogs.Count > 300)
        {
            MirrorLogs.RemoveAt(0);
        }

        MirrorLogList.ScrollIntoView(MirrorLogs[^1]);
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e) => MirrorLogs.Clear();

    private void HelpButton_Click(object sender, RoutedEventArgs e) => OpenUsageHelp();

    private static bool TryGetSingleApk(IDataObject data, out string apkPath)
    {
        apkPath = string.Empty;
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files)
        {
            return false;
        }

        var candidate = files[0];
        if (!File.Exists(candidate) || !string.Equals(Path.GetExtension(candidate), ".apk", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        apkPath = candidate;
        return true;
    }

    private void SetStatus(string text, StatusKind kind)
    {
        StatusText.Text = text;
        StatusDot.Fill = kind switch
        {
            StatusKind.Ready => new SolidColorBrush(Color.FromRgb(22, 163, 74)),
            StatusKind.Working => new SolidColorBrush(Color.FromRgb(37, 99, 235)),
            StatusKind.Warning => new SolidColorBrush(Color.FromRgb(217, 119, 6)),
            StatusKind.Error => new SolidColorBrush(Color.FromRgb(220, 38, 38)),
            _ => new SolidColorBrush(Color.FromRgb(152, 162, 179))
        };
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeComplete) return;
        e.Cancel = true;
        if (_closePending) return;

        var activeInstalls = InstallTasks.Count(task => ActiveInstallStates.Contains(task.Status));
        var activeMirrors = Devices.Count(device => device.IsMirroring || device.IsMirrorStarting);
        if (activeInstalls + activeMirrors > 0)
        {
            var answer = MessageBox.Show(
                this,
                $"当前有 {activeMirrors} 个投屏或启动任务和 {activeInstalls} 个安装任务。退出会停止本程序启动的这些进程，确定退出吗？",
                "确认退出",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                _pendingUpdate = null;
                e.Cancel = true;
                return;
            }
        }

        _closePending = true;
        _isClosing = true;
        ApplicationSurface.IsEnabled = false;
        _deviceTimer.Stop();
        AppToolbox.RefreshSuspended = true;
        SetStatus("正在停止采集、保存日志并恢复手机代理…", StatusKind.Working);
        try
        {
            await AppToolbox.StopCapturesAsync();
            if (_pendingUpdate is not null) await StartUpdateHelperAsync(_pendingUpdate);
        }
        catch (Exception ex)
        {
            if (_pendingUpdate is not null) UpdateDiagnostics.Record("退出并交接更新", ex.ToString());
            _pendingUpdate = null;
            _closePending = _isClosing = false;
            ApplicationSurface.IsEnabled = true;
            AppToolbox.RefreshSuspended = false;
            _deviceTimer.Start();
            SetStatus("退出未完成：" + ex.Message, StatusKind.Error);
            return;
        }
        AdbConsole.Shutdown();
        AppToolbox.Shutdown();
        _deviceTimer.Stop();
        _pairingWindow?.Close();
        _pairingWindow = null;
        _lifetime.Cancel();
        _updateService.Dispose();
        DisposeServices();
        _lifetime.Dispose();
        _closeComplete = true;
        // Closing can complete synchronously when no capture needs stopping.
        // Leave the original Closing event before issuing the final close.
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    private void DisposeServices()
    {
        DisposeMirrorDisplay();
        foreach (var serial in _mirrorStarting.Keys.ToArray()) CancelMirrorStart(serial);
        AdbConsole.Configure(null);
        AppToolbox.Configure(null);
        _toolPaths = null;
        _pairingWindow?.Close();
        _pairingWindow = null;
        _pairingService = null;

        if (_mirrorService is not null)
        {
            _mirrorService.Changed -= MirrorService_Changed;
            _mirrorService.Dispose();
            _mirrorService = null;
        }

        if (_installQueue is not null)
        {
            _installQueue.Changed -= InstallQueue_Changed;
            _installQueue.Dispose();
            _installQueue = null;
        }

        _adbService = null;
        _screenshotService = null;
        _servicesReady = false;
    }

    private enum StatusKind
    {
        Neutral,
        Ready,
        Working,
        Warning,
        Error
    }
}
