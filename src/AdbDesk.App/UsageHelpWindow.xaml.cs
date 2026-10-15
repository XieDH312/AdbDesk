using System.Windows;
using System.Windows.Controls;

namespace AdbDesk.App;

public partial class UsageHelpWindow : Window
{
    private sealed record Entry(string Category, string Action, string Shortcut, string Note);
    private static readonly Entry[] Entries =
    [
        new("屏幕电源", "手机熄屏", "左 Alt + O", "关闭手机实体屏幕，电脑投屏和控制继续；也可点工具栏按钮。"),
        new("屏幕电源", "手机亮屏", "左 Alt + Shift + O", "恢复手机实体显示；也可点工具栏按钮。"),
        new("屏幕电源", "手机电源键", "左 Alt + P", "相当于手机电源键，可能锁屏；仅关闭实体显示请使用“手机熄屏”。"),
        new("窗口显示", "全屏 / 退出全屏", "F11 或左 Alt + F", "只切换电脑投屏窗口。"),
        new("窗口显示", "显示设置", "工具栏“显示设置”按钮", "工具栏在投屏外侧；全屏移到顶部边缘展开。主界面设备行也可打开。"),
        new("窗口显示", "原始像素大小", "左 Alt + G", "调整窗口到 1:1 像素大小。"),
        new("窗口显示", "适配窗口", "左 Alt + W / 双击黑边", "调整窗口以减少窗口留边，不会识别视频内部黑边。"),
        new("窗口显示", "向左 / 向右旋转画面", "左 Alt + ← / →", "旋转电脑显示画面。"),
        new("窗口显示", "请求手机旋转", "左 Alt + R", "请求手机横竖屏切换，是否响应取决于当前应用。"),
        new("触摸导航", "点击 / 滑动", "鼠标左键 / 拖动", "对应手机触摸操作。"),
        new("触摸导航", "返回", "鼠标右键 / 左 Alt + B", "熄屏时右键也可能触发亮屏。"),
        new("触摸导航", "回到桌面", "鼠标中键 / 左 Alt + H", "对应 Home。"),
        new("触摸导航", "最近任务", "左 Alt + S", "查看最近使用的应用。"),
        new("触摸导航", "菜单", "左 Alt + M", "向当前应用发送菜单键。"),
        new("通知栏", "展开通知", "左 Alt + N", "打开手机通知栏。"),
        new("通知栏", "展开快捷设置", "左 Alt 按住，N 按两次", "连续展开通知与快捷设置。"),
        new("通知栏", "收起通知栏", "左 Alt + Shift + N", "收起通知及快捷设置。"),
        new("音量", "音量增加 / 减少", "左 Alt + ↑ / ↓", "调整手机音量。"),
        new("文本", "复制 / 剪切", "左 Alt + C / X", "在手机应用支持的文本选择中使用。"),
        new("文本", "粘贴", "左 Alt + V", "将电脑剪贴板文本粘贴到手机输入位置。"),
        new("文本", "注入文本", "左 Alt + Shift + V", "将剪贴板文本作为输入事件发送，支持范围取决于输入方式。"),
        new("文件", "安装 APK", "把 APK 拖入投屏窗口", "安装到当前窗口对应的手机；结果在主界面运行日志中查看。"),
        new("调试", "重置视频采集", "左 Alt + Shift + R", "画面异常时重新初始化视频采集与编码。"),
        new("调试", "显示 / 关闭帧率", "左 Alt + I", "帧率信息写入主界面运行日志。"),
        new("窗口显示", "关闭当前投屏", "左 Alt + Q", "只关闭当前手机的投屏，不退出主程序。")
    ];

    public UsageHelpWindow()
    {
        InitializeComponent();
        ShortcutGrid.ItemsSource = Entries;
        ConnectionHelpText.Text =
            "1. 在手机上开启开发者选项和 USB 调试。\n\n" +
            "2. 首次连接时，解锁手机并确认“允许 USB 调试”。等待授权时可重新插拔数据线。\n\n" +
            "3. 部分品牌安装 APK 还需在手机确认“通过 USB 安装”等权限。\n\n" +
            "4. 主界面发起的 APK 安装结果在底部运行日志中，进行时可从“取消安装”菜单取消指定任务。拖入投屏窗口的安装由 scrcpy 管理，结果也写入运行日志，但不能从主界面取消。\n\n" +
            "5. 开启无线要求手机与电脑在同一局域网，电脑可使用网线。切换连接方式可能短暂断开投屏；结果确认前保持 USB 连接。\n\n" +
            "6. ADB 控制台支持命令库、自定义命令、输出及历史。程序不会自动停止共享 ADB 服务；只有明确执行 kill-server 或确认工具箱服务重启时才会停止。";
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (ShortcutGrid is null) return;
        var query = SearchInput.Text.Trim();
        ShortcutGrid.ItemsSource = Entries.Where(entry => $"{entry.Category} {entry.Action} {entry.Shortcut} {entry.Note}".Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText("使用快捷键前先点击投屏画面；Alt 指左 Alt，O 为字母。\n\n" +
                string.Join('\n', Entries.Select(entry => $"{entry.Category} | {entry.Action} | {entry.Shortcut} | {entry.Note}")));
            StatusText.Text = "投屏操作列表已复制。";
        }
        catch (Exception ex) { StatusText.Text = "复制失败：" + ex.Message; }
    }
}
