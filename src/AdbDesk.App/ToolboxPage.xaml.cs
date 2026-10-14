using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AdbDesk.Core;
using Microsoft.Win32;

namespace AdbDesk.App;

public partial class ToolboxPage : UserControl
{
    private RunningAppsService? _service;
    private ToolPaths? _paths;
    private LayoutInspectorWindow? _layoutWindow;
    private IReadOnlyList<DeviceInfo> _devices = [];
    private RunningAppsSnapshot? _snapshot;
    private CancellationTokenSource? _operation;
    private bool _ready, _updating, _closed;
    private string? _snapshotSerial, _exportDirectory;
    private int _generation;
    public bool IsRunning => _operation is not null || _layoutWindow?.IsCapturing == true || CapturesRunning || IsAppManagerBusy;
    public bool RefreshSuspended { get; set; }
    public Func<Task<string>>? RestartRequested { get; set; }
    private string? Serial => (TargetCombo.SelectedItem as TargetChoice)?.Serial;

    public ToolboxPage()
    {
        InitializeComponent();
        _ready = true;
        UpdateDevices([]);
    }

    public void Configure(ToolPaths? paths)
    {
        _generation++;
        _operation?.Cancel();
        _layoutWindow?.StopAndClose();
        CloseAppManager();
        CloseCaptureWindows();
        _paths = paths;
        _service = paths is null ? null : new RunningAppsService(paths);
        ClearSnapshot();
        UpdateControls();
    }

    public void Shutdown() { _closed = true; _generation++; _operation?.Cancel(); _layoutWindow?.StopAndClose(); CloseAppManager(); CloseCaptureWindows(); }

    public void UpdateDevices(IEnumerable<DeviceInfo> devices)
    {
        _devices = devices.ToArray();
        if (_layoutWindow is { } inspector) inspector.SetDeviceReady(_devices.Any(d => d.Serial == inspector.Serial && d.IsReady));
        UpdateCaptureDevices();
        UpdateAppManagerDevices();
        if (Serial is { } current && !_devices.Any(d => d.Serial == current && d.IsReady))
        {
            _operation?.Cancel();
            if (_snapshot is not null) StatusText.Text = "所选设备已离线，列表为此前快照；请重新连接后刷新。";
        }
        if (IsRunning) { UpdateControls(); return; }
        var oldSerial = Serial;
        var choices = new List<TargetChoice> { new(null, "请选择设备") };
        choices.AddRange(_devices.Where(d => d.IsReady).Select(d => new TargetChoice(d.Serial,
            $"{(d.DeviceName.Length > 0 ? d.DeviceName : d.Model)} · {d.Serial}")));
        if (oldSerial is not null && choices.All(c => c.Serial != oldSerial)) choices.Add(new(oldSerial, $"设备离线 · {oldSerial}"));
        if (TargetCombo.ItemsSource is IReadOnlyList<TargetChoice> old && old.SequenceEqual(choices)) { UpdateControls(); return; }
        _updating = true;
        TargetCombo.ItemsSource = choices;
        TargetCombo.SelectedItem = choices.FirstOrDefault(c => c.Serial == oldSerial) ?? choices[0];
        if (oldSerial is null && choices.Count == 2) TargetCombo.SelectedItem = choices[1];
        _updating = false;
        UpdateControls();
        if (Serial != oldSerial) { ClearSnapshot(); if (IsVisible) _ = RefreshAsync(); }
    }

    private bool TargetReady => Serial is { } serial && _devices.Any(d => d.Serial == serial && d.IsReady);
    private void UpdateControls()
    {
        if (!_ready) return;
        var idle = !IsRunning && !_closed;
        TargetCombo.IsEnabled = idle;
        RefreshButton.IsEnabled = idle && TargetReady && _service is not null;
        LayoutButton.IsEnabled = idle && TargetReady && _paths is not null;
        AppManagerButton.IsEnabled = !_closed && TargetReady && _paths is not null && !RefreshSuspended;
        LogCaptureButton.IsEnabled = !_closed && (_logWindow is not null || (TargetReady && _paths is not null && _operation is null));
        NetworkCaptureButton.IsEnabled = !_closed && (_networkWindow is not null || (TargetReady && _paths is not null && _operation is null));
        CancelButton.IsEnabled = IsRunning && !_closed;
        RestartButton.IsEnabled = idle && _service is not null;
        ActionsPanel.IsEnabled = idle && TargetReady && _snapshotSerial == Serial && AppList.SelectedItem is RunningApp;
        CategoryCombo.IsEnabled = SearchInput.IsEnabled = AppList.IsEnabled = idle;
    }

