using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace AdbDesk.App;

public partial class ScreenshotWindow : Window
{
    private readonly byte[] _png;
    private readonly BitmapImage _bitmap;
    private readonly string _fileName;
    private string? _viewPath;
    private static string? _lastSaveDirectory;

    public ScreenshotWindow(byte[] png, string deviceName)
    {
        _png = png;
        using var stream = new MemoryStream(png, writable: false);
        _bitmap = new BitmapImage();
        _bitmap.BeginInit();
        _bitmap.CacheOption = BitmapCacheOption.OnLoad;
        _bitmap.StreamSource = stream;
        _bitmap.EndInit();
        _bitmap.Freeze();
        var safeName = string.Concat(deviceName.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        if (safeName.Length > 48) safeName = safeName[..48];
        _fileName = $"AdbDesk_{safeName}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height;
        Title = $"截图预览 · {deviceName}";
        DeviceNameText.Text = deviceName;
        ImageDetailsText.Text = $"{_bitmap.PixelWidth} × {_bitmap.PixelHeight} · PNG · {DateTime.Now:HH:mm:ss}";
        PreviewImage.Source = _bitmap;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存手机截图", Filter = "PNG 图片 (*.png)|*.png", DefaultExt = ".png",
            AddExtension = true, FileName = _fileName, OverwritePrompt = true,
            InitialDirectory = _lastSaveDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, _png);
            _lastSaveDirectory = Path.GetDirectoryName(dialog.FileName);
            StatusText.Text = $"已保存：{dialog.FileName}";
        }
        catch (Exception ex) { StatusText.Text = $"保存失败：{ex.Message}"; }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetImage(_bitmap);
            StatusText.Text = "图片已复制，可以粘贴到聊天窗口、画图等应用。";
        }
        catch (Exception ex) { StatusText.Text = $"复制失败，请重试：{ex.Message}"; }
    }

    private void View_Click(object sender, RoutedEventArgs e) => ViewOriginal();
    private void PreviewImage_Click(object sender, MouseButtonEventArgs e) => ViewOriginal();

    private void ViewOriginal()
    {
        try
        {
            // Keep the temporary file available for viewers which load lazily after this window closes.
            if (_viewPath is null || !File.Exists(_viewPath))
            {
                var directory = Path.Combine(Path.GetTempPath(), "AdbDesk", "Screenshots");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"{Guid.NewGuid():N}.png");
                File.WriteAllBytes(path, _png);
                _viewPath = path;
            }
            Process.Start(new ProcessStartInfo(_viewPath) { UseShellExecute = true });
            StatusText.Text = "已用系统图片查看器打开；保留图片请点击“另存为”。";
        }
        catch (Exception ex) { StatusText.Text = $"打开失败：{ex.Message}"; }
    }
}
