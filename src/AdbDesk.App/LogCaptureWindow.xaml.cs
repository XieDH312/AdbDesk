using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AdbDesk.Core;
using Microsoft.Win32;

namespace AdbDesk.App;

public partial class LogCaptureWindow : Window
{
    private readonly LogCaptureService _service;
    private readonly string _serial;
    private readonly object _sync = new();
    private readonly Queue<LogCaptureLine> _lines = new();
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _operation;
    private Task _captureCompletion = Task.CompletedTask;
    private bool _closed, _closeRequested, _closeAllowed, _deviceReady = true, _dirty;
    private int _characters;
    private long _totalLines;
    private string? _captureFolder;
    public bool IsCapturing => _operation is not null;
    public bool IsClosed => _closed;
    public string Serial => _serial;
    public Func<bool>? CanCapture { get; set; }
    public event EventHandler? CaptureStateChanged;

    public LogCaptureWindow(ToolPaths paths, string serial, string deviceName, RunningApp? app = null)
    {
        _serial = serial; _service = new LogCaptureService(paths);
        InitializeComponent();
        Title = $"日志采集 · {deviceName}";
        DeviceText.Text = $"{deviceName} · {serial}";
        FolderInput.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AdbDeskLogs");
        if (app is not null)
        {
            PackageInput.Text = app.PackageName; UserInput.Text = app.UserId.ToString(); ScopeInput.SelectedIndex = 1;
        }
        MaxHeight = SystemParameters.WorkArea.Height; MaxWidth = SystemParameters.WorkArea.Width;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => RenderPreview(); _timer.Start();
        UpdateControls();
    }

    public string PrefillAppScope(string packageName, int userId)
    {
        if (_closed || _closeRequested) return "日志窗口正在关闭，请等待关闭完成后重试。";
        if (IsCapturing) return "日志窗口正在采集，当前采集范围与筛选保持不变；请手动停止后再打开所选应用日志。";
        if (!_deviceReady) return $"设备 {_serial} 已离线，请重新连接后再打开应用日志。";
        PackageInput.Text = packageName;
        UserInput.Text = userId.ToString();
        ScopeInput.SelectedIndex = 1;
        StatusText.Text = $"已选择设备 {_serial} 的 {packageName}（Android 用户 {userId}）；点击“开始采集”后开始抓取。";
        return StatusText.Text;
    }

