using System.Windows;
using AdbDesk.Core;

namespace AdbDesk.App;

public partial class MainWindow
{
    private ScreenshotService? _screenshotService;

    private async void ScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing || !_servicesReady || _screenshotService is null ||
            sender is not FrameworkElement { DataContext: DeviceRow row } || !row.CanCaptureAction) return;

        row.IsCapturing = true;
        SetStatus($"正在截取 {row.DisplayName} 的屏幕…", StatusKind.Working);
        try
        {
            var png = await _screenshotService.CaptureAsync(row.Serial, _lifetime.Token);
            if (_isClosing) return;
            var preview = new ScreenshotWindow(png, row.DisplayName) { Owner = this };
            preview.Show();
            SetStatus("截图完成，可在预览中保存、查看或复制图片", StatusKind.Ready);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_isClosing) return;
            SetStatus($"截图失败：{ex.Message}", StatusKind.Error);
            MessageBox.Show(this, ex.Message, "截图失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { row.IsCapturing = false; }
    }
}
