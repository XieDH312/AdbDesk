using System.IO;
using System.Text;

namespace AdbDesk.App;

internal static class UpdateDiagnostics
{
    private const int Limit = 48000;
    private static readonly object Sync = new();
    private static string? _lastReport;
    internal static string DirectoryPath => Path.Combine(UpdatePreferences.DataDirectory, "Updates");
    private static string ReportPath => Path.Combine(DirectoryPath, "last-update-diagnostic.txt");

    internal static string Record(string phase, string detail)
    {
        var text = $"时间：{DateTimeOffset.Now:O}\n当前版本：{UpdatePreferences.CurrentVersion}（{UpdatePreferences.CurrentVersionCode}）\n阶段：{phase}\n系统：{Environment.OSVersion}\n\n{detail}";
        if (text.Length > Limit) text = text[..Limit] + "\n[诊断已截断]";
        lock (Sync)
        {
            _lastReport = text;
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                File.WriteAllText(ReportPath, text, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _lastReport += "\n诊断文件写入失败：" + ex.Message; }
            return _lastReport;
        }
    }

    internal static string ReadReport()
    {
        lock (Sync)
        {
            if (_lastReport is not null) return _lastReport;
            if (File.Exists(ReportPath))
            {
                using var reader = new StreamReader(ReportPath);
                var buffer = new char[Limit];
                var count = reader.ReadBlock(buffer, 0, buffer.Length);
                return new string(buffer, 0, count);
            }
            return $"AdbDesk {UpdatePreferences.CurrentVersion}（{UpdatePreferences.CurrentVersionCode}）\n尚未记录更新异常。";
        }
    }
}
