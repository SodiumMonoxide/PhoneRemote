using System.Reflection;
using RemoteControl;

internal static class Program
{
    private static int checks;
    [STAThread]
    private static int Main()
    {
        try
        {
            Routing(); LocalShortcuts(); Gating(); MouseAndRelease();
            Console.WriteLine($"PASS {checks} keyboard routing and capture checks; no hook installed or OS input injected");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Check(bool value, string title)
    {
        if (!value) throw new Exception(title);
        checks++;
    }
    private static void Routing()
    {
        var commands = new List<ControlInput>();
        var local = new List<Keys>();
        var route = new RemoteKeyboardRouter(commands.Add, local.Add);
        route.Start();
        foreach (int key in new[] { 0x09, 0x1B, 0x7A, 0x87, 0x5B, 0xA2, 0xA4, 0x41, 0xBA, 0x16, 0x1A })
        {
            Check(route.Route(key, true, false, false, true), $"physical key {key:X} captured");
            Check(route.Route(key, true, true, false, true), $"physical release {key:X} captured");
            Check(commands[^2].Action == "keyDown" && commands[^1].Action == "keyUp" &&
                commands[^2].VirtualKey == key && commands[^2].Extended, "physical code and extended flag preserved");
        }
        commands.Clear();
        route.Route(0xA2, false, false, false, true);
        route.Route(0x41, false, false, false, true);
        route.Route(0x41, false, false, false, true);
        route.Route(0x41, false, true, false, true);
        route.Route(0xA2, false, true, false, true);
        Check(commands.Select(c => c.Action).SequenceEqual(new[] { "keyDown", "keyDown", "keyDown", "keyUp", "keyUp" }), "modifier/chord/autorepeat order");
        Check(local.Count == 0, "ordinary Tab Escape F11 Win and Ctrl combinations never become local commands");
        commands.Clear();
        route.Route(0xA4, false, false, false, true);
        route.Route(0x09, false, false, false, true);
        route.Route(0x09, false, true, false, true);
        route.Route(0xA4, false, true, false, true);
        Check(commands.Count == 4 && local.Count == 0, "Alt+Tab transmitted as physical chord");
        route.Stop();
        Check(commands[^1].Action == "releaseAll" && !route.Active, "stop releases held remote state");
        int count = commands.Count; route.Stop();
        Check(commands.Count == count, "stop is idempotent");
    }
    private static void LocalShortcuts()
    {
        var commands = new List<ControlInput>(); var local = new List<Keys>(); int lost = 0;
        var route = new RemoteKeyboardRouter(commands.Add, local.Add, () => lost++);
        route.Start();
        route.Route(0xA2, false, false, false, true);
        route.Route(0xA4, false, false, false, true);
        Check(route.Route(0x0D, false, false, false, true), "fullscreen chord intercepted");
        Check(local.SequenceEqual(new[] { Keys.Control | Keys.Alt | Keys.Enter }) && commands[^1].Action == "releaseAll", "fullscreen releases modifiers before local command");
        Check(route.Route(0x0D, false, false, false, true) && local.Count == 1, "fullscreen autorepeat suppressed");
        foreach (int key in new[] { 0x0D, 0xA4, 0xA2 }) Check(route.Route(key, false, true, false, true), "local chord releases swallowed");
        int count = commands.Count;
        route.Route(0x41, false, false, false, true); route.Route(0x41, false, true, false, true);
        Check(commands.Count == count + 2, "ordinary input resumes after fullscreen chord released");
        route.Route(0xA2, false, false, false, true); route.Route(0xA4, false, false, false, true);
        Check(route.Route(0x7B, false, false, false, true), "emergency exit consumed locally");
        Check(!route.Active && local[^1] == (Keys.Control | Keys.Alt | Keys.F12) && commands[^1].Action == "releaseAll", "emergency exit disables forwarding and releases all");
        Check(!route.Route(0x41, false, false, false, true), "keys local after exit");
        route.Start(); commands.Clear();
        route.Route(0xA2, false, false, false, true); route.Route(0xA4, false, false, false, true);
        Check(!route.Route(0x2E, true, false, false, true) && lost == 1 && !route.Active, "secure attention remains owned by Windows");
        Check(commands.All(c => c.VirtualKey != 0x2E), "secure attention Delete never transmitted");
    }
    private static void Gating()
    {
        var commands = new List<ControlInput>(); int lost = 0; int exited = 0;
        var route = new RemoteKeyboardRouter(commands.Add, _ => exited++, () => lost++);
        Check(!route.Route(0x41, false, false, false, true), "inactive capture does not intercept");
        route.Start();
        Check(!route.Route(0x41, false, false, true, true) && commands.Count == 0, "injected keys never sent");
        Check(!route.Route(0x41, false, true, false, true) && commands.Count == 0, "unmatched up never sent");
        foreach (int key in new[] { 0, 1, 2, 4, 0x07, 0x0A, 0x3A, 0x88, 0x97, 0xC3, 0xE7, 0xFF, 300 })
            Check(!route.Route(key, false, false, false, true), "reserved/mouse/packet keys ignored");
        route.Route(0x41, false, false, false, true);
        Check(!route.Route(0x42, false, false, false, false), "other foreground window key passes through");
        Check(commands[^1].Action == "releaseAll" && !route.Active && lost == 1 && exited == 0, "focus loss differs from explicit exit");
        route.Start([0xA2]); commands.Clear();
        Check(!route.Route(0xA2, false, false, false, true) && !route.Route(0xA2, false, true, false, true) && commands.Count == 0, "keys already held at activation remain local until released");
        Check(route.Route(0xA2, false, false, false, true), "modifier captured on subsequent fresh press");
        route.Stop(); route.Start([0xA2, 0xA4]);
        Check(route.Route(0x7B, false, false, false, true) && !route.Active && exited == 1,
            "emergency escape also works with modifiers held before activation");
        route.Stop();
        var initial = new HashSet<int> { 0x10, 0x11, 0x12, 0xA0, 0xA2, 0xA4 };
        route.Start(ScopedKeyboardCapture.InitialHeldKeys(initial.Contains)); commands.Clear();
        foreach (int key in new[] { 0xA0, 0xA2, 0xA4 }) route.Route(key, false, true, false, true);
        route.Route(0xA4, false, false, false, true);
        Check(route.Route(0x7B, false, false, false, true) && route.Active && exited == 1 &&
            commands[^1].VirtualKey == 0x7B, "released initial modifier aliases cannot trigger a phantom emergency chord");
    }
    private static void MouseAndRelease()
    {
        using var view = new WindowsRemoteView { Size = new Size(800, 600) };
        view.SetFrame(new Bitmap(800, 600));
        var commands = new List<ControlInput>(); view.RemoteInput += commands.Add;
        view.InputEnabled = true; // Invisible, unfocused control: never installs a hook.
        Mouse(view, "OnMouseDown", 100, 100); Mouse(view, "OnMouseMove", 140, 140);
        Check(commands.Any(c => c.Action == "down"), "drag emits press");
        view.ReleaseAllInput();
        Check(!view.InputEnabled && commands.Any(c => c.Action == "up") && commands[^1].Action == "releaseAll", "exit releases drag and all input");
        int count = commands.Count;
        Mouse(view, "OnMouseDown", 100, 100); Mouse(view, "OnMouseMove", 140, 140);
        Check(commands.Count == count, "clicking paused view cannot reenter input capture");
        view.InputEnabled = true;
        typeof(WindowsRemoteView).GetMethod("OnLostFocus", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, [EventArgs.Empty]);
        Check(!view.InputEnabled && commands[^1].Action == "releaseAll", "focus loss releases without recapture");
        view.InputEnabled = true; commands.Clear();
        typeof(WindowsRemoteView).GetMethod("OnKeyPress", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, [new KeyPressEventArgs('x')]);
        Check(commands.Count == 0, "duplicate/injected WinForms text never forwards");
        view.ClearFrame();
        Check(!view.InputEnabled, "lost frame clears keyboard capture");
    }
    private static void Mouse(WindowsRemoteView view, string method, int x, int y) =>
        typeof(WindowsRemoteView).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view,
            [new MouseEventArgs(MouseButtons.Left, 1, x, y, 0)]);
}

namespace RemoteControl
{
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
}
