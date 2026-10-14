using System.Text;
using System.Text.RegularExpressions;

namespace AdbDesk.Core;

/// <summary>A single invocation of the bundled adb, with an explicit device boundary.</summary>
public sealed record ConsoleCommand
{
    private static readonly HashSet<string> GlobalCommands = new(StringComparer.Ordinal)
    {
        "devices", "help", "version", "connect", "disconnect", "pair", "mdns",
        "start-server", "kill-server", "server-status", "host-features", "track-devices", "keygen"
    };

    public IReadOnlyList<string> Arguments { get; }
    public string Preview { get; }
    public bool IsGlobal { get; }
    public bool CanRemember { get; }
    public string? StandardInput { get; }
    public string HistoryText { get; }

    private ConsoleCommand(List<string> arguments, bool global, bool remember, string historyText, string? secret)
    {
        Arguments = arguments.AsReadOnly();
        IsGlobal = global;
        CanRemember = remember;
        StandardInput = secret is null ? null : secret + "\n";
        HistoryText = historyText;
        Preview = "adb " + string.Join(' ', arguments.Select(QuoteArgument)) + (secret is null ? "" : " [已隐藏]");
    }

    // Records normally print every public property, including StandardInput.
    public override string ToString() => Preview;

    public string Redact(string text) => StandardInput is null
        ? text
        : text.Replace(StandardInput.TrimEnd('\n'), "[已隐藏]", StringComparison.Ordinal);

