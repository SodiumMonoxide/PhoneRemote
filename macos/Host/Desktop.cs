using System.Runtime.InteropServices;
using System.Text;

namespace RemoteControl;

internal sealed class Desktop
{
    private const int LeftDown = 1, LeftUp = 2, RightDown = 3, RightUp = 4, Moved = 5, Dragged = 6;
    private readonly List<ushort> held = [];
    private readonly HashSet<ushort> borrowed = [];
    private bool left, right;
    internal bool HasHeldInputs => left || right || held.Count != 0;
    private ulong Flags => held.Aggregate(0UL, (flags, key) => flags | MacKeys.Modifier(key));

    internal byte[] CaptureFrame()
    {
        IntPtr frame = Quartz.Frame(out int length);
        if (frame == IntPtr.Zero) throw new InvalidOperationException("主屏幕暂不可用。");
        try
        {
            byte[] result = new byte[length];
            Marshal.Copy(frame, result, 0, length);
            return result;
        }
        finally { Quartz.Free(frame); }
    }

    internal bool Apply(ControlInput command)
    {
        if (Quartz.CanInput() == 0)
            throw new InvalidOperationException("当前桌面未解锁或缺少辅助功能权限。");
        return command.Action switch
        {
            "move" or "click" or "doubleClick" or "rightClick" or "down" or "up" or "scroll" => Pointer(command),
            "keyDown" or "keyUp" => PhysicalKey(command),
            "text" => TypeText(command.Text),
            "key" => Shortcut(command.Key),
            "heartbeat" => true,
            "releaseAll" => ReleaseAll(),
            _ => false
        };
    }

    private bool ReleaseAll()
    {
        if (!ReleaseButtons()) throw new InvalidOperationException("未能释放远程输入。");
        return true;
    }


    internal bool ReleaseButtons()
    {
        borrowed.Clear();
        if (!HasHeldInputs) return true;
        if (Quartz.CanInput() == 0) return false;
        try
        {
            while (held.Count > 0) PostKey(held[^1], false);
            if (left) PostMouse(LeftUp, -1, -1);
            if (right) PostMouse(RightUp, -1, -1);
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    private void PostMouse(int type, double x, double y, int count = 0)
    {
        if (Quartz.Mouse(type, x, y, count, Flags) == 0) throw new InvalidOperationException("鼠标不可用。");
        if (type is LeftUp or LeftDown) left = type == LeftDown;
        if (type is RightUp or RightDown) right = type == RightDown;
    }

    private bool Pointer(ControlInput command)
    {
        bool hasPoint = command.X is >= 0 and <= 1 && command.Y is >= 0 and <= 1;
        if (!hasPoint && (command.X.HasValue || command.Y.HasValue || command.Action is not ("up" or "scroll")))
            return false;
        double x = command.X ?? -1, y = command.Y ?? -1;
        switch (command.Action)
        {
            case "move": PostMouse(left ? Dragged : Moved, x, y); break;
            case "down":
                if (!left) PostMouse(LeftDown, x, y);
                break;
            case "up":
                if (left) PostMouse(LeftUp, x, y);
                break;
            case "click" or "doubleClick":
                if (left) return false;
                PostMouse(LeftDown, x, y, 1);
                PostMouse(LeftUp, x, y);
                if (command.Action == "doubleClick")
                {
                    PostMouse(LeftDown, x, y, 2);
                    PostMouse(LeftUp, x, y);
                }
                break;
            case "rightClick":
                PostMouse(RightDown, x, y);
                PostMouse(RightUp, x, y);
                break;
            case "scroll":
                if (command.Delta is not int delta || delta == 0 || delta is < -8 or > 8) return false;
                if (hasPoint) PostMouse(left ? Dragged : Moved, x, y);
                if (Quartz.Scroll(delta, Flags) == 0) throw new InvalidOperationException("滚轮不可用。");
                break;
        }
        return true;
    }

    private bool PhysicalKey(ControlInput command)
    {
        if (command.VirtualKey is not int vk || vk is < 1 or > 254) return false;
        int mapped = MacKeys.FromVirtualKey(vk, command.Extended);
        if (mapped < 0) return true; // A Windows-only key should not interrupt the rest of the input stream.
        ushort key = (ushort)mapped;
        bool down = command.Action == "keyDown";
        if (borrowed.Contains(key))
        {
            if (!down) borrowed.Remove(key);
            return true;
        }
        if (!down && !held.Contains(key)) return true;
        if (down && !held.Contains(key) && Quartz.IsKeyDown(key) != 0)
        {
            borrowed.Add(key);
            return true;
        }
        PostKey(key, down);
        return true;
    }

    private void PostKey(ushort key, bool down)
    {
        ulong modifier = MacKeys.Modifier(key);
        ulong flags = down ? Flags | modifier :
            held.Where(other => other != key).Aggregate(0UL, (f, other) => f | MacKeys.Modifier(other));
        if (Quartz.Key(key, down ? 1 : 0, modifier, flags, null, 0) == 0)
            throw new InvalidOperationException("键盘不可用。");
        if (!down) held.Remove(key);
        else if (!held.Contains(key)) held.Add(key);
    }

    private bool Shortcut(string? name)
    {
        var (key, modifier) = MacKeys.Shortcut(name);
        if (key < 0) return false;
        bool ownModifier = modifier >= 0 && !held.Contains((ushort)modifier) && Quartz.IsKeyDown((ushort)modifier) == 0;
        if (ownModifier) PostKey((ushort)modifier, true);
        Tap((ushort)key);
        if (ownModifier) PostKey((ushort)modifier, false);
        return true;
    }

    private void Tap(ushort key)
    {
        if (held.Contains(key) || Quartz.IsKeyDown(key) != 0) return;
        PostKey(key, true);
        PostKey(key, false);
    }

    private bool TypeText(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 256) return false;
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        for (int i = 0; i < text.Length;)
        {
            if (!Rune.TryGetRuneAt(text, i, out var rune) ||
                (Rune.IsControl(rune) && rune.Value is not ('\n' or '\r' or '\t'))) return false;
            i += rune.Utf16SequenceLength;
        }
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is '\n' or '\r') Tap(36);
            else if (rune.Value == '\t') Tap(48);
            else
            {
                string unit = rune.ToString();
                if (Quartz.Key(0, 1, 0, Flags, unit, unit.Length) == 0 ||
                    Quartz.Key(0, 0, 0, Flags, unit, unit.Length) == 0)
                    throw new InvalidOperationException("无法输入文字。");
            }
        }
        return true;
    }
}
