using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using AdbDesk.Core;
using Microsoft.Win32;

namespace AdbDesk.App;

public partial class NetworkCaptureWindow
{
    private string HistoryRoot => Path.Combine(_stateRoot, "NetworkCaptures");
    private string? _liveDirectory, _historyDirectory;
    private string? ViewDirectory => _historyDirectory ?? _liveDirectory;
    private bool _browseBusy, _fileBusy;
    private int _historyGeneration, _pageIndex, _pageCount;
    private Task? _fileTask;
    private CancellationTokenSource? _fileCancellation;
    private readonly Queue<string> _diagnosticMessages = new();

    private void UpdateHistoryControls()
    {
        var available = !IsCapturing && !_closing && !_browseBusy && !_fileBusy;
        HistoryPicker.IsEnabled = HistoryOpenButton.IsEnabled = HistoryRefreshButton.IsEnabled = available;
        CurrentHistoryButton.IsEnabled = available && _liveDirectory is not null;
        PreviousPageButton.IsEnabled = available && _historyDirectory is not null && _pageIndex > 0;
        NextPageButton.IsEnabled = available && _historyDirectory is not null && _pageIndex + 1 < _pageCount;
        HarExportButton.IsEnabled = JsonExportButton.IsEnabled = available && ViewDirectory is not null;
        HarExportButton.ToolTip = JsonExportButton.ToolTip = "导出当前会话的全部已保存记录，不受页面和筛选影响；请先停止抓包。";
    }

    private async Task RefreshHistoryAsync()
    {
        if (_browseBusy || IsCapturing || _closing) return;
        _browseBusy = true;
        var generation = ++_historyGeneration;
        UpdateControls();
        try
        {
            var items = await Task.Run(() => NetworkCaptureHistory.List(HistoryRoot));
            if (IsClosed || _closing || generation != _historyGeneration) return;
            var selected = (HistoryPicker.SelectedItem as NetworkCaptureHistoryItem)?.Directory;
            HistoryPicker.ItemsSource = items;
            HistoryPicker.SelectedItem = items.FirstOrDefault(i => i.Directory == selected) ?? items.FirstOrDefault();
        }
        catch (Exception ex) { if (!IsClosed && !_closing) StatusText.Text = "读取历史失败：" + ex.Message; }
        finally { _browseBusy = false; UpdateControls(); }
    }

