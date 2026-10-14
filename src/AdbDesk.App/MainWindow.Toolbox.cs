using System.Windows;
using AdbDesk.Core;

namespace AdbDesk.App;

public partial class MainWindow
{
    private async Task<string> RestartAdbServerAsync()
    {
        if (_isClosing || _adbRestarting || _toolPaths is not { } paths) return "ADB 工具尚未就绪。";
        bool Busy() => AdbConsole.IsRunning || AppToolbox.IsRunning || _quickConnecting ||
            Devices.Any(d => d.IsConnectionBusy || d.IsCapturing) ||
            InstallTasks.Any(t => ActiveInstallStates.Contains(t.Status)) || _pairingWindow?.IsVisible == true;
        if (Busy())
            return "请先结束安装、截图、无线配对或控制台任务，再重启 ADB 服务。";

        // Modal confirmation pumps the dispatcher: device discovery must not
        // auto-start a toolbox query while this restart is being confirmed/drained.
        AppToolbox.RefreshSuspended = true;
        try
        {
            if (MessageBox.Show(this,
                    "将依次执行：\nadb kill-server\nadb start-server\n\n这会暂时中断电脑上所有工具使用的 ADB 连接和投屏。无线设备可能需要重新连接，投屏需要手动重新启动。\n\n确定重启吗？",
                    "重启 ADB 服务", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return "已取消重启。";

            if (_isClosing || Busy()) return "任务状态已变化，未重启 ADB；请结束当前任务后重试。";

            _adbRestarting = true;
            _deviceTimer.Stop();
            ApplicationSurface.IsEnabled = false;
            SetStatus("正在重启电脑 ADB 服务…", StatusKind.Working);
            try
            {
                // Drain the existing device probe before killing the server. Otherwise
                // that probe could spawn a replacement server between kill and start.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                while (Volatile.Read(ref _refreshing) != 0) await Task.Delay(100, timeout.Token);
                var status = await new RunningAppsService(paths).RestartServerAsync(_lifetime.Token);
                return "ADB 服务已重启。请按需重新连接无线设备并启动投屏。\r\n" + status;
            }
            catch (OperationCanceledException) when (!_isClosing)
            {
                return "等待设备查询结束超时，未重启 ADB 服务；请稍后重试。";
            }
            finally
            {
                _adbRestarting = false;
                if (!_isClosing)
                {
                    ApplicationSurface.IsEnabled = true;
                    _deviceTimer.Start();
                    await RefreshDevicesAsync();
                }
            }
        }
        finally { AppToolbox.RefreshSuspended = false; }
    }
}
