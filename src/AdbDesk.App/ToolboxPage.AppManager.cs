using System.Windows;
using AdbDesk.Core;

namespace AdbDesk.App;

public partial class ToolboxPage
{
    private AppManagerWindow? _appManagerWindow;
    private bool IsAppManagerBusy => _appManagerWindow?.IsBusy == true;
    private void UpdateAppManagerDevices()
    {
        if (_appManagerWindow is { } manager) manager.SetDeviceReady(_devices.Any(d => d.Serial == manager.Serial && d.IsReady));
    }
    private void CancelAppManager() => _appManagerWindow?.CancelOperation();
    private void CloseAppManager() => _appManagerWindow?.StopAndClose();
    private Task CloseAppManagerAsync() => _appManagerWindow?.StopAndCloseAsync() ?? Task.CompletedTask;
    private void AppManager_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || RefreshSuspended) return;
        if (_appManagerWindow is { IsClosed: false } current) { current.Activate(); return; }
        if (IsRunning || !TargetReady || _paths is null || Serial is not { } serial) return;
        var device = _devices.First(d => d.Serial == serial);
        var manager = new AppManagerWindow(_paths, serial, device.DeviceName.Length > 0 ? device.DeviceName : device.Model)
        {
            Owner = Window.GetWindow(this),
            CanOperate = () => !_closed && !RefreshSuspended && _operation is null && _layoutWindow?.IsCapturing != true && !CapturesRunning
        };
        manager.OpenAppLogs = app => OpenAppLogs(manager.Serial, app);
        _appManagerWindow = manager;
        manager.OperationStateChanged += (_, _) =>
        {
            if (manager.IsClosed && !manager.IsBusy && ReferenceEquals(_appManagerWindow, manager)) _appManagerWindow = null;
            UpdateControls();
        };
        manager.Closed += (_, _) =>
        {
            if (!manager.IsBusy && ReferenceEquals(_appManagerWindow, manager)) _appManagerWindow = null;
            UpdateControls();
        };
        manager.Show();
    }
}
