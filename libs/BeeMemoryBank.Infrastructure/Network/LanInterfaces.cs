using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Infrastructure.Network;

/// <summary>What an interface of this computer is, for the purpose of "may the LAN listener answer on it".</summary>
public enum LanInterfaceKind
{
    /// <summary>An ordinary network: Wi-Fi or Ethernet.</summary>
    Lan,

    /// <summary>A VPN or tailnet (a tunnel interface, a CGNAT 100.64/10 address): used only when the person opted in for it (decision D7).</summary>
    Vpn,

    /// <summary>A virtual adapter (WSL, Hyper-V, VirtualBox, VMware, Docker bridges): never used; a guest VM must not reach the listener.</summary>
    Virtual,
}

/// <summary>One IPv4 address of an interface that is up.</summary>
/// <param name="Id">The interface's identifier (on Windows the adapter GUID, elsewhere the BSD name such as <c>en0</c>).</param>
/// <param name="Name">The interface's name as the system shows it.</param>
/// <param name="Address">The IPv4 address.</param>
/// <param name="PrefixLength">The length of its network prefix (24 for 255.255.255.0).</param>
/// <param name="Gateway">The interface's IPv4 default gateway, or null.</param>
/// <param name="Kind">What it is (<see cref="LanInterfaceKind"/>).</param>
public sealed record LanInterface(string Id, string Name, IPAddress Address, int PrefixLength, IPAddress? Gateway, LanInterfaceKind Kind)
{
    /// <summary>The network address (the address with the host bits cleared).</summary>
    public IPAddress Network => LanSubnets.NetworkOf(Address, PrefixLength);

    /// <summary>True when <paramref name="other"/> is on this interface's subnet.</summary>
    public bool Contains(IPAddress other) => LanSubnets.SameSubnet(Address, other, PrefixLength);

    public override string ToString() => $"{Name} {Address}/{PrefixLength}";
}

/// <summary>The interfaces of this computer, read from the system; an interface so the listener's tests can say what the computer has.</summary>
public interface ILanInterfaces
{
    IReadOnlyList<LanInterface> Current();
}

/// <summary>Subnet arithmetic for IPv4.</summary>
public static class LanSubnets
{
    public static IPAddress NetworkOf(IPAddress address, int prefixLength)
    {
        var bytes = address.MapToIPv4().GetAddressBytes();
        var mask = MaskBytes(prefixLength);
        for (var i = 0; i < 4; i++) bytes[i] &= mask[i];
        return new IPAddress(bytes);
    }

