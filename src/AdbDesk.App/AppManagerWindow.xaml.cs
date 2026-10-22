using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AdbDesk.Core;
using Microsoft.Win32;

namespace AdbDesk.App;

public partial class AppManagerWindow : Window
{
    private readonly InstalledAppsService _service;
    private readonly InstalledAppNamesService _names;
    // The window fixes the device; user/version are part of each cached detail key.
    private readonly Dictionary<(int User, string Package, long Version), InstalledAppDetails> _details = new();
    private InstalledAppsSnapshot? _snapshot;
    private CancellationTokenSource? _operation;
    private Task? _activeTask;
    private bool _ready, _deviceReady = true, _snapshotValid;
    private bool _loadingNames;
    public string Serial { get; }
    public bool IsBusy => _operation is not null;
    public bool IsClosed { get; private set; }
    public Func<bool>? CanOperate { get; set; }
    public Func<InstalledApp, string>? OpenAppLogs { get; set; }
    public event EventHandler? OperationStateChanged;

    public AppManagerWindow(ToolPaths paths, string serial, string label)
    {
        Serial = serial;
        _service = new InstalledAppsService(paths);
        _names = new InstalledAppNamesService(paths);
        InitializeComponent();
        DeviceText.Text = $"{label} · {serial}";
        _ready = true;
        MaxHeight = SystemParameters.WorkArea.Height;
        MaxWidth = SystemParameters.WorkArea.Width;
        UpdateControls();
    }

