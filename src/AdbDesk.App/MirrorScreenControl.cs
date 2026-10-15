using System.Runtime.InteropServices;

namespace AdbDesk.App;

internal interface IMirrorKeyTarget
{
    bool TryFocus(nint window);
    bool IsForeground(nint window);
    bool ModifiersPressed();
    uint Submit(nint window, IReadOnlyList<MirrorInputKey> keys);
    void Release(IReadOnlyList<ushort> keys);
}

internal sealed record MirrorScreenResult(bool Sent, string Message);
internal readonly record struct MirrorInputKey(ushort VirtualKey, bool Up);

internal static class MirrorScreenControl
{
    private const ushort Alt = 0xA4, Shift = 0xA0, O = 0x4F;

    internal static MirrorScreenResult Send(Func<nint> resolveWindow, bool turnOn, IMirrorKeyTarget? target = null)
    {
        target ??= new WindowsTarget();
        var window = resolveWindow();
        if (window == 0) return new(false, "投屏窗口已结束，请重新启动投屏。");
        if (target.ModifiersPressed()) return new(false, "请先松开 Ctrl、Alt、Shift 和 Windows 键，再点击按钮。");
        if (!target.TryFocus(window) || !target.IsForeground(window) || resolveWindow() != window)
            return new(false, "无法激活对应投屏窗口，请点击投屏画面后重试。");

        var chord = turnOn ? new[] { Alt, Shift, O } : new[] { Alt, O };
        var batch = chord.Select(key => new MirrorInputKey(key, false))
            .Concat(chord.Reverse().Select(key => new MirrorInputKey(key, true))).ToArray();
        if (resolveWindow() != window || !target.IsForeground(window))
            return new(false, "投屏窗口已变化，请点击对应投屏画面后重试。");
        // Submit a whole OS input batch: synthetic window messages do not update
        // GetKeyState, so SDL can otherwise drop Shift between pumps and invert power-on.
        var accepted = target.Submit(window, batch);
        if (accepted != batch.Length)
        {
            var held = new List<ushort>();
            foreach (var key in batch.Take((int)Math.Min(accepted, (uint)batch.Length)))
            {
                if (key.Up) held.Remove(key.VirtualKey); else held.Add(key.VirtualKey);
            }
            if (held.Count > 0) target.Release(held.AsEnumerable().Reverse().ToArray());
            return new(false, "输入未完整提交，请点击投屏画面后重试，或使用操作帮助中的快捷键。");
        }
        return new(true, turnOn ? "已发送手机亮屏请求。" : "已发送手机熄屏请求，电脑投屏继续。");
    }

    private sealed class WindowsTarget : IMirrorKeyTarget
    {
        private nint _window;
        private uint _process, _thread;
        public bool TryFocus(nint window)
        {
            if (!IsWindow(window)) return false;
            _window = window; _thread = GetWindowThreadProcessId(window, out _process);
            return _thread != 0 && _process != 0 && (IsForeground(window) || (SetForegroundWindow(window) && IsForeground(window)));
        }
        private bool IsOriginalWindow(nint window) => window == _window && IsWindow(window) &&
            GetWindowThreadProcessId(window, out var process) == _thread && process == _process;
        public bool IsForeground(nint window) => IsOriginalWindow(window) && GetForegroundWindow() == window;
        public bool ModifiersPressed() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);
        public uint Submit(nint window, IReadOnlyList<MirrorInputKey> keys)
        {
            // Recheck at the final native boundary; refuse instead of typing into another foreground app.
            if (!IsForeground(window) || ModifiersPressed()) return 0;
            return Send(keys);
        }
        public void Release(IReadOnlyList<ushort> keys) => Send(keys.Select(key => new MirrorInputKey(key, true)).ToArray());
        private static uint Send(IReadOnlyList<MirrorInputKey> keys)
        {
            var inputs = keys.Select(key => new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput
                { Key = key.VirtualKey, Flags = key.Up ? 2u : 0u, Extra = 0x41444244 } } }).ToArray();
            return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        }
        // INPUT includes the larger mouse union member: 40 bytes on x64, 28 on x86.
        [StructLayout(LayoutKind.Sequential)] private struct Input { internal uint Type; internal InputUnion Data; }
        [StructLayout(LayoutKind.Explicit)] private struct InputUnion
        {
            [FieldOffset(0)] internal KeyboardInput Keyboard;
            [FieldOffset(0)] internal MouseInput Mouse;
        }
        [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
        { internal ushort Key, Scan; internal uint Flags, Time; internal nuint Extra; }
        [StructLayout(LayoutKind.Sequential)] private struct MouseInput
        { internal int X, Y; internal uint Data, Flags, Time; internal nuint Extra; }
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
    }
}
