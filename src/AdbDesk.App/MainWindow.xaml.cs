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
    private bool _quickConnecting;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        InitializeNetworkStatus();
        _deviceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _deviceTimer.Tick += DeviceTimer_Tick;
    }

    public ObservableCollection<DeviceRow> Devices { get; } = [];
    public ObservableCollection<InstallRow> InstallTasks { get; } = [];
    public ObservableCollection<string> MirrorLogs { get; } = [];

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await InitializeServicesAsync();

    private async Task InitializeServicesAsync()
    {
        RetryButton.IsEnabled = false;
        SetStatus("正在检测 ADB 与 scrcpy 工具…", StatusKind.Working);

        try
        {
            DisposeServices();
            var paths = ToolPaths.Discover(AppContext.BaseDirectory);
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
        if (_adbService is null || Interlocked.Exchange(ref _refreshing, 1) != 0)
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

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

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
        QuickConnectButton.Content = "连接中…";
        ShowQuickConnectResult($"正在连接 {ip}:{port}…", false);
        try
        {
            var result = await service.ConnectAsync($"{ip}:{port}", _lifetime.Token);
            if (_isClosing) return;
            ShowQuickConnectResult(result.Message, !result.Connected);
            AppendMirrorLog(result.Endpoint, result.Message);
            if (result.Connected) await RefreshDevicesAsync();
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

        var window = new PairingWindow(_pairingService, initialTab, RefreshDevicesAsync, GetNetworkHint)
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
        var targets = Devices.Where(device => device.IsSelected && device.IsMirroring).ToArray();
        if (targets.Length == 0)
        {
            SetStatus("所选设备当前没有正在运行的投屏", StatusKind.Warning);
            return;
        }

        foreach (var target in targets)
        {
            try
            {
                _mirrorService?.Stop(target.Serial);
            }
            catch (Exception ex)
            {
                AppendMirrorLog(target.Serial, $"停止失败：{ex.Message}");
            }
        }

        SetStatus($"正在停止 {targets.Length} 个投屏", StatusKind.Working);
    }

    private void MirrorButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DeviceRow row)
        {
            return;
        }

        if (row.IsMirroring)
        {
            try
            {
                _mirrorService?.Stop(row.Serial);
                SetStatus($"正在停止 {row.DisplayName} 的投屏", StatusKind.Working);
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

    private void StartMirror(DeviceRow row)
    {
        if (!_servicesReady || _mirrorService is null || !row.IsReady)
        {
            SetStatus("投屏服务未就绪，或设备尚未授权", StatusKind.Warning);
            return;
        }

        if (row.IsConnectionBusy)
        {
            SetStatus("该设备正在处理连接操作，请等待完成后再启动投屏", StatusKind.Warning);
            return;
        }

        try
        {
            _mirrorService.Start(row.Device, GetMirrorOptions());
            SetStatus($"正在启动 {row.DisplayName} 的独立投屏窗口", StatusKind.Working);
        }
        catch (Exception ex)
        {
            SetStatus($"启动投屏失败：{ex.Message}", StatusKind.Error);
            AppendMirrorLog(row.Serial, ex.Message);
        }
    }

    private MirrorOptions GetMirrorOptions()
    {
        var tag = (QualityComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "1280,30";
        var values = tag.Split(',');
        var maxSize = int.TryParse(values.ElementAtOrDefault(0), out var size) ? size : 1280;
        var maxFps = int.TryParse(values.ElementAtOrDefault(1), out var fps) ? fps : 30;
        return new MirrorOptions(maxSize, maxFps, AudioCheckBox.IsChecked == true);
    }

    private void MirrorService_Changed(MirrorEvent update)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var row = Devices.FirstOrDefault(device => string.Equals(device.Serial, update.Serial, StringComparison.Ordinal));
            if (row is not null)
            {
                row.IsMirroring = update.IsRunning;
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

    private void InstallRow_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || (sender as FrameworkElement)?.DataContext is not InstallRow row)
        {
            return;
        }

        e.Handled = true;
        new InstallDetailWindow(row)
        {
            Owner = this
        }.ShowDialog();
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
        InstallCountText.Text = InstallTasks.Count == 0 ? "暂无任务" : $"{active} 个进行中 · 共 {InstallTasks.Count} 个";
        EmptyInstallText.Visibility = InstallTasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            "1. 在手机上开启开发者选项和 USB 调试。\n\n" +
            "2. 首次连接时，请解锁手机并确认“允许 USB 调试”。显示“等待授权”时可重新插拔数据线。\n\n" +
            "3. 部分品牌安装 APK 时，还需在手机上确认“通过 USB 安装”或类似权限。\n\n" +
            "4. 投屏窗口由 scrcpy 独立打开。可直接把 APK 拖进该窗口；这是 scrcpy 原生安装，不进入主安装队列，结果会显示在投屏日志中，也不能从主界面取消。\n\n" +
            "5. 开启无线要求手机与电脑位于同一局域网，电脑可以使用网线。切换连接方式会重启手机 ADB 服务，投屏可能短暂断开；请在结果确认前保持 USB 数据线连接。\n\n" +
            "6. AdbDesk 只关闭自己启动的投屏和安装进程，不会停止系统 ADB 服务。",
            "连接与授权帮助",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

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

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        var activeInstalls = InstallTasks.Count(task => ActiveInstallStates.Contains(task.Status));
        var activeMirrors = Devices.Count(device => device.IsMirroring);
        if (activeInstalls + activeMirrors > 0)
        {
            var answer = MessageBox.Show(
                this,
                $"当前有 {activeMirrors} 个投屏和 {activeInstalls} 个安装任务。退出会停止本程序启动的这些进程，确定退出吗？",
                "确认退出",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _isClosing = true;
        _deviceTimer.Stop();
        _pairingWindow?.Close();
        _pairingWindow = null;
        _lifetime.Cancel();
        DisposeServices();
        _lifetime.Dispose();
    }

    private void DisposeServices()
    {
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
