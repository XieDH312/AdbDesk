using System.Windows;
using System.Windows.Interop;

namespace AdbDesk.App;

public partial class MainWindow
{
    private UsageHelpWindow? _usageHelpWindow;
    private void OpenUsageHelp()
    {
        if (_isClosing) return;
        if (_usageHelpWindow is not null)
        {
            if (_usageHelpWindow.WindowState == WindowState.Minimized) _usageHelpWindow.WindowState = WindowState.Normal;
            _usageHelpWindow.Activate(); return;
        }
        var window = new UsageHelpWindow();
        _usageHelpWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_usageHelpWindow, window)) _usageHelpWindow = null; };
        try
        {
            window.Owner = this;
            MirrorNativeWindow.CenterDialog(window, new WindowInteropHelper(this).Handle);
            window.Show();
        }
        catch (Exception ex) { window.Close(); SetStatus("无法打开帮助：" + ex.Message, StatusKind.Error); }
    }
}
