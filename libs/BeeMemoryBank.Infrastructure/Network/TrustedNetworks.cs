using System.Net;
using System.Text.Json;

namespace BeeMemoryBank.Infrastructure.Network;

/// <summary>Why a network is, or is not, one the sync surface answers on.</summary>
public static class LanTrustReasons
{
    /// <summary>Windows calls the network Private.</summary>
    public const string WindowsPrivate = "windows-private";
    /// <summary>Windows calls the network a domain network.</summary>
    public const string WindowsDomain = "windows-domain";
    /// <summary>Windows calls the network Public: direct sync is off there until the person changes the category in Windows Settings.</summary>
    public const string WindowsPublic = "windows-public";
    /// <summary>The person said yes for this network (or paired a device on it).</summary>
    public const string Trusted = "trusted";
    /// <summary>The person said "Not this network".</summary>
    public const string Refused = "refused";
    /// <summary>Nobody has been asked about this network yet; it is untrusted until somebody answers.</summary>
    public const string Unknown = "unknown";
    /// <summary>The router's hardware address is not known yet: looked at again in a moment.</summary>
    public const string Pending = "pending";
    /// <summary>A VPN the person has not opted in for (decision D7).</summary>
    public const string VpnOff = "vpn-off";
    /// <summary>A virtual adapter (a VM, WSL, Docker): never used.</summary>
    public const string Virtual = "virtual";
}

/// <summary>
/// One network this computer is on, as the LAN listener sees it.
/// </summary>
/// <param name="Interface">The interface and its address.</param>
/// <param name="Fingerprint">The network's fingerprint (<see cref="NetworkFingerprints"/>), the key of a person's answer; null while it is not known yet.</param>
/// <param name="Label">How the card names it.</param>
/// <param name="Trusted">The sync surface answers on this address.</param>
/// <param name="Reason">Why (<see cref="LanTrustReasons"/>).</param>
/// <param name="CanAsk">The person may be asked about it ("Sync with your paired devices on this network?").</param>
public sealed record LanNetworkView(LanInterface Interface, string? Fingerprint, string Label, bool Trusted, string Reason, bool CanAsk);

/// <summary>Which of this computer's networks the sync surface may answer on (docs/design/lan-peer-sync.md section 6).</summary>
public interface ITrustedNetworks
{
    /// <summary>Every usable address of this computer and whether it is trusted, now.</summary>
    IReadOnlyList<LanNetworkView> Evaluate();

    /// <summary>The person said yes ("Sync with your paired devices on this network") or no ("Not this network") for a network, or forgot their answer (null).</summary>
    void Answer(string fingerprint, string label, bool? trusted, string how);

    /// <summary>A device was paired through the network that holds <paramref name="address"/> (one of this computer's addresses, or the other side's): it is trusted.</summary>
    void TrustNetworkOf(IPAddress address, string how);

    /// <summary>What the person has answered so far (for the card's list, with "forget").</summary>
    IReadOnlyList<TrustedNetworkEntry> Answers();
}

/// <summary>
/// A person's answer about one network, as <see cref="TrustedNetworkStore"/> keeps it.
/// </summary>
/// <param name="Fingerprint">The network's fingerprint.</param>
/// <param name="Label">The name it had when the answer was given.</param>
/// <param name="Trusted">Yes or "Not this network".</param>
/// <param name="How">"paired" (a device was paired on it), "asked" (the person answered), "vpn" (the opt-in of a VPN).</param>
/// <param name="At">When.</param>
public sealed record TrustedNetworkEntry(string Fingerprint, string Label, bool Trusted, string How, DateTime At);

/// <summary>
/// The person's answers about networks, one small file per profile beside <c>network.settings.json</c>: <c>network.trusted.json</c>. Never carried with a
/// profile or put in a snapshot (it is in <c>VaultFiles</c>' never-carry list): a network trusted on one computer is that computer's business. The Node and
/// the Api both write it (the Api when this node joined another through a code), so a change is one read-modify-write under a lock file. A missing or
/// damaged file reads as "no answers" (every network untrusted until asked).
/// </summary>
public sealed class TrustedNetworkStore(string dataPath)
{
    public const string FileName = "network.trusted.json";
    private const int MaxEntries = 64;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private string FilePath => Path.Combine(dataPath, FileName);

    public IReadOnlyList<TrustedNetworkEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var dto = JsonSerializer.Deserialize<FileDto>(File.ReadAllText(FilePath), Json);
            return (dto?.Networks ?? []).Where(e => !string.IsNullOrEmpty(e.Fingerprint)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Sets (or with <paramref name="trusted"/> null removes) the answer for one fingerprint. The newest <see cref="MaxEntries"/> answers are kept.</summary>
    public void Set(string fingerprint, string label, bool? trusted, string how, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(fingerprint);
        Directory.CreateDirectory(dataPath);
        using var _ = AcquireLock();
        var entries = Load().Where(e => !string.Equals(e.Fingerprint, fingerprint, StringComparison.Ordinal)).ToList();
        if (trusted is { } yes) entries.Add(new TrustedNetworkEntry(fingerprint, Truncate(label, 120), yes, how, nowUtc));
        var kept = entries.OrderByDescending(e => e.At).Take(MaxEntries).OrderBy(e => e.At).ToList();
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new FileDto(kept), Json));
        File.Move(temp, FilePath, overwrite: true);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>A lock FILE, so the Node and the Api (two processes) never interleave a read-modify-write.</summary>
    private FileStream AcquireLock()
    {
        var path = FilePath + ".lock";
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 50) { Thread.Sleep(20); }
        }
    }

    private sealed record FileDto(List<TrustedNetworkEntry>? Networks);
}

