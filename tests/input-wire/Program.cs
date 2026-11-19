using System.Net;
using RemoteControl;

// The complete production HTTPS path is exercised, replacing only the native
// input backend. Test credentials/certificates live in a new temporary directory.
var native = new NativeRecorder();
var desktop = new Desktop(native.Send, () => true, _ => false);
var session = new RemoteControlSession(desktop);
string directory = Path.Combine(Path.GetTempPath(), "PhoneRemote-tests", "input-wire-" + Guid.NewGuid().ToString("N"));
int checks = 0;
await using var lan = await RemoteServer.StartAsync(ConnectionAddress.Lan, directory, session);
await using var tailscale = await RemoteServer.StartAsync(ConnectionAddress.Tailscale, directory, session);
using var first = new WindowsRemoteClient(RemoteClientProfile.Create("lan", lan.Address.AbsoluteUri, lan.Fingerprint));
using var second = new WindowsRemoteClient(RemoteClientProfile.Create("lan", tailscale.Address.AbsoluteUri, tailscale.Fingerprint));
await first.PairAsync(lan.GetStatus().Code!, true, false);
await first.RequireKeyboardSupportAsync();
Check(first.IsConnected && lan.GetStatus().Connected, "real client authenticates and negotiates keyboard capability");

await first.SendInputAsync(Key("keyDown", 0xA3, true));
await first.SendInputAsync(Key("keyDown", 0x41));
await first.SendInputAsync(Key("keyDown", 0x41));
await first.SendInputAsync(Key("keyUp", 0x41));
Check(native.Events is [var control, var letter, var repeat, var up] &&
    IsKey(control, 0xA3, false, true) && IsKey(letter, 0x41, false) &&
    IsKey(repeat, 0x41, false) && IsKey(up, 0x41, true),
    "JSON physical keys, extended flag, repeat and key-up reach native input backend in order");
int count = native.Events.Length;
await first.SendInputAsync(new() { Action = "heartbeat" });
Check(native.Events.Length == count && desktop.HasHeldInputs, "authenticated heartbeat preserves held key without an OS event");
await first.SendInputAsync(new() { Action = "releaseAll" });
Check(IsKey(native.Events[^1], 0xA3, true, true) && !desktop.HasHeldInputs,
    "capture-exit releaseAll traverses actual HTTP handler and releases extended modifier");
count = native.Events.Length;
await first.SendInputAsync(Key("keyUp", 0x42));
Check(native.Events.Length == count, "unheld wire key-up cannot release an unrelated local key");
await ExpectStatus(() => first.SendInputAsync(Key("keyDown", 1)), HttpStatusCode.BadRequest,
    "mouse virtual key is rejected by actual input endpoint");
await ExpectStatus(() => first.SendInputAsync(Key("keyDown", 0xE7)), HttpStatusCode.BadRequest,
    "Unicode packet key is rejected by actual input endpoint");
Check(native.Events.Length == count, "invalid wire keys never reach native backend");

await first.SendInputAsync(Key("keyDown", 0x70));
await first.DisconnectAsync();
Check(IsKey(native.Events[^1], 0x70, true) && !desktop.HasHeldInputs && !lan.GetStatus().Connected,
    "actual disconnect handler releases held function key");
await first.ResumeAsync(false);
await first.SendInputAsync(Key("keyDown", 0xA0));
await second.PairAsync(tailscale.GetStatus().Code!, true, true);
Check(IsKey(native.Events[^1], 0xA0, true) && !desktop.HasHeldInputs && tailscale.GetStatus().Connected,
    "authenticated endpoint handover releases previous controller input");
await second.RequireKeyboardSupportAsync();
await second.SendInputAsync(Key("keyDown", 0x5B));
count = native.Events.Length;
await ExpectStatus(() => first.SendInputAsync(new() { Action = "releaseAll" }), HttpStatusCode.Unauthorized,
    "stale controller releaseAll is rejected before reaching native backend");
Check(native.Events.Length == count && desktop.HasHeldInputs,
    "stale controller cannot release the newer controller's held Windows key");
await second.SendInputAsync(new() { Action = "releaseAll" });
Check(IsKey(native.Events[^1], 0x5B, true) && !desktop.HasHeldInputs,
    "current controller can release its held Windows key through second HTTPS endpoint");
await second.DisconnectAsync();
await first.ResumeAsync(true);
await first.SendInputAsync(Key("keyDown", 0xA2));
count = native.Events.Length;
await second.DisconnectAsync();
Check(native.Events.Length == count && desktop.HasHeldInputs && lan.GetStatus().Connected,
    "repeated old client disconnect cannot affect resumed controller");
await first.DisconnectAsync();
Check(!desktop.HasHeldInputs && IsKey(native.Events[^1], 0xA2, true), "resumed session disconnect still releases its modifier");
Console.WriteLine($"PASS {checks} keyboard wire checks; no OS input injected and no private profile opened");

void Check(bool value, string name)
{
    if (!value) throw new InvalidOperationException(name);
    checks++;
    Console.WriteLine("PASS " + name);
}
async Task ExpectStatus(Func<Task> action, HttpStatusCode status, string name)
{
    bool received = false;
    try { await action(); }
    catch (RemoteClientException error) when (error.StatusCode == status) { received = true; }
    Check(received, name);
}
static ControlInput Key(string action, int key, bool extended = false) =>
    new() { Action = action, VirtualKey = key, Extended = extended };
static bool IsKey(Desktop.NativeInput input, int key, bool up, bool extended = false) =>
    input.Type == 1 && input.Data.Keyboard.Key == key && input.Data.Keyboard.Flags == ((up ? 2U : 0) | (extended ? 1U : 0));

sealed class NativeRecorder
{
    private readonly List<Desktop.NativeInput> events = [];
    internal Desktop.NativeInput[] Events { get { lock (events) return events.ToArray(); } }
    internal int Send(Desktop.NativeInput[] inputs)
    {
        lock (events) events.AddRange(inputs);
        return inputs.Length;
    }
}