    public void SetDeviceReady(bool ready)
    {
        if (_closed || _deviceReady == ready) return;
        _deviceReady = ready;
        if (!ready) { _operation?.Cancel(); StatusText.Text = "设备已离线，正在结束采集；保留已保存日志和当前预览。"; }
        else StatusText.Text = "设备已重新连接；点击“开始采集”新建采集目录。";
        UpdateControls();
    }
    public void CancelCapture()
    {
        _operation?.Cancel();
        if (!_closed && IsCapturing) { StatusText.Text = "正在停止并写完管道中的日志，请稍候…"; StopButton.IsEnabled = false; }
    }
    public void StopAndClose() { CancelCapture(); if (!_closed) Close(); }
    public Task StopCaptureAsync() { CancelCapture(); return _captureCompletion; }
    public async Task StopAndCloseAsync()
    {
        StopAndClose();
        await StopCaptureAsync();
        if (!_closed) { _closeAllowed = true; Close(); }
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeAllowed || !IsCapturing) { _closed = true; _timer.Stop(); return; }
        e.Cancel = true;
        if (_closeRequested) return;
        _closeRequested = true;
        IsEnabled = false;
        await StopCaptureAsync();
        _closeAllowed = true;
        if (!_closed) Close();
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => CancelCapture();
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _closeRequested || !_deviceReady || IsCapturing) return;
        if (CanCapture?.Invoke() == false) { StatusText.Text = "请先结束工具箱查询或 ADB 服务重启，再开始采集。"; return; }
        LogCaptureOptions options;
        try
        {
            var scoped = ScopeInput.SelectedIndex == 1;
            if (scoped && !int.TryParse(UserInput.Text.Trim(), out _)) throw new ArgumentException("Android 用户编号必须是非负整数。");
            options = new(_serial, FolderInput.Text.Trim(), CrashInput.IsChecked == true,
                scoped ? PackageInput.Text.Trim() : null, scoped ? int.Parse(UserInput.Text.Trim()) : 0);
            options.Validate();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; return; }
        using var operation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _captureCompletion = completion.Task;
        _operation = operation; CaptureStateChanged?.Invoke(this, EventArgs.Empty); UpdateControls();
        StatusText.Text = "正在检查日志能力并创建采集目录…";
        string? newFolder = null;
        try
        {
            var result = await _service.CaptureAsync(options, ReceiveLine, session =>
            {
                newFolder = session.Folder;
                lock (_sync) { _lines.Clear(); _characters = 0; _totalLines = 0; _dirty = true; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _captureFolder = session.Folder;
                    if (_closed) return;
                    FolderButton.IsEnabled = true;
                    StatusText.Text = $"正在采集：{session.Scope}\n保存目录：{session.Folder}";
                }));
            }, operation.Token);
            if (!_closed) StatusText.Text = $"{(_deviceReady ? (result.Stopped ? "已停止" : "采集已结束") : "设备已离线，采集已停止")} · 已保存 {result.FileCount} 卷 / {result.BytesWritten:N0} 字节。\n{result.Session.Folder}";
        }
        catch (OperationCanceledException) { if (!_closed) StatusText.Text = "已取消启动采集，保留已有日志。"; }
        catch (Exception ex)
        {
            if (!_closed) StatusText.Text = "采集失败，已停止：" + ex.Message +
                (newFolder is not null ? "\n已成功写入的日志保留于：" + newFolder : "");
        }
        finally
        {
            // Remain active until the service has stopped its client, drained
            // both pipes and closed every file, even if this window was closed.
            _operation = null;
            try
            {
                CaptureStateChanged?.Invoke(this, EventArgs.Empty);
                if (!_closed) { RenderPreview(); UpdateControls(); }
            }
            finally { completion.TrySetResult(); }
        }
    }
    private void ReceiveLine(LogCaptureLine line)
    {
        lock (_sync)
        {
            _totalLines++; _lines.Enqueue(line); _characters += line.Text.Length; _dirty = true;
            while (_lines.Count > 2500 || _characters > 512 * 1024) _characters -= _lines.Dequeue().Text.Length;
        }
    }
    private void RenderPreview()
    {
        if (_closed) return;
        LogCaptureLine[] lines; long total;
        lock (_sync) { if (!_dirty) return; _dirty = false; lines = _lines.ToArray(); total = _totalLines; }
        var level = "VDIWEF"[Math.Clamp(LevelInput.SelectedIndex, 0, 5)];
        var filtered = lines.Where(line => line.Matches(level, TagInput.Text.Trim(), KeywordInput.Text.Trim())).ToArray();
        LogText.Text = string.Join(Environment.NewLine, filtered.Select(line => line.Text));
        if (FollowInput.IsChecked == true) LogText.ScrollToEnd();
        PreviewInfo.Text = $"累计收到 {total:N0} 行 · 预览缓存 {lines.Length:N0} 行 · 匹配 {filtered.Length:N0} 行；早期日志请查看采集目录。";
    }
    private void Filter_Changed(object sender, RoutedEventArgs e) { lock (_sync) _dirty = true; }
    private void Scope_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PackageInput is null || UserInput is null) return;
        PackageInput.IsEnabled = UserInput.IsEnabled = ScopeInput.SelectedIndex == 1;
    }
    private void UpdateControls()
    {
        StartButton.IsEnabled = !_closed && !_closeRequested && _deviceReady && !IsCapturing;
        StopButton.IsEnabled = !_closed && IsCapturing && _operation?.IsCancellationRequested != true;
        CaptureSettings.IsEnabled = !IsCapturing;
    }
    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择日志保存父目录（每次采集创建独立子目录）" };
        if (Directory.Exists(FolderInput.Text)) dialog.InitialDirectory = FolderInput.Text;
        if (dialog.ShowDialog(this) == true) FolderInput.Text = dialog.FolderName;
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { if (_captureFolder is not null) Process.Start(new ProcessStartInfo(_captureFolder) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = "打开目录失败：" + ex.Message; }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        RenderPreview();
        var dialog = new SaveFileDialog { Title = "导出当前预览（完整日志在采集目录）", Filter = "文本文件 (*.txt)|*.txt", FileName = $"log-preview-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
        if (dialog.ShowDialog(this) != true) return;
        var text = LogText.Text;
        try { await File.WriteAllTextAsync(dialog.FileName, text, new UTF8Encoding(false)); if (!_closed) StatusText.Text = "当前预览已导出：" + dialog.FileName; }
        catch (Exception ex) { if (!_closed) StatusText.Text = "导出失败：" + ex.Message; }
    }
}
