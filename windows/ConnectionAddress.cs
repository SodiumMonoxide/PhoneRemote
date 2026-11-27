using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteControl;

internal sealed record ConnectionAddress(IPAddress Address, int PrefixLength, bool IsTailscale)
{
    // Windows Wintun interface identity from Tailscale's tsconst package.
    private static readonly Guid TailscaleAdapterId = new("37217669-42da-4657-a55b-0d995d328250");
    private const string TailscaleAdapterDescription = "Tailscale Tunnel";
    private static readonly IPNetwork TailscaleRange = IPNetwork.Parse("100.64.0.0/10");

    public static IReadOnlyList<ConnectionAddress> Find()
    {
#if PHONEREMOTE_MAC
        IPAddress? tailAddress = MacNetwork.GetTailscaleAddress();
        return NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses
                .Select(entry => new { Adapter = adapter, Entry = entry, Tail = entry.Address.Equals(tailAddress) }))
            .Where(item => item.Tail || item.Adapter.OperationalStatus == OperationalStatus.Up)
            .Where(item => IsUsableAddress(item.Entry, item.Tail))
            .Select(item => new ConnectionAddress(item.Entry.Address, item.Entry.PrefixLength, item.Tail))
            .DistinctBy(item => item.Address)
            .ToArray();
#else
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .Select(adapter => new
            {
                Tailscale = IsTailscaleAdapter(adapter),
                Wireless = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                Properties = adapter.GetIPProperties()
            })
            .OrderByDescending(adapter => adapter.Tailscale)
            .ThenByDescending(adapter => adapter.Wireless)
            .ThenByDescending(adapter => adapter.Properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork))
            .SelectMany(adapter => adapter.Properties.UnicastAddresses
                .Where(entry => IsUsableAddress(entry, adapter.Tailscale))
                .Select(entry => new ConnectionAddress(entry.Address, entry.PrefixLength, adapter.Tailscale)))
            .DistinctBy(entry => entry.Address)
            .ToArray();
#endif
    }

    public bool AllowsPeer(IPAddress peer)
    {
        if (IsTailscale)
            return IsTailscaleIPv4(Address) && IsTailscaleIPv4(peer);

        if (!IsPrivateIPv4(Address) || !IsPrivateIPv4(peer) || PrefixLength is <= 0 or >= 32)
            return false;

        byte[] remote = peer.GetAddressBytes();
        byte[] local = Address.GetAddressBytes();
        for (int offset = 0; offset < PrefixLength; offset += 8)
        {
            int bits = Math.Min(PrefixLength - offset, 8);
            if (((remote[offset / 8] ^ local[offset / 8]) >> (8 - bits)) != 0)
                return false;
        }
        return true;
    }

    private static bool IsTailscaleAdapter(NetworkInterface adapter) =>
        (Guid.TryParse(adapter.Id, out Guid id) && id == TailscaleAdapterId) ||
        string.Equals(adapter.Description, TailscaleAdapterDescription, StringComparison.OrdinalIgnoreCase);

    private static bool IsUsableAddress(UnicastIPAddressInformation entry, bool tailscale) =>
#if !PHONEREMOTE_MAC
        entry.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred &&
#endif
        (tailscale
            ? entry.PrefixLength is > 0 and <= 32 && IsTailscaleIPv4(entry.Address)
            : entry.PrefixLength is > 0 and < 32 && IsPrivateIPv4(entry.Address));

    private static bool IsTailscaleIPv4(IPAddress ip) =>
        ip.AddressFamily == AddressFamily.InterNetwork && TailscaleRange.Contains(ip);

    private static bool IsPrivateIPv4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        byte[] bytes = ip.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168);
    }
}