/// <summary>
/// The trust of this computer's networks, decided per address: a virtual adapter never; a VPN only with the person's opt-in (D7); otherwise on Windows
/// the category Windows gives the network (Private and domain networks are trusted, Public ones are not), and on a Mac or Linux desktop - and on a Windows
/// network whose category could not be read - the person's answer for the network's fingerprint (D1): the network a device was paired on is trusted, any
/// other is untrusted until somebody says yes.
/// </summary>
public sealed class TrustedNetworks : ITrustedNetworks
{
    private readonly ILanInterfaces _interfaces;
    private readonly TrustedNetworkStore _store;
    private readonly IWindowsNetworkCategories? _windows;
    private readonly Func<IPAddress, string?> _macOf;
    private readonly Func<DateTime> _now;

    /// <param name="interfaces">The interfaces of this computer.</param>
    /// <param name="store">The person's answers.</param>
    /// <param name="windows">Windows' network categories; null where there are none (a Mac).</param>
    /// <param name="macOf">The ARP lookup of the router's hardware address (<see cref="NetworkFingerprints.GatewayMac"/> on a real computer).</param>
    public TrustedNetworks(ILanInterfaces interfaces, TrustedNetworkStore store, IWindowsNetworkCategories? windows,
        Func<IPAddress, string?>? macOf = null, Func<DateTime>? now = null)
    {
        _interfaces = interfaces;
        _store = store;
        _windows = windows;
        _macOf = macOf ?? NetworkFingerprints.GatewayMac;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>The one for this computer: Windows' categories on Windows, the fingerprints elsewhere.</summary>
    public static TrustedNetworks ForThisComputer(string dataPath) =>
        new(new SystemLanInterfaces(), new TrustedNetworkStore(dataPath), OperatingSystem.IsWindows() ? new WindowsNetworkCategories() : null);

    public IReadOnlyList<LanNetworkView> Evaluate()
    {
        var answers = _store.Load().ToDictionary(e => e.Fingerprint, StringComparer.Ordinal);
        IReadOnlyDictionary<string, WindowsNetworkCategory>? categories = null;
        if (_windows is not null)
        {
            try { categories = _windows.ByAdapterId(); }
            catch (Exception) { categories = null; }   // COM said no: the fingerprints and the person's answers decide, as on a Mac
        }

        var views = new List<LanNetworkView>();
        foreach (var lan in _interfaces.Current())
        {
            if (lan.Kind == LanInterfaceKind.Virtual)
            {
                views.Add(new LanNetworkView(lan, null, $"{lan.Name} ({lan.Address})", false, LanTrustReasons.Virtual, false));
                continue;
            }

            var (fingerprint, label) = NetworkFingerprints.Of(lan, _macOf);
            var answer = fingerprint is not null && answers.TryGetValue(fingerprint, out var a) ? a : null;

            if (lan.Kind == LanInterfaceKind.Vpn)
            {
                var on = answer?.Trusted == true;
                views.Add(new LanNetworkView(lan, fingerprint, label, on, on ? LanTrustReasons.Trusted : LanTrustReasons.VpnOff, fingerprint is not null));
                continue;
            }

            if (categories is not null && categories.TryGetValue(NormalizeAdapterId(lan.Id), out var category))
            {
                var (trusted, reason) = category switch
                {
                    WindowsNetworkCategory.Private => (true, LanTrustReasons.WindowsPrivate),
                    WindowsNetworkCategory.DomainAuthenticated => (true, LanTrustReasons.WindowsDomain),
                    _ => (false, LanTrustReasons.WindowsPublic),
                };
                views.Add(new LanNetworkView(lan, fingerprint, label, trusted, reason, false));
                continue;
            }

            if (fingerprint is null)
            {
                views.Add(new LanNetworkView(lan, null, label, false, LanTrustReasons.Pending, false));
                continue;
            }
            if (answer is { } given)
            {
                views.Add(new LanNetworkView(lan, fingerprint, label, given.Trusted,
                    given.Trusted ? LanTrustReasons.Trusted : LanTrustReasons.Refused, true));
                continue;
            }
            // Only a private range is worth asking about: a public address on a Wi-Fi is a network nobody should sync on directly.
            views.Add(new LanNetworkView(lan, fingerprint, label, false, LanTrustReasons.Unknown, LanSubnets.IsPrivate(lan.Address)));
        }
        return views;
    }

    public void Answer(string fingerprint, string label, bool? trusted, string how) => _store.Set(fingerprint, label, trusted, how, _now());

    public void TrustNetworkOf(IPAddress address, string how)
    {
        foreach (var view in Evaluate())
        {
            if (view.Fingerprint is null || view.Interface.Kind != LanInterfaceKind.Lan) continue;
            if (!view.Interface.Address.Equals(address) && !view.Interface.Contains(address)) continue;
            // Windows decides by its own category; nothing to remember there.
            if (view.Reason is LanTrustReasons.WindowsPrivate or LanTrustReasons.WindowsDomain or LanTrustReasons.WindowsPublic) continue;
            if (view.Reason == LanTrustReasons.Refused) continue;   // the person said no for this network: pairing on it does not overrule them
            _store.Set(view.Fingerprint, view.Label, true, how, _now());
        }
    }

    public IReadOnlyList<TrustedNetworkEntry> Answers() => _store.Load();

    /// <summary>Windows' adapter ids are GUIDs, written "{...}" by NetworkInterface and bare by COM: one form for both.</summary>
    internal static string NormalizeAdapterId(string id) => Guid.TryParse(id.Trim('{', '}'), out var g) ? g.ToString("D") : id.ToLowerInvariant();
}
