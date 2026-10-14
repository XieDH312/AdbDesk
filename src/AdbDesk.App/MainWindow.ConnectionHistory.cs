using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Media;
using AdbDesk.Core;

namespace AdbDesk.App;

public partial class MainWindow
{
    private readonly ConnectionHistoryStore _connectionHistoryStore = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdbDesk", "connection-history.json"));
    private IReadOnlyList<string> _connectionHistory = [];

    private void InitializeConnectionHistory()
    {
        try
        {
            _connectionHistory = _connectionHistoryStore.Load();
            if (_connectionHistory.Count > 0) FillConnectionEndpoint(_connectionHistory[0]);
        }
        catch (Exception ex) { ShowQuickConnectResult($"无法读取连接历史，仍可手动连接：{ex.Message}", true); }
    }

    private void FillConnectionEndpoint(string endpoint)
    {
        var separator = endpoint.LastIndexOf(':');
        QuickConnectIp.Text = endpoint[..separator];
        QuickConnectPort.Text = endpoint[(separator + 1)..];
    }

    private void RememberConnection(string endpoint, string successMessage)
    {
        try { _connectionHistory = _connectionHistoryStore.Remember(endpoint); }
        catch (Exception ex)
        {
            // Persisting preferences must never turn a successful ADB connection into a failure.
            ShowQuickConnectResult($"{successMessage} 历史记录未保存：{ex.Message}", true);
        }
    }

    private void ConnectionHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing || _quickConnecting) return;
        var menu = new ContextMenu
        {
            PlacementTarget = ConnectionHistoryButton,
            Placement = PlacementMode.Custom,
            Style = (Style)FindResource("ConnectionHistoryMenuStyle"),
            CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [
                // The template reserves 8 DIPs for the shadow. Align the visible card's
                // right edge to the button, with a 6 DIP gap below (above if space is limited).
                new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width + 8, targetSize.Height - 2), PopupPrimaryAxis.Horizontal),
                new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width + 8, -popupSize.Height + 2), PopupPrimaryAxis.Horizontal)
            ]
        };
        PopulateConnectionHistoryMenu(menu);
        ConnectionHistoryButton.ContextMenu = menu;
        menu.Closed += (_, _) => ConnectionHistoryButton.ContextMenu = null;
        menu.IsOpen = true;
    }

    private void PopulateConnectionHistoryMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        if (_connectionHistory.Count == 0)
            menu.Items.Add(new MenuItem { Header = "暂无历史，成功连接后自动保存", IsEnabled = false });
        foreach (var endpoint in _connectionHistory)
        {
            var header = new Grid { MinHeight = 30 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock
            {
                Text = endpoint, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0), FontSize = 13, FontWeight = FontWeights.Medium,
                TextWrapping = TextWrapping.NoWrap,
                ToolTip = "填入 IP 和端口；无线调试端口变化时请修改后连接"
            });
            var delete = new Button
            {
                Content = new TextBlock { Text = "\uE74D", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14 },
                Style = (Style)FindResource("ConnectionHistoryDeleteButtonStyle"),
                ToolTip = "删除这条历史记录"
            };
            AutomationProperties.SetName(delete, $"删除历史记录 {endpoint}");
            Grid.SetColumn(delete, 1);
            header.Children.Add(delete);
            var item = new MenuItem { Header = header };
            AutomationProperties.SetName(item, $"选择历史地址 {endpoint}");
            delete.Click += (_, args) =>
            {
                args.Handled = true;
                if (_isClosing || _quickConnecting) return;
                try
                {
                    _connectionHistory = _connectionHistoryStore.Remove(endpoint);
                    PopulateConnectionHistoryMenu(menu);
                    ShowQuickConnectResult($"已删除历史记录：{endpoint}", false);
                }
                catch (Exception ex) { ShowQuickConnectResult($"删除历史失败：{ex.Message}", true); }
            };
            item.Click += (_, args) =>
            {
                if (!ReferenceEquals(args.OriginalSource, item) || _isClosing || _quickConnecting) return;
                FillConnectionEndpoint(endpoint);
                ShowQuickConnectResult("已填入历史地址，点击“连接”即可。", false);
                QuickConnectButton.Focus();
            };
            menu.Items.Add(item);
        }
        if (_connectionHistory.Count > 0)
        {
            menu.Items.Add(new Separator());
            var clear = new MenuItem
            {
                Header = "清空全部历史", Foreground = (Brush)FindResource("MutedBrush"),
                Padding = new Thickness(10, 9, 10, 9), FontSize = 12
            };
            clear.Click += (_, _) =>
            {
                if (_isClosing || _quickConnecting) return;
                try
                {
                    _connectionHistoryStore.Clear();
                    _connectionHistory = [];
                    ShowQuickConnectResult("连接历史已清空。", false);
                }
                catch (Exception ex) { ShowQuickConnectResult($"清空历史失败：{ex.Message}", true); }
            };
            menu.Items.Add(clear);
        }
    }
}
