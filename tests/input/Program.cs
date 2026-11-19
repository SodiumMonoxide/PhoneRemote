using System.Diagnostics;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using RemoteControl;

var checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}
ControlInput Key(string action, int key, bool extended = false) => new() { Action = action, VirtualKey = key, Extended = extended };
bool IsKey(Desktop.NativeInput input, int key, bool up, bool extended = false) => input.Type == 1 &&
    input.Data.Keyboard.Key == key && input.Data.Keyboard.Flags == ((up ? 2U : 0) | (extended ? 1U : 0));

var fake = new NativeFixture();
var desktop = fake.Desktop;
Check(!desktop.Apply(Key("keyDown", 1)) && !desktop.Apply(Key("keyDown", 0xE7)) &&
      !desktop.Apply(Key("keyDown", 0xE5)) && !desktop.Apply(Key("keyDown", 256)) &&
      !desktop.Apply(new() { Action = "keyDown" }) && fake.Events.Count == 0, "invalid, mouse, process and Unicode packet VKs rejected without native input");
Check(desktop.Apply(Key("keyDown", 0xA2)) && desktop.Apply(Key("keyDown", 0x41)) &&
      desktop.HasHeldInputs, "physical Ctrl and letter held across requests");
Check(IsKey(fake.Events[0], 0xA2, false) && IsKey(fake.Events[1], 0x41, false), "physical key order preserved");
Check(desktop.Apply(Key("keyDown", 0x41)) && fake.Events.Count == 3, "held key repeat remains a key-down event");
Check(desktop.Apply(Key("keyUp", 0x41)) && IsKey(fake.Events[^1], 0x41, true) && desktop.HasHeldInputs,
    "letter release retains modifier");
int count = fake.Events.Count;
Check(desktop.Apply(Key("keyUp", 0x41)) && fake.Events.Count == count, "unheld key-up never releases a local key");
Check(desktop.Apply(new() { Action = "releaseAll" }) && IsKey(fake.Events[^1], 0xA2, true) && !desktop.HasHeldInputs,
    "releaseAll releases tracked modifier without disconnect");
count = fake.Events.Count;
Check(desktop.Apply(new() { Action = "heartbeat" }) && fake.Events.Count == count, "heartbeat injects no system events");

fake = new NativeFixture(); desktop = fake.Desktop;
desktop.Apply(Key("keyDown", 0xA3, true));
desktop.Apply(Key("keyUp", 0xA3, false));
Check(IsKey(fake.Events[0], 0xA3, false, true) && IsKey(fake.Events[1], 0xA3, true, true),
    "key-up preserves original extended flag instead of trusting mismatched release");
foreach (int vk in new[] { 9, 27, 0x70, 0x87, 0x60, 0x6F, 0xBA, 0xDE, 0x5B, 0x5C })
{
    Check(desktop.Apply(Key("keyDown", vk)) && desktop.Apply(Key("keyUp", vk)), "direct Tab/Esc/function/numpad/OEM/Windows key " + vk);
}

fake = new NativeFixture(); desktop = fake.Desktop;
fake.LocalHeld.Add(0xA2);
Check(desktop.Apply(Key("keyDown", 0xA2)) && desktop.Apply(Key("keyDown", 0xA2)) &&
    desktop.Apply(Key("keyUp", 0xA2)) && desktop.ReleaseButtons() && fake.Events.Count == 0,
    "already-held local key is borrowed and never injected or released");
desktop.Apply(Key("keyDown", 0xA4));
count = fake.Events.Count;
Check(!desktop.Apply(Key("keyDown", 0x2E, true)) && fake.Events.Count == count,
    "secure-attention Delete is not injected while Ctrl+Alt held");
desktop.ReleaseButtons();
Check(fake.Events.All(e => e.Type != 1 || e.Data.Keyboard.Key != 0xA2), "secure-attention cleanup preserves local Ctrl");

fake = new NativeFixture(); desktop = fake.Desktop;
desktop.Apply(Key("keyDown", 0xA0));
desktop.Apply(Key("keyDown", 0x42));
desktop.Apply(new() { Action = "down", X = 0.4, Y = 0.6 });
Check(desktop.ReleaseButtons() && !desktop.HasHeldInputs && IsKey(fake.Events[^3], 0x42, true) &&
    IsKey(fake.Events[^2], 0xA0, true) && fake.Events[^1].Data.Mouse.Flags == 4,
    "complete cleanup releases reverse-order keyboard and mouse");

fake = new NativeFixture(); desktop = fake.Desktop;
desktop.Apply(Key("keyDown", 0xA2)); desktop.Apply(Key("keyDown", 0x41));
fake.ReturnCounts.Enqueue(1);
Check(!desktop.ReleaseButtons() && desktop.HasHeldInputs && IsKey(fake.Events[^1], 0x41, true),
    "partial release tracks only unsent keys for retry");
count = fake.Events.Count;
Check(desktop.ReleaseButtons() && fake.Events.Count == count + 1 && IsKey(fake.Events[^1], 0xA2, true),
    "partial-release retry sends remaining modifier exactly once");

fake = new NativeFixture(); desktop = fake.Desktop;
fake.ReturnCounts.Enqueue(2); fake.ReturnCounts.Enqueue(0);
try { desktop.Apply(new() { Action = "click", X = 0.2, Y = 0.3 }); throw new Exception("expected native injection failure"); }
catch (InvalidOperationException) { }
Check(desktop.HasHeldInputs && desktop.ReleaseButtons() && fake.Events[^1].Data.Mouse.Flags == 4,
    "interrupted legacy click release survives failed cleanup and retries");

