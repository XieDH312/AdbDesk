using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AdbDesk.Core;
using Microsoft.Win32;

namespace AdbDesk.App;

public sealed record CaptureJsonNode(string Label, IReadOnlyList<CaptureJsonNode> Children);

public partial class NetworkCaptureWindow
{
    private int _detailGeneration;
    private string _requestText = "", _responseText = "", _requestFormatted = "", _responseFormatted = "";
    private IReadOnlyList<CaptureJsonNode> _requestTree = [], _responseTree = [];
    private const int DisplayTextLimit = 512 * 1024;

    private bool MatchesFilter(object item) => item is NetworkCaptureEntry entry && NetworkCaptureView.Matches(entry,
        SearchInput.Text.Trim(), DomainInput.Text.Trim(), FilterTag(MethodFilter), FilterTag(StatusFilter),
        ContentTypeInput.Text.Trim(), FailuresOnly.IsChecked == true, SlowOnly.IsChecked == true ? 1000 : null);
    private static string FilterTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
    private void Search_Changed(object sender, TextChangedEventArgs e) => RefreshFilter();
    private void Filter_Changed(object sender, RoutedEventArgs e) => RefreshFilter();
    private void RefreshFilter() { if (_ready) CollectionViewSource.GetDefaultView(_entries).Refresh(); }
    private void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        SearchInput.Clear(); DomainInput.Clear(); ContentTypeInput.Clear();
        MethodFilter.SelectedIndex = StatusFilter.SelectedIndex = 0;
        FailuresOnly.IsChecked = SlowOnly.IsChecked = false;
    }

    private async void Request_Changed(object sender, SelectionChangedEventArgs e)
    {
        var generation = ++_detailGeneration;
        _requestText = _responseText = _requestFormatted = _responseFormatted = "";
        _requestTree = _responseTree = [];
        RenderBodyViews();
        if (RequestList.SelectedItem is not NetworkCaptureEntry entry)
        {
            DetailsOutput.Clear(); RequestHeadersOutput.Clear(); ResponseHeadersOutput.Clear();
            return;
        }
        DetailsOutput.Text = Overview(entry);
        RequestHeadersOutput.Text = HeaderText(entry.RequestHeaderItems, entry.RequestHeaders, entry.RequestHeadersComplete);
        ResponseHeadersOutput.Text = HeaderText(entry.ResponseHeaderItems, entry.ResponseHeaders, entry.ResponseHeadersComplete);
        var directory = ViewDirectory;
        try
        {
            var bodies = await Task.Run(() =>
            {
                var request = directory is null ? entry.RequestBody : NetworkCaptureView.ReadBodyText(entry, false, directory);
                var response = directory is null ? entry.ResponseBody : NetworkCaptureView.ReadBodyText(entry, true, directory);
                return (request, response, requestFormatted: NetworkCaptureView.FormatJson(request),
                    responseFormatted: NetworkCaptureView.FormatJson(response), requestTree: JsonTree(request), responseTree: JsonTree(response));
            });
            if (IsClosed || _closing || generation != _detailGeneration) return;
            _requestText = bodies.request; _responseText = bodies.response;
            _requestFormatted = bodies.requestFormatted; _responseFormatted = bodies.responseFormatted;
            _requestTree = bodies.requestTree; _responseTree = bodies.responseTree;
            RenderBodyViews();
        }
        catch (Exception ex)
        {
            if (IsClosed || _closing || generation != _detailGeneration) return;
            _requestText = _requestFormatted = "无法读取完整正文：" + ex.Message + "\n\n有限预览：\n" + entry.RequestBody;
            _responseText = _responseFormatted = "无法读取完整正文：" + ex.Message + "\n\n有限预览：\n" + entry.ResponseBody;
            RenderBodyViews();
        }
    }

    private static string HeaderText(IReadOnlyList<NetworkHeader>? headers, string fallback, bool complete) =>
        (complete ? "" : "[头部不完整或未验证]\n") + (headers is null ? fallback : string.Join("\n", headers.Select(h => h.Name + ": " + h.Value)));
    private static string BodySummary(CapturedBody? body) => body is null ? "未保存 / 旧版预览" :
        $"{body.SavedBytes:N0} / {body.TotalBytes:N0} 字节 · {(body.Complete ? "完整" : "不完整")} {body.Error}";
    private static string Overview(NetworkCaptureEntry entry)
    {
        var text = new StringBuilder().AppendLine(entry.Method + " " + entry.Url)
            .AppendLine(entry.RequestTargetComplete ? "" : "警告：请求方法或 URL 超过保存上限，已截断；不能生成可复现 cURL。")
            .AppendLine($"时间：{entry.Time.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff}　协议：{entry.HttpVersion}")
            .AppendLine($"状态：{entry.StatusCode?.ToString() ?? "尚无响应"}　耗时：{entry.DurationMs?.ToString("F0") ?? "未知"} ms　{(entry.Finished ? "已结束" : "尚未完成 / 中断")}")
            .AppendLine("请求正文：" + BodySummary(entry.RequestCapture))
            .AppendLine("响应正文：" + BodySummary(entry.ResponseCapture))
            .AppendLine(entry.Error).AppendLine()
            .AppendLine("URL 参数（保留同名参数）：");
        if (Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) && uri.Query.Length > 1)
        {
            foreach (var part in uri.Query[1..].Split('&'))
            {
                var pair = part.Split('=', 2);
                text.AppendLine(Uri.UnescapeDataString(pair[0].Replace('+', ' ')) + " = " +
                    (pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : ""));
            }
        }
        else text.AppendLine("无");
        return text.AppendLine().AppendLine("正文文本显示最多 512 Ki 字符；JSON 树最多 2000 节点 / 32 层。保存正文可获取磁盘上的原始字节。")
            .AppendLine("导出整个会话不受当前筛选影响；复制 cURL 使用 PowerShell 7，并依赖本地正文文件。").ToString();
    }

    private void BodyMode_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) RenderBodyViews(); }
    private void RenderBodyViews()
    {
        if (BodyMode is null || RequestBodyOutput is null) return;
        var tree = BodyMode.SelectedIndex == 2;
        RequestJsonTree.Visibility = ResponseJsonTree.Visibility = tree ? Visibility.Visible : Visibility.Collapsed;
        RequestBodyOutput.Visibility = ResponseBodyOutput.Visibility = tree ? Visibility.Collapsed : Visibility.Visible;
        RequestBodyOutput.Text = DisplayText(BodyMode.SelectedIndex == 0 ? _requestText : _requestFormatted);
        ResponseBodyOutput.Text = DisplayText(BodyMode.SelectedIndex == 0 ? _responseText : _responseFormatted);
        RequestJsonTree.ItemsSource = _requestTree; ResponseJsonTree.ItemsSource = _responseTree;
    }
    private static string DisplayText(string value) => value.Length <= DisplayTextLimit ? value :
        value[..DisplayTextLimit] + "\n[界面显示已截断；请保存正文或导出 HAR 查看已保存内容]";

    private static IReadOnlyList<CaptureJsonNode> JsonTree(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
            var budget = 2000;
            return [BuildJsonNode("$", document.RootElement, 0, ref budget)];
        }
        catch (JsonException) { return [new("内容不是完整 JSON；请切换到原始文本查看。", [])]; }
    }
    private static CaptureJsonNode BuildJsonNode(string key, JsonElement value, int depth, ref int budget)
    {
        budget--;
        var children = new List<CaptureJsonNode>();
        var container = value.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        if (container && depth < 32 && budget > 0)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject())
                {
                    if (budget <= 0) { children.Add(new("… 节点显示已达上限", [])); break; }
                    children.Add(BuildJsonNode(property.Name, property.Value, depth + 1, ref budget));
                }
            }
            else
            {
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (budget <= 0) { children.Add(new("… 节点显示已达上限", [])); break; }
                    children.Add(BuildJsonNode($"[{index++}]", item, depth + 1, ref budget));
                }
            }
        }
        else if (container) children.Add(new("… 请在文本视图查看更深内容", []));
        var label = container ? value.ValueKind == JsonValueKind.Array ? $"[{value.GetArrayLength()} 项]" : "{对象}" : value.GetRawText();
        static string Short(string v) => v.Length > 220 ? v[..220] + "…" : v;
        return new(Short(key) + ": " + Short(label), children);
    }

    private void FindDetail_Click(object sender, RoutedEventArgs e)
    {
        var term = DetailSearchInput.Text;
        if (string.IsNullOrEmpty(term)) return;
        if (BodyMode.SelectedIndex == 2 && DetailTabs.SelectedIndex is 2 or 4) BodyMode.SelectedIndex = 1;
        var box = DetailTabs.SelectedIndex switch { 1 => RequestHeadersOutput, 2 => RequestBodyOutput, 3 => ResponseHeadersOutput, 4 => ResponseBodyOutput, _ => DetailsOutput };
        var start = Math.Min(box.Text.Length, box.SelectionStart + box.SelectionLength);
        var position = box.Text.IndexOf(term, start, StringComparison.OrdinalIgnoreCase);
        if (position < 0) position = box.Text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (position < 0) { StatusText.Text = "当前详情页未找到该内容（搜索范围为已显示文本）。"; return; }
        box.Focus(); box.Select(position, term.Length);
        box.ScrollToLine(box.GetLineIndexFromCharacterIndex(position));
    }

    private async void CopyCurl_Click(object sender, RoutedEventArgs e)
    {
        if (RequestList.SelectedItem is not NetworkCaptureEntry entry || ViewDirectory is not { } directory) return;
        var generation = _detailGeneration;
        try
        {
            var command = await Task.Run(() => NetworkCaptureExport.CreatePowerShellCurl(entry, directory));
            if (IsClosed || _closing || generation != _detailGeneration) return;
            Copy(command);
            StatusText.Text = "已复制 cURL（PowerShell 7）；依赖本地会话正文文件，请保留历史。";
        }
        catch (Exception ex) { if (!IsClosed && !_closing) StatusText.Text = "无法生成可复现的 cURL：" + ex.Message; }
    }

    private async void SaveBody_Click(object sender, RoutedEventArgs e)
    {
        if (_fileBusy || _closing || RequestList.SelectedItem is not NetworkCaptureEntry entry || ViewDirectory is not { } directory) return;
        if (DetailTabs.SelectedIndex is not (2 or 4)) { StatusText.Text = "请先选择“请求正文”或“响应正文”页。"; return; }
        var response = DetailTabs.SelectedIndex == 4;
        var body = response ? entry.ResponseCapture : entry.RequestCapture;
        if (body is null || body.FileName is null) { StatusText.Text = "此正文没有可保存的原始文件。"; return; }
        var dialog = new SaveFileDialog { FileName = (response ? "response" : "request") + (body.Complete ? ".bin" : "-partial.bin"), Filter = "原始正文|*.bin|所有文件|*.*" };
        if (dialog.ShowDialog(this) != true || !CheckExportDestination(dialog.FileName)) return;
        await FileOperationAsync(async token =>
        {
            var bytes = NetworkCaptureHistory.ReadBody(directory, body);
            var temporary = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, token);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, dialog.FileName, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }, body.Complete ? "已保存原始正文（保留传输时的压缩编码）。" : "已保存部分正文；原始捕获不完整。" );
    }
}
