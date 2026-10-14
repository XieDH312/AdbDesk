using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using AdbDesk.Core;

namespace AdbDesk.App;

public partial class MainWindow
{
    private readonly UpdateService _updateService = new();
    private AvailableUpdate? _availableUpdate;
    private PreparedUpdate? _pendingUpdate;

    private void InitializeUpdates()
    {
        UpdateButton.Content = $"v{UpdatePreferences.CurrentVersion} · 检查更新";
        UpdateButton.ToolTip = $"当前版本 v{UpdatePreferences.CurrentVersion} · versionCode {UpdatePreferences.CurrentVersionCode}";
        _ = CheckUpdatesInBackgroundAsync();
    }

    private async Task CheckUpdatesInBackgroundAsync()
    {
        var preferences = UpdatePreferences.Load();
        if (!preferences.AutoCheck) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token);
            _availableUpdate = await _updateService.CheckAsync(UpdatePreferences.CurrentVersion, UpdatePreferences.CurrentVersionCode, preferences.IncludePrereleases, _lifetime.Token);
            if (_isClosing) return;
            if (_availableUpdate is not null) UpdateButton.Content = $"v{UpdatePreferences.CurrentVersion} → v{_availableUpdate.Manifest.Version}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            UpdateDiagnostics.Record("启动检查", ex.ToString());
            if (!_isClosing) UpdateButton.ToolTip = "后台检查未完成，点击重试：" + ex.Message;
        }
    }

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing) return;
        var window = new UpdateWindow(_availableUpdate) { Owner = this };
        if (window.ShowDialog() != true || window.Prepared is null) return;
        _pendingUpdate = window.Prepared;
        Close();
    }

    private static async Task StartUpdateHelperAsync(PreparedUpdate prepared)
    {
        try { await WaitForUpdateHelperAsync(prepared); }
        catch
        {
            await File.WriteAllTextAsync(prepared.PlanPath + ".cancel", "Handoff canceled");
            throw;
        }
    }

    private static async Task WaitForUpdateHelperAsync(PreparedUpdate prepared)
    {
        var start = new ProcessStartInfo(prepared.HelperExePath)
        {
            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(prepared.HelperExePath)!,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(prepared.PlanPath);
        using var helper = Process.Start(start) ?? throw new IOException("无法启动更新程序，当前版本将保持运行。");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (helper.HasExited) throw new IOException("更新程序提前退出，当前版本将保持运行。请检查 Windows 是否拦截了程序。");
            if (File.Exists(prepared.ReadyPath))
            {
                try
                {
                    using var ready = JsonDocument.Parse(await File.ReadAllTextAsync(prepared.ReadyPath));
                    if (ready.RootElement.GetProperty("nonce").GetString() == prepared.Nonce &&
                        ready.RootElement.GetProperty("version").GetString() == prepared.Version) return;
                }
                catch (IOException) { }
                catch (JsonException) { }
            }
            await Task.Delay(150);
        }
        // A helper that did not acknowledge must never apply after a later normal exit.
        await File.WriteAllTextAsync(prepared.PlanPath + ".cancel", "Ready timeout");
        try { if (!helper.HasExited) helper.Kill(entireProcessTree: false); } catch { }
        throw new IOException("更新程序未及时就绪，已取消本次替换。当前版本仍可使用。");
    }
}