    public static bool SameSubnet(IPAddress a, IPAddress b, int prefixLength)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork && !a.IsIPv4MappedToIPv6) return false;
        if (b.AddressFamily != AddressFamily.InterNetwork && !b.IsIPv4MappedToIPv6) return false;
        return NetworkOf(a, prefixLength).Equals(NetworkOf(b, prefixLength));
    }

    private static byte[] MaskBytes(int prefixLength)
    {
        prefixLength = Math.Clamp(prefixLength, 0, 32);
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return [(byte)(mask >> 24), (byte)(mask >> 16), (byte)(mask >> 8), (byte)mask];
    }

    /// <summary>RFC 1918 (10/8, 172.16/12, 192.168/16): what a home or office LAN uses.</summary>
    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>The shared address space of RFC 6598 (100.64/10): carrier-grade NAT, and the tailnet addresses of Tailscale.</summary>
    public static bool IsCgnat(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    /// <summary>A unique local IPv6 address (fc00::/7), what a VPN may hand out.</summary>
    public static bool IsUniqueLocalV6(IPAddress ip) =>
        ip.AddressFamily == AddressFamily.InterNetworkV6 && (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
}

/// <summary>
/// The rule that tells a real network from a virtual adapter or a VPN, from the interface's type, name and description. One rule for the TLS
/// certificate's names (<c>LocalCaService</c>) and for where the LAN listener answers (the plan, section 14: "virtual adapters MUST be excluded, as
/// LocalCaService already excludes them").
/// </summary>
public static class LanInterfaceClassifier
{
    private static readonly string[] VirtualWords =
        ["virtual", "pseudo", "docker", "hyper-v", "virtualbox", "vmware", "loopback", "vethernet", "vboxnet", "bridge", "veth", "awdl", "llw", "anpi", "ap1"];

    private static readonly string[] VpnWords = ["vpn", "tailscale", "zerotier", "wireguard", "wintun", "openvpn", "tap-windows", "utun", "ipsec", "ppp"];

    /// <summary>What the interface is, or null when it is not used at all (down, loopback).</summary>
    public static LanInterfaceKind? Classify(NetworkInterface ni)
    {
        if (ni.OperationalStatus != OperationalStatus.Up) return null;
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) return null;
        var text = (ni.Name + " " + ni.Description).ToLowerInvariant();
        if (VpnWords.Any(text.Contains) || ni.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
            return LanInterfaceKind.Vpn;
        if (VirtualWords.Any(text.Contains)) return LanInterfaceKind.Virtual;
        return LanInterfaceKind.Lan;
    }

    /// <summary>
    /// The kind of one address of an interface of <paramref name="kind"/>: an address in the tailnet range is a VPN, whatever the adapter is called; a
    /// "virtual" adapter that holds the default gateway is this computer's way to its network (a Hyper-V external switch, "vEthernet (External)"; a
    /// Linux bridge carrying the host's address), so it is the LAN. A VM's or a container's own adapter has no gateway on the host.
    /// </summary>
    public static LanInterfaceKind KindOf(LanInterfaceKind kind, IPAddress address, IPAddress? gateway) =>
        kind == LanInterfaceKind.Lan && LanSubnets.IsCgnat(address) ? LanInterfaceKind.Vpn
        : kind == LanInterfaceKind.Virtual && gateway is not null ? LanInterfaceKind.Lan
        : kind;
}

/// <summary>The interfaces as the system reports them now.</summary>
public sealed class SystemLanInterfaces : ILanInterfaces
{
    public IReadOnlyList<LanInterface> Current()
    {
        var result = new List<LanInterface>();
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return result; }

        foreach (var ni in interfaces)
        {
            LanInterfaceKind? kind;
            try { kind = LanInterfaceClassifier.Classify(ni); }
            catch (NetworkInformationException) { continue; }
            if (kind is null) continue;

            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }

            IPAddress? gateway = null;
            try
            {
                gateway = props.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { }

            foreach (var unicast in props.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) continue;
                // 169.254/16: no DHCP answer; nothing worth listening on.
                if (address.GetAddressBytes() is [169, 254, _, _]) continue;
                int prefix;
                try { prefix = unicast.PrefixLength; }
                catch (PlatformNotSupportedException) { prefix = 24; }
                if (prefix is <= 0 or > 32) prefix = 24;
                result.Add(new LanInterface(ni.Id, ni.Name, address, prefix, gateway, LanInterfaceClassifier.KindOf(kind.Value, address, gateway)));
            }
        }
        return result;
    }
}

