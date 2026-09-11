namespace AdbDesk.Core;

public sealed class AdbService(ToolPaths paths)
{
    private static readonly HashSet<string> KnownStates = new(StringComparer.Ordinal)
    {
        "device", "offline", "unauthorized", "recovery", "sideload", "bootloader", "host"
    };

    public async Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            paths.Adb,
            ["devices", "-l"],
            TimeSpan.FromSeconds(15),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var detail = FirstUsefulText(result.StandardError, result.StandardOutput);
            throw new InvalidOperationException($"ADB 设备查询失败（退出代码 {result.ExitCode}）：{detail}");
        }
        return ParseDevices(result.StandardOutput);
    }

    internal static IReadOnlyList<DeviceInfo> ParseDevices(string output)
    {
        var devices = new List<DeviceInfo>();
        using var reader = new StringReader(output ?? string.Empty);
        while (reader.ReadLine() is { } rawLine)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices attached", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith('*') || line.StartsWith("adb ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2) continue;
            var state = fields[1];
            if (state == "no" && fields.Length > 2 && fields[2] == "permissions") state = "no permissions";
            if (!KnownStates.Contains(state) && state != "no permissions") continue;

            var modelField = fields.FirstOrDefault(field => field.StartsWith("model:", StringComparison.Ordinal));
            var model = modelField is null ? string.Empty : modelField["model:".Length..].Replace('_', ' ');
            devices.Add(new DeviceInfo(fields[0], model, state));
        }
        return devices;
    }

    private static string FirstUsefulText(params string[] values) => values
        .Select(value => value.Trim())
        .FirstOrDefault(value => value.Length > 0) ?? "未知错误";
}
