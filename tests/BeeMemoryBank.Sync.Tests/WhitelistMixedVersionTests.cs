using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using static BeeMemoryBank.Sync.Tests.WhitelistMesh;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Versions per concern are bookkeeping of each node: nothing on the wire changed. The whitelist events this version writes carry exactly the
/// members a 2.7.5 node reads, signed the same way, so a 2.7.5 node takes them as before; and the events a 2.7.5 node (or an older one, without
/// the members added since) writes are judged field by field here like any other, so the nodes of this version agree among themselves whatever
/// an older node sends them, in any order. (What an older node itself still does with crossing events is its own rule, one version per row:
/// docs/sync.md says what that leaves.)
/// </summary>
public class WhitelistMixedVersionTests : IAsyncLifetime
{
    private WhitelistMesh _mesh = null!;

    private static readonly Guid Tablet = Guid.Parse("dddddddd-0000-4000-8000-00000000c002");

    public async Task InitializeAsync() => _mesh = await CreateAsync(2);

    public async Task DisposeAsync() => await _mesh.DisposeAsync();

    // The members of each whitelist payload as release 2.7.5 writes and reads them (EventPayloads.cs of that release).
    private static readonly string[] AddMembers275 =
        ["node_id", "display_name", "public_key", "api_address", "can_generate_embeddings", "is_superadmin", "tls_spki", "tls_trust"];
    private static readonly string[] UpdateMembers275 = ["node_id", "api_address", "display_name", "is_superadmin", "tls_spki", "tls_trust"];
    private static readonly string[] RevokeMembers275 = ["node_id"];

    [Fact]
    public async Task TheEventsThisVersionWrites_CarryTheMembersOf275_AndNothingElse()
    {
        var author = _mesh[0];
        var add = await _mesh.AddAsync(author, "Phone", isSuperadmin: true, address: "https://phone.example");
        var update = await UpdateAsync(author, displayName: "Renamed", isSuperadmin: false, address: "https://moved.example", tlsTrust: BlindTrust.PublicCa);
        var revoke = await RevokeAsync(author);

        Members(add).Should().BeEquivalentTo(AddMembers275);
        Members(update).Should().BeEquivalentTo(UpdateMembers275);
        Members(revoke).Should().BeEquivalentTo(RevokeMembers275);
        foreach (var evt in new[] { add, update, revoke })
            Ed25519Signer.Verify((await author.NodeRepo.GetAsync())!.Ed25519PublicKey, EventSignature.BuildPayload(evt), evt.Signature)
                .Should().BeTrue("the signature covers what it always covered");
    }

    /// <summary>
    /// A sender from before the superadmin flag and the TLS mode (no <c>is_superadmin</c> in its update, no <c>tls_trust</c> anywhere): its update
    /// writes the fields it names and leaves the flag's version alone, so a promotion that crossed it stands, in every order.
    /// </summary>
    [Fact]
    public async Task EventsWithoutTheMembersAddedSince_AreJudgedFieldByField_InEveryOrder()
    {
        var observer = _mesh[1];
        var now = DateTime.UtcNow;
        // The phone is a superadmin node of this network, here acting as the older sender.
        await observer.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = Phone, DisplayName = "Older node", Ed25519PublicKey = _mesh.PhoneKey.Public, Status = WhitelistStatuses.Active, IsSuperadmin = true,
            CreatedAt = now, UpdatedAt = now
        });
        var tabletKey = Convert.ToBase64String(Ed25519Signer.GenerateKeyPair().publicKey);
        var add = Signed(EventTypes.WhitelistAdd, $$"""{"node_id":"{{Tablet}}","display_name":"Tablet","public_key":"{{tabletKey}}","api_address":null,"can_generate_embeddings":false}""", 100);
        var promote = Signed(EventTypes.WhitelistUpdate, $$"""{"node_id":"{{Tablet}}","api_address":null,"display_name":null,"is_superadmin":true}""", 105);
        var rename = Signed(EventTypes.WhitelistUpdate, $$"""{"node_id":"{{Tablet}}","api_address":null,"display_name":"Tablet renamed"}""", 110);
        SyncEvent[] history = [add, promote, rename];

        foreach (var order in Permutations([0, 1, 2]))
        {
            using (var conn = observer.Factory.CreateConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"DELETE FROM tbl_whitelist WHERE node_id = '{Tablet:D}' COLLATE NOCASE; DELETE FROM tbl_event WHERE node_id = '{Phone:D}' COLLATE NOCASE;";
                cmd.ExecuteNonQuery();
            }
            foreach (var i in order) (await observer.EventApplier.ApplyAsync(history[i])).Should().Be(EventApplyResult.Applied);
            var row = (await observer.WhitelistRepo.GetByNodeIdAsync(Tablet))!;
            (row.IsSuperadmin, row.DisplayName, row.VersionOf(WhitelistConcerns.Superadmin).LamportTs, row.VersionOf(WhitelistConcerns.Name).LamportTs)
                .Should().Be((true, "Tablet renamed", 105L, 110L), $"order {string.Join(",", order)}");
        }
    }

    private static IEnumerable<string> Members(SyncEvent evt) =>
        JsonDocument.Parse(evt.Payload).RootElement.EnumerateObject().Select(p => p.Name);

    private SyncEvent Signed(string type, string payload, long lamport)
    {
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = Phone, LamportTs = lamport, EventType = type, Payload = payload,
            ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow
        };
        evt.Signature = Ed25519Signer.Sign(_mesh.PhoneKey.Private, EventSignature.BuildPayload(evt));
        return evt;
    }
}
