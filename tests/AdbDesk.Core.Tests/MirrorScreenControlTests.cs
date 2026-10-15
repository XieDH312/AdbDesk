using AdbDesk.App;

internal static class MirrorScreenControlTests
{
    // Catches splitting Shift from O, missing releases, and input submitted after losing the owned target.
    internal static Task CommandsAsync()
    {
        var target = new Target();
        var result = MirrorScreenControl.Send(() => (nint)11, false, target);
        Equal(true, result.Sent, "screen-off request is submitted");
        Equal("164:False|79:False|79:True|164:True", target.Batch, "off includes all presses and releases");
        Equal(1, target.Calls, "the entire off chord is one submission");
        Equal((nint)11, target.Window, "off submits to the verified window");
        target = new Target();
        result = MirrorScreenControl.Send(() => (nint)22, true, target);
        Equal(true, result.Sent, "screen-on request is submitted");
        Equal("164:False|160:False|79:False|79:True|160:True|164:True", target.Batch, "Shift and O are in the same full input batch");
        Equal(1, target.Calls, "the on chord cannot be split by caller-side resolver calls");
        target = new Target { CanFocus = false };
        Equal(false, MirrorScreenControl.Send(() => (nint)11, false, target).Sent, "failed activation does not type anywhere");
        Equal(0, target.Calls, "no keys sent after failed activation");
        target = new Target { HasModifiers = true };
        Equal(false, MirrorScreenControl.Send(() => (nint)11, false, target).Sent, "physical modifiers require release first");
        Equal(0, target.Calls, "do not release user-held keys");
        target = new Target();
        var lookups = 0;
        Equal(false, MirrorScreenControl.Send(() => ++lookups <= 2 ? (nint)11 : (nint)22, false, target).Sent, "replacement before batch submission aborts command");
        Equal(0, target.Calls, "replaced target receives no input");
        target = new Target();
        Equal(false, MirrorScreenControl.Send(() => 0, false, target).Sent, "closed mirror cannot submit a screen command");
        Equal(0, target.Calls, "zero handle is never used for keyboard messages");
        target = new Target { Accepted = 2 };
        Equal(false, MirrorScreenControl.Send(() => (nint)11, true, target).Sent, "partial submission is not success");
        Equal("160|164", target.Released, "partial submission releases only the accepted held keys, in reverse order");
        target = new Target { Accepted = 0 };
        Equal(false, MirrorScreenControl.Send(() => (nint)11, true, target).Sent, "blocked input is not success");
        Equal("", target.Released, "zero accepted events do not release any physical keys");
        return Task.CompletedTask;
    }

    private sealed class Target : IMirrorKeyTarget
    {
        internal bool CanFocus = true, HasModifiers;
        internal int Accepted = -1, Calls;
        internal nint Window;
        internal string Batch = "", Released = "";
        public bool TryFocus(nint window) => CanFocus;
        public bool IsForeground(nint window) => CanFocus;
        public bool ModifiersPressed() => HasModifiers;
        public uint Submit(nint window, IReadOnlyList<MirrorInputKey> keys)
        {
            Calls++; Window = window;
            Batch = string.Join('|', keys.Select(key => $"{key.VirtualKey}:{key.Up}"));
            return (uint)(Accepted < 0 ? keys.Count : Accepted);
        }
        public void Release(IReadOnlyList<ushort> keys) => Released = string.Join('|', keys);
    }
    private static void Equal<T>(T expected, T actual, string name)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{name}: expected {expected}, got {actual}"); }
}
