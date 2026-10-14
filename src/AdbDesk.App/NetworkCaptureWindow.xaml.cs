using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using AdbDesk.Core;
using Microsoft.Win32;

namespace AdbDesk.App;

public partial class NetworkCaptureWindow : Window
{
    private readonly ToolPaths _paths;
    private readonly string _stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdbDesk");
    private readonly ObservableCollection<NetworkCaptureEntry> _entries = [];
    private readonly ConcurrentQueue<NetworkCaptureEntry> _pending = new();
    private readonly DispatcherTimer _timer;
    private NetworkCaptureSession? _session;
    private CancellationTokenSource? _operation;
    private Task? _startTask, _stopTask, _closeTask;
    private bool _ready, _deviceReady = true, _closing, _allowClose;
    private int _dropped;
    private string _lastDiagnosticError = "";
    public string Serial { get; }
    public bool IsClosed { get; private set; }
    public bool IsCapturing => _operation is not null || _session?.IsRunning == true || _stopTask is { IsCompleted: false };
    public Func<bool>? CanCapture { get; set; }
    public event EventHandler? CaptureStateChanged;

    public NetworkCaptureWindow(ToolPaths paths, string serial, string name)
    {
        _paths = paths; Serial = serial;
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height;
        MaxWidth = SystemParameters.WorkArea.Width;
        DeviceText.Text = $"{name} · {serial}";
        EngineInput.Text = Path.Combine(AppContext.BaseDirectory, "tools", "mitmproxy", "mitmdump.exe");
        RequestList.ItemsSource = _entries;
        CollectionViewSource.GetDefaultView(_entries).Filter = MatchesFilter;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Drain(), Dispatcher);
        _ready = true;
        Closed += (_, _) => { IsClosed = true; _timer.Stop(); CaptureStateChanged?.Invoke(this, EventArgs.Empty); };
        UpdateControls();
        Loaded += async (_, _) => await RefreshHistoryAsync();
    }

    public void SetDeviceReady(bool ready)
    {
        _deviceReady = ready;
        if (!ready && IsCapturing) { StatusText.Text = "设备已断开，正在停止；无法恢复的代理会保留恢复记录。"; _ = StopCaptureAsync(); }
        UpdateControls();
    }

    private NetworkCaptureSession NewSession() => new(_paths, EngineInput.Text.Trim(),
        Path.Combine(AppContext.BaseDirectory, "Assets", "network_capture.py"), _stateRoot);
    private bool HasRecovery => (_session ?? NewSession()).HasPendingRecovery(Serial);
    private void UpdateControls()
    {
        if (!_ready || IsClosed) return;
        var busy = IsCapturing;
        SettingsPanel.IsEnabled = ProxyCheck.IsEnabled = !busy && !_closing;
        StartButton.IsEnabled = !busy && !_closing && !_browseBusy && !_fileBusy && _deviceReady && !HasRecovery;
        StopButton.IsEnabled = busy && !_closing && _stopTask is not { IsCompleted: false };
        RecoveryButton.IsEnabled = !busy && !_closing && _deviceReady && HasRecovery;
        CertificateButton.IsEnabled = File.Exists((_session ?? NewSession()).CertificatePath);
        UpdateHistoryControls();
        CaptureStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (IsCapturing || _closing || _browseBusy || _fileBusy || !_deviceReady || CanCapture?.Invoke() == false) return;
        if (!int.TryParse(PortInput.Text, out var port) || port is < 1024 or > 65535)
        { StatusText.Text = "端口请输入 1024–65535。"; return; }
        if (!File.Exists(EngineInput.Text.Trim())) { StatusText.Text = "未找到 mitmdump.exe，请选择官方便携引擎。"; return; }
        using var operation = new CancellationTokenSource();
        _operation = operation;
        _session = NewSession();
        _historyDirectory = null; _liveDirectory = null; _pending.Clear(); _entries.Clear(); _dropped = 0;
        _detailGeneration++; _diagnosticMessages.Clear(); _lastDiagnosticError = ""; DiagnosticOutput.Clear();
        ModeText.Text = "实时 · 正在开始新会话";
        UpdateControls();
        StatusText.Text = "正在启动代理并建立设备通道…";
        try
        {
            _startTask = _session.StartAsync(Serial, port, ProxyCheck.IsChecked == true, entry =>
            {
                _pending.Enqueue(entry);
                while (_pending.Count > 200 && _pending.TryDequeue(out _)) Interlocked.Increment(ref _dropped);
            }, message => Dispatcher.BeginInvoke(() =>
            {
                if (!IsClosed) { StatusText.Text = message; AppendDiagnostic(message); UpdateControls(); }
            }), operation.Token);
            await _startTask;
            _liveDirectory = _session.CaptureDirectory;
            SettingsExpander.IsExpanded = false;
            ModeText.Text = "实时 · 最近 200 条；停止后可查看本次全部记录";
        }
        catch (OperationCanceledException) { StatusText.Text = "启动已取消，正在清理。"; }
        catch (Exception ex) { StatusText.Text = "启动失败：" + ex.Message; }
        finally { _liveDirectory ??= _session?.CaptureDirectory; _startTask = null; _operation = null; UpdateControls(); }
    }

    public Task StopCaptureAsync()
    {
        if (_stopTask is { IsCompleted: false }) return _stopTask;
        _operation?.Cancel();
        _stopTask = StopCoreAsync();
        UpdateControls();
        return _stopTask;
    }
    private async Task StopCoreAsync()
    {
        try
        {
            if (_startTask is { } starting) { try { await starting; } catch { /* Start reports its own failure. */ } }
            if (_session is { } session) await session.StopAsync();
            Drain();
            if (!IsClosed) StatusText.Text = HasRecovery
                ? "已停止，手机代理尚未完整恢复。请保持此设备序列号重连，然后点击“恢复遗留代理”。"
                : "已停止抓包，设备代理和本次通道已清理。";
        }
        catch (Exception ex) { if (!IsClosed) StatusText.Text = "停止或恢复失败：" + ex.Message; }
        finally { _ = Dispatcher.BeginInvoke(UpdateControls); }
    }
    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopCaptureAsync();

    private async void Recover_Click(object sender, RoutedEventArgs e)
    {
        if (IsCapturing || !_deviceReady || CanCapture?.Invoke() == false) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        _session ??= NewSession();
        UpdateControls();
        try
        {
            _startTask = _session.RecoverAsync(Serial, operation.Token);
            await _startTask;
            StatusText.Text = "遗留代理与通道已恢复。";
        }
        catch (Exception ex) { StatusText.Text = "恢复未完成：" + ex.Message; }
        finally { _startTask = null; _operation = null; UpdateControls(); }
    }

    public Task StopAndCloseAsync()
    {
        if (IsClosed) return Task.CompletedTask;
        return _closeTask is { IsCompleted: false } ? _closeTask : _closeTask = CloseCoreAsync();
    }
    private async Task CloseCoreAsync()
    {
        _closing = true;
        _detailGeneration++; _historyGeneration++;
        _fileCancellation?.Cancel();
        UpdateControls();
        await StopCaptureAsync();
        if (_fileTask is { } fileTask) { try { await fileTask; } catch { /* The owning handler reports the failure. */ } }
        if (IsClosed) return;
        if (_session?.IsRunning == true)
        {
            _closing = false;
            StatusText.Text = "抓包引擎尚未退出，窗口保持打开。请重试“停止并恢复”。";
            UpdateControls();
            return;
        }
        if (HasRecovery)
            MessageBox.Show(this, $"设备 {Serial} 的代理或反向端口尚未完全恢复。\n\n请以相同序列号重新连接设备，在工具箱 → 网络抓包中点击“恢复遗留代理”。恢复记录保存在：\n{_stateRoot}\n\n如果无线端口已改变，请按 Debug 配置说明中的恢复步骤处理。", "抓包已停止，设备设置待恢复", MessageBoxButton.OK, MessageBoxImage.Warning);
        _allowClose = true;
        Close();
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        await StopAndCloseAsync();
    }

    private void Drain()
    {
        if (IsClosed) return;
        if (_session is { } session)
        {
            _liveDirectory ??= session.CaptureDirectory;
            var d = session.Diagnostics;
            DiagnosticText.Text = $"引擎：{d.Engine}　ADB 通道：{d.Tunnel}　手机代理：{d.Proxy}\n流量：{d.Traffic}\n{d.LastError}";
            if (d.LastError.Length > 0 && d.LastError != _lastDiagnosticError)
            {
                _lastDiagnosticError = d.LastError;
                StatusText.Text = "抓包诊断：" + d.LastError;
                StatusText.ToolTip = StatusText.Text;
                AppendDiagnostic(d.LastError);
            }
        }
        if (_historyDirectory is not null || _browseBusy) return;
        for (var i = 0; i < 40 && _pending.TryDequeue(out var entry); i++)
        {
            var previous = _entries.FirstOrDefault(row => row.Id == entry.Id);
            if (previous is not null)
            {
                var selected = ReferenceEquals(RequestList.SelectedItem, previous);
                _entries[_entries.IndexOf(previous)] = entry;
                if (selected) RequestList.SelectedItem = entry;
            }
            else _entries.Add(entry);
            while (_entries.Count > 200) { _entries.RemoveAt(0); Interlocked.Increment(ref _dropped); }
        }
        CountText.Text = $"实时保留 {_entries.Count}/200 条 · 未展示 {_dropped} 次更新 · 历史独立保存，停止后查看全部 · 正文上限 4 MiB / 会话上限 256 MiB";
    }

    private void AppendDiagnostic(string message)
    {
        _diagnosticMessages.Enqueue($"{DateTime.Now:HH:mm:ss} {message}");
        while (_diagnosticMessages.Count > 60) _diagnosticMessages.Dequeue();
        DiagnosticOutput.Text = string.Join(Environment.NewLine, _diagnosticMessages);
        DiagnosticOutput.ScrollToEnd();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "mitmdump|mitmdump.exe", Title = "选择官方 mitmdump.exe" };
        if (dialog.ShowDialog(this) == true) EngineInput.Text = dialog.FileName;
    }
    private void Certificate_Click(object sender, RoutedEventArgs e)
    {
        var source = (_session ?? NewSession()).CertificatePath;
        var dialog = new SaveFileDialog { FileName = "AdbDesk-debug-ca.cer", Filter = "CA 证书|*.cer" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.Copy(source, dialog.FileName, true); StatusText.Text = "已导出公开 CA（不含私钥）。请按配置说明在调试手机安装，并配置 Debug App。"; }
        catch (Exception ex) { StatusText.Text = "导出失败：" + ex.Message; }
    }
    private void Help_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "docs", "network-capture.md")) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { StatusText.Text = "请打开程序目录 docs/network-capture.md。" + ex.Message; }
    }
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _pending.Clear(); _entries.Clear(); _dropped = 0; _detailGeneration++;
        DetailsOutput.Clear(); RequestHeadersOutput.Clear(); ResponseHeadersOutput.Clear();
        _requestText = _responseText = _requestFormatted = _responseFormatted = ""; _requestTree = _responseTree = []; RenderBodyViews();
        StatusText.Text = "已清空当前显示，磁盘历史未删除。";
    }
    private void CopyDetails_Click(object sender, RoutedEventArgs e) => Copy(DetailsOutput.Text + "\n\n请求头\n" + RequestHeadersOutput.Text + "\n请求正文\n" + _requestText + "\n\n响应头\n" + ResponseHeadersOutput.Text + "\n响应正文\n" + _responseText);
    private void CopyUrl_Click(object sender, RoutedEventArgs e) { if (RequestList.SelectedItem is NetworkCaptureEntry entry) Copy(entry.Url); }
    private void Copy(string value)
    {
        if (value.Length == 0) return;
        try { Clipboard.SetText(value); StatusText.Text = "已复制。"; }
        catch (Exception ex) { StatusText.Text = "复制失败：" + ex.Message; }
    }
}
