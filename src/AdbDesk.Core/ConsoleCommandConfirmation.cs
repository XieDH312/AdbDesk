using System.Text.RegularExpressions;

namespace AdbDesk.Core;

/// <summary>Confirmation hints for common manually entered and recalled operations, not a shell security policy.</summary>
public static class ConsoleCommandConfirmation
{
    public static string? Describe(ConsoleCommand command)
    {
        var args = command.Arguments.Skip(command.IsGlobal ? 0 : 2).ToArray();
        var verb = args[0];
        if (verb == "kill-server") return "将停止电脑上的 ADB 服务，所有手机连接和投屏可能中断。";
        if (verb == "uninstall") return "将卸载指定应用；未指定 -k 时应用数据也会删除。";
        if (verb == "disconnect") return "将断开无线 ADB 连接，对应的投屏和任务可能中断。";
        if (verb is "forward" or "reverse" && args.Any(a => a is "--remove" or "--remove-all"))
            return "将移除端口转发，依赖这些规则的调试、抓包或开发服务可能中断。";
        if (verb is "reboot" or "root" or "unroot" or "usb" or "tcpip" or "reconnect")
            return "将重启设备、调试服务或切换连接，现有连接和投屏可能中断。";
        if (verb == "logcat" && args.Contains("-c")) return "将清空手机日志缓冲区。";
        if (verb != "shell") return null;
        var tail = args[1];
        if (Regex.IsMatch(tail, @"^\s*(?:pm|cmd\s+package)\s+(?:clear|uninstall|disable(?:-user)?|enable|grant|revoke)\b", RegexOptions.CultureInvariant))
            return "将修改应用数据、安装状态或权限；清空数据和卸载可能无法恢复。";
        if (Regex.IsMatch(tail, @"^\s*(?:rm\b|settings\s+(?:put|delete|reset)\b|wm\s+(?:size|density)\s+\S+|am\s+force-stop\b)", RegexOptions.CultureInvariant))
            return "将删除文件、修改设备设置或停止应用，请确认参数和目标设备。";
        if (Regex.IsMatch(tail, @"^\s*cmd\s+appops\s+(?:set|reset)\b", RegexOptions.CultureInvariant))
            return "将修改应用的 AppOps 行为，可能影响应用功能。";
        if (Regex.IsMatch(tail, @"^\s*am\s+broadcast\b", RegexOptions.CultureInvariant))
            return "将发送广播，接收器可能触发应用操作。";
        if (Regex.IsMatch(tail, @"^\s*screenrecord\s+(?!--help\b)", RegexOptions.CultureInvariant))
            return "将在手机录制屏幕并写入文件，同名目标可能被覆盖。";
        if (Regex.IsMatch(tail, @"^\s*dumpsys\s+gfxinfo\s+\S+\s+reset\b", RegexOptions.CultureInvariant))
            return "将重置该应用当前的绘制统计，请先保存需要的结果。";
        return null;
    }
}
