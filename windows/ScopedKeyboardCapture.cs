using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RemoteControl;

// Pure routing state; it does not read, save or log keyboard input.
internal sealed class RemoteKeyboardRouter(Action<ControlInput> send, Action<Keys> localCommand, Action? scopeLost = null)
{
    private readonly HashSet<int> physical = [];
    private readonly HashSet<int> forwarded = [];
    private readonly HashSet<int> ignoredUntilUp = [];
    private readonly HashSet<int> blockedUntilUp = [];
    internal bool Active { get; private set; }

    internal void Start(IEnumerable<int>? alreadyHeld = null)
    {
        physical.Clear(); forwarded.Clear(); ignoredUntilUp.Clear(); blockedUntilUp.Clear();
        if (alreadyHeld is not null)
            foreach (int key in alreadyHeld) { ignoredUntilUp.Add(key); physical.Add(key); }
        Active = true;
    }

    internal void Stop()
    {
        bool wasActive = Active;
        Active = false;
        physical.Clear(); forwarded.Clear(); ignoredUntilUp.Clear(); blockedUntilUp.Clear();
        if (wasActive) send(new ControlInput { Action = "releaseAll" });
    }

    internal static bool IsKeyboardKey(int key) => key is 0x03 or 0x08 or 0x09 or 0x0C or 0x0D ||
        key is >= 0x10 and <= 0x39 || key is >= 0x41 and <= 0x5D || key is >= 0x5F and <= 0x87 ||
        key is >= 0x90 and <= 0x96 || key is >= 0xA0 and <= 0xB7 || key is >= 0xBA and <= 0xC0 ||
        key is >= 0xDB and <= 0xDF || key is 0xE1 or 0xE2 || key is >= 0xF6 and <= 0xFB || key is 0xFD or 0xFE;

    internal bool Route(int key, bool extended, bool up, bool injected, bool inScope)
    {
        if (!Active || injected) return false;
        if (!inScope) { Stop(); scopeLost?.Invoke(); return false; }
        if (!IsKeyboardKey(key)) return false;
        if (blockedUntilUp.Contains(key))
        {
            if (up) { blockedUntilUp.Remove(key); physical.Remove(key); }
            return true;
        }
        if (ignoredUntilUp.Contains(key))
        {
            if (up) { ignoredUntilUp.Remove(key); physical.Remove(key); }
            return false;
        }
        bool wasHeld = physical.Contains(key);
        if (up) physical.Remove(key); else physical.Add(key);
        bool control = physical.Overlaps([0x11, 0xA2, 0xA3]);
        bool alt = physical.Overlaps([0x12, 0xA4, 0xA5]);
        if (!up && control && alt && key is 0x7B or 0x0D)
        {
            if (!wasHeld)
            {
                send(new ControlInput { Action = "releaseAll" });
                forwarded.Clear();
                foreach (int held in physical) blockedUntilUp.Add(held);
                if (key == 0x7B) Active = false;
                localCommand(Keys.Control | Keys.Alt | (key == 0x7B ? Keys.F12 : Keys.Enter));
            }
            return true;
        }
        // Windows owns the secure attention sequence. It cannot be sent as desktop input.
        if (!up && control && alt && key == 0x2E)
        {
            Stop(); scopeLost?.Invoke(); return false;
        }
        if (up && !forwarded.Remove(key)) return false;
        if (!up) forwarded.Add(key);
        send(new ControlInput { Action = up ? "keyUp" : "keyDown", VirtualKey = key, Extended = extended });
        return true;
    }
}

// The hook exists only during explicit capture in this window. Other applications and
// injected events always pass through; no clipboard or keystroke history is collected.
internal sealed class ScopedKeyboardCapture : IDisposable
{
    private readonly Func<bool> inScope;
    private readonly Action<string> failed;
    private readonly HookCallback callback;
    private readonly RemoteKeyboardRouter router;
    private IntPtr hook;

    internal ScopedKeyboardCapture(Func<bool> inScope, Action<ControlInput> send, Action<Keys> command, Action<string> failed, Action scopeLost)
    {
        this.inScope = inScope;
        this.failed = failed;
        router = new RemoteKeyboardRouter(send, command, scopeLost);
        callback = Handle;
    }

    internal bool Running => hook != IntPtr.Zero;
    internal bool Start()
    {
        if (Running) return true;
        if (!inScope()) return false;
        router.Start(InitialHeldKeys(key => (GetAsyncKeyState(key) & 0x8000) != 0));
        hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
        if (hook != IntPtr.Zero) return true;
        int error = Marshal.GetLastWin32Error();
        router.Stop();
        failed("无法启用直接键盘控制：" + new Win32Exception(error).Message);
        return false;
    }

    internal void Stop()
    {
        var current = hook; hook = IntPtr.Zero;
        try { router.Stop(); }
        finally { if (current != IntPtr.Zero) UnhookWindowsHookEx(current); }
    }

    internal static bool IsForeground(IntPtr window) => window != IntPtr.Zero && GetForegroundWindow() == window;
    // GetAsyncKeyState reports both generic and left/right modifier aliases. Low-level
    // events use the sided codes, so retaining generic aliases would leave phantom holds.
    internal static IEnumerable<int> InitialHeldKeys(Func<int, bool> isHeld) => Enumerable.Range(1, 254)
        .Where(key => key is not (0x10 or 0x11 or 0x12) && RemoteKeyboardRouter.IsKeyboardKey(key) && isHeld(key));

    private IntPtr Handle(int code, IntPtr message, IntPtr data)
    {
        if (code < 0) return CallNextHookEx(hook, code, message, data);
        int kind = unchecked((int)message.ToInt64());
        if (kind is not (0x100 or 0x101 or 0x104 or 0x105))
            return CallNextHookEx(hook, code, message, data);
        try
        {
            var key = Marshal.PtrToStructure<KeyboardData>(data);
            bool handled = router.Route((int)key.VirtualKey, (key.Flags & 0x01) != 0,
                kind is 0x101 or 0x105, (key.Flags & 0x12) != 0, inScope());
            if (handled) return new IntPtr(1);
        }
        catch
        {
            // Never let a managed exception escape into the Windows hook chain.
            try { Stop(); } catch { }
            try { failed("键盘控制已停止，请重新进入键鼠控制。"); } catch { }
        }
        return CallNextHookEx(hook, code, message, data);
    }

    public void Dispose() => Stop();
    private delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData
    {
        internal uint VirtualKey, ScanCode, Flags, Time;
        internal UIntPtr ExtraInfo;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int type, HookCallback callback, IntPtr module, uint thread);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
}
