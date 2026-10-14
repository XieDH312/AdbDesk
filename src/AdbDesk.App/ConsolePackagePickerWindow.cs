using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdbDesk.Core;

namespace AdbDesk.App;

/// <summary>A read-only package chooser bound to the serial captured when it opens.</summary>
internal sealed class ConsolePackagePickerWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBox _filter = new() { Height = 34, Padding = new Thickness(9, 0, 9, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ListBox _list = new() { DisplayMemberPath = nameof(InstalledApp.PackageName), BorderThickness = new Thickness(1), Margin = new Thickness(0, 10, 0, 10) };
    private readonly TextBlock _status = new() { Text = "正在读取应用列表…", Margin = new Thickness(0, 0, 0, 8), FontSize = 12 };
    private readonly Button _choose = new() { Content = "填入包名", IsEnabled = false, MinWidth = 90 };
    private IReadOnlyList<InstalledApp> _apps = [];
    private int? _user;
    private bool _closed;
    public string? SelectedPackage { get; private set; }
    public int? SelectedUserId { get; private set; }

    public ConsolePackagePickerWindow(ToolPaths paths, string serial, string targetLabel)
    {
        Title = "选择已安装应用";
        Width = 600; Height = 480; MinWidth = 420; MinHeight = 330;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var panel = new DockPanel { Margin = new Thickness(18) };
        var title = new TextBlock { Text = targetLabel, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(title, Dock.Top); panel.Children.Add(title);
        DockPanel.SetDock(_filter, Dock.Top); panel.Children.Add(_filter);
        _filter.ToolTip = "按包名搜索；包含当前 Android 用户的系统和第三方应用";
        System.Windows.Automation.AutomationProperties.SetName(_filter, "搜索手机应用包名");
        var footer = new StackPanel();
        footer.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        actions.Children.Add(_choose); actions.Children.Add(cancel);
        footer.Children.Add(actions);
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        panel.Children.Add(_list); Content = panel;
        _filter.TextChanged += (_, _) => RefreshList();
        _list.SelectionChanged += (_, _) => _choose.IsEnabled = _list.SelectedItem is InstalledApp;
        _list.MouseDoubleClick += (_, args) =>
        {
            if (args.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(_list, source) is ListBoxItem)
                Choose();
        };
        _list.KeyDown += (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; Choose(); } };
        _choose.Click += (_, _) => Choose();
        Loaded += async (_, _) =>
        {
            var token = _lifetime.Token;
            try
            {
                var snapshot = await new InstalledAppsService(paths).LoadAsync(serial, token);
                if (_closed) return;
                _apps = snapshot.Apps;
                _user = snapshot.UserId;
                RefreshList();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!_closed) _status.Text = $"读取失败：{ex.Message}。关闭后可手动填写包名。"; }
        };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    private void RefreshList()
    {
        var selected = (_list.SelectedItem as InstalledApp)?.PackageName;
        var query = _filter.Text.Trim();
        var matched = _apps.Where(a => a.PackageName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        _list.ItemsSource = matched;
        _list.SelectedItem = matched.FirstOrDefault(a => a.PackageName == selected);
        if (_user is not null) _status.Text = $"Android 用户 {_user} · {matched.Length}/{_apps.Count} 个应用。这里只填入参数，返回控制台后再执行。";
    }

    private void Choose()
    {
        if (_closed || _list.SelectedItem is not InstalledApp app) return;
        SelectedPackage = app.PackageName;
        SelectedUserId = app.UserId;
        DialogResult = true;
    }
}
