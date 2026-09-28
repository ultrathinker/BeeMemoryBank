using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Recovery;
using Dapper;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Appliers of the five blind-node recovery events (plan 6.4, 5.5). The receiving node is never
/// unlocked: a blind node has no DEK, and these appliers must work there unchanged.
/// </summary>
public class RecoveryEventApplierTests : IAsyncLifetime
{
    private readonly ConcreteFixture _node = new();
    private readonly Peer _pc = new("pc", superadmin: true);
    private readonly Peer _phone = new("phone", superadmin: false);
    private readonly Peer _laptop = new("laptop", superadmin: false);

    private static readonly string FpA = new('a', 64);
    private static readonly string FpB = new('b', 64);

    public async Task InitializeAsync()
    {
        await _node.InitializeAsync();
        foreach (var peer in new[] { _pc, _phone, _laptop })
            await _node.WhitelistRepo.CreateAsync(peer.Entry());
        _node.Session.IsUnlocked.Should().BeFalse("the receiver plays a blind node: no DEK");
    }

    public Task DisposeAsync() => _node.DisposeAsync();

    // --- recovery_box_set -------------------------------------------------------------------

    [Fact]
    public async Task BoxSet_OwnBox_IsStoredActive()
    {
        var box = Guid.NewGuid();

        await Apply(_phone.BoxSet(box, "device", "d64t3", FpA, lamport: 10));

        var row = await Box(box);
        row.Should().NotBeNull();
        row!.Status.Should().Be("A");
        row.Kind.Should().Be("device");
        row.AuthorNodeId.Should().BeEquivalentTo(_phone.Id.ToString());
        row.DekFingerprint.Should().Be(FpA);
    }

