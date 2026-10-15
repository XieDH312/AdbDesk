namespace AdbDesk.App;

internal readonly record struct ToolbarRect(int Left, int Top, int Right, int Bottom)
{
    internal int Width => Right - Left;
    internal int Height => Bottom - Top;
    internal bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    internal bool Intersects(ToolbarRect other) => Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;
}

internal sealed record ToolbarPlacement(ToolbarRect Bounds, bool AutoHide, ToolbarRect RevealZone);

internal static class MirrorToolbarPlacement
{
    internal static ToolbarPlacement Calculate(ToolbarRect window, ToolbarRect client, ToolbarRect monitor, ToolbarRect work, int dpi)
    {
        var scale = Math.Max(96, dpi) / 96d;
        int Pixels(double value) => (int)Math.Ceiling(value * scale);
        var gap = Pixels(8);
        var fullscreen = client.Left <= monitor.Left + 2 && client.Top <= monitor.Top + 2 &&
                         client.Right >= monitor.Right - 2 && client.Bottom >= monitor.Bottom - 2;
        var sideWidth = Pixels(112); var sideHeight = Pixels(166);
        var horizontalWidth = Math.Max(1, Math.Min(Pixels(388), work.Width - 2 * gap));
        var horizontalHeight = BarHeight(horizontalWidth, scale);
        if (!fullscreen)
        {
            if (sideHeight + 2 * gap <= work.Height)
            {
                var y = Math.Clamp(window.Top, work.Top + gap, work.Bottom - gap - sideHeight);
                var right = new ToolbarRect(window.Right + gap, y, window.Right + gap + sideWidth, y + sideHeight);
                if (Inside(right, work, gap)) return new(right, false, default);
                var left = new ToolbarRect(window.Left - gap - sideWidth, y, window.Left - gap, y + sideHeight);
                if (Inside(left, work, gap)) return new(left, false, default);
            }
            if (horizontalWidth + 2 * gap <= work.Width && horizontalHeight + 2 * gap <= work.Height)
            {
                var x = Math.Clamp(window.Left + (window.Width - horizontalWidth) / 2, work.Left + gap, work.Right - gap - horizontalWidth);
                var above = new ToolbarRect(x, window.Top - gap - horizontalHeight, x + horizontalWidth, window.Top - gap);
                if (Inside(above, work, gap)) return new(above, false, default);
                var below = new ToolbarRect(x, window.Bottom + gap, x + horizontalWidth, window.Bottom + gap + horizontalHeight);
                if (Inside(below, work, gap)) return new(below, false, default);
            }
        }
        // Only this temporary, edge-revealed layout may intersect the phone image.
        var visible = new ToolbarRect(Math.Max(client.Left, monitor.Left), Math.Max(client.Top, monitor.Top),
            Math.Min(client.Right, monitor.Right), Math.Min(client.Bottom, monitor.Bottom));
        if (visible.Width <= 0 || visible.Height <= 0) throw new ArgumentException("投屏窗口不在目标显示器内。");
        var width = Math.Max(1, Math.Min(Pixels(388), visible.Width - 2 * gap));
        var height = Math.Min(BarHeight(width, scale), visible.Height);
        var overlayX = visible.Left + (visible.Width - width) / 2;
        var overlayY = Math.Min(visible.Top + gap, visible.Bottom - height);
        var edgeTop = Math.Max(window.Top, monitor.Top);
        var edge = new ToolbarRect(visible.Left, edgeTop, visible.Right, edgeTop + Pixels(4));
        return new(new(overlayX, overlayY, overlayX + width, overlayY + height), true, edge);
    }

    private static bool Inside(ToolbarRect candidate, ToolbarRect work, int gap) =>
        candidate.Left >= work.Left + gap && candidate.Top >= work.Top + gap && candidate.Right <= work.Right - gap && candidate.Bottom <= work.Bottom - gap;

    private static int BarHeight(int pixelWidth, double scale)
    {
        var perRow = Math.Max(1, (int)((pixelWidth / scale - 14) / 86));
        return (int)Math.Ceiling((64 + ((4 + perRow - 1) / perRow - 1) * 34) * scale);
    }
}

internal sealed class MirrorToolbarVisibility
{
    private long? _lastReveal;
    internal bool Update(bool autoHide, bool atEdge, bool overShownToolbar, bool ownedWindowOpen, long now)
    {
        if (!autoHide) { Reset(); return true; }
        if (atEdge || overShownToolbar || ownedWindowOpen) { _lastReveal = now; return true; }
        return _lastReveal is { } previous && now >= previous && now - previous < 800;
    }
    internal void Reset() => _lastReveal = null;
}
