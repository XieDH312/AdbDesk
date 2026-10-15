using AdbDesk.App;

internal static class MirrorToolbarPlacementTests
{
    // Catches putting any persistent toolbar inside the phone frame, ignoring taskbars, or losing negative monitor coordinates.
    internal static Task PlacementAsync()
    {
        var monitor = new ToolbarRect(0, 0, 1920, 1080);
        var work = new ToolbarRect(0, 0, 1920, 1040);
        var window = new ToolbarRect(100, 100, 500, 900);
        var layout = MirrorToolbarPlacement.Calculate(window, new(108, 130, 492, 892), monitor, work, 96);
        Equal(new ToolbarRect(508, 100, 620, 266), layout.Bounds, "portrait window docks to the right");
        Equal(false, layout.AutoHide, "exterior dock remains visible");
        Equal(false, layout.Bounds.Intersects(window), "persistent dock cannot cover the phone window");
        layout = MirrorToolbarPlacement.Calculate(new(1500, 100, 1918, 900), new(1508, 130, 1910, 892), monitor, work, 96);
        Equal(new ToolbarRect(1380, 100, 1492, 266), layout.Bounds, "right screen edge falls back to exterior left");
        layout = MirrorToolbarPlacement.Calculate(new(20, 200, 1900, 700), new(28, 230, 1892, 692), monitor, work, 96);
        Equal(new ToolbarRect(766, 128, 1154, 192), layout.Bounds, "wide window docks above when sides are unavailable");
        layout = MirrorToolbarPlacement.Calculate(new(20, 0, 1900, 700), new(28, 30, 1892, 692), monitor, work, 96);
        Equal(new ToolbarRect(766, 708, 1154, 772), layout.Bounds, "top-aligned wide window docks below");
        layout = MirrorToolbarPlacement.Calculate(monitor, monitor, monitor, work, 96);
        Equal(true, layout.AutoHide, "fullscreen never shows a permanent overlay");
        Equal(new ToolbarRect(766, 8, 1154, 72), layout.Bounds, "revealed fullscreen toolbar stays inside the target display");
        Equal(new ToolbarRect(0, 0, 1920, 4), layout.RevealZone, "only the top edge reveals a hidden toolbar");
        layout = MirrorToolbarPlacement.Calculate(new(-8, -8, 1928, 1048), new(0, 23, 1920, 1040), monitor, work, 96);
        Equal(true, layout.AutoHide, "maximized window without exterior space auto hides too");
        layout = MirrorToolbarPlacement.Calculate(new(-1800, -100, -1400, 800), new(-1790, -60, -1410, 790),
            new(-1920, -200, 0, 880), new(-1920, -160, 0, 840), 144);
        Equal(new ToolbarRect(-1388, -100, -1220, 149), layout.Bounds, "negative coordinates and 150 percent DPI remain in physical pixels");
        return Task.CompletedTask;
    }

    // Catches first-frame overlays, revealing from inside video, flicker while moving onto controls, and hiding owned help.
    internal static Task VisibilityAsync()
    {
        var state = new MirrorToolbarVisibility();
        Equal(false, state.Update(true, false, false, false, 0), "fullscreen starts hidden");
        Equal(true, state.Update(true, true, false, false, 1000), "top edge reveals the toolbar");
        Equal(true, state.Update(true, false, true, false, 1200), "pointer can move from edge onto buttons");
        Equal(true, state.Update(true, false, false, false, 1800), "brief grace period prevents flickering");
        Equal(false, state.Update(true, false, false, false, 2000), "leaving the toolbar hides after 800ms");
        Equal(true, state.Update(true, false, false, true, 2100), "help and settings remain usable while their owner is visible");
        Equal(true, state.Update(false, false, false, false, 2200), "outside dock remains visible without hovering");
        Equal(false, state.Update(true, false, false, false, 2300), "switching from docked mode does not flash an overlay");
        state.Update(true, true, false, false, 2400);
        state.Reset();
        Equal(false, state.Update(true, false, false, false, 2500), "switching apps clears reveal state");
        return Task.CompletedTask;
    }

    private static void Equal<T>(T expected, T actual, string message)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{message}: expected {expected}, got {actual}"); }
}