    [Fact]
    public async Task BoxSet_SomeoneElsesBox_IsRejected()
    {
        // The phone signs a box that claims to be the PC's: it would replace the PC's strong box.
        var box = Guid.NewGuid();
        var evt = _phone.BoxSet(box, "strong", "s1024t4", FpA, lamport: 10, author: _pc.Id);

        var act = () => Apply(evt);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await Box(box)).Should().BeNull();
    }

    [Theory]
    [InlineData("device", "s1024t4")]
    [InlineData("device", "s512t6")]
    [InlineData("strong", "d64t3")]
    [InlineData("strong", "s4096t8")]
    [InlineData("weird", "d64t3")]
    public async Task BoxSet_PresetNotAllowedForKind_IsRejected(string kind, string preset)
    {
        var box = Guid.NewGuid();

        var act = () => Apply(_phone.BoxSet(box, kind, preset, FpA, lamport: 10));

        await act.Should().ThrowAsync<InvalidDataException>();
        (await Box(box)).Should().BeNull();
    }

    [Fact]
    public async Task BoxSet_MalformedMaterial_IsRejected()
    {
        var box = Guid.NewGuid();

        var act = () => Apply(_phone.BoxSet(box, "device", "d64t3", FpA, lamport: 10, saltBytes: 8));

        await act.Should().ThrowAsync<InvalidDataException>();
        (await Box(box)).Should().BeNull();
    }

    [Theory]
    [InlineData(49, 0x07)] // unknown version byte
    [InlineData(48, 0x02)] // the unversioned legacy wrap: only a device box (a slot copy) may have it
    public async Task BoxSet_StrongBoxWithAWrapTheUnwrapWouldRefuse_IsRejected(int length, byte first)
    {
        var box = Guid.NewGuid();
        var evt = _pc.BoxSet(box, "strong", "s1024t4", FpA, lamport: 10, wrappedVersion: first, wrappedLength: length);

        var act = () => Apply(evt);

        await act.Should().ThrowAsync<InvalidDataException>();
        (await Box(box)).Should().BeNull();
    }

    [Fact]
    public async Task BoxSet_NewerBoxOfSameAuthorAndKind_SupersedesOlder()
    {
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();

        await Apply(_phone.BoxSet(older, "device", "d64t3", FpA, lamport: 10));
        await Apply(_phone.BoxSet(newer, "device", "d64t3", FpB, lamport: 20));

        (await Box(older))!.Status.Should().Be("R");
        (await Box(newer))!.Status.Should().Be("A");
    }

    [Fact]
    public async Task BoxSet_LateOlderBox_ArrivesAlreadySuperseded()
    {
        // Same two events, opposite arrival order: the result must be identical.
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();

        await Apply(_phone.BoxSet(newer, "device", "d64t3", FpB, lamport: 20));
        await Apply(_phone.BoxSet(older, "device", "d64t3", FpA, lamport: 10));

        (await Box(older))!.Status.Should().Be("R");
        (await Box(newer))!.Status.Should().Be("A");
    }

    [Fact]
    public async Task BoxSet_DifferentKindsAndAuthors_DoNotSupersedeEachOther()
    {
        var pcStrong = Guid.NewGuid();
        var pcDevice = Guid.NewGuid();
        var phoneDevice = Guid.NewGuid();

        await Apply(_pc.BoxSet(pcStrong, "strong", "s1024t4", FpA, lamport: 10));
        await Apply(_pc.BoxSet(pcDevice, "device", "d64t3", FpA, lamport: 11));
        await Apply(_phone.BoxSet(phoneDevice, "device", "d64t3", FpA, lamport: 12));

        (await ActiveBoxes()).Should().BeEquivalentTo([pcStrong, pcDevice, phoneDevice]);
    }

    [Fact]
    public async Task BoxSet_ResentAfterRetire_DoesNotResurrect()
    {
        var strong = Guid.NewGuid();
        var device = Guid.NewGuid();
        await Apply(_pc.BoxSet(strong, "strong", "s1024t4", FpA, lamport: 10));
        await Apply(_phone.BoxSet(device, "device", "d64t3", FpA, lamport: 11));
        await Apply(_pc.Retire([device], strong, lamport: 12));
        (await Box(device))!.Status.Should().Be("R");

        // A fresh event (new id, higher Lamport) carrying the same box id.
        await Apply(_phone.BoxSet(device, "device", "d64t3", FpA, lamport: 30));

        (await Box(device))!.Status.Should().Be("R");
    }

    [Fact]
    public async Task BoxSet_KeepsOnlyTheNewestInactiveBoxesPerAuthorAndKind()
    {
        // A peer publishing box after box: the active one plus the ten newest superseded stay.
        var boxes = Enumerable.Range(1, 15).Select(_ => Guid.NewGuid()).ToList();
        for (var i = 0; i < boxes.Count; i++)
            await Apply(_phone.BoxSet(boxes[i], "device", "d64t3", FpA, lamport: 100 + i));
        var pcStrong = Guid.NewGuid();
        await Apply(_pc.BoxSet(pcStrong, "strong", "s1024t4", FpA, lamport: 50));

        using var conn = _node.Factory.CreateConnection();
        var phoneRows = (await conn.QueryAsync<string>(
            "SELECT box_id FROM tbl_recovery_box WHERE author_node_id = @A COLLATE NOCASE",
            new { A = _phone.Id.ToString() })).Select(Guid.Parse).ToList();
        phoneRows.Should().BeEquivalentTo(boxes.Skip(4), "the active box and the 10 newest superseded ones");
        (await Box(boxes[^1]))!.Status.Should().Be("A");
        (await Box(pcStrong))!.Status.Should().Be("A", "another author's box is untouched");

        // A trimmed box sent again is stored superseded, never active.
        await Apply(_phone.BoxSet(boxes[0], "device", "d64t3", FpA, lamport: 100));
        (await Box(boxes[^1]))!.Status.Should().Be("A");
        (await ActiveBoxes()).Should().NotContain(boxes[0]);
    }

    // --- an inactive box keeps no key material (F7) -----------------------------------------
    // A box stops being active when a newer one supersedes it or a retire covers it. From then on the
    // old password must not open anything this node holds: not the row, and not the logged event that
    // carried the same bytes and would be served to every peer that pulls.

    [Fact]
    public async Task Supersede_DestroysTheOlderBoxMaterial_InTheRowAndTheEventLog()
    {
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();

        await Apply(_phone.BoxSet(older, "device", "d64t3", FpA, lamport: 10));
        await Apply(_phone.BoxSet(newer, "device", "d64t3", FpB, lamport: 20));

        (await MaterialLengths(older)).Should().Be((0, 0, 0));
        (await LoggedBoxIds()).Should().NotContain(older);
        (await MaterialLengths(newer)).Should().Be((32, 49, 12), "the active box is untouched");
        (await LoggedBoxIds()).Should().Contain(newer);
    }

    [Fact]
    public async Task LateOlderBox_IsStoredWithoutMaterial_AndNotKeptInTheLog()
    {
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();

        await Apply(_phone.BoxSet(newer, "device", "d64t3", FpB, lamport: 20));
        await Apply(_phone.BoxSet(older, "device", "d64t3", FpA, lamport: 10));

        (await Box(older))!.Status.Should().Be("R");
        (await MaterialLengths(older)).Should().Be((0, 0, 0));
        (await LoggedBoxIds()).Should().BeEquivalentTo([newer]);
    }

    [Fact]
    public async Task Retire_DestroysTheTargetMaterial_InTheRowAndTheEventLog()
    {
        var strong = Guid.NewGuid();
        var device = Guid.NewGuid();
        await Apply(_pc.BoxSet(strong, "strong", "s1024t4", FpA, lamport: 10));
        await Apply(_phone.BoxSet(device, "device", "d64t3", FpA, lamport: 11));

        await Apply(_pc.Retire([device], strong, lamport: 12));

        (await Box(device))!.Status.Should().Be("R");
        (await MaterialLengths(device)).Should().Be((0, 0, 0));
        (await LoggedBoxIds()).Should().BeEquivalentTo([strong]);
    }

    [Fact]
    public async Task DeferredRetire_DestroysTheMaterialWhenItApplies()
    {
        var strong = Guid.NewGuid();
        var device = Guid.NewGuid();
        await Apply(_phone.BoxSet(device, "device", "d64t3", FpA, lamport: 11));
        await Apply(_pc.Retire([device], strong, lamport: 12));
        (await MaterialLengths(device)).Should().Be((32, 49, 12), "the retire waits for its covering box");

        await Apply(_pc.BoxSet(strong, "strong", "s1024t4", FpA, lamport: 10));

        (await MaterialLengths(device)).Should().Be((0, 0, 0));
        (await LoggedBoxIds()).Should().BeEquivalentTo([strong]);
    }

    [Fact]
    public async Task RedeliveredEventOfAnInactiveBox_DoesNotBringItsMaterialBack()
    {
        var strong = Guid.NewGuid();
        var device = Guid.NewGuid();
        var deviceEvent = _phone.BoxSet(device, "device", "d64t3", FpA, lamport: 11);
        await Apply(_pc.BoxSet(strong, "strong", "s1024t4", FpA, lamport: 10));
        await Apply(deviceEvent);
        await Apply(_pc.Retire([device], strong, lamport: 12));

        // A peer that still holds the event sends it again: same id, same bytes.
        await Apply(deviceEvent);

        (await MaterialLengths(device)).Should().Be((0, 0, 0));
        (await LoggedBoxIds()).Should().NotContain(device);
    }

    [Fact]
    public async Task DestroyingMaterial_LeavesTheFileScrubOwed()
    {
        await Apply(_phone.BoxSet(Guid.NewGuid(), "device", "d64t3", FpA, lamport: 10));
        (await ScrubOwed()).Should().BeFalse("nothing was destroyed yet");

        await Apply(_phone.BoxSet(Guid.NewGuid(), "device", "d64t3", FpB, lamport: 20));

        (await ScrubOwed()).Should().BeTrue("freed pages and WAL frames may still hold the bytes until the scrub");
    }

    [Fact]
    public async Task Purge_CleansMaterialLeftByAnOlderBuild()
    {
        // What a node that ran an older build holds: a retired row with its bytes, and its event logged.
        var strong = Guid.NewGuid();
        var device = Guid.NewGuid();
        var deviceEvent = _phone.BoxSet(device, "device", "d64t3", FpA, lamport: 11);
        await Apply(_pc.BoxSet(strong, "strong", "s1024t4", FpA, lamport: 10));
        await Apply(deviceEvent);
        using (var conn = _node.Factory.CreateConnection())
        {
            await conn.ExecuteAsync("UPDATE tbl_recovery_box SET status = 'R' WHERE box_id = @B COLLATE NOCASE",
                new { B = device.ToString() });
        }
        (await MaterialLengths(device)).Should().Be((32, 49, 12));

        (await RecoveryBoxMaterialPurge.RunAsync(_node.Factory)).Should().BeGreaterThan(0);

        (await MaterialLengths(device)).Should().Be((0, 0, 0));
        (await LoggedBoxIds()).Should().BeEquivalentTo([strong]);
        (await RecoveryBoxMaterialPurge.RunAsync(_node.Factory)).Should().Be(0, "a second run finds nothing");
    }

    /// <summary>
    /// What a reader sees is a committed state: a peer pulling /api/sync/events, a backup's VACUUM INTO. At no committed
    /// state may the log hold a recovery_box_set event whose box is not active here: not while a newer box supersedes
    /// it, not in the gap before a separate purge, and not after its row is trimmed past the retention limit
    /// (release-a2 review: security #1, spec #2).
    /// </summary>
    [Fact]
    public async Task NoCommittedState_EverHoldsALoggedEventOfABoxThatIsNotActive()
    {
        using var cts = new CancellationTokenSource();
        long violations = 0, snapshots = 0;
        var reader = Task.Run(async () =>
        {
            using var conn = _node.Factory.CreateConnection();
            while (!cts.IsCancellationRequested)
            {
                Interlocked.Increment(ref snapshots);
                if (await conn.ExecuteScalarAsync<long>(InactiveLoggedSql) > 0) Interlocked.Increment(ref violations);
            }
        });

        for (var i = 0; i < 60; i++)
            await Apply(_phone.BoxSet(Guid.NewGuid(), "device", "d64t3", i % 2 == 0 ? FpA : FpB, lamport: 100 + i));
        cts.Cancel();
        await reader;

        using var conn = _node.Factory.CreateConnection();
        (await conn.ExecuteScalarAsync<long>(InactiveLoggedSql)).Should().Be(0, "at the end, rows trimmed past the limit leave no event");
        Interlocked.Read(ref snapshots).Should().BeGreaterThan(0);
        Interlocked.Read(ref violations).Should().Be(0, "no committed state may serve an inactive box's material");
        (await LoggedBoxIds()).Should().ContainSingle("only the active box's event is logged");
    }

    /// <summary>A logged recovery_box_set event whose box has no active row here.</summary>
    private const string InactiveLoggedSql =
        @"SELECT COUNT(*) FROM tbl_event e WHERE e.event_type = 'recovery_box_set' AND NOT EXISTS (
            SELECT 1 FROM tbl_recovery_box b WHERE b.status = 'A'
              AND b.box_id = json_extract(e.payload, '$.box_id') COLLATE NOCASE)";

    /// <summary>
    /// A node upgraded from a build before F7, with more than ten password changes on one device: that build logged
    /// every box event with its material and trimmed the rows past the ten-row limit, so the oldest events have no row
    /// left. After the startup purge, and after a peer redelivers a trimmed box, no old password opens anything in the
    /// database; the newest password still opens the active box.
    /// </summary>
    [Fact]
    public async Task AfterAnUpgradeWithMoreThanTenRotations_NoOldPasswordOpensAnything()
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        var fp = DekFingerprint.Of(dek);
        const int boxes = 13;
        var events = new List<SyncEvent>();
        for (var i = 0; i < boxes; i++)
            events.Add(_phone.BoxSet(Guid.NewGuid(), fp, 100 + i, RecoveryBoxCrypto.Wrap(dek, $"device-pw-{i}", RecoveryBoxKdf.Device64)));

        // The older build's state: every event logged; the newest box active and the ten before it retired, with
        // their material; the two oldest rows trimmed away.
        using (var conn = _node.Factory.CreateConnection())
            for (var i = 0; i < boxes; i++)
            {
                await _node.EventLogRepo.AppendAsync(events[i]);
                if (i < boxes - 1 - EventApplier.MaxInactiveBoxesPerAuthorAndKind) continue;
                var p = JsonSerializer.Deserialize<RecoveryBoxSetPayload>(events[i].Payload)!;
                await conn.ExecuteAsync(
                    @"INSERT INTO tbl_recovery_box (box_id, kind, author_node_id, dek_fingerprint, epoch_hint, kdf_preset, salt, wrapped, iv,
                        created_at, status, lamport_ts, source_node_id)
                      VALUES (@BoxId, 'device', @Author, @Fp, 1, @Preset, @Salt, @Wrapped, @Iv, @Now, @Status, @Lamport, @Author)",
                    new
                    {
                        p.BoxId, Author = _phone.Id.ToString(), Fp = fp, Preset = p.KdfPreset, Salt = Convert.FromBase64String(p.Salt),
                        Wrapped = Convert.FromBase64String(p.Wrapped), Iv = Convert.FromBase64String(p.Iv), Now = DateTime.UtcNow.ToString("O"),
                        Status = i == boxes - 1 ? "A" : "R", Lamport = events[i].LamportTs
                    });
            }

        await RecoveryBoxMaterialPurge.RunAsync(_node.Factory); // the startup repair
        await Apply(events[0]);                                // a peer redelivers a trimmed box

        var opened = await PasswordsThatOpenAnythingAsync(Enumerable.Range(0, boxes).Select(i => $"device-pw-{i}"));
        opened.Should().Equal([$"device-pw-{boxes - 1}"], "only the newest password opens a box, the active one");
        (await LoggedBoxIds()).Should().Equal([Guid.Parse(JsonSerializer.Deserialize<RecoveryBoxSetPayload>(events[^1].Payload)!.BoxId)]);
        (await ActiveBoxes()).Should().ContainSingle();
    }

    /// <summary>The attacker with a copy of the database: every box row and logged box event, tried with each password.</summary>
    private async Task<List<string>> PasswordsThatOpenAnythingAsync(IEnumerable<string> passwords)
    {
        var material = new List<(string Preset, byte[] Salt, byte[] Wrapped, byte[] Iv)>();
        using (var conn = _node.Factory.CreateConnection())
        {
            material.AddRange(await conn.QueryAsync<(string, byte[], byte[], byte[])>("SELECT kdf_preset, salt, wrapped, iv FROM tbl_recovery_box"));
            foreach (var payload in await conn.QueryAsync<string>("SELECT payload FROM tbl_event WHERE event_type = 'recovery_box_set'"))
            {
                var p = JsonSerializer.Deserialize<RecoveryBoxSetPayload>(payload)!;
                material.Add((p.KdfPreset, Convert.FromBase64String(p.Salt), Convert.FromBase64String(p.Wrapped), Convert.FromBase64String(p.Iv)));
            }
        }
        var usable = material.Where(m => RecoveryBoxKdf.IsWellFormed("device", m.Salt, m.Wrapped, m.Iv)).ToList();
        return passwords.Where(pw => usable.Any(m => RecoveryBoxCrypto.TryUnwrap(pw, m.Preset, m.Salt, m.Wrapped, m.Iv) != null)).ToList();
    }

    // --- recovery_box_retire ----------------------------------------------------------------

    [Fact]
    public async Task Retire_WithActiveCoveringBox_RetiresTarget()
    {
        var strong = Guid.NewGuid();
        var device = Guid.NewGuid();
        await Apply(_pc.BoxSet(strong, "strong", "s1024t4", FpA, lamport: 10));
        await Apply(_phone.BoxSet(device, "device", "d64t3", FpA, lamport: 11));

        await Apply(_pc.Retire([device], strong, lamport: 12));

        var row = (await Box(device))!;
        row.Status.Should().Be("R");
        row.RetiredByBoxId.Should().BeEquivalentTo(strong.ToString());
        (await Box(strong))!.Status.Should().Be("A");
        (await PendingRetires()).Should().Be(0);
    }

    [Fact]
    public async Task Retire_FromNonSuperadmin_IsRejected()
    {
        var strong = Guid.NewGuid();
        var device = Guid.NewGuid();
        await Apply(_pc.BoxSet(strong, "strong", "s1024t4", FpA, lamport: 10));
        await Apply(_phone.BoxSet(device, "device", "d64t3", FpA, lamport: 11));

        var act = () => Apply(_laptop.Retire([device], strong, lamport: 12));

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await Box(device))!.Status.Should().Be("A");
    }

    [Fact]
    public async Task Retire_BeforeCoveringBoxArrives_WaitsAndAppliesLater()
    {
        var strong = Guid.NewGuid();
        var device = Guid.NewGuid();
        await Apply(_phone.BoxSet(device, "device", "d64t3", FpA, lamport: 11));

        var result = await Apply(_pc.Retire([device], strong, lamport: 12));

        result.Should().Be(EventApplyResult.Applied, "a deferred retire must not block the pull loop");
        (await Box(device))!.Status.Should().Be("A", "the covering box is not here yet");
        (await PendingRetires()).Should().Be(1);

        await Apply(_pc.BoxSet(strong, "strong", "s1024t4", FpA, lamport: 10));

        (await Box(device))!.Status.Should().Be("R");
        (await PendingRetires()).Should().Be(0);
    }

    [Fact]
    public async Task Retire_BeforeTargetArrives_AppliesWhenTargetArrives()
    {
        var strong = Guid.NewGuid();
        var device = Guid.NewGuid();
        await Apply(_pc.BoxSet(strong, "strong", "s1024t4", FpA, lamport: 10));
        await Apply(_pc.Retire([device], strong, lamport: 12));

        await Apply(_phone.BoxSet(device, "device", "d64t3", FpA, lamport: 11));

        (await Box(device))!.Status.Should().Be("R");
    }

    [Fact]
    public async Task Retire_CoveringBoxNoLongerActive_KeepsWaiting()
    {
        // The covering box was superseded by a newer strong box of the same PC before the retire
        // arrived. It is not active here, so nothing is retired — and nothing is dropped either.
        var oldStrong = Guid.NewGuid();
        var newStrong = Guid.NewGuid();
        var device = Guid.NewGuid();
        await Apply(_pc.BoxSet(oldStrong, "strong", "s1024t4", FpA, lamport: 10));
        await Apply(_pc.BoxSet(newStrong, "strong", "s1024t4", FpB, lamport: 20));
        await Apply(_phone.BoxSet(device, "device", "d64t3", FpA, lamport: 11));

        await Apply(_pc.Retire([device], oldStrong, lamport: 21));

        (await Box(device))!.Status.Should().Be("A");
        (await PendingRetires()).Should().Be(1);
    }

    [Fact]
    public async Task Retire_CoveringBoxHoldsAnotherKey_IsRefused()
    {
        var strong = Guid.NewGuid();
        var device = Guid.NewGuid();
        await Apply(_pc.BoxSet(strong, "strong", "s1024t4", FpB, lamport: 10));
        await Apply(_phone.BoxSet(device, "device", "d64t3", FpA, lamport: 11));

        await Apply(_pc.Retire([device], strong, lamport: 12));

        (await Box(device))!.Status.Should().Be("A", "the only box with key A must survive");
        (await PendingRetires()).Should().Be(0);
    }

    [Fact]
    public async Task Retire_CannotRetireTheCoveringBoxItself()
    {
        var strong = Guid.NewGuid();
        await Apply(_pc.BoxSet(strong, "strong", "s1024t4", FpA, lamport: 10));

        await Apply(_pc.Retire([strong], strong, lamport: 12));

        (await Box(strong))!.Status.Should().Be("A");
    }

    [Fact]
    public async Task Retire_CoveringBoxMustBeStrong()
    {
        var pcDevice = Guid.NewGuid();
        var phoneDevice = Guid.NewGuid();
        await Apply(_pc.BoxSet(pcDevice, "device", "d64t3", FpA, lamport: 10));
        await Apply(_phone.BoxSet(phoneDevice, "device", "d64t3", FpA, lamport: 11));

        await Apply(_pc.Retire([phoneDevice], pcDevice, lamport: 12));

        (await Box(phoneDevice))!.Status.Should().Be("A");
    }

    // --- retired_link_set -------------------------------------------------------------------

    [Fact]
    public async Task Link_IsKeyedByCommitAndAuthor_ForgeryCannotShadowTheRealOne()
    {
        var commit = Guid.NewGuid();

        await Apply(_phone.Link(commit, FpA, FpB, lamport: 10, fill: 0x66));   // forged, first
        await Apply(_pc.Link(commit, FpA, FpB, lamport: 11, fill: 0x11));      // real
        await Apply(_phone.Link(commit, FpA, FpB, lamport: 12, fill: 0x77));   // forger again

        using var conn = _node.Factory.CreateConnection();
        var rows = (await conn.QueryAsync<(string Author, byte[] Wrapped)>(
            "SELECT author_node_id, wrapped FROM tbl_dek_retired_link WHERE commit_id = @C",
            new { C = commit.ToString() })).ToList();
        rows.Should().HaveCount(2);
        rows.Single(r => string.Equals(r.Author, _pc.Id.ToString(), StringComparison.OrdinalIgnoreCase))
            .Wrapped.Skip(1).Should().OnlyContain(b => b == 0x11);
        rows.Single(r => string.Equals(r.Author, _phone.Id.ToString(), StringComparison.OrdinalIgnoreCase))
            .Wrapped.Skip(1).Should().OnlyContain(b => b == 0x66, "a link is immutable once written");
    }

    [Fact]
    public async Task Link_SameFingerprints_IsRejected()
    {
        var act = () => Apply(_pc.Link(Guid.NewGuid(), FpA, FpA, lamport: 10, fill: 1));

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    // --- state_anchor -----------------------------------------------------------------------

    [Fact]
    public async Task Anchor_FromSuperadmin_IsStored()
    {
        var anchor = Guid.NewGuid();

        await Apply(_pc.Anchor(anchor, lamport: 10));

        using var conn = _node.Factory.CreateConnection();
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_state_anchor WHERE anchor_id = @A", new { A = anchor.ToString() }))
            .Should().Be(1);
    }

    [Fact]
    public async Task Anchor_FromNonSuperadmin_IsRejected()
    {
        var anchor = Guid.NewGuid();

        var act = () => Apply(_phone.Anchor(anchor, lamport: 10));

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Anchor_DigestWithoutItsFormat_IsRejected()
    {
        var evt = _pc.Anchor(Guid.NewGuid(), lamport: 10, digest: FpB);

        var act = () => Apply(evt);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("sd3:{0}/{0}", true)]   // state and history sections
    [InlineData("sd3:{0}/", false)]
    [InlineData("sd5:{0}/{0}/{0}", true)]   // state, history and trust
    [InlineData("sd5:{0}/{0}/{0}/{0}", false)]
    public async Task Anchor_DigestSections_AreValidated(string shape, bool accepted)
    {
        var act = () => Apply(_pc.Anchor(Guid.NewGuid(), lamport: 10, digest: string.Format(shape, FpB)));

        if (accepted) await act.Should().NotThrowAsync();
        else await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Anchor_BadVector_IsRejected()
    {
        var act = () => Apply(_pc.Anchor(Guid.NewGuid(), lamport: 10, vectorKey: "not-a-node"));

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    // --- sealed_secret_set ------------------------------------------------------------------

    [Fact]
    public async Task SealedSecret_NewerWins_LateOlderDoesNotOverwrite()
    {
        var name = $"restic:{BlindNodeId.NewId()}";

        await Apply(_pc.Secret(name, FpB, lamport: 20, fill: 0x22));
        await Apply(_phone.Secret(name, FpA, lamport: 10, fill: 0x11));

        using var conn = _node.Factory.CreateConnection();
        var row = await conn.QuerySingleAsync<(string Fp, byte[] Wrapped)>(
            "SELECT dek_fingerprint, wrapped FROM tbl_sealed_secret WHERE name = @N", new { N = name });
        row.Fp.Should().Be(FpB);
        row.Wrapped.Should().OnlyContain(b => b == 0x22);

        await Apply(_phone.Secret(name, FpA, lamport: 30, fill: 0x33));
        (await conn.ExecuteScalarAsync<string>("SELECT dek_fingerprint FROM tbl_sealed_secret WHERE name = @N", new { N = name }))
            .Should().Be(FpA, "a re-seal after rotation replaces the older seal");
    }

    [Theory]
    [InlineData("restic")]
    [InlineData("password:abc")]
    [InlineData("restic:../../etc")]
    public async Task SealedSecret_BadName_IsRejected(string name)
    {
        var act = () => Apply(_pc.Secret(name, FpA, lamport: 10, fill: 1));

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    // --- helpers ----------------------------------------------------------------------------

    private Task<EventApplyResult> Apply(SyncEvent evt) => _node.EventApplier.ApplyAsync(evt);

    private sealed record BoxRow(string BoxId, string Kind, string AuthorNodeId, string DekFingerprint, string Status, string? RetiredByBoxId);

    private async Task<BoxRow?> Box(Guid id)
    {
        using var conn = _node.Factory.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<BoxRow>(
            @"SELECT box_id AS BoxId, kind AS Kind, author_node_id AS AuthorNodeId, dek_fingerprint AS DekFingerprint,
                     status AS Status, retired_by_box_id AS RetiredByBoxId
              FROM tbl_recovery_box WHERE box_id = @Id COLLATE NOCASE", new { Id = id });
    }

    private async Task<List<Guid>> ActiveBoxes()
    {
        using var conn = _node.Factory.CreateConnection();
        return (await conn.QueryAsync<string>("SELECT box_id FROM tbl_recovery_box WHERE status = 'A'"))
            .Select(Guid.Parse).ToList();
    }

    private async Task<(long Salt, long Wrapped, long Iv)> MaterialLengths(Guid id)
    {
        using var conn = _node.Factory.CreateConnection();
        return await conn.QuerySingleAsync<(long, long, long)>(
            "SELECT length(salt), length(wrapped), length(iv) FROM tbl_recovery_box WHERE box_id = @Id COLLATE NOCASE",
            new { Id = id.ToString() });
    }

    /// <summary>The boxes whose recovery_box_set event is in this node's log (what it would serve to a peer).</summary>
    private async Task<List<Guid>> LoggedBoxIds()
    {
        using var conn = _node.Factory.CreateConnection();
        return (await conn.QueryAsync<string>("SELECT payload FROM tbl_event WHERE event_type = @T",
                new { T = EventTypes.RecoveryBoxSet }))
            .Select(p => Guid.Parse(JsonSerializer.Deserialize<RecoveryBoxSetPayload>(p)!.BoxId)).ToList();
    }

    private async Task<bool> ScrubOwed()
    {
        using var conn = _node.Factory.CreateConnection();
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_blind_state WHERE key = @K",
            new { K = StoredEventRepair.CleanupPendingKey }) > 0;
    }

    private async Task<long> PendingRetires()
    {
        using var conn = _node.Factory.CreateConnection();
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_recovery_box_pending_retire");
    }

    private sealed class ConcreteFixture : SyncTestFixture { }

    /// <summary>A whitelisted peer that signs events with its own key.</summary>
    private sealed class Peer(string name, bool superadmin)
    {
        private readonly (byte[] Public, byte[] Private) _keys = Ed25519Signer.GenerateKeyPair();
        public Guid Id { get; } = Guid.NewGuid();

        public WhitelistEntry Entry() => new()
        {
            NodeId = Id, DisplayName = name, Ed25519PublicKey = _keys.Public, Status = "A",
            IsSuperadmin = superadmin, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };

        public SyncEvent BoxSet(Guid box, string kind, string preset, string fp, long lamport, Guid? author = null,
            int saltBytes = 32, byte wrappedVersion = 0x01, int wrappedLength = 49)
        {
            var wrapped = Enumerable.Repeat((byte)2, wrappedLength).ToArray();
            wrapped[0] = wrappedVersion;
            return Sign(EventTypes.RecoveryBoxSet, lamport, new RecoveryBoxSetPayload(
                box.ToString(), kind, (author ?? Id).ToString(), fp, 1, preset,
                B64(saltBytes, 1), Convert.ToBase64String(wrapped), B64(12, 3)));
        }

        /// <summary>A device box with real material: the DEK wrapped under <paramref name="seal"/>'s password.</summary>
        public SyncEvent BoxSet(Guid box, string fp, long lamport, RecoveryBoxSeal seal) =>
            Sign(EventTypes.RecoveryBoxSet, lamport, new RecoveryBoxSetPayload(
                box.ToString(), "device", Id.ToString(), fp, 1, seal.KdfPreset,
                Convert.ToBase64String(seal.Salt), Convert.ToBase64String(seal.Wrapped), Convert.ToBase64String(seal.Iv)));

        public SyncEvent Retire(IReadOnlyList<Guid> boxes, Guid covering, long lamport) =>
            Sign(EventTypes.RecoveryBoxRetire, lamport, new RecoveryBoxRetirePayload(
                boxes.Select(b => b.ToString()).ToList(), covering.ToString()));

        public SyncEvent Link(Guid commit, string oldFp, string newFp, long lamport, byte fill) =>
            Sign(EventTypes.RetiredLinkSet, lamport, new RetiredLinkSetPayload(
                commit.ToString(), oldFp, newFp, Convert.ToBase64String([0x01, .. Enumerable.Repeat(fill, 48)]), B64(12, 4)));

        public SyncEvent Anchor(Guid anchor, long lamport, string? vectorKey = null, string? digest = null) =>
            Sign(EventTypes.StateAnchor, lamport, new StateAnchorPayload(
                anchor.ToString(), FpA,
                new Dictionary<string, long> { [vectorKey ?? Id.ToString().ToUpperInvariant()] = 42 },
                digest ?? "sd2:" + FpB, new string('c', 64), DateTime.UtcNow.ToString("O")));

        public SyncEvent Secret(string secretName, string fp, long lamport, byte fill) =>
            Sign(EventTypes.SealedSecretSet, lamport, new SealedSecretSetPayload(
                secretName, fp, B64(48, fill), B64(12, 5)));

        private SyncEvent Sign(string type, long lamport, object payload)
        {
            var evt = new SyncEvent
            {
                EventId = Guid.NewGuid(),
                NodeId = Id,
                LamportTs = lamport,
                EventType = type,
                Payload = JsonSerializer.Serialize(payload),
                Signature = [],
                ProtocolVersion = SyncProtocolVersion.Current,
                CreatedAt = DateTime.UtcNow,
            };
            evt.Signature = Ed25519Signer.Sign(_keys.Private, EventSignature.BuildPayload(evt));
            return evt;
        }

        private static string B64(int length, byte fill) =>
            Convert.ToBase64String(Enumerable.Repeat(fill, length).ToArray());
    }
}
