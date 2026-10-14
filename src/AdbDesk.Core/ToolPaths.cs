namespace AdbDesk.Core;

public record ToolPaths(string Adb, string Scrcpy)
{
    public static ToolPaths Discover(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var candidates = new List<string>();
        var overrideDirectory = Environment.GetEnvironmentVariable("ADBDESK_TOOLS");
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            candidates.Add(Path.GetFullPath(overrideDirectory));
        }
        else
        {
            var basePath = Path.GetFullPath(baseDirectory);
            candidates.Add(Path.Combine(basePath, "tools", "scrcpy"));
            var current = new DirectoryInfo(basePath);
            while (current is not null && !Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                current = current.Parent;
            }
            if (current is not null)
                candidates.Add(Path.Combine(current.FullName, "tools", "scrcpy"));
        }

        foreach (var directory in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var adb = Path.Combine(directory, "adb.exe");
            var scrcpy = Path.Combine(directory, "scrcpy.exe");
            if (File.Exists(adb) && File.Exists(scrcpy))
            {
                return new ToolPaths(Path.GetFullPath(adb), Path.GetFullPath(scrcpy));
            }
        }

        var searched = string.Join(Environment.NewLine, candidates.Select(path => $"  {path}"));
        throw new FileNotFoundException($"找不到完整的 ADB/scrcpy 工具目录。已检查：{Environment.NewLine}{searched}");
    }
}