    private async void RefreshHistory_Click(object sender, RoutedEventArgs e) => await RefreshHistoryAsync();
    private async void OpenHistory_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryPicker.SelectedItem is NetworkCaptureHistoryItem item) await LoadHistoryPageAsync(item.Directory, 0);
    }
    private async void CurrentHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_liveDirectory is { } directory) await LoadHistoryPageAsync(directory, 0);
    }
    private async void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        if (_historyDirectory is { } directory && _pageIndex > 0) await LoadHistoryPageAsync(directory, _pageIndex - 1);
    }
    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_historyDirectory is { } directory && _pageIndex + 1 < _pageCount) await LoadHistoryPageAsync(directory, _pageIndex + 1);
    }
    private async Task LoadHistoryPageAsync(string directory, int pageIndex)
    {
        if (_browseBusy || _fileBusy || IsCapturing || _closing) return;
        _browseBusy = true;
        var generation = ++_historyGeneration;
        _detailGeneration++;
        UpdateControls();
        StatusText.Text = "正在读取历史…";
        try
        {
            var page = await Task.Run(() => NetworkCaptureHistory.ReadPage(directory, pageIndex));
            if (IsClosed || _closing || generation != _historyGeneration) return;
            _historyDirectory = directory;
            _pageIndex = page.PageIndex; _pageCount = page.PageCount;
            _pending.Clear(); _entries.Clear();
            foreach (var entry in page.Entries) _entries.Add(entry);
            var displayPage = _pageCount == 0 ? 0 : _pageIndex + 1;
            ModeText.Text = $"历史 · {Path.GetFileName(directory)} · 第 {displayPage}/{_pageCount} 页";
            CountText.Text = $"会话共 {page.TotalCount} 条 · 本页 {_entries.Count} 条 · 筛选作用于本页；导出包含会话全部已保存记录";
            StatusText.Text = string.IsNullOrEmpty(page.Warning) ? "历史已打开，不会操作手机。" : page.Warning;
        }
        catch (Exception ex) { if (!IsClosed && !_closing) StatusText.Text = "读取历史失败：" + ex.Message; }
        finally { _browseBusy = false; UpdateControls(); }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = ViewDirectory ?? HistoryRoot;
            Directory.CreateDirectory(directory);
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(directory);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) { StatusText.Text = "打开文件夹失败：" + ex.Message; }
    }

    private async void HarExport_Click(object sender, RoutedEventArgs e)
    {
        if (!CanExport(out var directory)) return;
        var dialog = new SaveFileDialog { FileName = $"network-{DateTime.Now:yyyyMMdd-HHmmss}.har", Filter = "HAR 文件|*.har" };
        if (dialog.ShowDialog(this) != true || !CanExport(out _) || !CheckExportDestination(dialog.FileName)) return;
        await FileOperationAsync(token => NetworkCaptureExport.WriteHarAsync(dialog.FileName,
            NetworkCaptureHistory.ReadEntries(directory), directory, token), "已导出会话 HAR：" + dialog.FileName);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!CanExport(out var directory)) return;
        var dialog = new SaveFileDialog { FileName = $"network-{DateTime.Now:yyyyMMdd-HHmmss}.json", Filter = "JSON 文件|*.json" };
        if (dialog.ShowDialog(this) != true || !CanExport(out _) || !CheckExportDestination(dialog.FileName)) return;
        await FileOperationAsync(token =>
        {
            var temporary = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteString("CaptureDirectory", directory);
                    writer.WriteString("ExportedAt", DateTimeOffset.Now);
                    writer.WriteString("Note", "正文文件为会话目录中的相对路径；此 JSON 含有限预览。可用 HAR 导出包含正文的记录。");
                    writer.WriteStartArray("Entries");
                    foreach (var entry in NetworkCaptureHistory.ReadEntries(directory))
                    {
                        token.ThrowIfCancellationRequested();
                        JsonSerializer.Serialize(writer, entry);
                        writer.Flush();
                    }
                    writer.WriteEndArray(); writer.WriteEndObject();
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporary, dialog.FileName, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return Task.CompletedTask;
        }, "已导出会话 JSON：" + dialog.FileName);
    }

    private bool CanExport(out string directory)
    {
        directory = ViewDirectory ?? "";
        if (directory.Length != 0 && !IsCapturing && !_browseBusy && !_fileBusy && !_closing) return true;
        StatusText.Text = "请先停止抓包，再打开需要导出的会话。";
        return false;
    }
    private bool CheckExportDestination(string destination)
    {
        if (!Path.GetFullPath(destination).StartsWith(Path.GetFullPath(HistoryRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        StatusText.Text = "请选择历史目录以外的位置，避免覆盖原始抓包记录。";
        return false;
    }
    private async Task FileOperationAsync(Func<CancellationToken, Task> work, string success)
    {
        if (_fileBusy || _closing) return;
        _fileBusy = true;
        using var cancellation = new CancellationTokenSource();
        _fileCancellation = cancellation;
        UpdateControls();
        StatusText.Text = "正在处理文件…";
        try
        {
            _fileTask = Task.Run(() => work(cancellation.Token));
            await _fileTask;
            if (!IsClosed && !_closing) StatusText.Text = success;
        }
        catch (OperationCanceledException) { if (!IsClosed && !_closing) StatusText.Text = "文件操作已取消。"; }
        catch (Exception ex) { if (!IsClosed && !_closing) StatusText.Text = "文件操作失败：" + ex.Message; }
        finally { _fileTask = null; _fileCancellation = null; _fileBusy = false; UpdateControls(); }
    }
}
