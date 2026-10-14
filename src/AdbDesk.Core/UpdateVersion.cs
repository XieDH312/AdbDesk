using System.Numerics;
using System.Text.RegularExpressions;

namespace AdbDesk.Core;

public sealed class UpdateVersion : IComparable<UpdateVersion>
{
    private static readonly Regex Pattern = new(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly BigInteger[] numbers;
    private readonly string[] prerelease;
    private readonly string text;
    private UpdateVersion(string text, Match match)
    {
        this.text = text;
        numbers = Enumerable.Range(1, 3).Select(i => BigInteger.Parse(match.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
    }
    public bool IsPrerelease => prerelease.Length != 0;
    public static UpdateVersion Parse(string value) => TryParse(value, out var result) ? result! : throw new FormatException("版本号必须是有效的 SemVer，例如 1.2.0 或 1.2.0-beta.1。");
    public static bool TryParse(string? value, out UpdateVersion? result)
    {
        result = null;
        if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
        var match = Pattern.Match(value);
        if (!match.Success || match.Groups[4].Value.Split('.').Any(p => p.Length > 1 && p[0] == '0' && p.All(char.IsAsciiDigit))) return false;
        result = new UpdateVersion(value, match);
        return true;
    }
    public int CompareTo(UpdateVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < 3; i++) { var comparison = numbers[i].CompareTo(other.numbers[i]); if (comparison != 0) return comparison; }
        if (IsPrerelease != other.IsPrerelease) return IsPrerelease ? -1 : 1;
        for (var i = 0; i < Math.Min(prerelease.Length, other.prerelease.Length); i++)
        {
            var left = prerelease[i]; var right = other.prerelease[i];
            var leftNumeric = left.All(char.IsAsciiDigit); var rightNumeric = right.All(char.IsAsciiDigit);
            var comparison = leftNumeric && rightNumeric ? BigInteger.Parse(left).CompareTo(BigInteger.Parse(right)) : leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1) : string.CompareOrdinal(left, right);
            if (comparison != 0) return comparison;
        }
        return prerelease.Length.CompareTo(other.prerelease.Length);
    }
    public override string ToString() => text;
}
