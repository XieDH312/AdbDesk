using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using AdbDesk.Core;

namespace AdbDesk.App;

public partial class UpdateWindow : Window
{
    private readonly UpdateService _service = new();
    private AvailableUpdate? _available;
    private CancellationTokenSource? _operation;
    private bool _busy;
    public PreparedUpdate? Prepared { get; private set; }

    public UpdateWindow(AvailableUpdate? available)
    {
        InitializeComponent();
        var preferences = UpdatePreferences.Load();
        AutoCheck.IsChecked = preferences.AutoCheck;
        PreviewCheck.IsChecked = preferences.IncludePrereleases;
        _available = available;
        UpdateVersionText();
        Closed += (_, _) => _service.Dispose();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await CheckAsync();
    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync();

    private async Task CheckAsync()
    {
        if (_busy) return;
        BeginOperation("正在检查 GitHub 发行版…");
        try
        {
            _available = await _service.CheckAsync(UpdatePreferences.CurrentVersion, UpdatePreferences.CurrentVersionCode, PreviewCheck.IsChecked == true, _operation!.Token);
            StatusText.Text = _available is null ? "当前已是所选渠道的最新版本。" : "发现新版本，点击“下载并更新”开始。";
            NotesText.Text = _available?.Notes ?? "暂无更新。测试版本可在上方选择接收。";
            UpdateVersionText();
            if (!string.IsNullOrWhiteSpace(DiagnosticOutput.Text) && DiagnosticPanel.IsExpanded)
            {
                DiagnosticPanel.Header = "历史异常诊断（本次检查已成功）";
                DiagnosticPanel.IsExpanded = false;
            }
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消检查。"; }
        catch (Exception ex) { _available = null; ShowFailure("检查更新", ex); UpdateVersionText(); }
        finally { EndOperation(); }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _available is null) return;
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, UpdateInstaller.InstallationMarkerName)))
        {
            StatusText.Text = "当前为源码构建或旧版目录。请先从发行页面下载完整便携包，解压到独立目录运行一次，再使用应用内更新。";
            return;
        }
        BeginOperation("正在下载更新…");
        try
        {
            var progress = new Progress<UpdateProgress>(value =>
            {
                if (!_busy) return;
                DownloadProgress.IsIndeterminate = value.Total is not > 0;
                if (value.Total > 0) DownloadProgress.Value = value.Received * 100d / value.Total.Value;
                StatusText.Text = value.Message + (value.Total > 0 ? $"  {value.Received / 1048576d:F1} / {value.Total.Value / 1048576d:F1} MB" : "");
            });
            var update = await _service.DownloadAsync(_available, Path.Combine(UpdatePreferences.DataDirectory, "Updates"), progress, _operation!.Token);
            StatusText.Text = "正在校验并准备替换文件…";
            DownloadProgress.IsIndeterminate = true;
            using var process = Process.GetCurrentProcess();
            var processId = process.Id;
            var startTicks = process.StartTime.ToUniversalTime().Ticks;
            var token = _operation.Token;
            Prepared = await Task.Run(() => new UpdateInstaller(UpdatePreferences.CurrentVersion, UpdatePreferences.CurrentVersionCode)
                .StageAsync(update, AppContext.BaseDirectory, processId, startTicks, token), token);
            EndOperation();
            DialogResult = true;
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消更新，当前程序没有改变。"; }
        catch (Exception ex) { ShowFailure("下载或准备更新", ex); }
        finally { if (_busy) EndOperation(); }
    }

    private void BeginOperation(string message)
    {
        _busy = true; _operation = new CancellationTokenSource();
        CheckButton.IsEnabled = DownloadButton.IsEnabled = PreferencesPanel.IsEnabled = false;
        CancelButton.Content = "取消"; StatusText.Text = message; DownloadProgress.IsIndeterminate = true;
        DiagnosticPanel.Header = "历史异常诊断";
    }

    private void EndOperation()
    {
        _busy = false; _operation?.Dispose(); _operation = null;
        CheckButton.IsEnabled = PreferencesPanel.IsEnabled = true;
        DownloadButton.IsEnabled = _available is not null;
        CancelButton.Content = "关闭"; DownloadProgress.IsIndeterminate = false;
    }

    private void UpdateVersionText() => VersionText.Text = $"当前 v{UpdatePreferences.CurrentVersion}（{UpdatePreferences.CurrentVersionCode}）" +
        (_available is null ? "" : $"\n新版 v{_available.Manifest.Version}（{_available.VersionCode}）");

    private void ShowFailure(string phase, Exception exception)
    {
        StatusText.Text = phase + "失败：" + exception.Message;
        DiagnosticOutput.Text = UpdateDiagnostics.Record(phase, exception.ToString());
        DiagnosticPanel.Header = "本次更新异常";
        DiagnosticPanel.IsExpanded = true;
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(UpdateDiagnostics.ReadReport()); StatusText.Text = "已复制更新诊断，可粘贴反馈。"; }
        catch (Exception ex) { StatusText.Text = "复制失败：" + ex.Message; }
    }

    private void OpenUpdateFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(UpdateDiagnostics.DirectoryPath);
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(UpdateDiagnostics.DirectoryPath);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) { StatusText.Text = "打开目录失败：" + ex.Message; }
    }

    private async void Preferences_Click(object sender, RoutedEventArgs e)
    {
        try { new UpdatePreferences(AutoCheck.IsChecked == true, PreviewCheck.IsChecked == true).Save(); }
        catch (Exception ex) { StatusText.Text = "设置保存失败：" + ex.Message; return; }
        if (sender == PreviewCheck) await CheckAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { _operation?.Cancel(); StatusText.Text = "正在取消，请稍候…"; }
        else Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true; _operation?.Cancel(); StatusText.Text = "正在取消，请稍候再关闭。";
    }

    private void Page_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/XieDH312/AdbDesk/releases") { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = "无法打开浏览器：" + ex.Message; }
    }
}
