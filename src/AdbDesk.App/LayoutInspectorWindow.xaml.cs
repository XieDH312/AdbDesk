using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AdbDesk.Core;
using Microsoft.Win32;

namespace AdbDesk.App;

public partial class LayoutInspectorWindow : Window
{
    private readonly LayoutInspectionService _service;
    private readonly string _serial;
    private CancellationTokenSource? _operation;
    private LayoutSnapshot? _snapshot;
    private BitmapImage? _bitmap;
    private Dictionary<LayoutNode, LayoutTreeItem> _items = [];
    private LayoutTreeItem? _selected;
    private bool _closed, _deviceReady = true, _overlayAvailable;
    public bool IsCapturing => _operation is not null;
    public bool IsClosed => _closed;
    public string Serial => _serial;
    public Func<bool>? CanCapture { get; set; }
    public event EventHandler? CaptureStateChanged;
    public void CancelCapture() => _operation?.Cancel();

    public LayoutInspectorWindow(ToolPaths paths, string serial, string deviceName)
    {
        _serial = serial;
        _service = new LayoutInspectionService(paths);
        InitializeComponent();
        Title = $"当前页面布局 · {deviceName}";
        DeviceText.Text = $"{deviceName} · {serial}";
        MaxHeight = SystemParameters.WorkArea.Height;
        MaxWidth = SystemParameters.WorkArea.Width;
    }

    public void SetDeviceReady(bool ready)
    {
        if (_closed || _deviceReady == ready) return;
        _deviceReady = ready;
        if (!ready) { _operation?.Cancel(); StatusText.Text = "设备已离线；当前显示的是上次快照，可继续查看或导出。"; }
        else StatusText.Text = "设备已重新连接，请点击“刷新布局”读取当前页面。";
        UpdateControls();
    }

