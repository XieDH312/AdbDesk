using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AdbDesk.Core;

namespace AdbDesk.App;

public enum PairingTab { Qr, Code }

public partial class PairingWindow : Window
{
    private const string CanceledMessage = "已停止等待；已提交给 ADB 的配对仍可能完成。可在手机无线调试中删除已配对电脑。";
    private readonly PairingService _service;
    private readonly Func<Task> _refreshDevices;
    private readonly Func<string> _networkHint;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _operation;
    private QrPairingSession? _qrSession;
    private long _operationId;
    private bool _loaded;
    private bool _closed;

    public PairingWindow(PairingService service, PairingTab initialTab, Func<Task> refreshDevices, Func<string> networkHint)
    {
        _service = service;
        _refreshDevices = refreshDevices;
        _networkHint = networkHint;
        InitializeComponent();
        PairingTabs.SelectedIndex = initialTab == PairingTab.Qr ? 0 : 1;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
    }

    public void SelectTab(PairingTab tab) => PairingTabs.SelectedIndex = tab == PairingTab.Qr ? 0 : 1;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        FitToWorkArea();
        _loaded = true;
        if (PairingTabs.SelectedIndex == 0) await StartQrAsync();
        else ShowStatus("请填写手机配对弹窗中的地址和六位配对码。", false);
    }

    private void FitToWorkArea()
    {
        var work = SystemParameters.WorkArea;
        var monitor = MonitorFromWindow(new WindowInteropHelper(this).Handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var origin = transform.Transform(new Point(info.Work.Left, info.Work.Top));
            var end = transform.Transform(new Point(info.Work.Right, info.Work.Bottom));
            work = new Rect(origin, end);
        }
        var availableWidth = Math.Max(1, work.Width - 16);
        var availableHeight = Math.Max(1, work.Height - 16);
        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
        Left = Math.Clamp(Left, work.Left + 8, work.Right - Width - 8);
        Top = Math.Clamp(Top, work.Top + 8, work.Bottom - Height - 8);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private async void PairingTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _closed || !ReferenceEquals(e.Source, PairingTabs)) return;
        var wasPending = _operation is not null;
        StopOperation();
        PairingCode.Clear();
        if (PairingTabs.SelectedIndex == 0) await StartQrAsync();
        else ShowStatus(wasPending ? CanceledMessage + "\n请填写新的配对信息。" : "请填写手机配对弹窗中的地址和六位配对码。", false);
    }

    private async void RefreshQr_Click(object sender, RoutedEventArgs e) => await StartQrAsync();

    private async Task StartQrAsync()
    {
        if (_closed) return;
        StopOperation();
        try
        {
            var session = PairingService.CreateQrSession();
            using var stream = new MemoryStream(PairingQrRenderer.RenderPng(session.Payload));
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            await RunOperationAsync((token, progress) => _service.PairWithQrAsync(session, progress, token), session, image);
        }
        catch (Exception exception)
        {
            if (!_closed) ShowStatus($"无法生成二维码：{exception.Message}", true);
        }
    }

    private async void PairCode_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || _closed) return;
        var address = PairingAddress.Text.Trim();
        var code = PairingCode.Password;
        var connection = OptionalConnectionAddress.Text.Trim();
        PairingCode.Clear();
        await RunOperationAsync((token, progress) => _service.PairWithCodeAsync(address, code, connection, progress, token));
    }

    private async void ManualConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || _closed) return;
        var endpoint = ManualConnectionAddress.Text.Trim();
        await RunOperationAsync((token, _) => _service.ConnectAsync(endpoint, token));
    }

    private async Task RunOperationAsync(
        Func<CancellationToken, IProgress<string>, Task<PairingResult>> action,
        QrPairingSession? qrSession = null,
        BitmapSource? qrImage = null)
    {
        if (_closed) return;
        StopOperation();
        var id = _operationId;
        var source = new CancellationTokenSource();
        _operation = source;
        _qrSession = qrSession;
        QrImage.Source = qrImage;
        SetBusy(true);
        if (qrSession is not null)
        {
            UpdateQrCountdown();
            _timer.Start();
        }
        ShowStatus(qrSession is null ? "正在处理连接请求…" : "请用手机无线调试中的扫码入口扫描二维码。", false);
        var acceptingProgress = true;
        var progress = new Progress<string>(message =>
        {
            if (acceptingProgress && IsCurrent(id)) ShowStatus(message, false);
        });
        try
        {
            var result = await action(source.Token, progress);
            acceptingProgress = false;
            if (!IsCurrent(id)) return;
            var summary = result.Connected ? "设备已连接" : result.Paired ? "已配对，但尚未连接" : "尚未连接成功";
            ShowStatus($"{summary}\n{result.Message}", !result.Connected);
            if (!string.IsNullOrWhiteSpace(result.Endpoint)) ManualConnectionAddress.Text = result.Endpoint;
            if (result.Connected) await _refreshDevices();
        }
        catch (OperationCanceledException)
        {
            acceptingProgress = false;
            if (IsCurrent(id)) ShowStatus(CanceledMessage, true);
        }
        catch (Exception exception)
        {
            acceptingProgress = false;
            if (IsCurrent(id)) ShowStatus($"操作失败：{exception.Message}", true);
        }
        finally
        {
            if (IsCurrent(id))
            {
                // Retire queued Progress callbacks as well as late task results.
                _operationId++;
                _operation = null;
                _qrSession = null;
                _timer.Stop();
                QrImage.Source = null;
                if (qrSession is not null) QrExpiryText.Text = "本次二维码已停止，点击刷新可重新配对。";
                SetBusy(false);
            }
            source.Dispose();
        }
    }

    private bool IsCurrent(long id) => !_closed && id == _operationId;

    private void StopOperation()
    {
        _operationId++;
        var source = _operation;
        _operation = null;
        source?.Cancel();
        _qrSession = null;
        _timer?.Stop();
        if (_loaded && !_closed)
        {
            QrImage.Source = null;
            SetBusy(false);
        }
        // Each operation disposes its own source after its asynchronous work unwinds.
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_closed || _qrSession is null) return;
        if (_qrSession.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            StopOperation();
            QrExpiryText.Text = "二维码已过期，请刷新。";
            ShowStatus("二维码已过期，已停止等待。已提交的配对仍可能完成。", true);
            return;
        }
        UpdateQrCountdown();
    }

    private void UpdateQrCountdown()
    {
        if (_qrSession is null) return;
        var seconds = Math.Max(0, (int)Math.Ceiling((_qrSession.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds));
        QrExpiryText.Text = $"二维码剩余 {seconds} 秒";
    }

    private void SetBusy(bool busy)
    {
        CancelButton.IsEnabled = busy;
        PairCodeButton.IsEnabled = !busy;
        ManualConnectButton.IsEnabled = !busy;
        PairingAddress.IsEnabled = !busy;
        PairingCode.IsEnabled = !busy;
        OptionalConnectionAddress.IsEnabled = !busy;
        ManualConnectionAddress.IsEnabled = !busy;
    }

    private void ShowStatus(string message, bool warning)
    {
        ResultText.Text = message.Contains("等待手机") || message.Contains("未发现") || message.Contains("二维码已过期")
            ? message + "\n" + _networkHint()
            : message;
        ResultText.Foreground = warning ? new SolidColorBrush(Color.FromRgb(180, 83, 9)) : new SolidColorBrush(Color.FromRgb(29, 78, 216));
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        StopOperation();
        QrExpiryText.Text = "已停止等待，点击刷新可生成新二维码。";
        ShowStatus(CanceledMessage, true);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        StopOperation();
        PairingCode.Clear();
        QrImage.Source = null;
        _timer.Tick -= Timer_Tick;
    }
}
