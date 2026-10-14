using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AdbDesk.Core;
using Microsoft.Win32;

namespace AdbDesk.App;

public partial class AppManagerWindow : Window
{
    private readonly InstalledAppsService _service;
    private readonly Dictionary<string, InstalledAppDetails> _details = new(StringComparer.Ordinal);
    private InstalledAppsSnapshot? _snapshot;
    private CancellationTokenSource? _operation;
    private Task? _activeTask;
    private bool _ready, _deviceReady = true;
    public string Serial { get; }
    public bool IsBusy => _operation is not null;
    public bool IsClosed { get; private set; }
    public Func<bool>? CanOperate { get; set; }
    public event EventHandler? OperationStateChanged;

    public AppManagerWindow(ToolPaths paths, string serial, string label)
    {
        Serial = serial;
        _service = new InstalledAppsService(paths);
        InitializeComponent();
        DeviceText.Text = $"{label} · {serial}";
        _ready = true;
        UpdateControls();
    }

    public void SetDeviceReady(bool ready)
    {
        var wasReady = _deviceReady;
        _deviceReady = ready;
        if (!ready)
        {
            CancelOperation();
            if (!IsClosed) StatusText.Text = "设备已离线；当前列表为此前快照，重新连接后请刷新。";
        }
        else if (!wasReady && !IsClosed) StatusText.Text = "设备已重新连接，请刷新应用列表。";
        UpdateControls();
    }
    public void CancelOperation() => _operation?.Cancel();
    public void StopAndClose() { CancelOperation(); if (!IsClosed) Close(); }
    public async Task StopAndCloseAsync()
    {
        var active = _activeTask;
        StopAndClose();
        if (active is not null) await active;
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        IsClosed = true;
        CancelOperation();
        OperationStateChanged?.Invoke(this, EventArgs.Empty);
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelOperation();
    private bool Available => _ready && !IsClosed && _deviceReady && !IsBusy && (CanOperate?.Invoke() ?? true);

    private void UpdateControls()
    {
        if (!_ready || IsClosed) return;
        RefreshButton.IsEnabled = Available;
        CancelButton.IsEnabled = IsBusy;
        SearchInput.IsEnabled = CategoryCombo.IsEnabled = AppList.IsEnabled = !IsBusy;
        ActionsPanel.IsEnabled = Available && _snapshot is not null && AppList.SelectedItem is InstalledApp;
    }
    private Task OperateAsync(string message, Func<CancellationToken, Task> action)
    {
        if (!Available) return Task.CompletedTask;
        return _activeTask = RunOperationAsync(message, action);
    }
    private async Task RunOperationAsync(string message, Func<CancellationToken, Task> action)
    {
        using var operation = new CancellationTokenSource();
        _operation = operation;
        StatusText.Text = message;
        UpdateControls();
        OperationStateChanged?.Invoke(this, EventArgs.Empty);
        try { await action(operation.Token); }
        catch (OperationCanceledException ex)
        {
            if (!IsClosed && _deviceReady) StatusText.Text = "操作已停止；已发送到手机的操作可能已经执行，请刷新核对。" + (ex.Message.Contains("保留在") ? ex.Message : "");
        }
        catch (Exception ex)
        {
            if (!IsClosed) { StatusText.Text = "操作失败，请查看右侧详情。"; DetailOutput.Text = ex.Message; }
        }
        finally
        {
            _operation = null;
            UpdateControls();
            OperationStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    private async Task RefreshAsync()
    {
        await OperateAsync("正在读取当前用户的已安装应用…", async token =>
        {
            var snapshot = await _service.LoadAsync(Serial, token);
            token.ThrowIfCancellationRequested();
            if (IsClosed) return;
            _snapshot = snapshot;
            _details.Clear();
            UserText.Text = $"Android 用户 {snapshot.UserId} · 此窗口固定设备 {Serial}";
            AppList.SelectedItem = null;
            PackageText.Text = "选择应用查看详情";
            VersionText.Text = "";
            DetailOutput.Text = "应用类型、状态和数字版本号来自设备。版本名称将在选择应用后读取。";
            ApplyFilter();
            StatusText.Text = $"最后更新 {snapshot.CapturedAt:HH:mm:ss} · 共 {snapshot.Apps.Count} 个已安装应用";
        });
    }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) ApplyFilter(); }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (_ready) ApplyFilter(); }
    private void ApplyFilter()
    {
        if (_snapshot is null) return;
        var query = SearchInput.Text.Trim();
        var rows = _snapshot.Apps.Where(a => (CategoryCombo.SelectedIndex switch { 1 => !a.IsSystem, 2 => a.IsSystem, _ => true }) &&
            a.PackageName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        AppList.ItemsSource = rows;
        CountText.Text = $"显示 {rows.Length} / {_snapshot.Apps.Count} 个应用 · Android 用户 {_snapshot.UserId}";
        if (rows.Length == 0) CountText.Text += " · 没有匹配的应用";
        UpdateControls();
    }
    private async void App_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateControls();
        if (AppList.SelectedItem is not InstalledApp app)
        {
            PackageText.Text = "选择应用查看详情";
            VersionText.Text = "";
            DetailOutput.Clear();
            return;
        }
        PackageText.Text = app.PackageName;
        VersionText.Text = $"{app.Kind} · {app.EnabledState} · Android 用户 {app.UserId}\nversionCode：{app.VersionCode}";
        DetailOutput.Text = "正在读取版本名称和 APK 路径…";
        await LoadDetailsAsync(app);
    }
    private Task LoadDetailsAsync(InstalledApp app) => OperateAsync("正在读取应用详情…", async token =>
    {
        if (!_details.TryGetValue(app.PackageName, out var details))
        {
            details = await _service.DetailsAsync(Serial, app, token);
            token.ThrowIfCancellationRequested();
            _details[app.PackageName] = details;
        }
        if (IsClosed || !Equals(AppList.SelectedItem, app)) return;
        VersionText.Text = $"{app.Kind} · {app.EnabledState} · Android 用户 {app.UserId}\nversionName：{details.VersionName ?? "无法读取"}\nversionCode：{details.VersionCode?.ToString() ?? app.VersionCode.ToString()}";
        var raw = details.RawDetails.Length > 250000 ? details.RawDetails[..250000] + "\r\n[显示已截断]" : details.RawDetails;
        DetailOutput.Text = $"APK 路径（用户 {app.UserId}）：\r\n{string.Join("\r\n", details.ApkPaths)}\r\n\r\n以下为系统包元数据，可能包含其他 Android 用户的状态：\r\n{raw}";
        StatusText.Text = "已读取详情，可选中文本复制。";
    });
    private InstalledApp? SelectedApp => Available ? AppList.SelectedItem as InstalledApp : null;
    private async void Details_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is not { } app) return;
        _details.Remove(app.PackageName);
        await LoadDetailsAsync(app);
    }
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is not { } app) return;
        try { Clipboard.SetText(app.PackageName); StatusText.Text = "已复制包名。"; }
        catch (Exception ex) { StatusText.Text = "复制失败：" + ex.Message; }
    }
    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is not { } app) return;
        await OperateAsync("正在启动应用…", async token =>
        {
            var result = await _service.LaunchAsync(Serial, app, token);
            token.ThrowIfCancellationRequested();
            if (!IsClosed) { DetailOutput.Text = result; StatusText.Text = "启动命令已完成，请查看手机。"; }
        });
    }
    private bool Confirm(InstalledApp app, string title, string warning)
    {
        var snapshot = _snapshot;
        if (MessageBox.Show(this, $"设备：{Serial}\nAndroid 用户：{app.UserId}\n包名：{app.PackageName}\n\n{warning}", title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
        // Modal dialogs pump messages: the owner may reconfigure, disconnect or close meanwhile.
        return Available && ReferenceEquals(snapshot, _snapshot) && Equals(AppList.SelectedItem, app);
    }
    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is not { } app || !Confirm(app, "强制停止应用", "将中断此用户下该应用的任务。确定继续吗？")) return;
        await OperateAsync("正在强制停止应用…", async token =>
        {
            await _service.StopAsync(Serial, app, token);
            token.ThrowIfCancellationRequested();
            if (!IsClosed) StatusText.Text = "已完成强制停止命令。";
        });
    }
    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is not { } app || !Confirm(app, "清除应用数据", "将永久删除此 Android 用户下该应用的数据与登录状态，无法撤销。确定继续吗？")) return;
        await OperateAsync("正在清除应用数据…", async token =>
        {
            await _service.ClearDataAsync(Serial, app, token);
            token.ThrowIfCancellationRequested();
            _details.Remove(app.PackageName);
            if (!IsClosed) { StatusText.Text = "系统已确认：此用户的应用数据已清除。"; DetailOutput.Text = StatusText.Text; }
        });
    }
    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is not { } app || !Confirm(app, "为此 Android 用户卸载", "将为上述 Android 用户卸载该应用，并删除其应用数据。系统应用可能被设备拒绝。确定继续吗？")) return;
        var succeeded = false;
        await OperateAsync("正在为此用户卸载应用…", async token =>
        {
            await _service.UninstallAsync(Serial, app, token);
            token.ThrowIfCancellationRequested();
            succeeded = true;
            if (!IsClosed) StatusText.Text = "系统已确认：此用户下的应用已卸载。";
        });
        if (succeeded && !IsClosed) await RefreshAsync();
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is not { } app) return;
        var snapshot = _snapshot;
        var dialog = new OpenFolderDialog { Title = "选择 APK 导出目录（将创建独立子目录）" };
        if (dialog.ShowDialog(this) != true || !Available || !ReferenceEquals(snapshot, _snapshot) || !Equals(AppList.SelectedItem, app)) return;
        await OperateAsync("正在导出 base 与全部 split APK…", async token =>
        {
            var directory = await _service.ExportAsync(Serial, app, dialog.FolderName, token);
            token.ThrowIfCancellationRequested();
            if (!IsClosed)
            {
                DetailOutput.Text = $"导出完成：\r\n{directory}\r\n\r\n包含 base 与所有 split APK，不包含应用数据。拆分包需一同安装。";
                StatusText.Text = "APK 已导出，保存目录见右侧详情。";
            }
        });
    }
}