    public void SetDeviceReady(bool ready)
    {
        var wasReady = _deviceReady;
        _deviceReady = ready;
        if (!ready)
        {
            _snapshotValid = false;
            _details.Clear();
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
        // Let users search the package list while the optional names are being read.
        // Device actions remain serialized with the read and the toolbox lifecycle.
        SearchInput.IsEnabled = CategoryCombo.IsEnabled = StateCombo.IsEnabled = AppList.IsEnabled = !IsBusy || _loadingNames;
        ActionsPanel.IsEnabled = !IsClosed && _deviceReady && !IsBusy && _snapshotValid && AppList.SelectedItem is InstalledApp;
        foreach (var button in ActionsPanel.Children.OfType<Button>()) button.IsEnabled = Available;
        LogButton.IsEnabled = OpenAppLogs is not null;
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
            if (!IsClosed) { StatusText.Text = "操作失败，请查看右侧详情。"; DetailsTabs.SelectedIndex = 0; DetailOutput.Text = ex.Message; }
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
        var refreshed = false;
        await OperateAsync("正在读取当前用户的已安装应用…", async token =>
        {
            _snapshotValid = false;
            _details.Clear();
            NameStatusText.Text = "正在读取应用列表…";
            var snapshot = await _service.LoadAsync(Serial, token);
            token.ThrowIfCancellationRequested();
            if (IsClosed) return;
            _snapshot = snapshot;
            _snapshotValid = true;
            UserText.Text = $"Android 用户 {snapshot.UserId} · 此窗口固定设备 {Serial}";
            AppList.SelectedItem = null;
            PackageText.Text = "选择应用查看详情";
            VersionText.Text = "";
            ClearDetailViews();
            DetailOutput.Text = "应用类型、状态和数字版本号来自设备。版本名称将在选择应用后读取。";
            ApplyFilter();
            _loadingNames = true;
            UpdateControls();
            StatusText.Text = $"已读取 {snapshot.Apps.Count} 个应用，正在补充名称…";
            NameStatusText.Text = "正在补充名称，可先搜索包名；停止操作会保留已读取的列表。";
            try
            {
                var names = await _names.ReadAsync(Serial, snapshot.UserId, token);
                token.ThrowIfCancellationRequested();
                if (IsClosed || !_deviceReady || !ReferenceEquals(_snapshot, snapshot)) return;
                // Names belong only to this successful device/user snapshot. A refresh
                // always reads them again, so app updates or language changes cannot
                // keep a label from a previous version or another Android user.
                _snapshot = snapshot with
                {
                    Apps = snapshot.Apps.Select(app => app with
                    {
                        Name = app.IsEnabled && names.TryGetValue(app.PackageName, out var name) ? name : null
                    }).ToArray()
                };
                ApplyFilter();
                var named = _snapshot.Apps.Count(app => app.Name is not null);
                NameStatusText.Text = $"已读取 {named} 个应用名称；无启动入口或已停用的应用可能无法读取名称。";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (!IsClosed && _deviceReady)
                {
                    NameStatusText.Text = "名称读取已停止；已保留包名列表，点击“刷新应用”可重试。";
                    StatusText.Text = "已停止读取名称，应用列表仍可使用。";
                    if (AppList.SelectedItem is InstalledApp)
                        DetailOutput.Text = "名称读取已停止。点击“刷新详情”可读取所选应用的信息，或选择其他应用。";
                }
                return;
            }
            catch (Exception ex)
            {
                if (IsClosed || !_deviceReady) return;
                // Name enrichment is optional: it must never discard pm's complete list.
                var message = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
                NameStatusText.Text = "名称未读取，仍可按包名操作：" + (message.Length > 400 ? message[..400] + "…" : message);
            }
            finally { _loadingNames = false; UpdateControls(); }
            if (IsClosed || !_deviceReady) return;
            StatusText.Text = $"最后更新 {snapshot.CapturedAt:HH:mm:ss} · 共 {snapshot.Apps.Count} 个已安装应用";
            refreshed = true;
        });
        // A user may have selected a row while names were loading. Read that row's
        // details only after releasing the list operation, using the enriched record.
        if (refreshed && SelectedApp is { } selected) await LoadDetailsAsync(selected);
    }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) ApplyFilter(); }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (_ready) ApplyFilter(); }
    private void ApplyFilter()
    {
        if (_snapshot is null) return;
        var query = SearchInput.Text.Trim();
        var selected = AppList.SelectedItem;
        var rows = _snapshot.Apps.Where(a => (CategoryCombo.SelectedIndex switch { 1 => !a.IsSystem, 2 => a.IsSystem, _ => true }) &&
            (StateCombo.SelectedIndex switch { 1 => a.IsEnabled, 2 => !a.IsEnabled, _ => true }) &&
            (a.PackageName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             (a.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))).ToArray();
        AppList.ItemsSource = rows;
        if (selected is InstalledApp app)
            AppList.SelectedItem = rows.FirstOrDefault(row => CacheKey(row) == CacheKey(app));
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
            ClearDetailViews();
            return;
        }
        PackageText.Text = app.Name is { } name ? $"{name}\n{app.PackageName}" : app.PackageName;
        VersionText.Text = $"{app.Kind} · {app.EnabledState} · Android 用户 {app.UserId}\nversionCode：{app.VersionCode}";
        ClearDetailViews();
        if (!_snapshotValid || !_deviceReady)
        {
            DetailOutput.Text = "当前列表为此前快照，请在设备连接后刷新应用列表。";
            return;
        }
        if (!Available)
        {
            DetailOutput.Text = _loadingNames ? "正在补充应用名称，完成后会读取所选应用详情。" : "请先结束当前工具箱操作，再点击“刷新详情”。";
            return;
        }
        DetailOutput.Text = "正在读取版本、安装信息、权限和 APK 路径…";
        await LoadDetailsAsync(app);
    }
    private Task LoadDetailsAsync(InstalledApp app) => OperateAsync("正在读取应用详情…", async token =>
    {
        if (!_details.TryGetValue(CacheKey(app), out var details))
        {
            details = await _service.DetailsAsync(Serial, app, token);
            token.ThrowIfCancellationRequested();
            _details[CacheKey(app)] = details;
        }
        if (IsClosed || !Equals(AppList.SelectedItem, app)) return;
        VersionText.Text = $"{app.Kind} · {app.EnabledState} · Android 用户 {app.UserId}\nversionName：{details.VersionName ?? "无法读取"}\nversionCode：{details.VersionCode?.ToString() ?? app.VersionCode.ToString()}";
        var raw = details.RawDetails.Length > 250000 ? details.RawDetails[..250000] + "\r\n[显示已截断]" : details.RawDetails;
        DetailOutput.Text = details.ToSummary(app);
        RawOutput.Text = $"以下为系统包元数据，可能包含其他 Android 用户的状态；权限页仅显示用户 {app.UserId} 的运行时授权：\r\n\r\n{raw}";
        PermissionsList.ItemsSource = details.Permissions.Select(p => new
        {
            p.Name,
            Requested = details.RequestedPermissionsAvailable ? (p.IsRequested ? "是" : "否") : "未知",
            InstallState = PermissionState(p.InstallGranted),
            RuntimeState = PermissionState(p.RuntimeGranted)
        }).ToArray();
        PermissionHint.Text = $"运行时授权：Android 用户 {app.UserId}。申请权限不等于已获授权；“未知”表示系统未报告该状态。" +
            (details.Permissions.Count == 0 ? "\n没有可显示的权限记录。" : "");
        StatusText.Text = "已读取详情；权限变化后可点击“刷新详情”。";
    });
    private static (int, string, long) CacheKey(InstalledApp app) => (app.UserId, app.PackageName, app.VersionCode);
    private static string PermissionState(bool? granted) => granted switch { true => "已授予", false => "未授予", _ => "未知" };
    private void ClearDetailViews()
    {
        DetailOutput.Clear();
        RawOutput.Clear();
        PermissionsList.ItemsSource = null;
        PermissionHint.Text = "选择应用读取权限。";
    }
    private InstalledApp? SelectedApp => Available && _snapshotValid ? AppList.SelectedItem as InstalledApp : null;
    private async void Details_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is not { } app) return;
        _details.Remove(CacheKey(app));
        ClearDetailViews();
        await LoadDetailsAsync(app);
    }
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is not { } app) return;
        try { Clipboard.SetText(app.PackageName); StatusText.Text = "已复制包名。"; }
        catch (Exception ex) { StatusText.Text = "复制失败：" + ex.Message; }
    }
    private async void CopyInfo_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is not { } app) return;
        if (!_details.ContainsKey(CacheKey(app))) await LoadDetailsAsync(app);
        if (IsClosed || SelectedApp != app || !_details.TryGetValue(CacheKey(app), out var details)) return;
        try
        {
            Clipboard.SetText($"设备：{Serial}\r\n{details.ToSummary(app)}");
            StatusText.Text = "已复制应用信息（含当前用户的权限和 APK 路径）。";
        }
        catch (Exception ex) { StatusText.Text = "复制失败：" + ex.Message; }
    }
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is not { } app) return;
        await OperateAsync("正在打开手机应用设置…", async token =>
        {
            await _service.OpenSettingsAsync(Serial, app, token);
            token.ThrowIfCancellationRequested();
            if (!IsClosed) StatusText.Text = "应用设置已打开，请查看手机或投屏窗口。";
        });
    }
    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        // Opening/prefilling a window does not run ADB. The callback explains
        // active/other-device captures without altering their scope.
        if (!_ready || IsClosed || !_deviceReady || IsBusy || !_snapshotValid ||
            AppList.SelectedItem is not InstalledApp app || OpenAppLogs is null) return;
        try { StatusText.Text = OpenAppLogs(app); }
        catch (Exception ex) { StatusText.Text = "打开日志采集失败：" + ex.Message; }
    }
    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is not { } app) return;
        await OperateAsync("正在启动应用…", async token =>
        {
            var result = await _service.LaunchAsync(Serial, app, token);
            token.ThrowIfCancellationRequested();
            if (!IsClosed) { DetailsTabs.SelectedIndex = 0; DetailOutput.Text = result; StatusText.Text = "启动命令已完成，请查看手机。"; }
        });
    }
    private bool Confirm(InstalledApp app, string title, string warning)
    {
        var snapshot = _snapshot;
        if (MessageBox.Show(this, $"设备：{Serial}\nAndroid 用户：{app.UserId}\n包名：{app.PackageName}\n\n{warning}", title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
        // Modal dialogs pump messages: the owner may reconfigure, disconnect or close meanwhile.
        return Available && _snapshotValid && ReferenceEquals(snapshot, _snapshot) && Equals(AppList.SelectedItem, app);
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
            _details.Remove(CacheKey(app));
            if (!IsClosed) { ClearDetailViews(); DetailsTabs.SelectedIndex = 0; StatusText.Text = "系统已确认：此用户的应用数据已清除。"; DetailOutput.Text = StatusText.Text; }
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
        if (dialog.ShowDialog(this) != true || !Available || !_snapshotValid || !ReferenceEquals(snapshot, _snapshot) || !Equals(AppList.SelectedItem, app)) return;
        await OperateAsync("正在导出 base 与全部 split APK…", async token =>
        {
            var directory = await _service.ExportAsync(Serial, app, dialog.FolderName, token);
            token.ThrowIfCancellationRequested();
            if (!IsClosed)
            {
                DetailsTabs.SelectedIndex = 0;
                DetailOutput.Text = $"导出完成：\r\n{directory}\r\n\r\n包含 base 与所有 split APK，不包含应用数据。拆分包需一同安装。";
                StatusText.Text = "APK 已导出，保存目录见右侧详情。";
            }
        });
    }
}
