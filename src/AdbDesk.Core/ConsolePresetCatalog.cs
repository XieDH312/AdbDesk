using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdbDesk.Core;

public sealed record ConsoleParameter(string Label, string Kind = "text", string DefaultValue = "", string Hint = "")
{
    public string Browse => Kind is "apk" or "file" or "folder" or "package" ? Kind : "";

    public void Validate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"请填写{Label}。");
        if (value.Length > 4096 || value.Any(c => char.IsControl(c)))
            throw new ArgumentException($"{Label}过长或包含换行等控制字符。");
        var valid = Kind switch
        {
            "package" => Match(value, "^[A-Za-z][A-Za-z0-9_]*(?:\\.[A-Za-z][A-Za-z0-9_]*)*$"),
            "permission" => Match(value, "^[A-Za-z][A-Za-z0-9_]*(?:\\.[A-Za-z][A-Za-z0-9_]*)+$"),
            "component" => Match(value, "^[A-Za-z][A-Za-z0-9_.]*/[A-Za-z0-9_.$]+$"),
            "port" => Number(value, 1, 65535), "pid" => Number(value, 1, int.MaxValue),
            "user" => Number(value, 0, int.MaxValue),
            "coordinate" => Number(value, 0, 65535), "duration" => Number(value, 1, 180),
            "density" => Number(value, 72, 1200),
            "size" => Match(value, "^[1-9][0-9]{1,4}x[1-9][0-9]{1,4}$"),
            "code" => Match(value, "^[0-9]{6}$"),
            "endpoint" => Uri.TryCreate("tcp://" + value, UriKind.Absolute, out var endpoint) &&
                IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out _) && endpoint.Port is > 0 and <= 65535 &&
                endpoint.AbsolutePath == "/" && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0 && endpoint.UserInfo.Length == 0,
            "url" => Uri.TryCreate(value, UriKind.Absolute, out _),
            "remote-path" => value.StartsWith('/') && value.Length > 1,
            "file" or "folder" or "apk" => !value.StartsWith('-'),
            "text" => true,
            _ => false
        };
        if (!valid) throw new ArgumentException($"{Label}格式不正确。{Hint}");
    }

    private static bool Match(string value, string pattern) => Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant);
    private static bool Number(string value, int min, int max) => Match(value, "^[0-9]+$") &&
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= min && number <= max;
}

public sealed record ConsolePreset
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Command { get; init; }
    public string Keywords { get; init; } = "";
    public string Requirements { get; init; } = "已授权 ADB；实际支持以设备系统为准";
    public string Confirmation { get; init; } = "";
    public string Source { get; init; } = "";
    public bool Global { get; init; }
    public bool Continuous { get; init; }
    public ConsoleParameter[] Parameters { get; init; } = [];

    public bool Matches(string query)
    {
        var searchable = $"{Category} {Title} {Description} {Command} {Keywords} {Requirements}";
        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(word => searchable.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    public string BuildCommand(IReadOnlyList<string> values, bool validate = true)
    {
        if (values.Count != Parameters.Length) throw new ArgumentException("命令参数数量不正确。");
        if (validate)
            for (var i = 0; i < values.Count; i++) Parameters[i].Validate(values[i].Trim());
        // Substitute once: a parameter containing {1} must remain literal data.
        return Regex.Replace(Command, @"\{([0-9]+)\}", match =>
        {
            var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var value = values[index].Trim();
            if (value.Length == 0) value = $"<{Parameters[index].Label}>";
            return Command.StartsWith("shell ", StringComparison.Ordinal)
                ? "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'"
                : ConsoleCommand.QuoteArgument(value);
        });
    }
}

public static class ConsolePresetCatalog
{
    public static IReadOnlyList<ConsolePreset> All { get; } = Load();

    private static IReadOnlyList<ConsolePreset> Load()
    {
        using var stream = typeof(ConsolePresetCatalog).Assembly.GetManifestResourceStream("AdbDesk.Core.ConsolePresets.json")
            ?? throw new InvalidOperationException("缺少内置 ADB 命令库。");
        var presets = JsonSerializer.Deserialize<ConsolePreset[]>(stream) ?? [];
        if (presets.Length == 0 || presets.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != presets.Length)
            throw new InvalidOperationException("内置 ADB 命令库为空或有重复标识。");
        foreach (var preset in presets)
        {
            var used = Regex.Matches(preset.Command, @"\{([0-9]+)\}").Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order().ToArray();
            if (!used.SequenceEqual(Enumerable.Range(0, preset.Parameters.Length)))
                throw new InvalidOperationException("内置命令参数不完整：" + preset.Id);
        }
        return Array.AsReadOnly(presets);
    }
}