/// <summary>
/// The fingerprint of the network an interface is on, where the system has no idea of a "private" network (a Mac, a Linux desktop; decision D1): a hash
/// of the interface's IPv4 prefix and its default gateway's address and hardware (MAC) address, read from the ARP table. No Wi-Fi name: since macOS 14
/// reading it needs the Location permission. A hostile network can copy all three (it pretends to be the home router); what it then reaches is the sync
/// surface, which answers only nodes of this network (docs/design/lan-peer-sync.md section 14).
/// </summary>
public static class NetworkFingerprints
{
    /// <summary>
    /// The fingerprint of <paramref name="lan"/>'s network, and a label a person can recognise it by. The fingerprint is null while the gateway's MAC
    /// address is not known yet (it is asked for, and the next look usually has it): an unfinished fingerprint never matches a trusted one.
    /// </summary>
    public static (string? Fingerprint, string Label) Of(LanInterface lan, Func<IPAddress, string?> macOf)
    {
        var network = $"{lan.Network}/{lan.PrefixLength}";
        if (lan.Kind == LanInterfaceKind.Vpn)
            return (Hash($"vpn|{lan.Name}|{network}"), $"{lan.Name} ({lan.Address})");
        if (lan.Gateway is not { } gateway)
            return (Hash($"lan|{network}|-|-"), $"{network} (no router)");
        var mac = macOf(gateway);
        var label = $"{network} (router {gateway})";
        return mac is null ? (null, label) : (Hash($"lan|{network}|{gateway}|{mac}"), label);
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("bmb-lan-v1|" + text)))[..20].ToLowerInvariant();

    /// <summary>
    /// The MAC address of <paramref name="ip"/> from this computer's ARP table, lower case, colon separated, every byte two digits; null when the table
    /// does not have it. On Linux <c>/proc/net/arp</c>; on a Mac <c>/usr/sbin/arp -n</c>. When it is missing, one UDP datagram is sent to the address so
    /// the system resolves it for the next look.
    /// </summary>
    public static string? GatewayMac(IPAddress ip)
    {
        // The listener looks at its networks every 30 seconds: a router's MAC address found once is kept for a few minutes instead of asking the
        // system (a process on a Mac) every time.
        lock (MacCache)
            if (MacCache.TryGetValue(ip, out var cached) && DateTime.UtcNow - cached.At < MacCacheFor) return cached.Mac;
        var found = LookUp(ip);
        if (found is not null)
            lock (MacCache)
            {
                if (MacCache.Count > 64) MacCache.Clear();
                MacCache[ip] = (found, DateTime.UtcNow);
            }
        return found;
    }

    private static readonly Dictionary<IPAddress, (string Mac, DateTime At)> MacCache = new();
    private static readonly TimeSpan MacCacheFor = TimeSpan.FromMinutes(5);

    private static string? LookUp(IPAddress ip)
    {
        string? mac = null;
        try
        {
            if (OperatingSystem.IsLinux()) mac = FromProcArp(ip);
            else if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()) mac = FromArpCommand(ip);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
        if (mac is null) Nudge(ip);
        return mac;
    }

    private static string? FromProcArp(IPAddress ip)
    {
        const string path = "/proc/net/arp";
        if (!File.Exists(path)) return null;
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var cols = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length >= 4 && cols[0] == ip.ToString()) return Normalize(cols[3]);
        }
        return null;
    }

    private static string? FromArpCommand(IPAddress ip)
    {
        var psi = new ProcessStartInfo("/usr/sbin/arp", ["-n", ip.ToString()])
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        if (proc is null) return null;
        var output = proc.StandardOutput.ReadToEnd();
        if (!proc.WaitForExit(3000))
        {
            try { proc.Kill(); } catch { /* best effort */ }
            return null;
        }
        // "? (192.168.1.1) at 0:11:22:33:44:55 on en0 ifscope [ethernet]"
        var at = output.IndexOf(" at ", StringComparison.Ordinal);
        if (at < 0) return null;
        var rest = output[(at + 4)..].Split(' ', 2)[0];
        return Normalize(rest);
    }

    /// <summary>"0:11:2:33:44:55" or "00-11-22-..." to "00:11:02:33:44:55"; null for anything that is not six bytes, or the all-zero "incomplete" entry.</summary>
    internal static string? Normalize(string raw)
    {
        var parts = raw.Split(':', '-');
        if (parts.Length != 6) return null;
        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
            if (!byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i])) return null;
        if (bytes.All(b => b == 0)) return null;
        return string.Join(':', bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }

    private static void Nudge(IPAddress ip)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Send([0], 1, new IPEndPoint(ip, 9));   // the discard port: makes the system resolve the router's MAC
        }
        catch (SocketException) { }
    }
}
