using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Recovery;
using Dapper;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>Integrity anchors (plan 5.5): MAC, digest, publication and the check at restore.</summary>
public class StateAnchorTests : IAsyncLifetime
{
    private const string Password = "AnchorPass1";
    private readonly ConcreteFixture _f = new();
    private byte[] _dek = null!;
    private byte[] _publicKey = null!;
    private Guid _nodeId;

    public async Task InitializeAsync()
    {
        await _f.InitializeAsync();
        await _f.InitService.InitializeAsync("admin", "Anchor", Password);
        await _f.Session.UnlockAsync(Password);
        _dek = _f.Session.GetMasterDek();
        var identity = (await _f.NodeRepo.GetAsync())!;
        _publicKey = identity.Ed25519PublicKey;
        _nodeId = identity.NodeId;
        await _f.ArticleService.CreateAsync("One", "/Notes", ["alpha"], "first body");
        await _f.ArticleService.CreateAsync("Two", "/Notes", [], "second body");
    }

    public Task DisposeAsync() => _f.DisposeAsync();

    private StateAnchorService Anchors(bool superadmin = true) => new(_f.Session, _f.Factory,
        new RecoveryEventPublisher(_f.NodeRepo, _f.EventLogRepo, _f.Clock, _f.Session, _f.EventApplier, new SyncTrigger(),
            new FixedOwnStanding(superadmin)));

    /// <summary>
    /// Verifies with this node's own signed anchor events (plus <paramref name="events"/>), this node's key,
    /// and this node as the only superadmin unless <paramref name="superadmin"/> says otherwise.
    /// </summary>
    private async Task<AnchorVerification> VerifyAsync(byte[] dek, IReadOnlyList<SyncEvent>? events = null)
    {
        var anchorEvents = await _f.EventLogRepo.GetRecentAsync(100, 0, EventTypes.StateAnchor);
        using var conn = _f.Factory.CreateConnection();
        return await StateAnchorService.VerifyAsync(conn, dek, [.. anchorEvents, .. events ?? []],
            new Dictionary<Guid, byte[]> { [_nodeId] = _publicKey }, headProven: true);
    }

    // --- MAC --------------------------------------------------------------------------------

    [Fact]
    public void Hmac_VerifiesOnlyWithTheSameKeyAndFields()
    {
        var vector = new Dictionary<string, long> { ["AAAAAAAA-0000-0000-0000-000000000001"] = 5 };
        var fp = DekFingerprint.Of(_dek);
        var hmac = StateAnchorCrypto.ComputeHmac(_dek, "anchor", fp, vector, "d1", "2026-09-27T00:00:00Z");

        StateAnchorCrypto.Verify(_dek, "anchor", fp, vector, "d1", "2026-09-27T00:00:00Z", hmac).Should().BeTrue();
        StateAnchorCrypto.Verify(MasterKeyManager.GenerateMasterDek(), "anchor", fp, vector, "d1", "2026-09-27T00:00:00Z", hmac).Should().BeFalse();
        StateAnchorCrypto.Verify(_dek, "anchor", fp, vector, "d2", "2026-09-27T00:00:00Z", hmac).Should().BeFalse();
        StateAnchorCrypto.Verify(_dek, "anchor", fp, new Dictionary<string, long> { ["AAAAAAAA-0000-0000-0000-000000000001"] = 6 },
            "d1", "2026-09-27T00:00:00Z", hmac).Should().BeFalse();
    }

    [Fact]
    public void Hmac_KeyIsDerived_NotTheDekItself()
    {
        var vector = new Dictionary<string, long>();
        var canonical = System.Text.Encoding.UTF8.GetBytes(StateAnchorCrypto.Canonical("a", "f", vector, "d", "t"));
        var withRawDek = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(_dek, canonical));