    public void StopAndClose() { _operation?.Cancel(); if (!_closed) Close(); }
    private async void Window_Loaded(object sender, RoutedEventArgs e) => await CaptureAsync();
    private void Window_Closing(object? sender, CancelEventArgs e) { _closed = true; _operation?.Cancel(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await CaptureAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();

    private void UpdateControls()
    {
        RefreshButton.IsEnabled = !_closed && _deviceReady && !IsCapturing;
        CancelButton.IsEnabled = !_closed && IsCapturing;
        SaveXmlButton.IsEnabled = !IsCapturing && _snapshot?.Xml is not null;
        SaveImageButton.IsEnabled = !IsCapturing && _bitmap is not null;
    }

    private async Task CaptureAsync()
    {
        if (_closed || !_deviceReady || IsCapturing) return;
        if (CanCapture?.Invoke() == false) { StatusText.Text = "请先结束工具箱查询或 ADB 服务重启，再刷新布局。"; return; }
        using var operation = new CancellationTokenSource();
        _operation = operation;
        CaptureStateChanged?.Invoke(this, EventArgs.Empty);
        UpdateControls();
        StatusText.Text = "正在读取控件层级和截图，请保持手机页面静止…";
        try
        {
            var snapshot = await _service.CaptureAsync(_serial, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (_closed) return;
            // Decode before replacing the old snapshot, so malformed images do not
            // leave a half-updated screenshot/tree pair on screen.
            BitmapImage? bitmap = null;
            if (snapshot.Png is not null)
            {
                try
                {
                    using var stream = new MemoryStream(snapshot.Png, writable: false);
                    bitmap = new BitmapImage();
                    bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                }
                catch (Exception ex)
                {
                    snapshot = snapshot with { Png = null, Warnings = snapshot.Warnings.Append("截图解码失败：" + ex.Message).ToArray() };
                    bitmap = null;
                }
            }
            if (bitmap is null && snapshot.Hierarchy is null) throw new IOException(string.Join("\r\n", snapshot.Warnings));
            ShowSnapshot(snapshot, bitmap);
        }
        catch (OperationCanceledException)
        {
            if (!_closed) StatusText.Text = _deviceReady ? "已停止读取，保留上次快照。" : "设备已离线，保留上次快照。";
        }
        catch (Exception ex) { if (!_closed) StatusText.Text = "读取失败（保留上次快照）：" + ex.Message; }
        finally { _operation = null; CaptureStateChanged?.Invoke(this, EventArgs.Empty); if (!_closed) UpdateControls(); }
    }

    private void ShowSnapshot(LayoutSnapshot snapshot, BitmapImage? bitmap)
    {
        _snapshot = snapshot; _bitmap = bitmap; _selected = null;
        _items = [];
        PropertiesGrid.ItemsSource = null;
        SelectionBox.Visibility = Visibility.Collapsed;
        var roots = snapshot.Hierarchy?.Roots.Select(BuildTree).ToArray() ?? [];
        foreach (var root in roots) root.IsExpanded = true;
        NodeTree.ItemsSource = roots;
        ScreenImage.Source = bitmap;
        var width = bitmap?.PixelWidth ?? Math.Clamp(snapshot.Hierarchy?.Nodes.Select(n => n.Bounds?.Right ?? 0).DefaultIfEmpty(360).Max() ?? 360, 1, 8192);
        var height = bitmap?.PixelHeight ?? Math.Clamp(snapshot.Hierarchy?.Nodes.Select(n => n.Bounds?.Bottom ?? 0).DefaultIfEmpty(720).Max() ?? 720, 1, 8192);
        ScreenCanvas.Width = ScreenImage.Width = width;
        ScreenCanvas.Height = ScreenImage.Height = height;
        _overlayAvailable = snapshot.Hierarchy is not null && (bitmap is null ||
            snapshot.Hierarchy.Nodes.All(n => n.Bounds is not { } b || (b.Right <= width && b.Bottom <= height)));
        ImagePlaceholder.Visibility = bitmap is null ? Visibility.Visible : Visibility.Collapsed;
        ImagePlaceholder.Text = "截图不可用\n可在右侧选择控件查看位置";
        ImageInfo.Text = bitmap is null ? "仅显示布局坐标范围" : $"{width} × {height} · {snapshot.FinishedAt:HH:mm:ss} · 点击查看控件";
        NodeCountText.Text = $"{snapshot.Hierarchy?.Nodes.Count ?? 0} 个节点" + (snapshot.Hierarchy is null ? " · 布局读取失败" : "");
        var warnings = snapshot.Warnings.ToList();
        if (!_overlayAvailable && snapshot.Hierarchy is not null) warnings.Add("布局坐标超出截图范围，可能在采集时旋转了屏幕；已禁用边框和截图定位，请保持页面静止后刷新。");
        StatusText.Text = $"快照 {snapshot.StartedAt:HH:mm:ss}–{snapshot.FinishedAt:HH:mm:ss} · {string.Join("、", snapshot.Hierarchy?.Nodes.Select(n => n.PackageName).Where(p => p.Length > 0).Distinct() ?? [])}" +
            (warnings.Count > 0 ? "\r\n" + string.Join("\r\n", warnings) : "\r\n读取完成。截图与布局不是实时同步，页面变化后请手动刷新。");
    }

    private LayoutTreeItem BuildTree(LayoutNode node)
    {
        var item = new LayoutTreeItem(node);
        _items[node] = item;
        item.Children = node.Children.Select(BuildTree).ToArray();
        return item;
    }

    private void Screen_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_overlayAvailable || _snapshot?.Hierarchy is not { } tree) return;
        var point = e.GetPosition(ScreenCanvas);
        var node = tree.HitTest(point.X, point.Y);
        if (node is null) { StatusText.Text = "此处没有可读取的控件节点，可能是自绘区域。"; return; }
        SelectNode(node);
    }
    private void SelectNode(LayoutNode node)
    {
        if (!_items.TryGetValue(node, out var item)) return;
        for (var parent = node.Parent; parent is not null; parent = parent.Parent) _items[parent].IsExpanded = true;
        if (_selected is not null) _selected.IsSelected = false;
        item.IsSelected = true;
        ShowProperties(item);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => RevealSelection(item)));
    }

    private void RevealSelection(LayoutTreeItem selected)
    {
        if (_closed || !ReferenceEquals(_selected, selected)) return;
        var path = new Stack<LayoutTreeItem>();
        for (var node = selected.Node; node is not null; node = node.Parent) path.Push(_items[node]);
        ItemsControl owner = NodeTree;
        while (path.TryPop(out var item))
        {
            owner.ApplyTemplate(); owner.UpdateLayout();
            var container = owner.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
            if (container is null)
            {
                // A selected model alone cannot bring an unrealized virtualized
                // item into view. Realize each ancestor/child by its item index.
                FindPanel(owner, owner)?.RevealIndex(owner.Items.IndexOf(item));
                owner.UpdateLayout();
                container = owner.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
            }
            if (container is null) return;
            if (path.Count == 0) { container.IsSelected = true; container.BringIntoView(); }
            else { container.IsExpanded = true; owner = container; }
        }
    }

    private static LayoutVirtualizingPanel? FindPanel(DependencyObject element, ItemsControl owner)
    {
        if (element is LayoutVirtualizingPanel panel && ReferenceEquals(ItemsControl.GetItemsOwner(panel), owner)) return panel;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (FindPanel(VisualTreeHelper.GetChild(element, i), owner) is { } result) return result;
        return null;
    }
    private void NodeTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is LayoutTreeItem item) ShowProperties(item);
    }
    private void TreeItem_Selected(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource) && sender is TreeViewItem item) { item.BringIntoView(); e.Handled = true; }
    }
    private void ShowProperties(LayoutTreeItem item)
    {
        _selected = item;
        PropertiesGrid.ItemsSource = item.Node.Attributes.Select(pair => new NodeProperty(pair.Key, pair.Value)).ToArray();
        if (_overlayAvailable && item.Node.Bounds is { } bounds)
        {
            Canvas.SetLeft(SelectionBox, bounds.Left); Canvas.SetTop(SelectionBox, bounds.Top);
            SelectionBox.Width = bounds.Width; SelectionBox.Height = bounds.Height;
            SelectionBox.StrokeThickness = Math.Max(2, ScreenCanvas.Width / 160);
            SelectionBox.Visibility = Visibility.Visible;
        }
        else SelectionBox.Visibility = Visibility.Collapsed;
    }
    private void Find_Click(object sender, RoutedEventArgs e) => FindNext();
    private void Search_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { FindNext(); e.Handled = true; } }
    private void FindNext()
    {
        var query = SearchInput.Text.Trim();
        if (query.Length == 0 || _snapshot?.Hierarchy is not { } hierarchy) return;
        var matches = hierarchy.Nodes.Where(n => n.Matches(query)).ToArray();
        if (matches.Length == 0) { StatusText.Text = "没有匹配的控件。"; return; }
        var index = Array.IndexOf(matches, _selected?.Node);
        var next = (index + 1) % matches.Length;
        SelectNode(matches[next]);
        StatusText.Text = $"搜索“{query}”：第 {next + 1} / {matches.Length} 项";
    }
    private void CopyValue_Click(object sender, RoutedEventArgs e)
    {
        if (PropertiesGrid.SelectedItem is NodeProperty property) Copy(property.RawValue);
        else StatusText.Text = "请先在右侧选择一行属性。";
    }
    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) { StatusText.Text = "请先选择一个控件。"; return; }
        Copy(string.Join("\r\n", _selected.Node.Attributes.Select(pair => $"{pair.Key}: {pair.Value}")));
    }
    private void Copy(string text)
    {
        try { Clipboard.SetText(text); StatusText.Text = "已复制到剪贴板。"; }
        catch (Exception ex) { StatusText.Text = "复制失败：" + ex.Message; }
    }
    private void SaveXml_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot?.Xml is not { } xml) return;
        var dialog = new SaveFileDialog { Title = "保存当前布局 XML", Filter = "布局 XML (*.xml)|*.xml", FileName = $"layout-{_snapshot.FinishedAt:yyyyMMdd-HHmmss}.xml", AddExtension = true, DefaultExt = ".xml", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, xml, new UTF8Encoding(false)); StatusText.Text = "已保存：" + dialog.FileName; }
        catch (Exception ex) { StatusText.Text = "保存失败：" + ex.Message; }
    }
    private void SaveImage_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot?.Png is not { } png || _bitmap is null) return;
        var dialog = new SaveFileDialog { Title = "保存原始截图（不含选中边框）", Filter = "PNG 图片 (*.png)|*.png", FileName = $"layout-{_snapshot.FinishedAt:yyyyMMdd-HHmmss}.png", AddExtension = true, DefaultExt = ".png", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllBytes(dialog.FileName, png); StatusText.Text = "已保存原始截图：" + dialog.FileName; }
        catch (Exception ex) { StatusText.Text = "保存失败：" + ex.Message; }
    }
    private sealed record NodeProperty(string Name, string RawValue)
    {
        public string Value => RawValue.Length > 4000 ? RawValue[..4000] + "…（显示已截断，复制可获取完整值）" : RawValue;
    }
}

public sealed class LayoutVirtualizingPanel : VirtualizingStackPanel
{
    public void RevealIndex(int index) { if (index >= 0) BringIndexIntoView(index); }
}

public sealed class LayoutTreeItem(LayoutNode node) : INotifyPropertyChanged
{
    private bool _expanded, _selected;
    public LayoutNode Node { get; } = node;
    public IReadOnlyList<LayoutTreeItem> Children { get; internal set; } = [];
    public bool IsExpanded { get => _expanded; set { if (_expanded != value) { _expanded = value; OnChanged(); } } }
    public bool IsSelected { get => _selected; set { if (_selected != value) { _selected = value; OnChanged(); } } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
