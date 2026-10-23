using System.IO;
using System.Windows;
using AdbDesk.Core;

namespace AdbDesk.App;

public partial class MainWindow
{
    private WirelessDiscoveryWindow? _wirelessDiscoveryWindow;

    private void DiscoverDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing || _adbRestarting) return;
        if (!_servicesReady || _toolPaths is not { } paths || _adbService is not { } adb || _pairingService is not { } pairing)
        {
            SetStatus("发现服务尚未就绪，请先检查 ADB 工具。", StatusKind.Warning);
            return;
        }
        if (_wirelessDiscoveryWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AdbDesk", "wireless-device-names.json");
        var discovery = new WirelessDiscoveryService(paths, cachePath);
        var window = new WirelessDiscoveryWindow(discovery, pairing, adb, OnWirelessConnectionConfirmedAsync,
            endpoint => OpenPairingWindow(PairingTab.Code, string.IsNullOrWhiteSpace(endpoint) ? null : endpoint))
        {
            Owner = this,
            DisconnectAsync = DisconnectDeviceAsync
        };
        _wirelessDiscoveryWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_wirelessDiscoveryWindow, window)) _wirelessDiscoveryWindow = null;
        };
        window.Show();
    }

    private async Task OnWirelessConnectionConfirmedAsync(string endpoint)
    {
        if (_isClosing) return;
        var message = $"已连接并确认设备在线：{endpoint}。";
        if (!_quickConnecting) FillConnectionEndpoint(endpoint);
        ShowQuickConnectResult(message, false);
        RememberConnection(endpoint, message);
        await RefreshDevicesAsync();
        if (!_isClosing && _wirelessDiscoveryWindow is { } discovery)
            await discovery.RefreshAfterPairingAsync();
    }

    private async Task OnWirelessPairingCompletedAsync()
    {
        if (_isClosing) return;
        await RefreshDevicesAsync();
        if (!_isClosing && _wirelessDiscoveryWindow is { } discovery)
            await discovery.RefreshAfterPairingAsync();
    }

    private async Task<DisconnectResult> DisconnectDeviceAsync(DeviceInfo device, CancellationToken cancellationToken)
    {
        if (_isClosing || _adbRestarting || _adbService is not { } service)
            return new DisconnectResult(false, "设备服务未就绪。");
        if (!device.IsNetworkConnection) return new DisconnectResult(false, "仅可断开指定的无线 ADB 连接。");
        var row = Devices.FirstOrDefault(item => item.Serial == device.Serial);
        if (row is not null && !row.CanDisconnectAction)
            return new DisconnectResult(false, "这台设备正在处理其他操作，请稍后再断开连接。");
        if (InstallTasks.Any(task => task.Serial == device.Serial && ActiveInstallStates.Contains(task.Status)))
            return new DisconnectResult(false, "这台设备有安装任务，请等待完成或取消安装后再断开连接。");

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        if (row is not null)
        {
            row.IsDisconnecting = true;
            row.IsSelected = false;
            row.SetWirelessStatus(string.Empty);
        }
        _deviceRevision++;
        DisconnectResult result;
        try
        {
            result = await service.DisconnectWirelessAsync(device, lifetime.Token);
            if (_isClosing) return result;
            row?.SetWirelessStatus(result.Message);
            AppendMirrorLog(device.Serial, result.Message);
            if (result.Disconnected)
            {
                _mirrorService?.Stop(device.Serial);
                if (row is not null)
                {
                    row.IsMirroring = false;
                    row.Update(row.Device with { State = "offline" });
                    row.PropertyChanged -= DeviceRow_PropertyChanged;
                    Devices.Remove(row);
                }
                UpdateDeviceSummary();
            }
            SetStatus(result.Message, result.Disconnected ? StatusKind.Ready : StatusKind.Warning);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            result = new DisconnectResult(false, $"无法确认断开结果：{exception.Message}");
            if (!_isClosing)
            {
                row?.SetWirelessStatus(result.Message);
                AppendMirrorLog(device.Serial, result.Message);
                SetStatus(result.Message, StatusKind.Error);
            }
        }
        finally
        {
            if (row is not null) row.IsDisconnecting = false;
            _deviceRevision++;
        }
        if (!_isClosing && !cancellationToken.IsCancellationRequested) await RefreshDevicesAsync();
        return result;
    }

    private void CloseWirelessDiscovery()
    {
        var window = _wirelessDiscoveryWindow;
        _wirelessDiscoveryWindow = null;
        window?.Close();
    }
}
