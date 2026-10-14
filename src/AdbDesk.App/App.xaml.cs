using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AdbDesk.Core;

namespace AdbDesk.App;

public partial class App : Application
{
    private SingleInstanceGate? _instance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0] == "--apply-update")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await ApplyUpdateAsync(e.Args);
            return;
        }
        // A stable identity across install directories/build versions, isolated per user/session.
        using var currentProcess = Process.GetCurrentProcess();
        var identity = $"AdbDesk.Main.v1:{Environment.UserDomainName}:{Environment.UserName}:{currentProcess.SessionId}";
        try
        {
            _instance = SingleInstanceGate.TryAcquire(identity, () =>
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(ActivateExisting));
            });
            if (_instance is null)
            {
                if (!await SingleInstanceGate.NotifyAsync(identity, allowForeground: pid => AllowSetForegroundWindow((uint)pid)))
                    MessageBox.Show("AdbDesk 已在运行，请从任务栏打开已有窗口。", "AdbDesk", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"AdbDesk 启动失败：{exception.Message}", "AdbDesk", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void ActivateExisting()
    {
        if (MainWindow is not { } main || Dispatcher.HasShutdownStarted) return;
        if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
        if (!main.IsVisible) main.Show();
        // Activate the owned dialog when the main window is blocked by a modal operation.
        var target = Windows.OfType<Window>().LastOrDefault(window => window.IsVisible && window.Owner == main) ?? main;
        target.Activate();
        var handle = new WindowInteropHelper(target).Handle;
        if (handle != IntPtr.Zero && !SetForegroundWindow(handle))
        {
            var flash = new FlashInfo
            {
                Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = handle,
                Flags = 3, Count = 3, Timeout = 0
            };
            FlashWindowEx(ref flash);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