        StateAnchorCrypto.ComputeHmac(_dek, "a", "f", vector, "d", "t").Should().NotBe(withRawDek);
    }

    [Fact]
    public void CanonicalVector_IgnoresKeyOrderAndCase()
    {
        var a = new Dictionary<string, long> { ["bbbbbbbb-0000-0000-0000-000000000002"] = 2, ["AAAAAAAA-0000-0000-0000-000000000001"] = 1 };
        var b = new Dictionary<string, long> { ["aaaaaaaa-0000-0000-0000-000000000001"] = 1, ["BBBBBBBB-0000-0000-0000-000000000002"] = 2 };

        StateAnchorCrypto.CanonicalVector(a).Should().Be(StateAnchorCrypto.CanonicalVector(b));
    }

    // --- digest -----------------------------------------------------------------------------

    [Fact]
    public async Task Digest_IsStable_AndCoversEveryRowOfThisNode()
    {
        using var conn = _f.Factory.CreateConnection();
        var first = await StateDigest.ComputeAsync(conn);
        var second = await StateDigest.ComputeAsync(conn);

        first.Digest.Should().Be(second.Digest);
        var maxLamport = await conn.ExecuteScalarAsync<long>("SELECT MAX(lamport_ts) FROM tbl_article");
        first.PositionVector.Should().ContainKey(_nodeId.ToString().ToUpperInvariant())
            .WhoseValue.Should().BeGreaterThanOrEqualTo(maxLamport);
    }

    [Theory]
    [InlineData("UPDATE tbl_article SET title = 'Renamed' WHERE title = 'One'")]
    [InlineData("UPDATE tbl_article SET lamport_ts = 999 WHERE title = 'One'")]
    [InlineData("UPDATE tbl_article_body SET ciphertext_hash = 'ff' WHERE article_id = (SELECT id FROM tbl_article WHERE title = 'One')")]
    [InlineData("PRAGMA foreign_keys = OFF; DELETE FROM tbl_article WHERE title = 'Two'")]
    // The body bytes themselves, with the stored hash column left as it was.
    [InlineData("UPDATE tbl_blob SET data = randomblob(length(data)) WHERE hash = (SELECT b.ciphertext_hash FROM tbl_article_body b JOIN tbl_article a ON a.id = b.article_id WHERE a.title = 'One')")]
    [InlineData("UPDATE tbl_concept_tag SET name = 'renamed-tag' WHERE id IN (SELECT concept_tag_id FROM tbl_article_concept_tag)")]
    public async Task Digest_ChangesWithAnyRowChange(string tamper)
    {
        using var conn = _f.Factory.CreateConnection();
        var before = (await StateDigest.ComputeAsync(conn)).Digest;

        await conn.ExecuteAsync(tamper);

        (await StateDigest.ComputeAsync(conn)).Digest.Should().NotBe(before);
    }

    [Theory]
    [InlineData("a|/b", "/c", "a", "/b|/c")] // a '|'-joined encoding reads both as "a|/b|/c"
    [InlineData("ab", "/c", "a", "b/c")]      // plain concatenation reads both as "ab/c"
    public async Task Digest_FieldBoundariesCannotBeShifted(string title1, string path1, string title2, string path2)
    {
        using var conn = _f.Factory.CreateConnection();
        await conn.ExecuteAsync("UPDATE tbl_article SET title = @T, tree_path = @P WHERE title = 'One'", new { T = title1, P = path1 });
        var first = (await StateDigest.ComputeAsync(conn)).Digest;

        await conn.ExecuteAsync("UPDATE tbl_article SET title = @T, tree_path = @P WHERE title = @Old", new { T = title2, P = path2, Old = title1 });

        (await StateDigest.ComputeAsync(conn)).Digest.Should().NotBe(first);
    }

    [Fact]
    public async Task Digest_CarriesItsFormat()
    {
        using var conn = _f.Factory.CreateConnection();

        var digest = (await StateDigest.ComputeAsync(conn)).Digest;

        digest.Should().MatchRegex("^sd7:[0-9a-f]{2048}/[0-9a-f]{2048}/([0-9a-f]{32})*$");
        StateDigest.FormatOf(digest).Should().Be(StateDigest.FormatVersion);
    }

    [Fact]
    public async Task KeyBytes_ChangedWithoutAnyVersionChange_AreDetected()
    {
        using var conn = _f.Factory.CreateConnection();
        var before = (await StateDigest.ComputeAsync(conn)).Digest;

        await conn.ExecuteAsync("UPDATE tbl_article_body SET iv = randomblob(12) WHERE article_id = (SELECT id FROM tbl_article WHERE title = 'One')");

        (await StateDigest.ComputeAsync(conn)).Digest.Should().NotBe(before);
    }

    [Fact]
    public async Task ReKeyedArticle_KeyEntryGoesBeyondAnOlderCut_ContentStaysCovered()
    {
        // A re-key (BMB-49): new IV and ciphertext, a new key generation and a later key version; the
        // content version is untouched. BMB-57's columns are simulated here.
        using var conn = _f.Factory.CreateConnection();
        await conn.ExecuteAsync(@"ALTER TABLE tbl_article_body ADD COLUMN key_gen TEXT;
                                  ALTER TABLE tbl_article_body ADD COLUMN key_lamport_ts INTEGER;
                                  ALTER TABLE tbl_article_body ADD COLUMN key_source_node_id TEXT;");
        var anchored = await StateDigest.ComputeAsync(conn);
        var articleId = (await conn.QuerySingleAsync<string>("SELECT id FROM tbl_article WHERE title = 'One'")).ToLowerInvariant();

        var newBytes = SecureRandom.GetBytes(64);
        var newHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(newBytes));
        await conn.ExecuteAsync("INSERT INTO tbl_blob (hash, data, size, created_at) VALUES (@H, @D, 64, @T)",
            new { H = newHash, D = newBytes, T = DateTime.UtcNow.ToString("O") });
        await conn.ExecuteAsync(
            @"UPDATE tbl_article_body SET iv = randomblob(12), ciphertext_hash = @H, key_gen = '1',
                     key_lamport_ts = @L, key_source_node_id = @S
              WHERE article_id = (SELECT id FROM tbl_article WHERE title = 'One')",
            new { H = newHash, L = anchored.PositionVector.Values.Max() + 100, S = _nodeId.ToString() });

        var cut = anchored.PositionVector.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var later = await StateDigest.ComputeAsync(conn, cut);

        StateDigest.Matches(anchored.Digest, later).Should().BeTrue("the older anchor stays valid for everything it covered");
        later.BeyondCut.Should().ContainSingle().Which.Should().Be(
            new StateDigestEntry("article_key", articleId, anchored.PositionVector.Values.Max() + 100, _nodeId.ToString().ToUpperInvariant()));
    }

    [Fact]
    public async Task EditAfterTheAnchor_PutsContentAndKeyEntriesBeyondTheCut()
    {
        using var conn = _f.Factory.CreateConnection();
        var anchored = await StateDigest.ComputeAsync(conn);
        var cut = anchored.PositionVector.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var one = await conn.QuerySingleAsync<string>("SELECT id FROM tbl_article WHERE title = 'One'");

        await _f.ArticleService.UpdateAsync(Guid.Parse(one), "One", "/Notes", ["alpha"], "edited body");

        var later = await StateDigest.ComputeAsync(conn, cut);
        later.BeyondCut.Select(e => e.Type).Should().BeEquivalentTo(["article", "article_key"]);
        StateDigest.Matches(anchored.Digest, later).Should().BeTrue("an edit replaces a version, it does not break the anchor");
    }

    [Fact]
    public async Task CoveredEntryAlteredInPlace_IsCaught_EvenNextToANewerChange()
    {
        // Two changes after the anchor: one legitimate (a new article), one tampering (a covered article's
        // title rewritten without a new version). The first only leaves its bucket out.
        using var conn = _f.Factory.CreateConnection();
        var anchored = await StateDigest.ComputeAsync(conn);
        var cut = anchored.PositionVector.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        await _f.ArticleService.CreateAsync("Three", "/Notes", [], "after");
        var affected = (await StateDigest.ComputeAsync(conn, cut)).AffectedBuckets;
        // Tamper a covered article outside the new article's buckets (both collide 1 in 4096; First throws then).
        var victim = (await conn.QueryAsync<(string Id, string Title)>("SELECT id, title FROM tbl_article WHERE title IN ('One', 'Two')"))
            .First(a => !affected.Contains(StateDigest.BucketOf(new StateDigestEntry("article", a.Id.ToLowerInvariant(), 0, ""))));
        await conn.ExecuteAsync("UPDATE tbl_article SET title = 'Tampered' WHERE id = @Id", new { victim.Id });

        var later = await StateDigest.ComputeAsync(conn, cut);

        StateDigest.Matches(anchored.Digest, later).Should().BeFalse();
    }

    [Fact]
    public async Task VictimInsideTheBucketOfANewerChange_IsNamedAsUnchecked_AndNothingIsCalledConfirmed()
    {
        await Anchors().PublishAsync();
        using var conn = _f.Factory.CreateConnection();
        var victim = (await conn.ExecuteScalarAsync<string>("SELECT id FROM tbl_article WHERE title = 'One'"))!.ToLowerInvariant();
        var bucket = StateDigest.BucketOf(new StateDigestEntry("article", victim, 0, ""));
        // A newer row (a tombstone past the cut) landing in the victim's own bucket, then the victim altered in place.
        var neighbour = Enumerable.Range(0, 10_000).Select(_ => Guid.NewGuid().ToString())
            .First(id => StateDigest.BucketOf(new StateDigestEntry("tombstone", id, 0, "")) == bucket);
        await conn.ExecuteAsync(
            "INSERT INTO tbl_tombstone (article_id, created_at, expires_at, lamport_ts, source_node_id) VALUES (@Id, '2026-09-01', '2026-10-01', 1000000, @Node)",
            new { Id = neighbour, Node = _nodeId.ToString() });
        await conn.ExecuteAsync("UPDATE tbl_article SET title = 'Tampered' WHERE lower(id) = @victim", new { victim });

        var result = await VerifyAsync(_dek);

        result.DigestMatches.Should().BeTrue("the victim's bucket is not compared — the documented limit");
        result.State.Should().Be("partially_confirmed").And.NotBe("confirmed");
        result.Unchecked.Should().Contain(e => e.Type == "article" && e.Id == victim, "an entry that was not compared is named");
    }

    [Fact]
    public async Task SignedAnchorOfAnotherDigestFormat_IsReportedAsSuch_NotAsAMismatch()
    {
        using (var conn = _f.Factory.CreateConnection())
        {
            var state = await StateDigest.ComputeAsync(conn);
            var id = Guid.NewGuid().ToString();
            var created = DateTime.UtcNow.ToString("O");
            var fp = DekFingerprint.Of(_dek);
            var oldFormat = "sd2:" + state.Digest[FormatPrefixLength..].Split('/')[0];
            await new RecoveryEventPublisher(_f.NodeRepo, _f.EventLogRepo, _f.Clock, _f.Session, _f.EventApplier,
                    new SyncTrigger(), new FixedOwnStanding(true))
                .PublishAsync(EventTypes.StateAnchor, new StateAnchorPayload(id, fp, state.PositionVector, oldFormat,
                    StateAnchorCrypto.ComputeHmac(_dek, id, fp, state.PositionVector, oldFormat, created), created));
        }

        var result = await VerifyAsync(_dek);

        result.Found.Should().BeFalse();
        result.OtherFormatAnchorDates.Should().ContainSingle();
    }

    private static readonly int FormatPrefixLength = StateDigest.FormatPrefix.Length;

    [Fact]
    public async Task Digest_IgnoresTheWrappedEntityKeys_ThatEveryNodeRewrapsAtARotation()
    {
        using var conn = _f.Factory.CreateConnection();
        var before = (await StateDigest.ComputeAsync(conn)).Digest;

        await conn.ExecuteAsync("UPDATE tbl_article_body SET encrypted_dek = randomblob(length(encrypted_dek)), dek_iv = randomblob(12)");

        (await StateDigest.ComputeAsync(conn)).Digest.Should().Be(before,
            "a blind node never rotates, so the anchor must not depend on the wrap under the master key");
    }

    // --- publish and verify -----------------------------------------------------------------

    [Fact]
    public async Task PublishedAnchor_IsConfirmed_ByTheSameKey()
    {
        await Anchors().PublishAsync();

        var result = await VerifyAsync(_dek);

        result.Confirmed.Should().BeTrue();
        result.Unverified.Should().BeEmpty();
        (await _f.EventLogRepo.GetRecentAsync(10, 0, EventTypes.StateAnchor)).Should().ContainSingle();
    }

    [Fact]
    public async Task AnchorWhoseOwnTrustSectionDoesNotListItsSignerAsSuperadmin_IsNotTrusted()
    {
        // A genuine event and MAC, but the digest was taken without the signer's own record: at its own cut
        // the signer was not a superadmin, so it vouches for nothing.
        using (var conn = _f.Factory.CreateConnection())
        {
            var state = await StateDigest.ComputeAsync(conn, includeSelfAsSuperadmin: false);
            var id = Guid.NewGuid().ToString();
            var created = DateTime.UtcNow.ToString("O");
            var fp = DekFingerprint.Of(_dek);
            await new RecoveryEventPublisher(_f.NodeRepo, _f.EventLogRepo, _f.Clock, _f.Session, _f.EventApplier,
                    new SyncTrigger(), new FixedOwnStanding(true))
                .PublishAsync(EventTypes.StateAnchor, new StateAnchorPayload(id, fp, state.PositionVector, state.Digest,
                    StateAnchorCrypto.ComputeHmac(_dek, id, fp, state.PositionVector, state.Digest, created), created));
        }

        (await VerifyAsync(_dek)).Found.Should().BeFalse();
    }

    [Fact]
    public async Task AnchorRowWithoutItsSignedEvent_IsNotTrusted()
    {
        // A row a blind node could have put there itself: the HMAC is genuine (copied), the event is gone.
        await Anchors().PublishAsync();
        using (var conn = _f.Factory.CreateConnection())
            await conn.ExecuteAsync("DELETE FROM tbl_event WHERE event_type = 'state_anchor'");

        (await VerifyAsync(_dek)).Found.Should().BeFalse();
    }

    [Fact]
    public async Task AnchorEventWithABrokenSignature_IsNotTrusted()
    {
        await Anchors().PublishAsync();
        using (var conn = _f.Factory.CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_event SET signature = zeroblob(64) WHERE event_type = 'state_anchor'");

        (await VerifyAsync(_dek)).Found.Should().BeFalse();
    }

    [Fact]
    public async Task SignedAnchorMadeUnderAnotherDek_IsNotTrusted()
    {
        // A genuine superadmin's signature over an anchor whose fingerprint and MAC belong to another key
        // (an old DEK a revoked device still knows): only the restored key is a trust root.
        var otherDek = MasterKeyManager.GenerateMasterDek();
        using (var conn = _f.Factory.CreateConnection())
        {
            var state = await StateDigest.ComputeAsync(conn);
            var id = Guid.NewGuid().ToString();
            var created = DateTime.UtcNow.ToString("O");
            var fp = DekFingerprint.Of(otherDek);
            await new RecoveryEventPublisher(_f.NodeRepo, _f.EventLogRepo, _f.Clock, _f.Session, _f.EventApplier,
                    new SyncTrigger(), new FixedOwnStanding(true))
                .PublishAsync(EventTypes.StateAnchor, new StateAnchorPayload(id, fp, state.PositionVector, state.Digest,
                    StateAnchorCrypto.ComputeHmac(otherDek, id, fp, state.PositionVector, state.Digest, created), created));
        }

        var result = await VerifyAsync(_dek);

        result.Found.Should().BeFalse();
        result.State.Should().Be("unconfirmed");
    }

    [Fact]
    public async Task NonSuperadmin_CannotPublishAnAnchor_EvenThroughTheOwnEventHelper()
    {
        var act = () => Anchors(superadmin: false).PublishAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*superadmin*");
        using var conn = _f.Factory.CreateConnection();
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_state_anchor")).Should().Be(0);
    }

    [Fact]
    public async Task UnprovenHead_NeverConfirms_EvenAGenuineAnchor()
    {
        await Anchors().PublishAsync();
        var anchorEvents = await _f.EventLogRepo.GetRecentAsync(100, 0, EventTypes.StateAnchor);
        using var conn = _f.Factory.CreateConnection();

        var result = await StateAnchorService.VerifyAsync(conn, _dek, anchorEvents,
            new Dictionary<Guid, byte[]> { [_nodeId] = _publicKey }, headProven: false);

        result.Confirmed.Should().BeFalse();
        result.HeadProven.Should().BeFalse();
        result.State.Should().Be("unconfirmed");
    }

    // --- history section ---------------------------------------------------------------------

    /// <summary>An edit (one article version, its body in tbl_blob) and a conflict copy with inline bytes.</summary>
    private async Task AddHistoryAsync()
    {
        using var conn = _f.Factory.CreateConnection();
        var oneId = (await conn.ExecuteScalarAsync<string>("SELECT id FROM tbl_article WHERE title = 'One'"))!;
        await _f.ArticleService.UpdateAsync(Guid.Parse(oneId), plaintext: "first body, edited");
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_conflict_version (id, article_id, source_node_id, lamport_ts, ciphertext, iv, encrypted_dek, dek_iv, created_at, expires_at, metadata_json)
              VALUES (@Id, @Article, @Node, 3, randomblob(64), randomblob(12), randomblob(60), randomblob(12), '2026-09-01T00:00:00Z', '2026-09-08T00:00:00Z', '{""Title"":""One"",""TreePath"":""/Notes""}')",
            new { Id = Guid.NewGuid().ToString(), Article = oneId, Node = _nodeId.ToString() });
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_article_version")).Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("UPDATE tbl_article_version SET title = 'Forged'")]
    [InlineData("UPDATE tbl_article_version SET version_number = version_number + 7")]
    [InlineData("DELETE FROM tbl_article_version")]
    [InlineData("UPDATE tbl_article_version SET iv = randomblob(12)")]
    [InlineData("UPDATE tbl_blob SET data = randomblob(length(data)) WHERE hash IN (SELECT ciphertext_hash FROM tbl_article_version)")]
    [InlineData("UPDATE tbl_conflict_version SET metadata_json = '{\"Title\":\"Forged\"}'")]
    [InlineData("UPDATE tbl_conflict_version SET expires_at = '2026-09-02T00:00:00Z'")]
    [InlineData("DELETE FROM tbl_conflict_version")]
    [InlineData("UPDATE tbl_conflict_version SET ciphertext = randomblob(64)")]
    public async Task TamperedHistory_IsReportedAsDiffering_StateStaysConfirmed(string tamper)
    {
        await AddHistoryAsync();
        await Anchors().PublishAsync();
        using (var conn = _f.Factory.CreateConnection())
            await conn.ExecuteAsync(tamper);

        var result = await VerifyAsync(_dek);

        result.Confirmed.Should().BeTrue("history is compared on its own, beside the state");
        result.HistoryState.Should().Be("differs");
        result.History!.DifferingBuckets.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task UntouchedHistory_MatchesTheAnchor()
    {
        await AddHistoryAsync();
        await Anchors().PublishAsync();

        var result = await VerifyAsync(_dek);

        result.Confirmed.Should().BeTrue();
        result.HistoryState.Should().Be("matches");
    }

    [Fact]
    public async Task AlteredHistoryEntry_IsListedAsUnverified()
    {
        await AddHistoryAsync();
        await Anchors().PublishAsync();
        string versionId;
        using (var conn = _f.Factory.CreateConnection())
        {
            versionId = (await conn.ExecuteScalarAsync<string>("SELECT id FROM tbl_article_version LIMIT 1"))!;
            await conn.ExecuteAsync("UPDATE tbl_article_version SET title = 'Forged' WHERE id = @versionId", new { versionId });
        }

        var result = await VerifyAsync(_dek);

        result.History!.Unverified.Should().Contain(e => e.Type == "article_version" && e.Id == versionId.ToLowerInvariant());
    }

    [Theory]
    [InlineData("tbl_article_version", "article_version")]
    [InlineData("tbl_conflict_version", "conflict_version")]
    public async Task HistoryReSealedByACampaign_ShowsInItsKeyEntry_WithTheKeyGeneration(string table, string type)
    {
        await AddHistoryAsync();
        await Anchors().PublishAsync();
        using var conn = _f.Factory.CreateConnection();
        await conn.ExecuteAsync($"ALTER TABLE {table} ADD COLUMN key_gen TEXT"); // BMB-57's column, backfilled null = '0'
        (await VerifyAsync(_dek)).HistoryState.Should().Be("matches", "a null key_gen is generation '0', as before the column existed");
        var id = (await conn.ExecuteScalarAsync<string>($"SELECT id FROM {table} LIMIT 1"))!.ToLowerInvariant();

        await conn.ExecuteAsync($"UPDATE {table} SET key_gen = 'campaign-1'");

        var result = await VerifyAsync(_dek);
        result.HistoryState.Should().Be("differs");
        result.History!.Unverified.Should().Contain(e => e.Type == type + "_key" && e.Id == id);
        if (StateDigest.BucketOf(new StateDigestEntry(type, id, 0, "")) != StateDigest.BucketOf(new StateDigestEntry(type + "_key", id, 0, "")))
            result.History.Unverified.Should().NotContain(e => e.Type == type && e.Id == id, "the content entry is untouched by a re-seal");
    }

    [Fact]
    public async Task HistoryWrittenByAnotherNode_DoesNotBreakTheStateConfirmation()
    {
        await Anchors().PublishAsync();
        await AddHistoryAsync(); // e.g. the anchoring node's own version rows, which a blind copy lacks

        var result = await VerifyAsync(_dek);

        result.DigestMatches.Should().BeTrue("history rows are not in the state buckets");
        result.HistoryState.Should().Be("differs");
    }

    // --- skipped boxes -------------------------------------------------------------------------

    private static RecoverySetBox JunkBox(string kind, string preset, string fp, int order)
    {
        var wrapped = SecureRandom.GetBytes(49);
        wrapped[0] = 0x01;
        return new RecoverySetBox($"00000000-0000-0000-0000-{order:D12}", kind, Guid.NewGuid().ToString(), fp, 9, preset,
            Convert.ToBase64String(SecureRandom.GetBytes(32)), Convert.ToBase64String(wrapped), Convert.ToBase64String(SecureRandom.GetBytes(12)),
            DateTime.UtcNow.ToString("O"), 9, null);
    }

    private static async Task<RecoverySetBox> RealBox(byte[] dek, string kind, string preset)
    {
        var seal = await HeavyDerivationQueue.RunAsync(() => RecoveryBoxCrypto.Wrap(dek, Password, preset));
        return new RecoverySetBox("ffffffff-ffff-ffff-ffff-ffffffffffff", kind, Guid.NewGuid().ToString(), DekFingerprint.Of(dek), 2, preset,
            Convert.ToBase64String(seal.Salt), Convert.ToBase64String(seal.Wrapped), Convert.ToBase64String(seal.Iv), DateTime.UtcNow.ToString("O"), 2, null);
    }

    [Theory]
    [InlineData(true, false)]  // junk device boxes under the genuine newest fingerprint, past the per-key cap
    [InlineData(false, true)]  // junk strong boxes under many fake fingerprints, past the heavy budget
    [InlineData(true, true)]
    public async Task SkippedBoxes_NeverConfirmTheAnchorOfAnOlderKey(bool junkUnderGenuine, bool fakeStrongs)
    {
        // This node's key is the OLD one; its genuine anchor is signed and verifies under it. A newer key
        // exists and its boxes are in the set, but junk keeps them from being tried.
        await Anchors().PublishAsync();
        var newer = MasterKeyManager.GenerateMasterDek();
        var newFp = DekFingerprint.Of(newer);
        var boxes = new List<RecoverySetBox> { await RealBox(_dek, "device", RecoveryBoxKdf.Device64) };
        if (junkUnderGenuine)
        {
            boxes.AddRange(Enumerable.Range(0, RecoveryAttemptBudget.DefaultPerKey).Select(i => JunkBox("device", "d64t3", newFp, i)));
            boxes.Add(await RealBox(newer, "device", RecoveryBoxKdf.Device64));
        }
        if (fakeStrongs)
        {
            boxes.AddRange(Enumerable.Range(1, 2).Select(i => JunkBox("strong", "s512t6", new string('0', 63) + i, i)));
            boxes.Add(await RealBox(newer, "strong", RecoveryBoxKdf.Strong512));
        }
        var set = new RecoverySet(RecoverySet.FormatV1, boxes, [], [], [], DateTime.UtcNow.ToString("O"));

        using var keys = (await RecoveryKeyResolver.ResolveAsync(set, Password, budget: new RecoveryAttemptBudget(maxHeavy: 2)))!;
        var anchorEvents = await _f.EventLogRepo.GetRecentAsync(100, 0, EventTypes.StateAnchor);
        AnchorVerification result;
        using (var conn = _f.Factory.CreateConnection())
            result = await StateAnchorService.VerifyAsync(conn, keys.Current, anchorEvents,
                new Dictionary<Guid, byte[]> { [_nodeId] = _publicKey }, keys.HeadProven);

        keys.Current.Should().Equal(_dek, "only the old key opened");
        result.Confirmed.Should().BeFalse("a box that was never tried may hold the newer key");
        result.State.Should().Be("unconfirmed");
    }

    [Theory]
    [InlineData("device", RecoveryBoxKdf.Device64, 0, 1, false)] // light: the one box takes the last light slot
    [InlineData("device", RecoveryBoxKdf.Device64, 0, 2, true)]  // control: a slot to spare
    [InlineData("strong", RecoveryBoxKdf.Strong512, 1, 0, false)] // heavy: the one box takes the last heavy slot
    [InlineData("strong", RecoveryBoxKdf.Strong512, 2, 0, true)]  // control
    public async Task TheLastBoxTakingTheLastSlot_LeavesTheHeadUnproven_AndTheAnchorUnconfirmed(
        string kind, string preset, int maxHeavy, int maxLight, bool confirms)
    {
        await Anchors().PublishAsync();
        var set = new RecoverySet(RecoverySet.FormatV1, [await RealBox(_dek, kind, preset)], [], [], [], DateTime.UtcNow.ToString("O"));

        using var keys = (await RecoveryKeyResolver.ResolveAsync(set, Password, budget: new RecoveryAttemptBudget(maxHeavy, maxLight)))!;
        var anchorEvents = await _f.EventLogRepo.GetRecentAsync(100, 0, EventTypes.StateAnchor);
        AnchorVerification result;
        using (var conn = _f.Factory.CreateConnection())
            result = await StateAnchorService.VerifyAsync(conn, keys.Current, anchorEvents,
                new Dictionary<Guid, byte[]> { [_nodeId] = _publicKey }, keys.HeadProven);

        keys.RemainingBoxes.Should().Be(0, "no box was left over");
        keys.HeadProven.Should().Be(confirms);
        result.Confirmed.Should().Be(confirms, "a search that stopped at its bound proves nothing past it");
    }

    // --- trust section (superadmins) -------------------------------------------------------------

    /// <summary>A superadmin row with an id whose trust bucket differs from every id in <paramref name="avoid"/>.</summary>
    private Task<Guid> AddSuperadminAsync(byte[]? key = null, params Guid[] avoid) => AddPeerAsync(true, key, avoid);

    private async Task<Guid> AddPeerAsync(bool superadmin, byte[]? key = null, params Guid[] avoid)
    {
        int Bucket(Guid id) => StateDigest.BucketOf(new StateDigestEntry("peer", id.ToString(), 0, ""));
        var taken = avoid.Append(_nodeId).Select(Bucket).ToHashSet();
        var id = Enumerable.Range(0, 10_000).Select(_ => Guid.NewGuid()).First(g => !taken.Contains(Bucket(g)));
        await _f.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = "peer", Ed25519PublicKey = key ?? Ed25519Signer.GenerateKeyPair().publicKey,
            IsSuperadmin = superadmin, Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        return id;
    }

    [Fact]
    public async Task ASuperadminTheAnchorCovers_IsVouchedFor_OneAddedAfterIsNot()
    {
        var covered = await AddSuperadminAsync();
        await Anchors().PublishAsync();
        var added = await AddSuperadminAsync(avoid: covered);

        var result = await VerifyAsync(_dek);

        result.Confirmed.Should().BeTrue("the trust section is judged on its own, beside the state");
        result.Vouches(covered).Should().BeTrue();
        result.Vouches(added).Should().BeFalse("no anchor under the master key names it a superadmin");
    }

    [Fact]
    public async Task ASuperadminWhoseKeyWasSwapped_IsNotVouchedFor()
    {
        var peer = await AddSuperadminAsync();
        await Anchors().PublishAsync();
        using (var conn = _f.Factory.CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_whitelist SET ed25519_public_key = randomblob(32) WHERE node_id = @Id COLLATE NOCASE",
                new { Id = peer.ToString() });

        (await VerifyAsync(_dek)).Vouches(peer).Should().BeFalse();
    }

    [Fact]
    public async Task AnOrdinaryRow_TheAnchorCovers_IsVouchedFor_OneAddedAfterIsNot()
    {
        var covered = await AddPeerAsync(superadmin: false);
        await Anchors().PublishAsync();
        var added = await AddPeerAsync(superadmin: false, avoid: covered);

        var result = await VerifyAsync(_dek);

        result.Vouches(covered).Should().BeTrue();
        result.Vouches(added).Should().BeFalse("no anchor under the master key lists it in the mesh");
    }

    [Fact]
    public async Task OtherRowsChangingNextToTheSigner_NeitherVoidTheAnchorNorUnvouchTheUnchangedRows()
    {
        // Rows added after the anchor land wherever their ids hash to — next to the signer's and the covered
        // row's old buckets as well. Row by row, only the new rows are unvouched.
        var covered = await AddPeerAsync(superadmin: false);
        await Anchors().PublishAsync();
        int Bucket(Guid id) => StateDigest.BucketOf(new StateDigestEntry("peer", id.ToString(), 0, ""));
        var nextToSigner = await AddPeerWithIdAsync(Enumerable.Range(0, 10_000).Select(_ => Guid.NewGuid()).First(g => Bucket(g) == Bucket(_nodeId)));
        var nextToCovered = await AddPeerWithIdAsync(Enumerable.Range(0, 10_000).Select(_ => Guid.NewGuid()).First(g => Bucket(g) == Bucket(covered)));

        var result = await VerifyAsync(_dek);

        result.Found.Should().BeTrue("the signer's own row is checked on its own");
        result.Vouches(covered).Should().BeTrue();
        result.Vouches(nextToSigner).Should().BeFalse();
        result.Vouches(nextToCovered).Should().BeFalse();
    }

    private async Task<Guid> AddPeerWithIdAsync(Guid id)
    {
        await _f.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = "peer", Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey,
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        return id;
    }

    [Fact]
    public async Task WithoutAMatchingAnchor_NoSuperadminIsVouchedFor()
    {
        var peer = await AddSuperadminAsync();

        (await VerifyAsync(_dek)).Vouches(peer).Should().BeFalse("there is no anchor at all");
    }

    [Fact]
    public async Task TamperedState_IsNotConfirmed()
    {
        await Anchors().PublishAsync();
        using (var conn = _f.Factory.CreateConnection())
            await conn.ExecuteAsync("PRAGMA foreign_keys = OFF; DELETE FROM tbl_article WHERE title = 'Two'");

        var result = await VerifyAsync(_dek);

        result.Found.Should().BeTrue();
        result.DigestMatches.Should().BeFalse();
        result.Confirmed.Should().BeFalse();
    }

    [Fact]
    public async Task AnchorUnderAnotherKey_IsOnlyReportedByDate()
    {
        var payload = await Anchors().PublishAsync();

        var result = await VerifyAsync(MasterKeyManager.GenerateMasterDek());

        result.Found.Should().BeFalse();
        result.UnconfirmedAnchorDates.Should().Contain(payload.CreatedAt);
    }

    [Fact]
    public async Task SignedAnchorWithAWrongHmac_IsNotTrusted()
    {
        // Signed by a genuine superadmin, but its MAC was not made with the key being restored (a node
        // without the current DEK, or a forgery): the signature alone does not vouch for the state.
        using (var conn = _f.Factory.CreateConnection())
        {
            var state = await StateDigest.ComputeAsync(conn);
            await new RecoveryEventPublisher(_f.NodeRepo, _f.EventLogRepo, _f.Clock, _f.Session, _f.EventApplier,
                    new SyncTrigger(), new FixedOwnStanding(true))
                .PublishAsync(EventTypes.StateAnchor, new StateAnchorPayload(Guid.NewGuid().ToString(), DekFingerprint.Of(_dek),
                    state.PositionVector, state.Digest, new string('0', 64), DateTime.UtcNow.ToString("O")));
        }

        (await VerifyAsync(_dek)).Found.Should().BeFalse();
    }

    [Fact]
    public async Task RowsNewerThanTheAnchor_AreCheckedBySignature()
    {
        await Anchors().PublishAsync();
        var later = await _f.ArticleService.CreateAsync("Three", "/Notes", [], "after the anchor");
        var events = await _f.EventLogRepo.GetByArticleAsync(later.Id);

        var withEvents = await VerifyAsync(_dek, events);
        var withoutEvents = await VerifyAsync(_dek);

        withEvents.MatchesAnchor.Should().BeTrue("the state at the anchor is untouched");
        withEvents.Confirmed.Should().BeFalse("a row newer than the anchor is not covered by it");
        withEvents.State.Should().Be("partially_confirmed");
        withEvents.NewerRows.Should().Be(2, "the new article's content entry and its key entry");
        withEvents.NewerRowsSigned.Should().Be(2);
        withEvents.Unverified.Should().BeEmpty();
        withoutEvents.Unverified.Select(u => u.Id).Should().OnlyContain(id => id == later.Id.ToString()).And.HaveCount(2);
    }

    [Fact]
    public async Task NewerRow_WithAForgedEvent_StaysUnverified()
    {
        await Anchors().PublishAsync();
        var later = await _f.ArticleService.CreateAsync("Three", "/Notes", [], "after the anchor");
        var events = (await _f.EventLogRepo.GetByArticleAsync(later.Id)).ToList();
        foreach (var e in events) e.Signature = new byte[64];

        (await VerifyAsync(_dek, events)).Unverified.Should().HaveCount(2);
    }

    private sealed class ConcreteFixture : SyncTestFixture { }
}
