using System.Net;

namespace RemoteControl;

// Two isolated bindings preserve the live PC's LAN and Tailscale listeners.
internal sealed record ConnectionAddress(IPAddress Address, int PrefixLength, bool IsTailscale)
{
    internal static IPAddress Lan { get; } = IPAddress.Loopback;
    internal static IPAddress Tailscale { get; } = IPAddress.Parse("127.0.0.2");
    public static IReadOnlyList<ConnectionAddress> Find() => [new(Lan, 8, false), new(Tailscale, 8, true)];
    public bool AllowsPeer(IPAddress peer) => IPAddress.IsLoopback(peer);
}
