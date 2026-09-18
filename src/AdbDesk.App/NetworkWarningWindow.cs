using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace AdbDesk.App;

internal static class NetworkWarningWindow
{
    public static bool Confirm(Window owner, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = new Window
        {
            Title = "检查局域网连接", Owner = owner, Width = 510, SizeToContent = SizeToContent.Height,
            MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 40),
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/AdbDesk.ico"))
        };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = Math.Max(120, SystemParameters.WorkArea.Height - 190), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14 }
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, IsDefault = true, MinWidth = 80 };
        var proceed = new Button { Content = "仍然连接", MinWidth = 100, Margin = new Thickness(10, 0, 0, 0) };
        cancel.Click += (_, _) => window.DialogResult = false;
        proceed.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(proceed);
        panel.Children.Add(buttons);
        window.Content = panel;
        using var registration = cancellationToken.Register(() => window.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (window.IsVisible) window.DialogResult = false;
        })));
        var accepted = window.ShowDialog() == true;
        cancellationToken.ThrowIfCancellationRequested();
        return accepted;
    }
}
