using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace AdbDesk.App;

// A native-owned tool window follows only the scrcpy process belonging to this device.
// It is not globally topmost and disappears when another application is foreground.
internal sealed class MirrorToolbar : IDisposable
{
    private readonly Func<IntPtr> _getHandle;
    private readonly Action<Window> _openSettings;
    private readonly Action<string, bool> _report;
    private readonly DispatcherTimer _timer;
    private Window? _window;
    private IntPtr _owner;
    private bool _disposed;
    private int _failures;
    private UsageHelpWindow? _helpWindow;
    private TextBlock? _status;
    private readonly MirrorToolbarVisibility _visibility = new();
    private bool? _wasAutoHide;

    internal MirrorToolbar(Func<IntPtr> getHandle, Action<Window> openSettings, Action<string, bool> report)
    {
        _getHandle = getHandle; _openSettings = openSettings; _report = report;
        _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        _timer.Tick += Update;
        _timer.Start();
        Update(null, EventArgs.Empty);
    }

    private void Update(object? sender, EventArgs e)
    {
        if (_disposed) return;
        try { UpdateWindow(); _failures = 0; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            // The native owner may disappear between handle lookup and showing its tool window.
            CloseWindow();
            if (++_failures >= 3) Dispose();
        }
    }

    private void UpdateWindow()
    {
        if (_disposed) return;
        var handle = _getHandle();
        if (handle == IntPtr.Zero || !MirrorNativeWindow.IsWindow(handle)) { CloseWindow(); return; }
        if (_owner != handle) { CloseWindow(); _owner = handle; }
        var ownedOpen = _window?.OwnedWindows.Cast<Window>().Any(window => window.IsVisible) == true;
        if (MirrorNativeWindow.IsIconic(handle) || MirrorNativeWindow.RootOwner(MirrorNativeWindow.GetForegroundWindow()) != MirrorNativeWindow.RootOwner(handle))
        { if (!ownedOpen) _window?.Hide(); _visibility.Reset(); return; }
        if (!MirrorNativeWindow.TryClientBounds(handle, out var bounds) || !MirrorNativeWindow.GetWindowRect(handle, out var frame) ||
            !MirrorNativeWindow.TryMonitorBounds(handle, out var monitor, out var work))
        { if (!ownedOpen) _window?.Hide(); _visibility.Reset(); return; }
        var dpi = (int)Math.Max(96, MirrorNativeWindow.GetDpiForWindow(handle));
        var placement = MirrorToolbarPlacement.Calculate(ToRect(frame), ToRect(bounds), ToRect(monitor), ToRect(work), dpi);
        if (_window is null)
        {
            var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
            _status = new TextBlock { Text = "悬停按钮查看说明与快捷键", FontSize = 10, Foreground = Brushes.SlateGray,
                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(5, 3, 5, 0) };
            var content = new StackPanel(); content.Children.Add(actions); content.Children.Add(_status);
            var window = new Window
            {
                Title = "AdbDesk 投屏工具栏", Width = 388, Height = 64, ShowInTaskbar = false,
                ShowActivated = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true, Background = Brushes.Transparent, Opacity = .96,
                Content = new Border { Background = Brushes.White, BorderBrush = Brushes.LightSlateGray, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9), Padding = new Thickness(6), Child = content },
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            AddAction(actions, "显示设置", "上下铺满、左右铺满和自定义裁剪；F11 切换全屏。", () => _openSettings(window));
            AddAction(actions, "手机熄屏", "关闭手机实体屏幕，电脑投屏继续。快捷键：左 Alt + O（字母 O）。", () => SendScreenPower(false));
            AddAction(actions, "手机亮屏", "恢复手机实体屏幕。快捷键：左 Alt + Shift + O。", () => SendScreenPower(true));
            AddAction(actions, "操作帮助", "查看全屏、旋转、返回、Home、音量等操作，可搜索和复制。", () => OpenHelp(window));
            window.Closed += (_, _) => { if (ReferenceEquals(_window, window)) _window = null; };
            // Track before creating the HWND so a disappearing owner cannot leak a hidden WPF window.
            _window = window;
            var interop = new WindowInteropHelper(window);
            interop.EnsureHandle();
            interop.Owner = handle;
        }
        var toolbar = _window;
        if (toolbar is null) return;
        ownedOpen = toolbar.OwnedWindows.Cast<Window>().Any(window => window.IsVisible);
        if (placement.AutoHide && _wasAutoHide != true)
        {
            _visibility.Reset();
            if (!ownedOpen) toolbar.Hide();
        }
        _wasAutoHide = placement.AutoHide;
        var pointer = MirrorNativeWindow.TryCursorPosition(out var cursorX, out var cursorY);
        var atEdge = pointer && placement.RevealZone.Contains(cursorX, cursorY);
        var overBar = pointer && toolbar.IsVisible && placement.Bounds.Contains(cursorX, cursorY);
        if (!_visibility.Update(placement.AutoHide, atEdge, overBar, ownedOpen, Environment.TickCount64))
        { toolbar.Hide(); return; }
        toolbar.Width = placement.Bounds.Width * 96d / dpi;
        toolbar.Height = placement.Bounds.Height * 96d / dpi;
        // Position the hidden HWND before first Show, so no frame appears over the video at (0,0).
        var toolbarHandle = new WindowInteropHelper(toolbar).Handle;
        MirrorNativeWindow.SetWindowPos(toolbarHandle, IntPtr.Zero, placement.Bounds.Left, placement.Bounds.Top,
            placement.Bounds.Width, placement.Bounds.Height, 0x0010);
        if (!toolbar.IsVisible) toolbar.Show();
    }