    public static ConsoleCommand Parse(string input, string? selectedSerial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        if (input.Length > 8192) throw new ArgumentException("命令过长（最多 8192 字符）。", nameof(input));
        if (input.Any(c => c is '\r' or '\n' or '\0'))
            throw new ArgumentException("控制台只接受单条命令，不支持换行批处理。", nameof(input));

        var position = 0;
        var verb = ReadToken(input, ref position);
        if (verb.Equals("adb", StringComparison.OrdinalIgnoreCase) || verb.Equals("adb.exe", StringComparison.OrdinalIgnoreCase))
            verb = ReadToken(input, ref position);
        var explicitSerial = false;
        if (verb == "-s")
        {
            var serial = ReadToken(input, ref position);
            if (string.IsNullOrWhiteSpace(selectedSerial) || serial != selectedSerial)
                throw new ArgumentException("-s 必须与界面中选中的设备完全一致。", nameof(input));
            explicitSerial = true;
            verb = ReadToken(input, ref position);
        }
        if (string.IsNullOrWhiteSpace(verb) || verb.StartsWith('-'))
            throw new ArgumentException("请输入 ADB 子命令；不支持 -d、-e、-t 或服务器重定向参数，请使用界面选择设备。", nameof(input));
        if (!Regex.IsMatch(verb, "^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant))
            throw new ArgumentException("请输入单个 ADB 子命令，不支持主机终端或批处理语法。", nameof(input));
        if (verb is "exec-out" or "exec-in")
            throw new ArgumentException("控制台只支持文本输出；请用现有截图功能保存截图，不支持 exec-out/exec-in。", nameof(input));
        if (verb is "raw" or "server" or "inc-server" or "attach" or "detach")
            throw new ArgumentException("此命令不支持控制台的单设备目标约束，请使用常规设备命令或设备管理功能。", nameof(input));

        var global = GlobalCommands.Contains(verb);

        var commandArguments = new List<string> { verb };
        string? secret = null;
        string history;
        bool remember;
        if (verb == "shell")
        {
            var tail = input[position..].Trim();
            if (tail.Length == 0) throw new ArgumentException("不支持交互式 shell，请在 shell 后输入 Android 命令。", nameof(input));
            // adb concatenates shell argv for Android's shell. Keep the tail intact so
            // quotes, escapes and pipes keep their Android meaning. A single outer
            // host-style quoted argument is unwrapped exactly once.
            if (tail[0] is '\'' or '"')
            {
                var tailPosition = 0;
                var unwrapped = ReadToken(tail, ref tailPosition);
                if (string.IsNullOrWhiteSpace(tail[tailPosition..])) tail = unwrapped;
            }
            if (string.IsNullOrWhiteSpace(tail) || tail.StartsWith('-'))
                throw new ArgumentException("请输入非交互式 Android 命令；此控制台不支持 shell 的终端选项。", nameof(input));
            commandArguments.Add(tail);
            history = "shell " + tail;
            remember = IsRememberableShell(tail);
        }
        else
        {
            while (!string.IsNullOrWhiteSpace(input[position..])) commandArguments.Add(ReadToken(input, ref position));
            if (verb.StartsWith("wait-for-", StringComparison.Ordinal) && commandArguments.Count > 1)
                throw new ArgumentException("第一版只支持单独的 wait-for 等待命令；请在等待完成后另行输入要执行的命令。", nameof(input));
            if (commandArguments.Contains("--proto-binary"))
                throw new ArgumentException("控制台只支持文本输出，不支持二进制协议输出。", nameof(input));
            if (commandArguments.Count >= 2 &&
                ((verb == "forward" && commandArguments[1] is "--list" or "--remove-all") ||
                 (verb == "reconnect" && commandArguments[1] == "offline"))) global = true;
            if (verb == "pair")
            {
                if (commandArguments.Count != 3 || !Regex.IsMatch(commandArguments[2], "^[0-9]{6}$", RegexOptions.CultureInvariant))
                    throw new ArgumentException("请输入 pair IP:端口 六位配对码，或使用设备管理中的无线配对界面。", nameof(input));
                secret = commandArguments[2];
                commandArguments.RemoveAt(2);
            }
            history = string.Join(' ', commandArguments.Select(QuoteArgument)) + (secret is null ? "" : " [已隐藏]");
            remember = IsRememberableCommand(commandArguments);
        }
        if (global && explicitSerial)
            throw new ArgumentException("全局命令不接受 -s，请移除设备参数。", nameof(input));
        if (!global && string.IsNullOrWhiteSpace(selectedSerial))
            throw new ArgumentException("请先选择一台已就绪设备再运行设备命令。", nameof(selectedSerial));
        if (!global && selectedSerial!.Any(char.IsWhiteSpace))
            throw new ArgumentException("设备序列号不能包含空白字符。", nameof(selectedSerial));
        var arguments = new List<string>();
        if (!global) arguments.AddRange(["-s", selectedSerial!]);
        arguments.AddRange(commandArguments);
        return new ConsoleCommand(arguments, global, remember, history, secret);
    }

    private static bool IsRememberableShell(string tail)
    {
        // Exact query forms only. Never infer that arbitrary shell text is safe to
        // persist simply because its first word is a query command.
        if (tail is "getprop" or "wm size" or "wm density" or "pm list packages" or "pm list packages -3" or
            "pm list packages -s" or "dumpsys battery" or "dumpsys display" or "dumpsys window" or
            "dumpsys activity" or "dumpsys meminfo" or "ip addr" or "df -h" or "id" or "uname -a") return true;
        return Regex.IsMatch(tail, "^getprop [a-zA-Z0-9_.-]{1,128}$", RegexOptions.CultureInvariant);
    }

    private static bool IsRememberableCommand(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 3 && arguments[0] is "push" or "pull" &&
            IsPathArgument(arguments[1]) && IsPathArgument(arguments[2])) return true;
        if (arguments.Count == 3 && arguments[0] == "install" && arguments[1] == "-r" &&
            IsPathArgument(arguments[2])) return true;
        if (arguments.SequenceEqual(["logcat", "-v", "time"]) ||
            arguments.SequenceEqual(["logcat", "-d", "-v", "time"])) return true;
        if (arguments.Count == 1 && arguments[0] is "devices" or "help" or "version" or "start-server" or
            "kill-server" or "server-status" or "host-features" or "get-state" or "get-serialno" or
            "get-devpath" or "reboot" or "root" or "unroot" or "usb" or "logcat") return true;
        if (arguments.Count != 2) return false;
        var verb = arguments[0];
        var argument = arguments[1];
        return (verb is "install" or "bugreport" && IsPathArgument(argument)) ||
               (verb == "uninstall" && Regex.IsMatch(argument, "^[a-zA-Z][a-zA-Z0-9_]*(\\.[a-zA-Z][a-zA-Z0-9_]*)+$", RegexOptions.CultureInvariant)) ||
               (verb == "devices" && argument == "-l") ||
               (verb == "mdns" && argument is "check" or "services") ||
               (verb == "logcat" && argument is "-d" or "-c") ||
               (verb == "reboot" && argument is "bootloader" or "recovery") ||
               (verb == "tcpip" && ushort.TryParse(argument, out var port) && port > 0) ||
               (verb is "connect" or "disconnect" && Regex.IsMatch(argument, "^[a-zA-Z0-9_.:\\[\\]-]{1,255}$", RegexOptions.CultureInvariant));
    }

    private static bool IsPathArgument(string value) => !string.IsNullOrWhiteSpace(value) && !value.StartsWith('-');

    private static string ReadToken(string input, ref int position)
    {
        while (position < input.Length && char.IsWhiteSpace(input[position])) position++;
        var result = new StringBuilder();
        char quote = '\0';
        while (position < input.Length)
        {
            var c = input[position];
            if (quote == '\0' && char.IsWhiteSpace(c)) break;
            position++;
            if (c is '\'' or '"')
            {
                if (quote == c) { quote = '\0'; continue; }
                if (quote == '\0') { quote = c; continue; }
            }
            if (c == '\\' && quote != '\'')
            {
                var slashCount = 1;
                while (position < input.Length && input[position] == '\\') { slashCount++; position++; }
                if (position < input.Length && input[position] == '"')
                {
                    result.Append('\\', slashCount / 2);
                    position++;
                    if (slashCount % 2 != 0) result.Append('"');
                    else quote = quote == '"' ? '\0' : '"';
                }
                else result.Append('\\', slashCount);
                continue;
            }
            if (quote == '\0' && c is '&' or '|' or ';' or '<' or '>' or '`')
                throw new ArgumentException("不支持主机终端的管道、重定向或命令链；Android 管道请放在 shell 命令后。");
            result.Append(c);
        }
        if (quote != '\0') throw new ArgumentException("命令中的引号未闭合。");
        return result.ToString();
    }

    /// <summary>Quotes one non-shell argument for Parse, including Windows paths.</summary>
    public static string QuoteArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '&' or '|' or ';' or '<' or '>' or '`'))
            return value;
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }
}
