using System.Windows;

namespace AdbDesk.App;

public partial class InstallDetailWindow : Window
{
    public InstallDetailWindow(InstallRow task)
    {
        InitializeComponent();
        DataContext = task;
        Title = $"安装任务详情 · {task.FileName}";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