    private static ToolbarRect ToRect(MirrorNativeWindow.Rect rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private void AddAction(Panel panel, string text, string hint, Action action)
    {
        var button = new Button { Content = text, ToolTip = hint, Height = 30, Width = 82,
            Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 0, 4, 4) };
        button.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { ShowStatus("操作未完成：" + ex.Message, false); }
        };
        panel.Children.Add(button);
    }

    private void SendScreenPower(bool turnOn)
    {
        if (_disposed) return;
        try
        {
            var result = MirrorScreenControl.Send(_getHandle, turnOn);
            ShowStatus(result.Message, result.Sent);
        }
        catch (Exception ex) { ShowStatus("屏幕请求发送失败：" + ex.Message, false); }
    }

    private void ShowStatus(string text, bool success)
    {
        if (_status is not null) { _status.Text = text; _status.ToolTip = text; }
        _report(text, success);
    }

    private void OpenHelp(Window owner)
    {
        if (_disposed) return;
        if (_helpWindow is not null)
        {
            if (_helpWindow.WindowState == WindowState.Minimized) _helpWindow.WindowState = WindowState.Normal;
            _helpWindow.Activate(); return;
        }
        var help = new UsageHelpWindow();
        _helpWindow = help;
        help.Closed += (_, _) => { if (ReferenceEquals(_helpWindow, help)) _helpWindow = null; };
        try { help.Owner = owner; MirrorNativeWindow.CenterDialog(help, _owner); help.Show(); }
        catch (Exception ex) { help.Close(); ShowStatus("无法打开操作帮助：" + ex.Message, false); }
    }

    private void CloseWindow()
    {
        var window = _window; _window = null; _owner = IntPtr.Zero;
        _visibility.Reset(); _wasAutoHide = null;
        _helpWindow?.Close(); _helpWindow = null; _status = null;
        window?.Close();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer.Stop(); _timer.Tick -= Update; CloseWindow();
    }
}

internal static class MirrorNativeWindow
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { internal int Size; internal Rect Monitor, Work; internal uint Flags; }
    internal static bool TryMonitorBounds(IntPtr handle, out Rect monitor, out Rect work)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var success = GetMonitorInfo(MonitorFromWindow(handle, 2), ref info);
        monitor = info.Monitor; work = info.Work;
        return success && monitor.Right > monitor.Left && monitor.Bottom > monitor.Top && work.Right > work.Left && work.Bottom > work.Top;
    }
    internal static bool TryCursorPosition(out int x, out int y)
    {
        var success = GetCursorPos(out var point); x = point.X; y = point.Y; return success;
    }
    internal static void CenterDialog(Window dialog, IntPtr owner)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(owner, 2), ref info)) { dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen; return; }
        var bounds = info.Work;
        var scale = Math.Max(96, GetDpiForWindow(owner)) / 96d;
        var availableWidth = Math.Max(320, (bounds.Right - bounds.Left - 32) / scale);
        var availableHeight = Math.Max(300, (bounds.Bottom - bounds.Top - 32) / scale);
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.MinWidth = Math.Min(dialog.MinWidth, availableWidth); dialog.MinHeight = Math.Min(dialog.MinHeight, availableHeight);
        dialog.Width = Math.Min(dialog.Width, availableWidth); dialog.Height = Math.Min(dialog.Height, availableHeight);
        dialog.SourceInitialized += (_, _) =>
        {
            var width = (int)(dialog.Width * scale); var height = (int)(dialog.Height * scale);
            SetWindowPos(new WindowInteropHelper(dialog).Handle, IntPtr.Zero, bounds.Left + (bounds.Right - bounds.Left - width) / 2,
                bounds.Top + (bounds.Bottom - bounds.Top - height) / 2, width, height, 0x0010);
        };
    }
    internal static IntPtr RootOwner(IntPtr handle) => handle == IntPtr.Zero ? IntPtr.Zero : GetAncestor(handle, 3);
    internal static bool TryClientBounds(IntPtr handle, out Rect bounds)
    {
        bounds = default;
        if (!GetClientRect(handle, out var client)) return false;
        var origin = new Point();
        if (!ClientToScreen(handle, ref origin)) return false;
        bounds = new() { Left = origin.X, Top = origin.Y, Right = origin.X + client.Right, Bottom = origin.Y + client.Bottom };
        return bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr handle, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr handle, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr handle, ref Point point);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(IntPtr handle, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
}
