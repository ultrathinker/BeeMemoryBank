using Makaretu.Dns;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Browses the local network for other BeeMemoryBank nodes advertising
/// <c>_beememorybank._tcp.local</c> and parses their TXT/SRV/address records into
/// <see cref="MdnsNodeRecord"/> DTOs.
/// </summary>
/// <remarks>
/// Each <see cref="DiscoverAsync"/> call performs a self-contained scan (it brings up its own
/// short-lived <see cref="ServiceDiscovery"/>, sends a PTR query, collects answers for the scan
/// window, then tears down). Concurrent scans are serialised — Makaretu's multicast sockets are
/// process-wide and don't tolerate parallel responders cleanly.
/// </remarks>
public sealed class MdnsBrowser
{
    private readonly ILogger<MdnsBrowser>? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public MdnsBrowser(ILogger<MdnsBrowser>? logger = null) => _logger = logger;

    /// <summary>
    /// Performs one bounded discovery scan and returns the distinct nodes currently visible on the
    /// LAN. Deduplicates by <c>nodeId</c> (falling back to the instance name when a TXT nodeId is
    /// absent). Returns an empty list if nothing is found — callers (the join wizard) must keep the
    /// manual-URL-entry path working when this is empty.
    /// </summary>
    /// <param name="scanTime">How long to listen for answers. Default 3s.</param>
    /// <param name="serviceType">Overridable for isolated tests; defaults to <see cref="MdnsConstants.ServiceType"/>.</param>
    public async Task<IReadOnlyList<MdnsNodeRecord>> DiscoverAsync(
        TimeSpan? scanTime = null,
        string serviceType = MdnsConstants.ServiceType,
        CancellationToken cancellationToken = default)
    {
        var scan = scanTime ?? TimeSpan.FromSeconds(3);
        var found = new Dictionary<string, MdnsNodeRecord>(StringComparer.Ordinal);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var sd = new ServiceDiscovery();
            sd.ServiceInstanceDiscovered += (_, e) =>
            {
                if (TryParse(e.Message, e.ServiceInstanceName, out var rec) && rec is not null)
                {
                    var key = rec.NodeId != Guid.Empty
                        ? rec.NodeId.ToString()
                        : e.ServiceInstanceName.ToString();
                    found[key] = rec;
                }
            };

            // Fire the PTR query for our service type. Makaretu re-sends automatically whenever a new
            // network interface comes up during the scan.
            sd.QueryServiceInstances(serviceType);

            try { await Task.Delay(scan, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }

            return found.Values.ToList();
        }
        catch (Exception ex) when (IsMulticastUnavailable(ex))
        {
            // Multicast socket bind failed (sandboxed/CI environment, or no usable interface).
            // Discovery simply finds nothing — the manual-URL path covers this case.
            _logger?.LogWarning(ex,
                "mDNS browse unavailable in this environment (multicast socket bind failed); returning no nodes");
            return Array.Empty<MdnsNodeRecord>();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Parses a discovered DNS message (the one delivered with
    /// <see cref="ServiceDiscovery.ServiceInstanceDiscovered"/>) into a node record by extracting the
    /// SRV (port + target), TXT (metadata) and A/AAAA (host IP) records keyed by the instance name.
    /// </summary>
    /// <remarks>
    /// Public + static so it is unit-testable with a constructed <see cref="Message"/> (no network).
    /// </remarks>
    public static bool TryParse(Message message, DomainName instanceName, out MdnsNodeRecord? record)
    {
        record = null;
        var instanceStr = instanceName.ToString();

        // Records for this instance can live in either Answers or AdditionalRecords of the mDNS reply.
        var records = message.Answers.Concat(message.AdditionalRecords).ToList();

        var srv = records.OfType<SRVRecord>().FirstOrDefault(r => NameEquals(r.Name, instanceStr));
        var txt = records.OfType<TXTRecord>().FirstOrDefault(r => NameEquals(r.Name, instanceStr));

        var props = ParseTxt(txt);
        Guid.TryParse(props.GetValueOrDefault(MdnsConstants.TxtNodeId), out var nodeId);
        var name = props.GetValueOrDefault(MdnsConstants.TxtName) ?? instanceStr;
        var version = props.GetValueOrDefault(MdnsConstants.TxtVersion) ?? "";
        bool.TryParse(props.GetValueOrDefault(MdnsConstants.TxtHttps), out var https);

        // Resolve the host: prefer an A/AAAA keyed by the SRV target hostname, then by the instance
        // name, then fall back to the target hostname itself (a .local name) as a last resort.
        string? host = null;
        var targetStr = srv?.Target?.ToString();
        if (targetStr is not null)
        {
            var byTarget = records.OfType<AddressRecord>().FirstOrDefault(r => NameEquals(r.Name, targetStr));
            host = byTarget?.Address.ToString();
        }
        if (host is null)
        {
            var byInstance = records.OfType<AddressRecord>().FirstOrDefault(r => NameEquals(r.Name, instanceStr));
            host = byInstance?.Address.ToString();
        }
        host ??= targetStr;

        var port = srv?.Port ?? 0;

        // Need at least a reachable host:port to be useful to the join wizard.
        if (host is null || port == 0)
            return false;

        record = new MdnsNodeRecord(nodeId, name, version, https, host, port);
        return true;
    }

    private static Dictionary<string, string> ParseTxt(TXTRecord? txt)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (txt is null) return dict;
        foreach (var entry in txt.Strings)
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            var key = entry[..eq];
            var val = entry[(eq + 1)..];
            dict[key] = val;
        }
        return dict;
    }

    private static bool NameEquals(DomainName? name, string expected)
        => string.Equals(name?.ToString(), expected, StringComparison.Ordinal);

    private static bool IsMulticastUnavailable(Exception ex)
    {
        // SocketException on bind (no multicast-capable interface / sandboxed socket namespace) is the
        // expected failure mode in restricted environments.
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException) return true;
        }
        return false;
    }
}