    private void ClearSnapshot()
    {
        _snapshot = null; _snapshotSerial = null;
        AppList.ItemsSource = null;
        if (_ready) { SummaryText.Text = "选择设备后刷新应用；列表以包名显示。"; DetailOutput.Clear(); }
    }

    private async void Page_VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_ready && IsVisible && !IsRunning) await RefreshAsync();
    }
    private async void Target_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updating) return;
        CloseAppManager();
        ClearSnapshot(); UpdateControls();
        if (IsVisible) await RefreshAsync();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) { _operation?.Cancel(); _layoutWindow?.CancelCapture(); CancelAppManager(); _logWindow?.CancelCapture(); if (_networkWindow is { } network) _ = network.StopCaptureAsync(); }

    private void Layout_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || IsRunning || RefreshSuspended || !TargetReady || _paths is null || Serial is not { } serial) return;
        if (_layoutWindow is { } current && current.Serial == serial) { current.Activate(); return; }
        _layoutWindow?.StopAndClose();
        var device = _devices.First(d => d.Serial == serial);
        var inspector = new LayoutInspectorWindow(_paths, serial, device.DeviceName.Length > 0 ? device.DeviceName : device.Model) { Owner = Window.GetWindow(this) };
        _layoutWindow = inspector;
        inspector.CanCapture = () => !_closed && !RefreshSuspended && _operation is null && !IsAppManagerBusy;
        inspector.CaptureStateChanged += (_, _) =>
        {
            if (inspector.IsClosed && !inspector.IsCapturing && ReferenceEquals(_layoutWindow, inspector)) _layoutWindow = null;
            UpdateControls();
        };
        inspector.Closed += (_, _) =>
        {
            // Closing cancels the capture, but its bounded remote-file cleanup
            // is still an ADB operation. Keep it busy until that cleanup ends.
            if (!inspector.IsCapturing && ReferenceEquals(_layoutWindow, inspector)) _layoutWindow = null;
            UpdateControls();
        };
        inspector.Show();
    }

    private async Task RefreshAsync()
    {
        if (RefreshSuspended || IsRunning || _closed || !TargetReady || _service is null || Serial is not { } serial) return;
        await OperateAsync("正在读取应用…", async (service, token) =>
        {
            var snapshot = await service.LoadAsync(serial, token);
            token.ThrowIfCancellationRequested();
            if (_closed || Serial != serial) return;
            _snapshot = snapshot; _snapshotSerial = serial;
            ApplyFilter();
            DetailOutput.Text = snapshot.Warnings.Count > 0 ? string.Join("\r\n", snapshot.Warnings) :
                "最近任务卡片不等于正在运行；后台列表仅表示查询时进程存在。选择应用可查看详细信息。";
            StatusText.Text = $"最后更新 {snapshot.CapturedAt:HH:mm:ss} · 前台 {snapshot.Foreground.Count} · 最近任务 {snapshot.Recent.Count} · 进程记录 {snapshot.Processes.Count}" +
                (snapshot.Warnings.Count > 0 ? $" · {snapshot.Warnings.Count} 项读取异常（见详情）" : "");
        });
    }

    private async Task OperateAsync(string message, Func<RunningAppsService, CancellationToken, Task> action)
    {
        if (IsRunning || _closed || _service is null) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        var generation = _generation;
        UpdateControls(); StatusText.Text = message;
        try { await action(_service, operation.Token); }
        catch (OperationCanceledException ex) { if (!_closed && generation == _generation) StatusText.Text = "操作已停止。" + (ex.Message.Contains("保留在") ? ex.Message : ""); }
        catch (Exception ex) { if (!_closed && generation == _generation) { StatusText.Text = "操作失败，展开详细输出查看原因。"; DetailOutput.Text = ex.Message; DetailExpander.IsExpanded = true; } }
        finally
        {
            _operation = null;
            if (!_closed) { UpdateDevices(_devices); UpdateControls(); }
        }
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) ApplyFilter(); }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (_ready) ApplyFilter(); }
    private void ApplyFilter()
    {
        if (_snapshot is null) return;
        var foreground = _snapshot.Foreground.Select(a => (a.PackageName, a.UserId)).ToHashSet();
        IEnumerable<RunningApp> rows = CategoryCombo.SelectedIndex switch
        {
            0 => _snapshot.Foreground,
            1 => _snapshot.Recent,
            _ => _snapshot.Processes.Where(a => !foreground.Contains((a.PackageName, a.UserId)))
        };
        var query = SearchInput.Text.Trim();
        var filtered = rows.Where(a => $"{a.PackageName} {a.Activity} {a.Pid}".Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        AppList.ItemsSource = filtered;
        SummaryText.Text = $"当前显示 {filtered.Length} 项 · " + (CategoryCombo.SelectedIndex switch
        {
            0 => "系统报告的已恢复 Activity；分屏、悬浮窗显示情况可能因系统而异。",
            1 => "系统最近任务记录，可能与厂商桌面显示的卡片有所不同。",
            _ => "排除已识别前台应用；一个应用可能有多个进程，缓存进程单独标注。"
        });
    }
    private void App_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (AppList.SelectedItem is RunningApp app) DetailOutput.Text = $"{app.Identity}\r\n{app.Status}\r\n{app.Activity}";
        UpdateControls();
    }
    private RunningApp? SelectedApp => ActionsPanel.IsEnabled ? AppList.SelectedItem as RunningApp : null;
    private void CopyPackage_Click(object sender, RoutedEventArgs e) { if (SelectedApp is { } app) Copy(app.PackageName); }
    private void CopyActivity_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is not { } app) return;
        if (app.Activity.Length == 0) { StatusText.Text = "该记录没有 Activity 信息。"; return; }
        Copy(app.Activity);
    }
    private void Copy(string text)
    {
        try { Clipboard.SetText(text); StatusText.Text = "已复制：" + text; }
        catch (Exception ex) { StatusText.Text = "复制失败：" + ex.Message; }
    }
    private async void Details_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is not { } app || Serial is not { } serial) return;
        await OperateAsync("正在读取版本和 APK 路径…", async (service, token) =>
        {
            var detail = await service.DetailsAsync(serial, app, token);
            token.ThrowIfCancellationRequested();
            DetailOutput.Text = detail.Length > 300000 ? detail[..300000] + "\r\n[显示已截断]" : detail;
            DetailExpander.IsExpanded = true;
            StatusText.Text = "已读取应用详情，可选中文本复制。";
        });
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is not { } app || Serial is not { } serial) return;
        var dialog = new OpenFolderDialog { Title = "选择 APK 保存目录（将创建独立子目录）" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await OperateAsync("正在导出 APK（包含全部拆分包）…", async (service, token) =>
        {
            var directory = await service.ExportAsync(serial, app, dialog.FolderName, token);
            token.ThrowIfCancellationRequested();
            _exportDirectory = directory;
            OpenExportButton.IsEnabled = true;
            DetailOutput.Text = $"导出完成：{directory}\r\n包含 base 与全部 split APK，不包含应用数据。拆分包不能作为独立 APK 安装。";
            DetailExpander.IsExpanded = true;
            StatusText.Text = "APK 已导出，点击“打开导出目录”查看。";
        });
    }
    private void OpenExport_Click(object sender, RoutedEventArgs e)
    {
        if (_exportDirectory is null) return;
        try
        {
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            info.ArgumentList.Add(_exportDirectory);
            Process.Start(info)?.Dispose();
        }
        catch (Exception ex) { StatusText.Text = "打开目录失败：" + ex.Message; }
    }
    private async void ForceStop_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is not { } app || Serial is not { } serial) return;
        if (MessageBox.Show(Window.GetWindow(this), $"设备：{serial}\n应用：{app.Identity}\n\n强制停止会中断该应用的任务，确定继续吗？", "强制停止应用", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var succeeded = false;
        await OperateAsync("正在停止指定应用…", async (service, token) =>
        {
            await service.StopAsync(serial, app, token);
            token.ThrowIfCancellationRequested();
            succeeded = true;
            StatusText.Text = "已发送强制停止命令。";
        });
        if (succeeded) await RefreshAsync();
    }
    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is { } app) OpenLogs(app);
    }
    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (RestartRequested is null || IsRunning) return;
        RestartButton.IsEnabled = false;
        try
        {
            var result = await RestartRequested();
            if (!_closed)
            {
                if (result.StartsWith("ADB 服务已重启", StringComparison.Ordinal)) ClearSnapshot();
                DetailOutput.Text = result;
                StatusText.Text = result.Split('\n')[0].Trim();
                DetailExpander.IsExpanded = true;
            }
        }
        catch (Exception ex) { if (!_closed) { StatusText.Text = "重启失败，展开详细输出查看原因。"; DetailOutput.Text = ex.Message; DetailExpander.IsExpanded = true; } }
        finally { if (!_closed) UpdateControls(); }
    }
    private sealed record TargetChoice(string? Serial, string Label);
}
