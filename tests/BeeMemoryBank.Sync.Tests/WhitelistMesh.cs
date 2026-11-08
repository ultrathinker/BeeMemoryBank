using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Nodes that trust each other as superadmins, and one phone they decide about. Each author writes its own row the way the product does (the
/// event logged first, then the row stamped with the concerns the event carries), and an observer takes the events in any order.
/// </summary>
public sealed class WhitelistMesh : IAsyncDisposable
{
    public static readonly Guid Phone = Guid.Parse("dddddddd-0000-4000-8000-00000000c001");
    public readonly (byte[] Public, byte[] Private) PhoneKey = Ed25519Signer.GenerateKeyPair();

    public List<SyncTestFixture> Nodes { get; } = [];

    public static async Task<WhitelistMesh> CreateAsync(int count)
    {
        var mesh = new WhitelistMesh();
        for (var i = 0; i < count; i++) mesh.Nodes.Add(await NodeAsync("Node" + (char)('A' + i)));
        foreach (var host in mesh.Nodes)
            foreach (var peer in mesh.Nodes.Where(p => p != host))
                await TrustAsync(host, peer);
        return mesh;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var node in Nodes) await node.DisposeAsync();
    }

    public SyncTestFixture this[int i] => Nodes[i];

    public static RowVersion Version(SyncEvent evt) => new(evt.LamportTs, evt.NodeId);

    /// <summary>Moves <paramref name="node"/>'s clock past <paramref name="evt"/> without it seeing the event: what it writes next has a higher Lamport time.</summary>
    public static void AdvancePast(SyncTestFixture node, SyncEvent evt) => node.Clock.RaiseTo(evt.LamportTs + 1);

    public static Task<WhitelistEntry?> RowAsync(SyncTestFixture node) => node.WhitelistRepo.GetByNodeIdAsync(Phone, includeDeleted: true);

    /// <summary>Adds the phone on <paramref name="node"/> as an admin does: the event first, then the row with its version on every concern.</summary>
    public async Task<SyncEvent> AddAsync(SyncTestFixture node, string name, bool isSuperadmin = false, string? address = null)
    {
        var now = DateTime.UtcNow;
        var entry = new WhitelistEntry
        {
            NodeId = Phone, DisplayName = name, Ed25519PublicKey = PhoneKey.Public, ApiAddress = address,
            Status = WhitelistStatuses.Active, IsSuperadmin = isSuperadmin, CreatedAt = now, UpdatedAt = now
        };
        var version = await node.EventLogger.LogWhitelistAddAsync(entry);
        entry.StampLocal(version, WhitelistEventConcerns.OfAdd);
        if (await node.WhitelistRepo.GetByNodeIdAsync(Phone, includeDeleted: true) is null) await node.WhitelistRepo.CreateAsync(entry);
        else await node.WhitelistRepo.UpdateAsync(entry);
        return await LastAsync(node, EventTypes.WhitelistAdd);
    }

    public static async Task<SyncEvent> RevokeAsync(SyncTestFixture node)
    {
        var version = await node.EventLogger.LogWhitelistRevokeAsync(Phone);
        if (await node.WhitelistRepo.GetByNodeIdAsync(Phone, includeDeleted: true) is not null) await node.WhitelistRepo.RevokeAsync(Phone, version);
        return await LastAsync(node, EventTypes.WhitelistRevoke);
    }

    /// <summary>An update as the product writes one: the event, then the author's own row with the fields it names stamped (if it has a row).</summary>
    public static async Task<SyncEvent> UpdateAsync(SyncTestFixture node, string? displayName = null, bool? isSuperadmin = null,
        string? address = null, string? tlsSpki = null, string? tlsTrust = null)
    {
        var version = await node.EventLogger.LogWhitelistUpdateAsync(Phone, address, displayName, isSuperadmin, tlsSpki, tlsTrust);
        if (await node.WhitelistRepo.GetByNodeIdAsync(Phone, includeDeleted: true) is { } row)
        {
            if (displayName != null) row.DisplayName = displayName;
            if (isSuperadmin is { } flag) row.IsSuperadmin = flag;
            if (address != null) row.ApiAddress = WhitelistEventConcerns.StoredAddress(address);
            if (tlsTrust != null) (row.TlsTrust, row.TlsSpki) = (tlsTrust, tlsSpki);
            row.StampLocal(version, WhitelistEventConcerns.ForLocalUpdate(Phone, version.SourceNodeId, address, displayName, isSuperadmin, tlsSpki, tlsTrust));
            await node.WhitelistRepo.UpdateAsync(row);
        }
        return await LastAsync(node, EventTypes.WhitelistUpdate);
    }

    public static async Task<SyncEvent> LastAsync(SyncTestFixture node, string type) =>
        (await node.EventLogRepo.GetAfterSequenceAsync(0, 100_000)).Last(e => e.EventType == type);

    /// <summary>Applies <paramref name="evt"/> on <paramref name="node"/> unless it wrote it.</summary>
    public static async Task DeliverAsync(SyncTestFixture node, SyncEvent evt)
    {
        if ((await node.NodeRepo.GetAsync())!.NodeId == evt.NodeId) return;
        (await node.EventApplier.ApplyAsync(evt)).Should().Be(EventApplyResult.Applied);
    }

    /// <summary>Takes <paramref name="node"/> back to before it heard of the phone: no row, none of the events in its log.</summary>
    public static void Forget(SyncTestFixture node, IEnumerable<SyncEvent> events)
    {
        using var conn = node.Factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        var ids = events.Select(e => $"'{e.EventId:D}'");
        cmd.CommandText = $"DELETE FROM tbl_whitelist WHERE node_id = '{Phone:D}' COLLATE NOCASE; " +
                          $"DELETE FROM tbl_event WHERE lower(event_id) IN ({string.Join(",", ids)});";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Everything a node decides about the phone, the version of each concern included.</summary>
    public static async Task<string> StateAsync(SyncTestFixture node)
    {
        var row = await RowAsync(node);
        if (row is null) return "no row";
        var versions = string.Join(" ", WhitelistConcernSet.Each.Select(c => $"{c}={row.VersionOf(c).LamportTs}:{row.VersionOf(c).SourceNodeId.ToString()[..8]}"));
        return $"{row.Status} sa={row.IsSuperadmin} name='{row.DisplayName}' addr={row.ApiAddress ?? "-"} tls={row.TlsTrust ?? "-"}/{row.TlsSpki ?? "-"} "
               + $"row={row.LamportTs} key={(row.Ed25519PublicKey.Length > 0 ? "yes" : "no")} deleted={(row.DeletedAt is null ? "no" : "yes")} {versions}";
    }

    public static IEnumerable<int[]> Permutations(int[] items)
    {
        if (items.Length <= 1) { yield return items; yield break; }
        for (var i = 0; i < items.Length; i++)
            foreach (var rest in Permutations(items.Where((_, j) => j != i).ToArray()))
                yield return [items[i], .. rest];
    }

    /// <summary>
    /// <paramref name="observer"/> takes <paramref name="history"/> in every order, and must end on <paramref name="expected"/> (the state the
    /// events give, field by field, whatever the order) each time.
    /// </summary>
    public static async Task EveryOrderEndsOnAsync(SyncTestFixture observer, IReadOnlyList<SyncEvent> history, string expected)
    {
        foreach (var order in Permutations(Enumerable.Range(0, history.Count).ToArray()))
        {
            Forget(observer, history);
            foreach (var i in order) await DeliverAsync(observer, history[i]);
            (await StateAsync(observer)).Should().Be(expected, $"order {string.Join(",", order)}");
        }
    }

    private static async Task<SyncTestFixture> NodeAsync(string name)
    {
        var node = new ConcreteFixture();
        await node.InitializeAsync();
        await node.InitService.InitializeAsync("admin", name, "password" + name);
        await node.Session.UnlockAsync("password" + name);
        return node;
    }

    private static async Task TrustAsync(SyncTestFixture host, SyncTestFixture peer)
    {
        var identity = (await peer.NodeRepo.GetAsync())!;
        var now = DateTime.UtcNow;
        await host.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = identity.NodeId, DisplayName = identity.DisplayName, Ed25519PublicKey = identity.Ed25519PublicKey,
            Status = WhitelistStatuses.Active, IsSuperadmin = true, CreatedAt = now, UpdatedAt = now
        });
    }

    private sealed class ConcreteFixture : SyncTestFixture { }
}
