using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace RemoteControl;

internal sealed class ControlInput
{
    public string? Action { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public int? Delta { get; set; }
    public string? Text { get; set; }
    public string? Key { get; set; }
    public int? VirtualKey { get; set; }
    public bool Extended { get; set; }
}

internal sealed class Desktop
{
    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseWheel = 0x0800;
    private const uint MouseAbsolute = 0x8000;
    private const uint KeyboardInput = 1;
    private const uint KeyExtended = 0x0001;
    private const uint KeyUp = 0x0002;
    private const uint KeyUnicode = 0x0004;
    private const uint DesktopReadObjects = 0x0001;
    private const int UserObjectName = 2;
    private const uint CursorShowing = 1;
    private const uint DrawIconNormal = 3;
    private static readonly int InputSize = Marshal.SizeOf<NativeInput>();

    private static readonly Dictionary<string, (Keys Key, Keys Modifier)> KeyBindings = new(StringComparer.Ordinal)
    {
        ["Enter"] = (Keys.Enter, Keys.None),
        ["Escape"] = (Keys.Escape, Keys.None),
        ["Backspace"] = (Keys.Back, Keys.None),
        ["Delete"] = (Keys.Delete, Keys.None),
        ["Tab"] = (Keys.Tab, Keys.None),
        ["Space"] = (Keys.Space, Keys.None),
        ["ArrowUp"] = (Keys.Up, Keys.None),
        ["ArrowDown"] = (Keys.Down, Keys.None),
        ["ArrowLeft"] = (Keys.Left, Keys.None),
        ["ArrowRight"] = (Keys.Right, Keys.None),
        ["Home"] = (Keys.Home, Keys.None),
        ["End"] = (Keys.End, Keys.None),
        ["PageUp"] = (Keys.PageUp, Keys.None),
        ["PageDown"] = (Keys.PageDown, Keys.None),
        ["Ctrl+A"] = (Keys.A, Keys.ControlKey),
        ["Ctrl+C"] = (Keys.C, Keys.ControlKey),
        ["Ctrl+V"] = (Keys.V, Keys.ControlKey),
        ["Ctrl+X"] = (Keys.X, Keys.ControlKey),
        ["Ctrl+Z"] = (Keys.Z, Keys.ControlKey),
        ["Alt+Tab"] = (Keys.Tab, Keys.Menu),
        ["Win+D"] = (Keys.D, Keys.LWin),
        ["F5"] = (Keys.F5, Keys.None)
    };

    private bool leftButtonDown;
    private readonly Dictionary<ushort, bool> heldKeys = new();
    private readonly HashSet<ushort> borrowedKeys = new();
    private readonly List<NativeInput> pendingReleases = new();
    private readonly Func<NativeInput[], int> sendInput;
    private readonly Func<bool> interactive;
    private readonly Func<int, bool> isKeyDown;

    // Tests supply an in-memory backend; the production path keeps all native input here.
    internal Desktop(Func<NativeInput[], int>? sendInput = null, Func<bool>? interactive = null,
        Func<int, bool>? isKeyDown = null)
    {
        this.sendInput = sendInput ?? (events => (int)SendInput((uint)events.Length, events, InputSize));
        this.interactive = interactive ?? IsInteractiveDesktop;
        this.isKeyDown = isKeyDown ?? (key => GetAsyncKeyState(key) < 0);
    }

    internal bool HasHeldInputs => leftButtonDown || heldKeys.Count != 0 || pendingReleases.Count != 0;

    public byte[] CaptureFrame()
    {
        EnsureInteractiveDesktop();
        Rectangle bounds = Screen.PrimaryScreen?.Bounds
            ?? throw new InvalidOperationException("找不到 Windows 主显示器。");
        using var screenshot = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(screenshot))
        {
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            DrawCursor(graphics, bounds);
        }

