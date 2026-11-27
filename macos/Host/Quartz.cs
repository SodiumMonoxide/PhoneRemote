using System.Runtime.InteropServices;

namespace RemoteControl;

internal static class Quartz
{
    internal const string Library = "PhoneRemoteQuartz";

    [DllImport(Library, EntryPoint = "pr_permissions")]
    internal static extern int RequestPermissions();

    [DllImport(Library, EntryPoint = "pr_can_input")]
    internal static extern int CanInput();

    [DllImport(Library, EntryPoint = "pr_frame")]
    internal static extern IntPtr Frame(out int length);

    [DllImport(Library, EntryPoint = "pr_free")]
    internal static extern void Free(IntPtr bytes);

    [DllImport(Library, EntryPoint = "pr_key_is_down")]
    internal static extern int IsKeyDown(ushort code);

    [DllImport(Library, EntryPoint = "pr_key", CharSet = CharSet.Unicode)]
    internal static extern int Key(ushort code, int down, ulong modifier, ulong flags,
        [MarshalAs(UnmanagedType.LPWStr)] string? text, int length);

    [DllImport(Library, EntryPoint = "pr_mouse")]
    internal static extern int Mouse(int type, double x, double y, int explicitCount, ulong flags);

    [DllImport(Library, EntryPoint = "pr_scroll")]
    internal static extern int Scroll(int delta, ulong flags);
}
