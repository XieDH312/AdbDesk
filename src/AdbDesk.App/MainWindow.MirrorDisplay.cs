using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AdbDesk.App;

public partial class MainWindow
{
    private readonly Dictionary<string, CancellationTokenSource> _mirrorStarting = new(StringComparer.Ordinal);

    private void StopMirror(DeviceRow row)
    {
        CancelMirrorStart(row.Serial);
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

    private MirrorTargetDisplay GetMirrorTargetDisplay()
    {
        var monitor = MirrorMonitorFromWindow(new WindowInteropHelper(this).Handle, 2);
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
