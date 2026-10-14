using System.Globalization;
using System.Xml;
using System.Text.RegularExpressions;

namespace AdbDesk.Core;

public sealed record LayoutBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public long Area => (long)Width * Height;
    public bool Contains(double x, double y) => x >= Left && y >= Top && x < Right && y < Bottom;

    internal static LayoutBounds? Parse(string value)
    {
        var match = Regex.Match(value, @"^\[(-?\d+),(-?\d+)\]\[(-?\d+),(-?\d+)\]$", RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var numbers = new int[4];
        for (var i = 0; i < 4; i++)
            if (!int.TryParse(match.Groups[i + 1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]) || Math.Abs((long)numbers[i]) > 100000) return null;
        return numbers[2] > numbers[0] && numbers[3] > numbers[1] ? new(numbers[0], numbers[1], numbers[2], numbers[3]) : null;
    }
}

public sealed class LayoutNode
{
    private readonly List<LayoutNode> _children = [];
    public int Order { get; }
    public LayoutNode? Parent { get; }
    public int Depth => Parent is null ? 0 : Parent.Depth + 1;
    public IReadOnlyList<LayoutNode> Children => _children;
    public IReadOnlyDictionary<string, string> Attributes { get; }
    public LayoutBounds? Bounds { get; }
    public string Text => Value("text");
    public string ResourceId => Value("resource-id");
    public string ClassName => Value("class");
    public string PackageName => Value("package");
    public string Description => Value("content-desc");
    public string Label
    {
        get
        {
            var label = ClassName.Split('.').LastOrDefault();
            if (string.IsNullOrWhiteSpace(label)) label = "node";
            var detail = Text.Length > 0 ? Text : Description.Length > 0 ? Description : ResourceId;
            if (detail.Length > 100) detail = detail[..100] + "…";
            return detail.Length == 0 ? label : $"{label} · {detail.Replace('\n', ' ')}";
        }
    }
    internal LayoutNode(int order, LayoutNode? parent, IReadOnlyDictionary<string, string> attributes)
    {
        Order = order; Parent = parent; Attributes = attributes;
        Bounds = LayoutBounds.Parse(Value("bounds"));
        parent?._children.Add(this);
    }
    public string Value(string key) => Attributes.TryGetValue(key, out var value) ? value : "";
    public bool Matches(string query) => Attributes.Values.Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase));
}

public sealed record LayoutHierarchy(IReadOnlyList<LayoutNode> Roots, IReadOnlyList<LayoutNode> Nodes, int? Rotation)
{
    public const int MaximumCharacters = 4 * 1024 * 1024;
    public static LayoutHierarchy Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) throw new InvalidDataException("布局 XML 为空。");
        if (xml.Length > MaximumCharacters) throw new InvalidDataException("布局 XML 超过 4 MB 字符限制。");
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = MaximumCharacters, IgnoreComments = true, IgnoreWhitespace = true
        };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        var nodes = new List<LayoutNode>();
        var roots = new List<LayoutNode>();
        var ancestors = new Stack<LayoutNode>();
        var foundRoot = false;
        int? rotation = null;
        while (reader.Read())
        {
            if (reader.Depth > 128) throw new InvalidDataException("控件层级超过 128 层，无法安全显示。");
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Depth == 0)
                {
                    if (reader.Name != "hierarchy") throw new InvalidDataException("设备返回的内容不是 UI Automator 布局。");
                    foundRoot = true;
                    if (int.TryParse(reader.GetAttribute("rotation"), out var parsed) && parsed is >= 0 and <= 3) rotation = parsed;
                    continue;
                }
                if (reader.Name != "node") throw new InvalidDataException($"不支持的布局节点：{reader.Name}。");
                if (nodes.Count >= 20000) throw new InvalidDataException("控件超过 20000 个，请切换到较简单的页面后重试。");
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                while (reader.MoveToNextAttribute()) values[reader.Name] = reader.Value;
                reader.MoveToElement();
                var node = new LayoutNode(nodes.Count, ancestors.TryPeek(out var parent) ? parent : null, values);
                nodes.Add(node);
                if (node.Parent is null) roots.Add(node);
                if (!reader.IsEmptyElement) ancestors.Push(node);
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "node") ancestors.Pop();
        }
        if (!foundRoot) throw new InvalidDataException("没有找到布局根节点。");
        return new(roots, nodes, rotation);
    }

    public LayoutNode? HitTest(double x, double y) => Nodes.Where(node => node.Bounds?.Contains(x, y) == true)
        .OrderBy(node => node.Bounds!.Area).ThenByDescending(node => node.Depth).ThenByDescending(node => node.Order).FirstOrDefault();
}
