using System.Security.Cryptography;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Boot;
using Dapper;

namespace BeeMemoryBank.Storage.Tests.Boot;

/// <summary>The four stores of the boot store and the one transaction that rewrites it (docs/design/step-b-spec.md 2.3, 3.4, 4.3, 6.2, 6.3).</summary>
public class BootStoresTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_boot_stores_" + Guid.NewGuid().ToString("N"));
    private BootStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _store = new BootStore(Path.Combine(_dir, "beememorybank.boot.db"));
        await _store.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private static string Fp() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private static BootDoor Slot(long id, string type = "user", string? fp = null, byte seed = 1)
    {
        var wrapped = new byte[49]; wrapped[0] = seed;
        var iv = new byte[12]; iv[0] = seed;
        var salt = new byte[32]; salt[0] = seed;
        return new BootDoor(0, BootDoorKinds.Slot, id, type, wrapped, iv, salt, 65536, 3, 4, null, null,
            BootDoorHash.OfSlot(id, type, wrapped, iv, salt, 65536, 3, 4), fp, false);
    }

    private static BootDoor Agent(long id, string keyHash, string? fp = null)
    {
        var wrapped = new byte[49]; var iv = new byte[12]; var salt = new byte[16];
        return new BootDoor(0, BootDoorKinds.Agent, id, null, wrapped, iv, salt, null, null, null, keyHash, 1,
            BootDoorHash.OfAgent(id, keyHash, 1, salt, wrapped, iv), fp, false);
    }

    private static BootDbKeyRow Wrapper(string fp, int generation = 1) =>
        new(fp, generation, RandomNumberGenerator.GetBytes(48), RandomNumberGenerator.GetBytes(12), DateTime.UtcNow);

    private BootRewrite Plan(IReadOnlyList<BootDoor> doors, string? current = null, IReadOnlyCollection<string>? candidates = null,
        IReadOnlyList<BootDbKeyRow>? add = null, BootIdentity? identity = null,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? whitelist = null, string version = "1") =>
        new(doors, current, candidates ?? [], add ?? [], identity, whitelist ?? [], version);

    [Fact]
    public async Task APendingDoorIsListedAndRetiringDeletesEveryCopy()
    {
        var doors = new BootDoorStore(_store.Connections);
        var first = Slot(5, seed: 1);
        var replacement = Slot(5, seed: 2);

        var id1 = await doors.AddPendingAsync(first);
        var id2 = await doors.AddPendingAsync(replacement);
        (await doors.AddPendingAsync(first)).Should().Be(id1, "the same content is one row");

        var listed = await doors.ListAsync();
        listed.Should().HaveCount(2).And.OnlyContain(d => d.Pending);
        id2.Should().NotBe(id1);
        listed[0].EncryptedMasterDek.Should().Equal(first.EncryptedMasterDek);
        listed[0].ArgonMemory.Should().Be(65536);
        listed[0].SourceId.Should().Be(5);

        await doors.RetireAsync(BootDoorKinds.Slot, 5);
        (await doors.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AgentsAreFoundByKeyHash_AndADoorRecordsTheFingerprintOfTheDekItYielded()
    {
        var doors = new BootDoorStore(_store.Connections);
        await doors.AddPendingAsync(Agent(9, "hash-a"));
        await doors.AddPendingAsync(Agent(10, "hash-b"));
        await doors.AddPendingAsync(Slot(1));

        var found = await doors.FindAgentAsync("hash-a");
        found.Should().ContainSingle().Which.SourceId.Should().Be(9);
        (await doors.FindAgentAsync("nope")).Should().BeEmpty();

        var fp = Fp();
        await doors.SetDekFingerprintAsync(found[0].DoorId, fp);
        (await doors.FindAgentAsync("hash-a"))[0].DekFingerprint.Should().Be(fp);
    }

    [Fact]
    public async Task ARewriteReplacesTheDoors_KeepsKnownFingerprintsOfUnchangedDoors_AndStoresTheMirroredVersion()
    {
        var doors = new BootDoorStore(_store.Connections);
        var writer = new BootStoreWriter(_store.Connections);
        var fp = Fp();
        var unchanged = Slot(1, fp: null);
        var changed = Slot(2, seed: 1);
        await doors.AddPendingAsync(unchanged with { DekFingerprint = fp });
        await doors.AddPendingAsync(changed with { DekFingerprint = fp });

        // The sealed database now holds slot 1 as before and slot 2 with new content, and has no slot 3 any more.
        var newer = Slot(2, seed: 7);
        var result = await writer.RewriteAsync(Plan([unchanged, newer], current: fp, version: "42"));

        var listed = await doors.ListAsync();
        listed.Should().HaveCount(2).And.OnlyContain(d => !d.Pending);
        listed.Single(d => d.SourceId == 1).DekFingerprint.Should().Be(fp, "a door whose content did not change keeps its known fingerprint");
        listed.Single(d => d.SourceId == 2).DekFingerprint.Should().BeNull("a replaced door's fingerprint is unknown until an unlock records it");
        (await _store.GetMetaAsync(BootMetaKeys.UnlockMaterialVersion)).Should().Be("42");
        result.Doors.Should().Be(2);
    }

    [Fact]
    public async Task WrappersKeptAreTheCurrentDeksAndThoseOfKnownDoors_EveryOtherIsDeleted()
    {
        var writer = new BootStoreWriter(_store.Connections);
        var keys = new BootDbKeyStore(_store.Connections);
        var oldFp = Fp(); var newFp = Fp(); var strayFp = Fp();
        await keys.AddIfMissingAsync(Wrapper(oldFp));
        await keys.AddIfMissingAsync(Wrapper(newFp));
        await keys.AddIfMissingAsync(Wrapper(strayFp));

        // Every door is known: one yields the new DEK. The stray and the old wrapper go.
        var result = await writer.RewriteAsync(Plan([Slot(1, fp: newFp)], current: newFp));

        (await keys.ListAsync()).Select(r => r.DekFingerprint).Should().Equal(newFp);
        result.WrappersPruned.Should().Be(2);
    }

    [Fact]
    public async Task ADoorOfUnknownFingerprintKeepsTheWrappersOfEveryDekTheSessionHolds_AndWithoutASessionNothingIsDeleted()
    {
        var writer = new BootStoreWriter(_store.Connections);
        var keys = new BootDbKeyStore(_store.Connections);
        var oldFp = Fp(); var newFp = Fp(); var strayFp = Fp();
        await keys.AddIfMissingAsync(Wrapper(oldFp));
        await keys.AddIfMissingAsync(Wrapper(newFp));
        await keys.AddIfMissingAsync(Wrapper(strayFp));

        // Locked or closing: the door's DEK is unknown and the session holds none -> nothing may be deleted.
        await writer.RewriteAsync(Plan([Slot(1)], current: null, candidates: []));
        (await keys.ListAsync()).Should().HaveCount(3);

        // Unlocked: the session holds the new and the old DEK -> those two stay, the stray goes.
        await writer.RewriteAsync(Plan([Slot(1)], current: newFp, candidates: [newFp, oldFp]));
        (await keys.ListAsync()).Select(r => r.DekFingerprint).Should().BeEquivalentTo([oldFp, newFp]);
    }

    [Fact]
    public async Task AWrapperAHeldDoorNeedsIsNeverDeleted_EvenWhenTheSessionLacksThatDek()
    {
        var writer = new BootStoreWriter(_store.Connections);
        var keys = new BootDbKeyStore(_store.Connections);
        var heldFp = Fp(); var currentFp = Fp();
        await keys.AddIfMissingAsync(Wrapper(heldFp));
        await keys.AddIfMissingAsync(Wrapper(currentFp));

        await writer.RewriteAsync(Plan([Slot(1, fp: heldFp)], current: currentFp));

        (await keys.ListAsync()).Select(r => r.DekFingerprint).Should().BeEquivalentTo([heldFp, currentFp]);
    }

    [Fact]
    public async Task AWrapperIsNeverAddedTwiceForTheSameFingerprintAndGeneration()
    {
        var keys = new BootDbKeyStore(_store.Connections);
        var fp = Fp();
        var first = Wrapper(fp);
        (await keys.AddIfMissingAsync(first)).Should().BeTrue();
        (await keys.AddIfMissingAsync(Wrapper(fp))).Should().BeFalse();
        (await keys.FindAsync(fp, 1))!.Wrapped.Should().Equal(first.Wrapped, "the first row stays");
        (await keys.FindAsync(fp, 2)).Should().BeNull();
        (await keys.FindAsync(Fp(), 1)).Should().BeNull();
    }

    [Fact]
    public async Task TheMirrorsServeTheIdentityAndTheActivePeers_AndASuspensionEndsAtTheNextRewrite()
    {
        var writer = new BootStoreWriter(_store.Connections);
        var mirror = new BootMirrorStore(_store.Connections);
        var node = Guid.NewGuid(); var peer = Guid.NewGuid(); var revoked = Guid.NewGuid();
        var peerKey = RandomNumberGenerator.GetBytes(32);
        IReadOnlyDictionary<string, object?> Row(Guid id, string status, bool superadmin, byte[] key) => new Dictionary<string, object?>
        {
            ["node_id"] = id.ToString("D"), ["display_name"] = "n", ["ed25519_public_key"] = key, ["status"] = status,
            ["is_superadmin"] = superadmin ? 1 : 0, ["created_at"] = "2026-01-01T00:00:00.0000000Z", ["updated_at"] = "2026-01-01T00:00:00.0000000Z",
            ["unknown_column_is_ignored"] = 5,
        };
        var rows = new[] { Row(peer, "A", true, peerKey), Row(revoked, "R", false, peerKey) };
        var identityKey = RandomNumberGenerator.GetBytes(32);

        await writer.RewriteAsync(Plan([], identity: new BootIdentity(node, "Home", identityKey), whitelist: rows));

        var identity = await mirror.GetIdentityAsync();
        identity.Should().NotBeNull();
        identity!.NodeId.Should().Be(node);
        identity.DisplayName.Should().Be("Home");
        identity.Ed25519PublicKey.Should().Equal(identityKey);

        var active = await mirror.GetActivePeerAsync(peer);
        active!.Value.IsSuperadmin.Should().BeTrue();
        active.Value.PublicKey.Should().Equal(peerKey);
        (await mirror.GetActivePeerAsync(revoked)).Should().BeNull();
        (await mirror.GetActivePeerAsync(Guid.NewGuid())).Should().BeNull();

        await mirror.SuspendAsync(peer);
        (await mirror.GetActivePeerAsync(peer)).Should().BeNull("suspended until the next rewrite");

        await writer.RewriteAsync(Plan([], identity: new BootIdentity(node, "Home", identityKey), whitelist: rows));
        (await mirror.GetActivePeerAsync(peer)).Should().NotBeNull();
    }

    [Fact]
    public async Task ABlindPeerNeverCarriesAuthorityWhateverItsRowSays()
    {
        var writer = new BootStoreWriter(_store.Connections);
        var mirror = new BootMirrorStore(_store.Connections);
        var blind = BlindNodeId.NewId();
        await writer.RewriteAsync(Plan([], whitelist: [new Dictionary<string, object?>
        {
            ["node_id"] = blind.ToString("D"), ["display_name"] = "b", ["ed25519_public_key"] = new byte[32], ["status"] = "A",
            ["is_superadmin"] = 1, ["created_at"] = "x", ["updated_at"] = "x",
        }]));

        (await mirror.GetActivePeerAsync(blind))!.Value.IsSuperadmin.Should().BeFalse();
    }

    [Fact]
    public async Task ARewriteIsOneTransaction_AFailureHalfwayLeavesEverythingAsItWas()
    {
        var doors = new BootDoorStore(_store.Connections);
        var writer = new BootStoreWriter(_store.Connections);
        await doors.AddPendingAsync(Slot(1));

        // A whitelist row without the NOT NULL display_name fails after the doors were already replaced inside the transaction.
        var act = async () => await writer.RewriteAsync(Plan([Slot(2)], whitelist: [new Dictionary<string, object?> { ["node_id"] = "x" }]));
        await act.Should().ThrowAsync<Exception>();

        (await doors.ListAsync()).Should().ContainSingle().Which.SourceId.Should().Be(1);
    }

    [Fact]
    public async Task TheInboxNumbersEventsFromTheFloor_ServesThemAfterAPosition_AndDeduplicates()
    {
        var inbox = new RelayInbox(_store.Connections);
        await _store.SetMetaAsync(BootMetaKeys.SeqFloor, "1000");
        var peer = Guid.NewGuid();

        var e1 = Event("a"); var e2 = Event("b"); var e3 = Event("c");
        (await inbox.AppendAsync(e1, peer, 7)).Should().Be(1000);
        (await inbox.AppendAsync(e2, peer, 8)).Should().Be(1001);
        (await inbox.AppendAsync(e1, peer, 7)).Should().Be(1000, "the stored sequence when the id is there");
        await _store.SetMetaAsync(BootMetaKeys.SeqFloor, "5000");
        (await inbox.AppendAsync(e3, null, null)).Should().Be(5000, "max(floor, max+1)");

        var all = await inbox.ReadAfterAsync(0, 10);
        all.Select(r => r.RelaySeq).Should().Equal(1000, 1001, 5000);
        all[0].Event.SequenceNum.Should().Be(1000, "served with this node's sequence numbers");
        all[0].Event.EventId.Should().Be(e1.EventId);
        all[0].Event.Payload.Should().Be(e1.Payload);
        all[0].Event.Signature.Should().Equal(e1.Signature);
        all[0].Event.CreatedAt.Should().Be(e1.CreatedAt);
        all[0].OriginPeer.Should().Be(peer);
        all[0].OriginSeq.Should().Be(7);
        all[2].OriginPeer.Should().BeNull();

        (await inbox.ReadAfterAsync(1000, 10)).Select(r => r.RelaySeq).Should().Equal(1001, 5000);
        (await inbox.ReadAfterAsync(0, 2)).Should().HaveCount(2);

        await inbox.DeleteAsync([e2.EventId]);
        (await inbox.ReadAfterAsync(0, 10)).Select(r => r.RelaySeq).Should().Equal(1000, 5000);
    }

    [Fact]
    public async Task TheInboxKeepsBlobsAndTheMaximumReportOfEachPeer_AndSummarisesBoth()
    {
        var inbox = new RelayInbox(_store.Connections);
        var summary0 = await inbox.GetSummaryAsync();
        summary0.Should().Be(new RelayInboxSummary(0, 0, 0, 0, null, 0));

        await inbox.AppendAsync(Event("payload-12345"), null, null);
        await inbox.StoreBlobAsync("hash-1", [1, 2, 3]);
        await inbox.StoreBlobAsync("hash-1", [9, 9, 9]);
        await inbox.StoreBlobAsync("hash-2", [4, 5]);
        (await inbox.GetBlobAsync("hash-1")).Should().Equal(1, 2, 3);
        (await inbox.GetBlobAsync("nope")).Should().BeNull();
        (await inbox.ListBlobHashesAsync()).Should().BeEquivalentTo(["hash-1", "hash-2"]);

        var summary = await inbox.GetSummaryAsync();
        summary.Events.Should().Be(1);
        summary.EventBytes.Should().Be("payload-12345".Length);
        summary.Blobs.Should().Be(2);
        summary.BlobBytes.Should().Be(5);
        summary.OldestReceivedAt.Should().NotBeNull().And.BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        summary.MaxRelaySeq.Should().BeGreaterThan(0);

        await inbox.DeleteBlobsAsync(["hash-1"]);
        (await inbox.ListBlobHashesAsync()).Should().Equal("hash-2");

        var peer = Guid.NewGuid();
        await inbox.RecordReportAsync(peer, 10);
        await inbox.RecordReportAsync(peer, 7);
        await inbox.RecordReportAsync(peer, 12);
        (await inbox.ListReportsAsync()).Should().Equal([(peer, 12L)]);
        await inbox.ClearReportsAsync();
        (await inbox.ListReportsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task TwoWritersNeverGetTheSameRelaySequence()
    {
        var inbox = new RelayInbox(_store.Connections);
        var events = Enumerable.Range(0, 40).Select(i => Event("p" + i)).ToList();
        var numbers = await Task.WhenAll(events.Select(e => Task.Run(() => inbox.AppendAsync(e, null, null))));
        numbers.Should().OnlyHaveUniqueItems();
        (await inbox.ReadAfterAsync(0, 100)).Should().HaveCount(40);
    }

    private static SyncEvent Event(string payload) => new()
    {
        EventId = Guid.NewGuid(),
        NodeId = Guid.NewGuid(),
        LamportTs = 5,
        EventType = "metadata_key_set",
        ArticleId = Guid.NewGuid(),
        Payload = payload,
        Signature = RandomNumberGenerator.GetBytes(64),
        ProtocolVersion = 4,
        CreatedAt = new DateTime(2026, 10, 10, 8, 30, 0, DateTimeKind.Utc).AddTicks(1234),
    };
}
