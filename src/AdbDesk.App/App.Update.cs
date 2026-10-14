using System.Diagnostics;
using System.Windows;
using AdbDesk.Core;

namespace AdbDesk.App;

public partial class App
{
    private async Task ApplyUpdateAsync(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("更新参数无效。");
            var result = await new UpdateInstaller(UpdatePreferences.CurrentVersion, UpdatePreferences.CurrentVersionCode).ApplyAsync(args[1], CancellationToken.None);
            if (!result.Success)
            {
                UpdateDiagnostics.Record("替换更新", result.Message + "\n日志：" + result.LogPath);
                MessageBox.Show(result.Message + "\n\n日志：" + result.LogPath, "AdbDesk 更新未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            if ((result.Success || result.RolledBack) && !string.IsNullOrWhiteSpace(result.TargetExePath))
            {
                Process.Start(new ProcessStartInfo(result.TargetExePath)
                {
                    UseShellExecute = false, WorkingDirectory = System.IO.Path.GetDirectoryName(result.TargetExePath)!
                });
            }
            Shutdown(result.Success ? 0 : 1);
        }
        catch (Exception ex)
        {
            UpdateDiagnostics.Record("更新助手", ex.ToString());
            MessageBox.Show("更新未完成：" + ex.Message + "\n请保留更新缓存和原程序目录。", "AdbDesk 更新", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
