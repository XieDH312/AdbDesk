using System.Globalization;
using System.Text.RegularExpressions;

namespace AdbDesk.Core;

/// <summary>Parses only one active package and its explicitly selected Android user.</summary>
internal static class InstalledAppDetailsParser
{
    public static InstalledAppDetails Parse(string raw, InstalledApp app, IReadOnlyList<string> paths)
    {
        var lines = raw.Split('\n').Select(line => new Line(line.Length - line.TrimStart(' ', '\t').Length, line.Trim()))
            .Where(line => line.Text.Length > 0).ToArray();
        var rootIndent = lines.Length == 0 ? 0 : lines.Min(line => line.Indent);
        var packages = new List<Line[]>();
        // Updated system apps can appear again under Hidden/Disabled system packages.
        // A package-name match alone is not sufficient to identify the active version.
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Indent != rootIndent || lines[i].Text != "Packages:") continue;
            var sectionEnd = EndOfBlock(lines, i);
            var section = lines[(i + 1)..sectionEnd];
            foreach (var index in DirectChildren(section))
            {
                if (!Regex.IsMatch(section[index].Text, @"^Package \[" + Regex.Escape(app.PackageName) + @"\](?:\s|$)")) continue;
                packages.Add(section[(index + 1)..EndOfBlock(section, index)]);
            }
            i = sectionEnd - 1;
        }
        if (packages.Count != 1)
            throw new IOException("无法识别唯一的活动包详情，应用可能已卸载或系统不支持此输出格式。");

        var block = packages[0];
        var direct = DirectChildren(block).ToArray();
        var metadata = direct.Select(index => block[index].Text).ToArray();
        var requested = new HashSet<string>(StringComparer.Ordinal);
        var install = new Dictionary<string, bool?>(StringComparer.Ordinal);
        var installOverrides = new Dictionary<string, bool?>(StringComparer.Ordinal);
        Dictionary<string, bool?>? userInstall = null;
        var runtime = new Dictionary<string, bool?>(StringComparer.Ordinal);
        var requestedSections = 0;
        var requestedComplete = true;
        var userSections = 0;
        string? userInstallTime = null;

        foreach (var index in direct)
        {
            var line = block[index];
            if (line.Text == "requested permissions:")
            {
                requestedSections++;
                var entries = block[(index + 1)..EndOfBlock(block, index)];
                foreach (var entry in DirectChildren(entries).Select(j => entries[j].Text))
                {
                    // AOSP annotates hard/soft restricted requests; this is not a grant state.
                    var request = Regex.Match(entry, @"^(?<name>[^\s:]+)(?::\s*restricted=(?:true|false))?$");
                    if (request.Success) requested.Add(request.Groups["name"].Value);
                    else requestedComplete = false;
                }
            }
            else if (line.Text == "install permissions:")
                ReadGrants(block[(index + 1)..EndOfBlock(block, index)], install, app.UserId, installOverrides);
            else if (Regex.IsMatch(line.Text, @"^User " + app.UserId.ToString(CultureInfo.InvariantCulture) + @":(?:\s|$)"))
            {
                if (++userSections > 1) throw new IOException("包详情包含重复的 Android 用户信息，请刷新后重试。");
                var userBlock = block[(index + 1)..EndOfBlock(block, index)];
                var userChildren = DirectChildren(userBlock).ToArray();
                userInstallTime = Scalar(userChildren.Select(j => userBlock[j].Text), "firstInstallTime");
                foreach (var j in userChildren)
                {
                    if (userBlock[j].Text == "runtime permissions:")
                        ReadGrants(userBlock[(j + 1)..EndOfBlock(userBlock, j)], runtime, app.UserId);
                    else if (userBlock[j].Text == "install permissions:")
                    {
                        userInstall ??= new(StringComparer.Ordinal);
                        ReadGrants(userBlock[(j + 1)..EndOfBlock(userBlock, j)], userInstall, app.UserId);
                    }
                }
            }
        }
        // AOSP emits the system-user state without a userId and only prints other users
        // when their state differs. Selected-user overrides must win regardless of order.
        foreach (var (name, value) in installOverrides) install[name] = value;
        // Some system output may scope install grants per user. A present selected-user
        // section replaces the legacy package-level section, including when it is empty.
        install = userInstall ?? install;
        return new(Scalar(metadata, "versionName"), LongToken(metadata, "versionCode"), paths, raw)
        {
            MinSdk = IntToken(metadata, "minSdk"),
            TargetSdk = IntToken(metadata, "targetSdk"),
            FirstInstallTime = userInstallTime ?? Scalar(metadata, "firstInstallTime"),
            FirstInstallTimeIsUserScoped = userInstallTime is not null,
            LastUpdateTime = Scalar(metadata, "lastUpdateTime"),
            RequestedPermissionsAvailable = requestedSections == 1 && requestedComplete,
            Permissions = requested.Concat(install.Keys).Concat(runtime.Keys).Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => new InstalledAppPermission(name, requested.Contains(name), install.GetValueOrDefault(name), runtime.GetValueOrDefault(name))).ToArray()
        };
    }

    private static void ReadGrants(Line[] entries, Dictionary<string, bool?> grants, int selectedUser,
        Dictionary<string, bool?>? userOverrides = null)
    {
        foreach (var index in DirectChildren(entries))
        {
            var entry = Regex.Match(entries[index].Text, @"^(?<name>[^\s:]+):(?<attributes>.*)$");
            if (!entry.Success) continue;
            var attributes = entry.Groups["attributes"].Value;
            var target = grants;
            var userMarkers = Regex.Matches(attributes, @"\buserId\b", RegexOptions.IgnoreCase);
            if (userMarkers.Count > 0)
            {
                var user = Regex.Match(attributes, @"(?:^|[\s,])userId=(?<value>[0-9]+)(?=[\s,]|$)");
                // Unknown, malformed, repeated and other-user annotations must never
                // be mistaken for the unqualified package default.
                if (userMarkers.Count != 1 || !user.Success ||
                    !int.TryParse(user.Groups["value"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var userId) ||
                    userId != selectedUser) continue;
                target = userOverrides ?? grants;
            }
            var values = Regex.Matches(attributes, @"(?:^|[\s,])granted=(?<value>true|false)(?=[\s,]|$)")
                .Select(match => match.Groups["value"].Value).Distinct(StringComparer.Ordinal).ToArray();
            bool? value = values.Length == 1 ? values[0] == "true" : null;
            var name = entry.Groups["name"].Value;
            if (target.TryGetValue(name, out var previous) && previous != value) value = null;
            target[name] = value;
        }
    }

    private static string? Scalar(IEnumerable<string> lines, string name)
    {
        var values = lines.Where(line => line.StartsWith(name + "=", StringComparison.Ordinal))
            .Select(line => line[(name.Length + 1)..].Trim()).Distinct(StringComparer.Ordinal).ToArray();
        return values.Length == 1 && values[0].Length > 0 && values[0] != "null" && values[0] != "unknown" ? values[0] : null;
    }

    private static long? LongToken(IEnumerable<string> lines, string name)
    {
        var values = lines.SelectMany(line => Regex.Matches(line, @"(?:^|\s)" + name + @"=(?<value>[^\s]+)"))
            .Select(match => match.Groups["value"].Value).Distinct(StringComparer.Ordinal).ToArray();
        return values.Length == 1 && long.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static int? IntToken(IEnumerable<string> lines, string name)
    {
        var value = LongToken(lines, name);
        return value is >= 0 and <= int.MaxValue ? (int)value : null;
    }

    private static IEnumerable<int> DirectChildren(Line[] lines)
    {
        if (lines.Length == 0) yield break;
        var indent = lines.Min(line => line.Indent);
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Indent == indent) yield return i;
    }

    private static int EndOfBlock(Line[] lines, int start)
    {
        var end = start + 1;
        while (end < lines.Length && lines[end].Indent > lines[start].Indent) end++;
        return end;
    }

    private sealed record Line(int Indent, string Text);
}
