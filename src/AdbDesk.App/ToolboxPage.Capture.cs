using System.Windows;
using AdbDesk.Core;

namespace AdbDesk.App;

public partial class ToolboxPage
{
    private LogCaptureWindow? _logWindow;
    private NetworkCaptureWindow? _networkWindow;
    private bool CapturesRunning => _logWindow?.IsCapturing == true || _networkWindow?.IsCapturing == true;

    private void UpdateCaptureDevices()
    {
        if (_logWindow is { } log) log.SetDeviceReady(_devices.Any(d => d.Serial == log.Serial && d.IsReady));
        if (_networkWindow is { } network) network.SetDeviceReady(_devices.Any(d => d.Serial == network.Serial && d.IsReady));
    }
    public async Task StopCapturesAsync()
    {
        var log = _logWindow;
        var network = _networkWindow;
        await Task.WhenAll(log?.StopAndCloseAsync() ?? Task.CompletedTask, network?.StopAndCloseAsync() ?? Task.CompletedTask, CloseAppManagerAsync());
        if (CapturesRunning) throw new InvalidOperationException("抓取进程尚未完全停止，请在抓取窗口重试停止后退出。");
    }
    private void CloseCaptureWindows()
    {
        _logWindow?.StopAndClose();
        if (_networkWindow is { } network) _ = network.StopAndCloseAsync();
    }
    private bool CanStartCapture() => !_closed && !RefreshSuspended && _operation is null && _layoutWindow?.IsCapturing != true && !IsAppManagerBusy;
    private void LogCapture_Click(object sender, RoutedEventArgs e) => OpenLogs(null);
    private void OpenLogs(RunningApp? app)
    {
        if (_closed || RefreshSuspended) return;
        if (_logWindow is { IsClosed: false } existing)
        {
            existing.Activate();
            StatusText.Text = "日志窗口已打开；可在窗口停止后调整采集范围。";
            return;
        }
        if (_logWindow?.IsCapturing == true || !TargetReady || _paths is null || Serial is not { } serial) return;
        var device = _devices.First(d => d.Serial == serial);
        var window = new LogCaptureWindow(_paths, serial, device.DeviceName.Length > 0 ? device.DeviceName : device.Model, app)
        { Owner = Window.GetWindow(this), CanCapture = CanStartCapture };
        _logWindow = window;
        window.CaptureStateChanged += (_, _) =>
        {
            if (window.IsClosed && !window.IsCapturing && ReferenceEquals(_logWindow, window)) _logWindow = null;
            UpdateControls();
        };
        window.Closed += (_, _) => { if (!window.IsCapturing && ReferenceEquals(_logWindow, window)) _logWindow = null; UpdateControls(); };
        window.Show();
    }
    private void NetworkCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || RefreshSuspended) return;
        if (_networkWindow is { IsClosed: false } current) { current.Activate(); return; }
        if (_networkWindow?.IsCapturing == true || !TargetReady || _paths is null || Serial is not { } serial) return;
        var device = _devices.First(d => d.Serial == serial);
        var window = new NetworkCaptureWindow(_paths, serial, device.DeviceName.Length > 0 ? device.DeviceName : device.Model)
        { Owner = Window.GetWindow(this), CanCapture = CanStartCapture };
        _networkWindow = window;
        window.CaptureStateChanged += (_, _) =>
        {
            if (window.IsClosed && !window.IsCapturing && ReferenceEquals(_networkWindow, window)) _networkWindow = null;
            UpdateControls();
        };
        window.Closed += (_, _) => { if (!window.IsCapturing && ReferenceEquals(_networkWindow, window)) _networkWindow = null; UpdateControls(); };
        window.Show();
    }
}
