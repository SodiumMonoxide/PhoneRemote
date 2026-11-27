using RemoteControl;

var desktop = new Desktop();
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
}
bool Send(string kind, int key) => desktop.Apply(new ControlInput { Action = kind, VirtualKey = key });
bool Pointer(string kind, double? x, double? y) => desktop.Apply(new ControlInput { Action = kind, X = x, Y = y });

Check(Send("keyDown", 0x5b) && Send("keyDown", 0x43), "Windows+C");
Check(Quartz.Keys.SequenceEqual([(55, true, 1UL << 20), (8, true, 1UL << 20)]), "Command+C on Mac");
Check(Send("keyUp", 0x43) && Send("keyUp", 0x5b) && !desktop.HasHeldInputs, "release modifier");
Check(Quartz.Keys[^1] == (55, false, 0UL), "Command flags cleared");
Quartz.LocalKey = 59;
int previous = Quartz.Keys.Count;
Check(Send("keyDown", 0xa2) && Send("keyUp", 0xa2) && Quartz.Keys.Count == previous, "preserve local Control");
Quartz.LocalKey = -1;
Check(Send("keyDown", 0x5d) && Send("keyUp", 0x5d) && Quartz.Keys.Count == previous, "unmapped Windows key");
Check(Send("keyDown", 0xa0) && Send("keyDown", 0xa1) && Send("keyUp", 0xa0), "two Shift keys");
Check(Quartz.Keys[^1] == (56, false, 1UL << 17), "one Shift still pressed");
Check(Pointer("down", .4, .6) && Pointer("move", .5, .8), "drag");
Check(Quartz.MouseEvents[^1] == (6, .5, .8), "drag event");
Check(desktop.ReleaseButtons() && !desktop.HasHeldInputs && Quartz.MouseEvents[^1].Type == 2, "disconnect releases inputs");
Check(!Pointer("move", 1.1, 0) && !Pointer("scroll", double.NaN, .2), "reject invalid coordinates");
Check(Pointer("doubleClick", .5, .5) && !desktop.HasHeldInputs, "double click");
Check(desktop.Apply(new ControlInput { Action = "text", Text = "你😀\n" }), "Unicode text");
Check(Quartz.Text.SequenceEqual(["你", "😀"]), "preserve surrogate pairs");
Check(!desktop.Apply(new ControlInput { Action = "text", Text = "a\0b" }), "reject invalid string before posting");
Console.WriteLine("Mac input checks passed");

namespace RemoteControl
{
    internal static class Quartz
    {
        internal static int LocalKey = -1;
        internal static readonly List<(int, bool, ulong)> Keys = [];
        internal static readonly List<(int Type, double X, double Y)> MouseEvents = [];
        internal static readonly List<string> Text = [];
        internal static int CanInput() => 1;
        internal static IntPtr Frame(out int length) => throw new NotSupportedException();
        internal static void Free(IntPtr bytes) => throw new NotSupportedException();
        internal static int IsKeyDown(ushort code) => code == LocalKey ? 1 : 0;
        internal static int Key(ushort code, int down, ulong modifier, ulong flags, string? text, int length)
        {
            Keys.Add((code, down != 0, flags));
            if (down != 0 && text != null) Text.Add(text);
            return 1;
        }
        internal static int Mouse(int type, double x, double y, int explicitCount, ulong flags)
        {
            MouseEvents.Add((type, x, y));
            return 1;
        }
        internal static int Scroll(int delta, ulong flags) => 1;
    }
}
