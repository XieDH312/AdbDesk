using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AdbDesk.Core;

namespace AdbDesk.App;

internal sealed record MirrorDisplayFrame(MirrorDisplaySize NaturalSize, byte[] Png);

public partial class MirrorDisplayWindow : Window
{
    private readonly Func<CancellationToken, Task<MirrorDisplayFrame>> _capture;
    private readonly CancellationTokenSource _closed;
    private readonly CancellationToken _token;
    private readonly int _targetWidth, _targetHeight;
    private MirrorCropRegion _region;
    private MirrorDisplaySize? _sourceSize;
    private MirrorDisplaySize? _naturalSize;
    private BitmapSource? _bitmap;
    private Rect _imageRect;
    private bool _ready, _updatingEdges, _capturing, _edgesValid = true;
    private Point? _dragStart;
    private MirrorCropRegion? _dragRegion;
    private string _dragKind = "";
    public MirrorDisplaySettings? Result { get; private set; }
    public bool Remember => RememberCheckBox.IsChecked == true;

    internal MirrorDisplayWindow(string name, MirrorDisplaySettings settings, int targetWidth, int targetHeight,
        Func<CancellationToken, Task<MirrorDisplayFrame>> capture, CancellationToken lifetime)
    {
        _capture = capture;
        _closed = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _token = _closed.Token;
        _targetWidth = targetWidth;
        _targetHeight = targetHeight;
        _region = settings.EffectiveRegion;
        _sourceSize = settings.SourceSize;
        InitializeComponent();
        DeviceText.Text = name + " · 显示设置";
        ModeComboBox.SelectedIndex = (int)settings.Mode;
        FullScreenCheckBox.IsChecked = settings.FullScreen;
        OutputBorder.Height = 290d * targetHeight / targetWidth;
        _ready = true;
        WriteEdges();
        UpdatePreview();
        Closed += (_, _) => { _closed.Cancel(); _closed.Dispose(); PreviewCanvas.ReleaseMouseCapture(); };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshFrameAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshFrameAsync();
    private async Task RefreshFrameAsync()
    {
        if (_capturing || _token.IsCancellationRequested) return;
        _capturing = true;
        RefreshFrameButton.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        StatusText.Text = "正在读取手机完整画面…";
        try
        {
            var frame = await _capture(_token);
            _token.ThrowIfCancellationRequested();
            var current = MirrorDisplayGeometry.ReadScreenshotSize(frame.Png);
            using var stream = new MemoryStream(frame.Png, false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            var changed = _sourceSize is not null && _sourceSize != current && _region != MirrorCropRegion.Full;
            if (changed) _region = MirrorCropRegion.Full;
            _sourceSize = current;
            _naturalSize = frame.NaturalSize;
            _bitmap = bitmap;
            SourceImage.Source = bitmap;
            WriteEdges();
            StatusText.Text = changed ? "手机方向或分辨率已变化，已还原选区，请重新裁剪。" : "拖动选区或填写裁剪比例，右侧预览会同步更新。";
            UpdatePreview();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_token.IsCancellationRequested) StatusText.Text = "读取画面失败：" + ex.Message + "。可刷新重试。";
        }
        finally
        {
            _capturing = false;
            if (!_token.IsCancellationRequested) { RefreshFrameButton.IsEnabled = true; UpdatePreview(); }
        }
    }

    private MirrorDisplaySettings ReadSettings() => new((MirrorScaleMode)Math.Max(0, ModeComboBox.SelectedIndex),
        FullScreenCheckBox.IsChecked == true, _region, _sourceSize);
    private void Mode_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) UpdatePreview(); }
    private void Preview_SizeChanged(object sender, SizeChangedEventArgs e) { if (_ready) UpdatePreview(); }

    private void WriteEdges()
    {
        _updatingEdges = true;
        LeftInput.Text = (_region.Left * 100).ToString("0.##", CultureInfo.InvariantCulture);
        TopInput.Text = (_region.Top * 100).ToString("0.##", CultureInfo.InvariantCulture);
        RightInput.Text = ((1 - _region.Right) * 100).ToString("0.##", CultureInfo.InvariantCulture);
        BottomInput.Text = ((1 - _region.Bottom) * 100).ToString("0.##", CultureInfo.InvariantCulture);
        _updatingEdges = false;
        _edgesValid = true;
        EdgeErrorText.Text = "";
    }

    private void Edges_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _updatingEdges) return;
        var inputs = new[] { LeftInput, TopInput, RightInput, BottomInput };
        var values = new double[4];
        _edgesValid = true;
        for (var i = 0; i < 4; i++)
            if (!double.TryParse(inputs[i].Text.Replace('，', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out values[i]) ||
                !double.IsFinite(values[i]) || values[i] < 0 || values[i] > 98) _edgesValid = false;
        if (_edgesValid)
        {
            var next = new MirrorCropRegion(values[0] / 100, values[1] / 100, 1 - values[2] / 100, 1 - values[3] / 100);
            try { next.Validate(); _region = next; if (ModeComboBox.SelectedIndex == 0) ModeComboBox.SelectedIndex = 4; }
            catch (ArgumentException) { _edgesValid = false; }
        }
        EdgeErrorText.Text = _edgesValid ? "" : "请输入有效百分比，左右和上下分别至少保留 2%。";
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (!_ready) return;
        var mode = (MirrorScaleMode)Math.Max(0, ModeComboBox.SelectedIndex);
        ModeHintText.Text = mode switch
        {
            MirrorScaleMode.FillHeight => "填满高度，左右超出的内容会居中裁掉；较窄的画面保留左右留边。",
            MirrorScaleMode.FillWidth => "填满宽度，上下超出的内容会居中裁掉；较宽的画面保留上下留边。",
            MirrorScaleMode.Fill => "同时填满宽高，超出的边缘会居中裁掉。",
            MirrorScaleMode.Custom => "只显示选区，等比缩放。拖动四角缩放选区，拖动内部移动。",
            _ => "保留手机完整画面，忽略自定义裁剪。"
        };
        ApplyButton.IsEnabled = !_capturing && _edgesValid && (mode == MirrorScaleMode.Complete || _bitmap is not null);
        if (_bitmap is null || _sourceSize is null || _naturalSize is null) return;
        var scale = Math.Min(PreviewCanvas.ActualWidth / _bitmap.PixelWidth, PreviewCanvas.ActualHeight / _bitmap.PixelHeight);
        if (scale <= 0) return;
        _imageRect = new((PreviewCanvas.ActualWidth - _bitmap.PixelWidth * scale) / 2,
            (PreviewCanvas.ActualHeight - _bitmap.PixelHeight * scale) / 2, _bitmap.PixelWidth * scale, _bitmap.PixelHeight * scale);
        SourceImage.Width = _imageRect.Width; SourceImage.Height = _imageRect.Height;
        Canvas.SetLeft(SourceImage, _imageRect.Left); Canvas.SetTop(SourceImage, _imageRect.Top);
        var region = mode == MirrorScaleMode.Complete ? MirrorCropRegion.Full : _region;
        var selection = new Rect(_imageRect.Left + region.Left * _imageRect.Width, _imageRect.Top + region.Top * _imageRect.Height,
            (region.Right - region.Left) * _imageRect.Width, (region.Bottom - region.Top) * _imageRect.Height);
        Canvas.SetLeft(SelectionOutline, selection.Left); Canvas.SetTop(SelectionOutline, selection.Top);
        SelectionOutline.Width = selection.Width; SelectionOutline.Height = selection.Height;
        var handles = new[] { CornerTL, CornerTR, CornerBL, CornerBR };
        for (var i = 0; i < 4; i++)
        {
            Canvas.SetLeft(handles[i], (i % 2 == 0 ? selection.Left : selection.Right) - 4.5);
            Canvas.SetTop(handles[i], (i < 2 ? selection.Top : selection.Bottom) - 4.5);
        }
        ShadePath.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(_imageRect), new RectangleGeometry(selection));
        try
        {
            var crop = MirrorDisplayGeometry.CreateCrop(_naturalSize, _sourceSize, _targetWidth, _targetHeight, ReadSettings());
            var transposed = _naturalSize != _sourceSize;
            var rect = crop is null ? new Int32Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight) : transposed
                ? new Int32Rect(crop.Y, crop.X, crop.Height, crop.Width) : new Int32Rect(crop.X, crop.Y, crop.Width, crop.Height);
            OutputImage.Source = new CroppedBitmap(_bitmap, rect);
            OutputDetailsText.Text = $"保留 {rect.Width} × {rect.Height} · 电脑 {_targetWidth} × {_targetHeight}";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        { OutputImage.Source = null; OutputDetailsText.Text = ex.Message; ApplyButton.IsEnabled = false; }
    }

    private Point NormalizedPoint(Point point) => new(Math.Clamp((point.X - _imageRect.Left) / _imageRect.Width, 0, 1),
        Math.Clamp((point.Y - _imageRect.Top) / _imageRect.Height, 0, 1));
    private void Preview_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_bitmap is null || _capturing || !_imageRect.Contains(e.GetPosition(PreviewCanvas))) return;
        if (ModeComboBox.SelectedIndex == 0) _region = MirrorCropRegion.Full;
        var point = NormalizedPoint(e.GetPosition(PreviewCanvas));
        _dragStart = point; _dragRegion = _region;
        var nearLeft = Math.Abs(point.X - _region.Left) * _imageRect.Width < 13;
        var nearRight = Math.Abs(point.X - _region.Right) * _imageRect.Width < 13;
        var nearTop = Math.Abs(point.Y - _region.Top) * _imageRect.Height < 13;
        var nearBottom = Math.Abs(point.Y - _region.Bottom) * _imageRect.Height < 13;
        _dragKind = (nearLeft ? "l" : nearRight ? "r" : "") + (nearTop ? "t" : nearBottom ? "b" : "");
        if (_dragKind.Length == 0) _dragKind = point.X > _region.Left && point.X < _region.Right && point.Y > _region.Top && point.Y < _region.Bottom ? "move" : "new";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) _dragKind = "new";
        if (ModeComboBox.SelectedIndex == 0) ModeComboBox.SelectedIndex = 4;
        PreviewCanvas.CaptureMouse(); e.Handled = true;
    }
    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || _dragRegion is not { } original || e.LeftButton != MouseButtonState.Pressed) return;
        var point = NormalizedPoint(e.GetPosition(PreviewCanvas));
        var dx = point.X - start.X; var dy = point.Y - start.Y;
        if (_dragKind == "move")
        {
            dx = Math.Clamp(dx, -original.Left, 1 - original.Right); dy = Math.Clamp(dy, -original.Top, 1 - original.Bottom);
            _region = new(original.Left + dx, original.Top + dy, original.Right + dx, original.Bottom + dy);
        }
        else if (_dragKind == "new")
        {
            if (Math.Abs(point.X - start.X) < .02 || Math.Abs(point.Y - start.Y) < .02) return;
            _region = new(Math.Min(point.X, start.X), Math.Min(point.Y, start.Y), Math.Max(point.X, start.X), Math.Max(point.Y, start.Y));
        }
        else _region = new(_dragKind.Contains('l') ? Math.Clamp(original.Left + dx, 0, Math.Max(0, original.Right - .02)) : original.Left,
            _dragKind.Contains('t') ? Math.Clamp(original.Top + dy, 0, Math.Max(0, original.Bottom - .02)) : original.Top,
            _dragKind.Contains('r') ? Math.Clamp(original.Right + dx, Math.Min(1, original.Left + .02), 1) : original.Right,
            _dragKind.Contains('b') ? Math.Clamp(original.Bottom + dy, Math.Min(1, original.Top + .02), 1) : original.Bottom);
        WriteEdges(); UpdatePreview();
    }
    private void Preview_MouseUp(object sender, MouseButtonEventArgs e) { _dragStart = null; _dragRegion = null; PreviewCanvas.ReleaseMouseCapture(); }
    private void Preview_LostCapture(object sender, MouseEventArgs e) { _dragStart = null; _dragRegion = null; }
    private void Center_Click(object sender, RoutedEventArgs e)
    {
        var width = _region.Right - _region.Left; var height = _region.Bottom - _region.Top;
        _region = new((1 - width) / 2, (1 - height) / 2, (1 + width) / 2, (1 + height) / 2);
        WriteEdges(); UpdatePreview();
    }
    private void Reset_Click(object sender, RoutedEventArgs e) { _region = MirrorCropRegion.Full; WriteEdges(); UpdatePreview(); }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyButton.IsEnabled) return;
        try
        {
            Result = ReadSettings(); Result.Validate();
            if (_naturalSize is not null && _sourceSize is not null)
                MirrorDisplayGeometry.CreateCrop(_naturalSize, _sourceSize, _targetWidth, _targetHeight, Result);
            DialogResult = true;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
}
