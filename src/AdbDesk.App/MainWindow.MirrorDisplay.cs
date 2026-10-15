using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.IO;
using AdbDesk.Core;

namespace AdbDesk.App;

public partial class MainWindow
{
    private readonly Dictionary<string, CancellationTokenSource> _mirrorStarting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MirrorToolbar> _mirrorToolbars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MirrorDisplayWindow> _mirrorDisplayWindows = new(StringComparer.Ordinal);
    private readonly MirrorDisplaySettingsStore _mirrorDisplayStore = new(Path.Combine(UpdatePreferences.DataDirectory, "mirror-display.json"));
    private Dictionary<string, MirrorDisplaySettings>? _mirrorDisplayOverrides;

    private static string MirrorDeviceKey(DeviceRow row) => row.Serial + "|" + row.Model;
    private MirrorDisplaySettings GetDisplaySettings(DeviceRow row)
    {
        _mirrorDisplayOverrides ??= _mirrorDisplayStore.Load();
        if (_mirrorDisplayOverrides.TryGetValue(MirrorDeviceKey(row), out var saved)) return saved;
        var tag = (MirrorFitComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();
        return new(tag switch { "crop" => MirrorScaleMode.Fill, "height" => MirrorScaleMode.FillHeight,
            "width" => MirrorScaleMode.FillWidth, _ => MirrorScaleMode.Complete }, MirrorFullScreenCheckBox.IsChecked == true);
    }

    private void MirrorDisplayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DeviceRow row }) OpenMirrorDisplaySettings(row);
    }

    private void OpenMirrorDisplaySettings(DeviceRow row, Window? owner = null)
    {
        try { OpenMirrorDisplaySettingsCore(row, owner); }
        catch (Exception ex) { if (!_isClosing) SetStatus("无法打开显示设置：" + ex.Message, StatusKind.Error); }
    }

    private void OpenMirrorDisplaySettingsCore(DeviceRow row, Window? owner)
    {
        if (_isClosing || !row.IsReady || row.IsConnectionBusy || _adbService is not { } adb || _screenshotService is not { } screenshots) return;
        if (_mirrorDisplayWindows.TryGetValue(row.Serial, out var existing)) { existing.Activate(); return; }
        var mirrors = _mirrorService;
        var device = row.Device;
        var handle = mirrors?.GetWindowHandle(row.Serial) ?? IntPtr.Zero;
        MirrorTargetDisplay target;
        try { target = GetMirrorTargetDisplay(handle); }
        catch (Exception ex) { SetStatus("无法打开显示设置：" + ex.Message, StatusKind.Error); return; }
        var settings = GetDisplaySettings(row);
        if (handle != IntPtr.Zero && MirrorNativeWindow.GetWindowRect(handle, out var bounds))
            settings = settings with { FullScreen = Math.Abs(bounds.Left - target.Left) <= 2 && Math.Abs(bounds.Top - target.Top) <= 2 &&
                Math.Abs(bounds.Right - target.Left - target.Width) <= 2 && Math.Abs(bounds.Bottom - target.Top - target.Height) <= 2 };
        var dialog = new MirrorDisplayWindow(row.DisplayName, settings, target.Width, target.Height, async token =>
        {
            if (_isClosing || !ReferenceEquals(_adbService, adb) || !Devices.Contains(row) || !row.IsReady || row.Device.TransportId != device.TransportId)
                throw new InvalidOperationException("设备连接已变化，请关闭后重新打开显示设置。");
            var natural = await adb.GetDisplaySizeAsync(device, token);
            var png = await screenshots.CaptureAsync(device.Serial, token);
            return new MirrorDisplayFrame(natural, png);
        }, _lifetime.Token) { Owner = owner ?? this };
        // Center over the actual display, not over the tiny floating toolbar.
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        var targetHandle = handle == IntPtr.Zero ? new WindowInteropHelper(this).Handle : handle;
        var scale = Math.Max(96, MirrorNativeWindow.GetDpiForWindow(targetHandle)) / 96d;
        dialog.MinWidth = Math.Min(dialog.MinWidth, Math.Max(480, (target.Width - 48) / scale));
        dialog.MinHeight = Math.Min(dialog.MinHeight, Math.Max(380, (target.Height - 64) / scale));
        dialog.Width = Math.Max(dialog.MinWidth, Math.Min(dialog.Width, (target.Width - 48) / scale));
        dialog.Height = Math.Max(dialog.MinHeight, Math.Min(dialog.Height, (target.Height - 64) / scale));
        dialog.SourceInitialized += (_, _) =>
        {
            var width = (int)(dialog.Width * scale); var height = (int)(dialog.Height * scale);
            MirrorNativeWindow.SetWindowPos(new WindowInteropHelper(dialog).Handle, IntPtr.Zero,
                target.Left + (target.Width - width) / 2, target.Top + (target.Height - height) / 2, width, height, 0x0010);
        };
        _mirrorDisplayWindows[row.Serial] = dialog;
        try
        {
            if (dialog.ShowDialog() != true || dialog.Result is not { } result || _isClosing) return;
            if (!ReferenceEquals(_mirrorService, mirrors) || !Devices.Contains(row) || !row.IsReady || row.Device.TransportId != device.TransportId)
            { SetStatus("设备连接已变化，未应用显示设置，请重新打开。", StatusKind.Warning); return; }
            if (dialog.Remember) _mirrorDisplayStore.Save(MirrorDeviceKey(row), result);
            else _mirrorDisplayStore.Remove(MirrorDeviceKey(row));
            _mirrorDisplayOverrides ??= _mirrorDisplayStore.Load();
            _mirrorDisplayOverrides[MirrorDeviceKey(row)] = result;
            var running = mirrors?.IsRunning(row.Serial) == true;
            if (running)
            {
                StartMirror(row, target, replaceExisting: true);
            }
            else SetStatus("显示设置已保存，下次启动这台设备的投屏时生效。", StatusKind.Ready);
        }
        catch (Exception ex) { SetStatus("应用显示设置失败：" + ex.Message, StatusKind.Error); }
        finally { _mirrorDisplayWindows.Remove(row.Serial); }
    }

    private void EnsureMirrorToolbar(DeviceRow row)
    {
        if (_isClosing || _mirrorToolbars.ContainsKey(row.Serial) || _mirrorService is not { } mirrors) return;
        _mirrorToolbars[row.Serial] = new(() => mirrors.GetWindowHandle(row.Serial), owner => OpenMirrorDisplaySettings(row, owner), (message, success) =>
        {
            if (_isClosing) return;
            AppendMirrorLog(row.Serial, message);
            SetStatus(message, success ? StatusKind.Ready : StatusKind.Warning);
        });
    }

    private void CloseMirrorToolbar(string serial)
    {
        if (_mirrorToolbars.Remove(serial, out var toolbar)) toolbar.Dispose();
    }

    private void DisposeMirrorDisplay()
    {
        foreach (var window in _mirrorDisplayWindows.Values.ToArray()) window.Close();
        _mirrorDisplayWindows.Clear();
        foreach (var toolbar in _mirrorToolbars.Values.ToArray()) toolbar.Dispose();
        _mirrorToolbars.Clear();
    }

    private void StopMirror(DeviceRow row)
    {
        CancelMirrorStart(row.Serial);
        CloseMirrorToolbar(row.Serial);
        _mirrorService?.Stop(row.Serial);
    }

    private void CancelMirrorStart(string serial)
    {
        if (!_mirrorStarting.Remove(serial, out var source)) return;
        source.Cancel();
        var row = Devices.FirstOrDefault(item => item.Serial == serial);
        if (row is not null) row.IsMirrorStarting = false;
        // The asynchronous start operation owns disposal of its cancellation source.
    }

    private MirrorTargetDisplay GetMirrorTargetDisplay(IntPtr handle = default)
    {
        var monitor = MirrorMonitorFromWindow(handle == IntPtr.Zero ? new WindowInteropHelper(this).Handle : handle, 2);
        var info = new MirrorMonitorInfo { Size = Marshal.SizeOf<MirrorMonitorInfo>() };
        if (!MirrorGetMonitorInfo(monitor, ref info) || info.Monitor.Right <= info.Monitor.Left || info.Monitor.Bottom <= info.Monitor.Top)
            throw new InvalidOperationException("无法读取电脑显示器尺寸，请先选择完整显示并关闭启动全屏。");
        // Full monitor bounds include the taskbar area occupied by fullscreen playback.
        return new(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top);
    }

    private sealed record MirrorTargetDisplay(int Left, int Top, int Width, int Height);
    [StructLayout(LayoutKind.Sequential)]
    private struct MirrorNativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MirrorMonitorInfo
    {
        public int Size;
        public MirrorNativeRect Monitor;
        public MirrorNativeRect Work;
        public uint Flags;
    }
    [DllImport("user32.dll", EntryPoint = "MonitorFromWindow")]
    private static extern IntPtr MirrorMonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MirrorGetMonitorInfo(IntPtr monitor, ref MirrorMonitorInfo info);
}