fake = new NativeFixture(); desktop = fake.Desktop;
var session = new RemoteControlSession(desktop);
var firstEndpoint = new RemoteServer(); var secondEndpoint = new RemoteServer();
var peer = IPAddress.Loopback;
session.OpenLocked(firstEndpoint, new string('A', 64), peer);
Check(session.ApplyLocked(Key("keyDown", 0xA2)) == StatusCodes.Status204NoContent, "session accepts physical key-down");
AgeInput(session);
session.MaintainLocked();
Check(!desktop.HasHeldInputs && IsKey(fake.Events[^1], 0xA2, true), "stale held keyboard released by two-second watchdog");
session.ApplyLocked(Key("keyDown", 0xA0));
AgeInput(session);
Check(session.ApplyLocked(new() { Action = "heartbeat" }) == 204, "authenticated heartbeat refreshes held-input watchdog");
session.MaintainLocked();
Check(desktop.HasHeldInputs, "fresh heartbeat preserves held modifier");
session.DisconnectLocked(secondEndpoint);
Check(desktop.HasHeldInputs, "disconnecting another endpoint cannot release current controller input");
session.OpenLocked(secondEndpoint, new string('B', 64), peer);
Check(!desktop.HasHeldInputs && !session.AuthorizeLocked(firstEndpoint, new string('A', 64), peer),
    "handover releases old keys and rejects old authorization");
session.ApplyLocked(Key("keyDown", 0x44));
session.DisconnectLocked(secondEndpoint);
Check(!desktop.HasHeldInputs, "disconnect releases current held keys");

fake = new NativeFixture(); desktop = fake.Desktop; session = new RemoteControlSession(desktop);
session.OpenLocked(firstEndpoint, new string('C', 64), peer);
session.ApplyLocked(Key("keyDown", 0xA2));
fake.ReturnCounts.Enqueue(0);
Check(session.ApplyLocked(Key("keyDown", 0x41)) == 503 && !desktop.HasHeldInputs,
    "failed physical-key injection returns unavailable and releases earlier modifier");
session.ApplyLocked(Key("keyDown", 0xA2));
fake.Interactive = false;
session.DisconnectLocked(firstEndpoint);
Check(desktop.HasHeldInputs && session.ApplyLocked(new() { Action = "heartbeat" }) == 503,
    "unavailable desktop keeps cleanup pending and blocks fresh input");
fake.Interactive = true;
session.MaintainLocked();
Check(!desktop.HasHeldInputs && session.ApplyLocked(new() { Action = "heartbeat" }) == 204,
    "pending cleanup retries when interactive desktop returns");

fake = new NativeFixture(); desktop = fake.Desktop; session = new RemoteControlSession(desktop);
session.OpenLocked(firstEndpoint, new string('D', 64), peer);
session.ApplyLocked(Key("keyDown", 0xA2));
AgeInput(session);
Check(session.AuthorizeLocked(firstEndpoint, new string('D', 64), peer), "authorized non-input traffic renews session");
session.MaintainLocked();
Check(!desktop.HasHeldInputs && session.IsConnectedLocked(firstEndpoint),
    "ordinary authorized traffic does not renew held-input watchdog");
session.ApplyLocked(Key("keyDown", 0xA2));
var activeSession = typeof(RemoteControlSession).GetField("active", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
activeSession.GetType().GetProperty("LastActivity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
    .SetValue(activeSession, Stopwatch.GetTimestamp() - Stopwatch.Frequency * 61);
Check(!session.AuthorizeLocked(firstEndpoint, new string('E', 64), peer), "unauthenticated traffic cannot renew idle session");
session.MaintainLocked();
Check(!session.IsConnectedLocked(firstEndpoint) && !desktop.HasHeldInputs,
    "sixty-second idle session still expires and releases keyboard despite invalid traffic");
Check(MarshalSize() == (IntPtr.Size == 8 ? 40 : 28), "native INPUT ABI layout unchanged");
Console.WriteLine($"PASS {checks} physical-key protocol and release checks; no OS input injected");

static void AgeInput(RemoteControlSession session) => typeof(RemoteControlSession)
    .GetField("inputActivity", BindingFlags.Instance | BindingFlags.NonPublic)!
    .SetValue(session, (long?)(Stopwatch.GetTimestamp() - Stopwatch.Frequency * 3));
static int MarshalSize() => System.Runtime.InteropServices.Marshal.SizeOf<Desktop.NativeInput>();

internal sealed class NativeFixture
{
    internal readonly List<Desktop.NativeInput> Events = [];
    internal readonly HashSet<int> LocalHeld = [];
    internal readonly Queue<int> ReturnCounts = [];
    internal bool Interactive = true;
    internal Desktop Desktop { get; }
    internal NativeFixture() => Desktop = new Desktop(events =>
    {
        int sent = ReturnCounts.Count == 0 ? events.Length : ReturnCounts.Dequeue();
        Events.AddRange(events.Take(sent));
        return sent;
    }, () => Interactive, key => LocalHeld.Contains(key));
}

namespace RemoteControl { internal sealed class RemoteServer; }