        using var frame = new MemoryStream();
        double scale = Math.Min(1600.0 / bounds.Width, 1000.0 / bounds.Height);
        if (scale >= 1)
        {
            screenshot.Save(frame, ImageFormat.Jpeg);
        }
        else
        {
            int width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
            int height = Math.Max(1, (int)Math.Round(bounds.Height * scale));
            using var resized = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(resized))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                graphics.DrawImage(screenshot, new Rectangle(0, 0, width, height));
            }
            resized.Save(frame, ImageFormat.Jpeg);
        }

        EnsureInteractiveDesktop();
        return frame.ToArray();
    }

    public bool Apply(ControlInput command)
    {
        return command.Action switch
        {
            "move" or "click" or "doubleClick" or "rightClick" or "down" or "up" => ApplyPointer(command),
            "scroll" => ApplyScroll(command),
            "text" => ApplyText(command.Text),
            "key" => ApplyKey(command.Key),
            "keyDown" or "keyUp" => ApplyPhysicalKey(command),
            "heartbeat" => true,
            "releaseAll" => ReleaseAll(),
            _ => false
        };
    }

    public bool ReleaseButtons()
    {
        borrowedKeys.Clear();
        if (!HasHeldInputs) return true;
        if (!interactive()) return false;
        // Release only input this Desktop injected, in reverse key-down order.
        var releases = pendingReleases.ToList();
        var keys = heldKeys.Reverse().ToArray();
        releases.AddRange(keys.Select(key => KeyboardEvent(key.Key, 0, KeyUp | (key.Value ? KeyExtended : 0))));
        if (leftButtonDown) releases.Add(MouseEvent(MouseLeftUp));
        int sent = Math.Clamp(sendInput(releases.ToArray()), 0, releases.Count);
        int pendingCount = pendingReleases.Count;
        pendingReleases.RemoveRange(0, Math.Min(sent, pendingCount));
        for (int index = 0; index < keys.Length && index + pendingCount < sent; index++)
            heldKeys.Remove(keys[index].Key);
        if (leftButtonDown && sent == releases.Count) leftButtonDown = false;
        return !HasHeldInputs;
    }

    private bool ReleaseAll()
    {
        if (!ReleaseButtons())
            throw new InvalidOperationException("Windows 暂时无法释放输入，请在被控电脑解锁后重试。");
        return true;
    }

    internal static bool IsSupportedVirtualKey(int key) => key is 0x03 or 0x08 or 0x09 or 0x0C or 0x0D ||
        key is >= 0x10 and <= 0x39 || key is >= 0x41 and <= 0x5D ||
        key is >= 0x5F and <= 0x87 || key is >= 0x90 and <= 0x96 ||
        key is >= 0xA0 and <= 0xB7 || key is >= 0xBA and <= 0xC0 ||
        key is >= 0xDB and <= 0xDF || key is 0xE1 or 0xE2 || key is >= 0xF6 and <= 0xFB || key is 0xFD or 0xFE;

    private bool ApplyPhysicalKey(ControlInput command)
    {
        if (command.VirtualKey is not int value || !IsSupportedVirtualKey(value)) return false;
        ushort key = (ushort)value;
        bool down = command.Action == "keyDown";
        if (!down)
        {
            if (borrowedKeys.Remove(key)) return true;
            if (!heldKeys.TryGetValue(key, out bool extended)) return true;
            EnsureInteractiveDesktop();
            if (sendInput([KeyboardEvent(key, 0, KeyUp | (extended ? KeyExtended : 0))]) != 1)
                throw new InvalidOperationException("Windows 未接收按键释放。");
            heldKeys.Remove(key);
            return true;
        }
        EnsureInteractiveDesktop();
        // Ctrl+Alt+Delete is a secure-attention sequence, never a remotely injected shortcut.
        if (key == (ushort)Keys.Delete && ModifierHeld(Keys.ControlKey, Keys.LControlKey, Keys.RControlKey) &&
            ModifierHeld(Keys.Menu, Keys.LMenu, Keys.RMenu)) return false;
        if (borrowedKeys.Contains(key)) return true;
        if (!heldKeys.ContainsKey(key) && isKeyDown(key))
        {
            borrowedKeys.Add(key);
            return true;
        }
        bool keyExtended = heldKeys.TryGetValue(key, out bool existing) ? existing : command.Extended;
        if (sendInput([KeyboardEvent(key, 0, keyExtended ? KeyExtended : 0)]) != 1)
            throw new InvalidOperationException("Windows 未接收按键按下。");
        heldKeys[key] = keyExtended;
        return true;
    }

    private bool ModifierHeld(params Keys[] keys) => keys.Any(key => heldKeys.ContainsKey((ushort)key) || isKeyDown((int)key));

    private bool ApplyPointer(ControlInput command)
    {
        var events = new List<NativeInput>(5);
        if (TryGetMove(command, out NativeInput move))
        {
            events.Add(move);
        }
        else if (command.Action != "up" || command.X.HasValue || command.Y.HasValue)
        {
            return false;
        }

        switch (command.Action)
        {
            case "click" when !leftButtonDown:
                events.Add(MouseEvent(MouseLeftDown));
                events.Add(MouseEvent(MouseLeftUp));
                break;
            case "doubleClick" when !leftButtonDown:
                events.Add(MouseEvent(MouseLeftDown));
                events.Add(MouseEvent(MouseLeftUp));
                events.Add(MouseEvent(MouseLeftDown));
                events.Add(MouseEvent(MouseLeftUp));
                break;
            case "click" or "doubleClick":
                return false;
            case "rightClick":
                events.Add(MouseEvent(MouseRightDown));
                events.Add(MouseEvent(MouseRightUp));
                break;
            case "down" when !leftButtonDown:
                events.Add(MouseEvent(MouseLeftDown));
                break;
            case "up" when leftButtonDown:
                events.Add(MouseEvent(MouseLeftUp));
                break;
        }

        if (events.Count > 0)
        {
            EnsureInteractiveDesktop();
            Inject(events.ToArray());
        }
        if (command.Action is "down" or "up")
        {
            leftButtonDown = command.Action == "down";
        }
        return true;
    }

    private bool ApplyScroll(ControlInput command)
    {
        if (command.Delta is not int delta || delta == 0 || delta < -8 || delta > 8)
        {
            return false;
        }

        NativeInput scroll = MouseEvent(MouseWheel, data: unchecked((uint)(delta * 120)));
        if (TryGetMove(command, out NativeInput move))
        {
            EnsureInteractiveDesktop();
            Inject([move, scroll]);
            return true;
        }
        if (command.X.HasValue || command.Y.HasValue)
        {
            return false;
        }

        EnsureInteractiveDesktop();
        Inject([scroll]);
        return true;
    }

    private bool ApplyText(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 256)
        {
            return false;
        }

        var events = new List<NativeInput>(text.Length * 2);
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        for (int index = 0; index < text.Length;)
        {
            if (!Rune.TryGetRuneAt(text, index, out Rune rune))
            {
                return false;
            }

            if (rune.Value is '\r' or '\n' or '\t')
            {
                Keys key = rune.Value == '\t' ? Keys.Tab : Keys.Enter;
                events.Add(KeyEvent(key));
                events.Add(KeyEvent(key, release: true));
            }
            else
            {
                if (Rune.IsControl(rune))
                {
                    return false;
                }
                for (int unit = 0; unit < rune.Utf16SequenceLength; unit++)
                {
                    ushort codeUnit = text[index + unit];
                    events.Add(KeyboardEvent(0, codeUnit, KeyUnicode));
                    events.Add(KeyboardEvent(0, codeUnit, KeyUnicode | KeyUp));
                }
            }
            index += rune.Utf16SequenceLength;
        }

        EnsureInteractiveDesktop();
        Inject(events.ToArray());
        return true;
    }

    private bool ApplyKey(string? name)
    {
        if (name is null || !KeyBindings.TryGetValue(name, out var binding))
        {
            return false;
        }

        EnsureInteractiveDesktop();
        bool pressModifier = binding.Modifier != Keys.None && !isKeyDown((int)binding.Modifier);
        NativeInput press = KeyEvent(binding.Key);
        NativeInput release = KeyEvent(binding.Key, release: true);
        Inject(pressModifier
            ? [KeyEvent(binding.Modifier), press, release, KeyEvent(binding.Modifier, release: true)]
            : [press, release]);
        return true;
    }

    private static bool TryGetMove(ControlInput command, out NativeInput move)
    {
        move = default;
        if (command.X is not double x || command.Y is not double y ||
            !double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x > 1 || y < 0 || y > 1)
        {
            return false;
        }

        move = MouseEvent(MouseMove | MouseAbsolute,
            (int)Math.Round(x * ushort.MaxValue), (int)Math.Round(y * ushort.MaxValue));
        return true;
    }

    private void EnsureInteractiveDesktop()
    {
        if (!interactive())
        {
            throw new InvalidOperationException("Windows 桌面暂不可用，请在电脑上解锁或关闭系统提示。");
        }
    }

    private static bool IsInteractiveDesktop()
    {
        IntPtr desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var name = new StringBuilder(64);
            return GetUserObjectInformation(desktop, UserObjectName, name, (uint)(name.Capacity * sizeof(char)), out _) &&
                string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    private static void DrawCursor(Graphics graphics, Rectangle bounds)
    {
        var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref cursor) || (cursor.Flags & CursorShowing) == 0 ||
            !bounds.Contains(cursor.Position.X, cursor.Position.Y) || !GetIconInfo(cursor.Cursor, out IconInfo icon))
        {
            return;
        }

        try
        {
            IntPtr context = graphics.GetHdc();
            try
            {
                DrawIconEx(context, cursor.Position.X - bounds.X - icon.HotspotX,
                    cursor.Position.Y - bounds.Y - icon.HotspotY, cursor.Cursor, 0, 0, 0, IntPtr.Zero, DrawIconNormal);
            }
            finally
            {
                graphics.ReleaseHdc(context);
            }
        }
        finally
        {
            if (icon.Mask != IntPtr.Zero)
            {
                DeleteObject(icon.Mask);
            }
            if (icon.Color != IntPtr.Zero)
            {
                DeleteObject(icon.Color);
            }
        }
    }

    private static NativeInput MouseEvent(uint flags, int x = 0, int y = 0, uint data = 0)
    {
        return new NativeInput
        {
            Data = new InputData { Mouse = new MouseInput { X = x, Y = y, MouseData = data, Flags = flags } }
        };
    }

    private static NativeInput KeyEvent(Keys key, bool release = false)
    {
        uint flags = release ? KeyUp : 0;
        if (key is Keys.Delete or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown or
            Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.LWin)
        {
            flags |= KeyExtended;
        }
        return KeyboardEvent((ushort)key, 0, flags);
    }

    private static NativeInput KeyboardEvent(ushort key, ushort scanCode, uint flags)
    {
        return new NativeInput
        {
            Type = KeyboardInput,
            Data = new InputData { Keyboard = new KeyInput { Key = key, ScanCode = scanCode, Flags = flags } }
        };
    }

    private void Inject(NativeInput[] events)
    {
        int sent = Math.Clamp(sendInput(events), 0, events.Length);
        if (sent == events.Length)
        {
            return;
        }

        if (sent > 0)
        {
            ReleaseInterruptedInputs(events.AsSpan(0, sent));
        }
        throw new InvalidOperationException("Windows 未接收本次输入。请确认当前窗口接受普通用户输入。");
    }

    private void ReleaseInterruptedInputs(ReadOnlySpan<NativeInput> sent)
    {
        var releases = new List<NativeInput>();
        uint mouseRelease = 0;
        foreach (NativeInput input in sent)
        {
            if (input.Type == KeyboardInput)
            {
                KeyInput key = input.Data.Keyboard;
                if ((key.Flags & KeyUp) == 0)
                {
                    releases.Add(KeyboardEvent(key.Key, key.ScanCode, key.Flags | KeyUp));
                }
                else
                {
                    releases.RemoveAll(release => release.Data.Keyboard.Key == key.Key &&
                        release.Data.Keyboard.ScanCode == key.ScanCode);
                }
            }
            else
            {
                mouseRelease = input.Data.Mouse.Flags switch
                {
                    MouseLeftDown => mouseRelease | MouseLeftUp,
                    MouseLeftUp => mouseRelease & ~MouseLeftUp,
                    MouseRightDown => mouseRelease | MouseRightUp,
                    MouseRightUp => mouseRelease & ~MouseRightUp,
                    _ => mouseRelease
                };
            }
        }

        if (mouseRelease != 0)
        {
            releases.Add(MouseEvent(mouseRelease));
        }
        if (releases.Count > 0)
        {
            releases.Reverse();
            int released = Math.Clamp(sendInput(releases.ToArray()), 0, releases.Count);
            pendingReleases.AddRange(releases.Skip(released));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeInput
    {
        public uint Type;
        public InputData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputData
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyInput
    {
        public ushort Key, ScanCode;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Cursor;
        public NativePoint Position;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon, HotspotX, HotspotY;
        public IntPtr Mask, Color;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] NativeInput[] input, int size);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder name, uint length, out uint required);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorInfo(ref CursorInfo cursor);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height,
        uint animationStep, IntPtr brush, uint flags);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr obj);
}
