using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AdbDesk.Core;

public sealed record LogCaptureOptions(string Serial, string ParentFolder, bool CrashOnly = false,
    string? PackageName = null, int UserId = 0)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ParentFolder);
        _ = BuildCommand(Serial, CrashOnly, null);
        if (UserId < 0 || UserId > 21474) throw new ArgumentOutOfRangeException(nameof(UserId));
        if (PackageName is not null && !Regex.IsMatch(PackageName, @"\A[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*\z"))
            throw new ArgumentException("请输入有效的 Android 包名。", nameof(PackageName));
    }

    public static ConsoleCommand BuildCommand(string serial, bool crashOnly, int? uid)
    {
        if (uid < 0) throw new ArgumentOutOfRangeException(nameof(uid));
        return ConsoleCommand.Parse("logcat -v threadtime " + (crashOnly ? "-b crash" : "-b main -b system -b crash") +
            (uid is { } value ? " --uid=" + value.ToString(CultureInfo.InvariantCulture) : "") + " *:V", serial);
    }

    public static int ParseUid(string output, string packageName, int userId)
    {
        var values = Regex.Matches(output, @"(?m)^package:(?<package>\S+)\s+uid:(?<uid>\d+)\s*$")
            .Where(m => m.Groups["package"].Value == packageName)
            .Select(m => int.TryParse(m.Groups["uid"].Value, CultureInfo.InvariantCulture, out var uid) ? uid : -1).Distinct().ToArray();
        if (values.Length != 1 || values[0] < 0 || values[0] / 100000 != userId)
            throw new InvalidDataException("无法唯一确认该包在所选 Android 用户下的 UID；请确认包名和用户编号，或选择全部设备日志。");
        return values[0];
    }
}

public sealed record LogCaptureLine(string Text, char? Level, string Tag, int? Pid)
{
    private static readonly Regex ThreadTime = new(@"^\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d+\s+(?<pid>\d+)\s+\d+\s+(?<level>[VDIWEFA])\s+(?<tag>.*?):\s?", RegexOptions.Compiled);
    public static LogCaptureLine Parse(string text)
    {
        var match = ThreadTime.Match(text);
        return match.Success
            ? new(text, match.Groups["level"].Value[0], match.Groups["tag"].Value.Trim(), int.TryParse(match.Groups["pid"].Value, out var pid) ? pid : null)
            : new(text, null, "", null);
    }
    public bool Matches(char minimumLevel, string tag, string keyword) =>
        (minimumLevel == 'V' || (Level is { } level && Rank(level) >= Rank(minimumLevel))) &&
        (tag.Length == 0 || Tag.Contains(tag, StringComparison.OrdinalIgnoreCase)) &&
        (keyword.Length == 0 || Text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    private static int Rank(char value) => "VDIWEFA".IndexOf(value);
}

/// <summary>Preview only. Raw capture is written before decoding, without these display limits.</summary>
public sealed class LogCaptureLineDecoder
{
    private readonly StringBuilder _pending = new();
    private LogCaptureLine? _previous;
    public void Append(string chunk, Action<LogCaptureLine> emit)
    {
        foreach (var c in chunk)
        {
            if (c == '\n') { Emit(emit); continue; }
            _pending.Append(c);
            if (_pending.Length >= 16384) Emit(emit);
        }
    }
    public void Complete(Action<LogCaptureLine> emit) { if (_pending.Length > 0) Emit(emit); }
    private void Emit(Action<LogCaptureLine> emit)
    {
        var text = _pending.ToString().TrimEnd('\r'); _pending.Clear();
        var line = LogCaptureLine.Parse(text);
        if (line.Level is null && _previous is not null && text.Length > 0 && char.IsWhiteSpace(text[0]))
            line = line with { Level = _previous.Level, Tag = _previous.Tag, Pid = _previous.Pid };
        else if (line.Level is null) _previous = null;
        if (line.Level is not null) _previous = line;
        emit(line);
    }
}
