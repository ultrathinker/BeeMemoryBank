namespace BeeMemoryBank.Sync.Metadata;

public static class MetadataCapabilities
{
    public const string EnvelopeV1 = "metadata-envelope-v1";

    /// <summary>
    /// The node takes <c>node_endpoints</c> events (docs/design/lan-peer-sync.md section 8). A 2.7.5 node does not advertise it, and a LOCKED, encrypted
    /// 2.7.5 full node refuses an event type it does not know in its relay inbox - which would stall every push to it behind one announcement. So the
    /// event is never served (pull) or sent (push) to a peer without this capability; the cursor moves over it.
    /// </summary>
    public const string PeerEndpointsV1 = "peer-endpoints-v1";

    /// <summary>What this build advertises in identity, authenticate, report-position, my-standing and join.</summary>
    public static readonly IReadOnlyList<string> Advertised = [EnvelopeV1, PeerEndpointsV1];

    public static bool HasEnvelopeV1(IReadOnlyCollection<string>? capabilities) =>
        capabilities is not null && capabilities.Contains(EnvelopeV1);

    /// <summary>True when a peer that advertised <paramref name="capabilities"/> may be given a <c>node_endpoints</c> event.</summary>
    public static bool HasPeerEndpointsV1(IReadOnlyCollection<string>? capabilities) =>
        capabilities is not null && capabilities.Contains(PeerEndpointsV1);

    /// <summary>
    /// An event that a peer with <paramref name="capabilities"/> must not be given: today only <c>node_endpoints</c> to a peer without
    /// <see cref="PeerEndpointsV1"/>. One rule for the pull (the server) and the push (the client).
    /// </summary>
    public static bool IsWithheldFrom(string eventType, IReadOnlyCollection<string>? capabilities) =>
        eventType == EventTypes.NodeEndpoints && !HasPeerEndpointsV1(capabilities);

    /// <summary>Parses the comma-separated form used in query strings and tbl_whitelist.last_capabilities.</summary>
    public static IReadOnlyList<string> Parse(string? commaSeparated) =>
        string.IsNullOrWhiteSpace(commaSeparated)
            ? []
            : commaSeparated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>The protocol this node declares and accepts; per host, flipped once by the activation (spec 5.6).</summary>
public interface ISyncProtocolPolicy
{
    /// <summary>3 before activation, 4 after.</summary>
    int Declared { get; }

    /// <summary>True once this node has applied metadata_activation.</summary>
    bool SealsMetadata { get; }

    /// <summary>Before activation: declared >= 3. After: declared >= 4, or 3 with the envelope capability.</summary>
    bool IsCompatiblePeer(int? declaredProtocol, IReadOnlyCollection<string>? capabilities);

    /// <summary>False when the peer declares a protocol this build cannot apply (above 4).</summary>
    bool ShouldPullFrom(int? declaredProtocol);

    /// <summary>Called by the activation and by the metadata_activation applier. Idempotent.</summary>
    void MarkActivated();

    /// <summary>
    /// Forgets that the last read said "not activated", so that the next <see cref="SealsMetadata"/> reads the table again. For the caller that has just
    /// written the activation by another way than the applier (a restore, a join snapshot imports the table) and must not act on the answer of a few
    /// seconds ago. A policy that reads nothing has nothing to forget.
    /// </summary>
    void RecheckNow() { }
}
