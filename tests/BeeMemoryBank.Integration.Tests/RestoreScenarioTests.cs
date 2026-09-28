using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Recovery;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The source network for the restore tests, built once: a PC paired with a blind node that rotated the
/// master key twice, with an article under each key and one body that still sits under the FIRST key
/// (a late event from a device that had not rotated). From it: the signed package a blind node would
/// hand out, a backup folder (database copy + recovery set), and — where restic is installed — a restic
/// repository with its recovery set next to it.
/// </summary>
public sealed class RestoreSourceFixture : IAsyncLifetime
{
    public const string Password = "RestorePass1";
    public const string ResticPassword = "restic-repo-secret-123";

    public RecoveryTestFactory Source { get; } = new();
    public RecoveryTestFactory Blind { get; } = new(blind: true);
    public Guid SourceNodeId { get; private set; }
    public Guid BlindNodeIdentity { get; private set; }
    /// <summary>Chosen in a trust bucket of its own (not the PC's, not the blind node's): a bucket is vouched as a whole.</summary>
    public Guid OldLaptopId { get; private set; }
    /// <summary>The old laptop's signing key (a superadmin in the blind node's whitelist).</summary>
    public (byte[] PublicKey, byte[] PrivateKey) OldLaptopKey { get; } = Ed25519Signer.GenerateKeyPair();
    /// <summary>The LWW version of the old laptop's row on the blind node.</summary>
    public const long OldLaptopRowVersion = 777;
    public string Work { get; } = Path.Combine(Path.GetTempPath(), "bmb_restore_src_" + Guid.NewGuid().ToString("N"));
    public Dictionary<Guid, string> Bodies { get; } = new();
    public Guid LateArticle { get; private set; }
    public string PackagePath => Path.Combine(Work, "package.tar.gz");
    public byte[] Signature { get; private set; } = [];
    /// <summary>The signed events the blind node holds (what its restore route hands out with the package).</summary>
    public IReadOnlyList<SyncEvent> BlindEvents { get; private set; } = [];
    public byte[] ProducerKey { get; private set; } = [];
    public string BackupFolder => Path.Combine(Work, "backup");
    public string? ResticRepo { get; private set; }
    public string CurrentFingerprint { get; private set; } = "";
    public HashSet<string> AllFingerprints { get; } = [];

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Work);
        var client = Source.CreateClient();
        await Source.InitializeNodeAsync(password: Password);
        var session = Source.Services.GetRequiredService<SessionService>();
        await session.UnlockAsync(Password);
        // The blind node exists before the PC's anchor and is in the PC's whitelist, as after pairing: the
        // anchor's trust section then lists exactly the mesh the blind node's copy lists.
        await Blind.InitializeNodeAsync();
        NodeIdentity blindIdentity, pcIdentity;
        using (var scope = Blind.Services.CreateScope())
            blindIdentity = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        using (var scope = Source.Services.CreateScope())
            pcIdentity = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var blindId = blindIdentity.NodeId;
        int TrustBucket(Guid id) => StateDigest.BucketOf(new StateDigestEntry("peer", id.ToString(), 0, ""));
        var taken = new[] { blindId, pcIdentity.NodeId }.Select(TrustBucket).ToHashSet();
        OldLaptopId = Enumerable.Range(0, 10_000).Select(_ => Guid.NewGuid()).First(g => !taken.Contains(TrustBucket(g)));
        using (var scope = Source.Services.CreateScope())
        {
            var sourceWhitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
            await sourceWhitelist.CreateAsync(new WhitelistEntry
            {
                NodeId = blindId, DisplayName = "Blind", Ed25519PublicKey = blindIdentity.Ed25519PublicKey,
                Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            // The old laptop is a superadmin in the PC's eyes too, so the PC's anchor vouches for it.
            await sourceWhitelist.CreateAsync(new WhitelistEntry
            {
                NodeId = OldLaptopId, DisplayName = "Old laptop", Ed25519PublicKey = OldLaptopKey.PublicKey, IsSuperadmin = true,
                Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
        }

        using (var scope = Source.Services.CreateScope())
        {
            var admin = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin"))!;
            await Source.Services.GetRequiredService<RecoveryTriggers>().OnSuperadminLogin(admin, Password);
            await scope.ServiceProvider.GetRequiredService<SealedSecretService>().PublishAsync(
                SealedSecretService.ResticName(blindId), System.Text.Encoding.UTF8.GetBytes(ResticPassword));
        }

        var firstDek = session.GetMasterDek();
        AllFingerprints.Add(DekFingerprint.Of(firstDek));
        await CreateArticleAsync(client, "Under key 1", "body one");
        LateArticle = await CreateArticleAsync(client, "Late body", "late body under key 1");

        await RotateAsync(client, session);
        await CreateArticleAsync(client, "Under key 2", "body two");
        await RotateAsync(client, session);
        await CreateArticleAsync(client, "Under key 3", "body three");
        CurrentFingerprint = DekFingerprint.Of(session.GetMasterDek());

        // The late body: its article DEK wrapped under the FIRST master key again, as a device that
        // had not rotated would have written it.
        var connFactory = Source.Services.GetRequiredService<DbConnectionFactory>();
        using (var conn = connFactory.CreateConnection())
        {
            var body = await conn.QuerySingleAsync<(byte[] Dek, byte[] Iv)>(
                "SELECT encrypted_dek, dek_iv FROM tbl_article_body WHERE article_id = @Id COLLATE NOCASE", new { Id = LateArticle.ToString() });
            var articleDek = EnvelopeFraming.Article.UnwrapDek(LateArticle, body.Dek, body.Iv, session.GetMasterDek());
            var (wrapped, iv) = EnvelopeFraming.RewrapDek(articleDek, body.Dek, firstDek, EnvelopeFraming.Article.DekAad(LateArticle));
            await conn.ExecuteAsync("UPDATE tbl_article_body SET encrypted_dek = @W, dek_iv = @I WHERE article_id = @Id COLLATE NOCASE",
                new { W = wrapped, I = iv, Id = LateArticle.ToString() });
        }

        (await Source.Services.GetRequiredService<StateAnchorScheduler>().PublishIfDueAsync(DateTime.UtcNow)).Should().BeTrue();
        await BuildBlindNodeAsync();

        // The package the blind node hands out: the blind package, signed blind-manifest.json included.
        using (var scope = Blind.Services.CreateScope())
        {
            var package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
                .BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin: false);
            File.Copy(package.FilePath, PackagePath);
            Signature = await File.ReadAllBytesAsync(package.FilePath + ".sig");
            BlindEvents = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAllAfterSequenceAsync(0, 100_000);
        }
        using (var scope = Blind.Services.CreateScope())
        {
            var identity = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
            ProducerKey = identity.Ed25519PublicKey;
            BlindNodeIdentity = identity.NodeId;
        }

        // The blind node's backup: database copy + recovery set.
        Directory.CreateDirectory(BackupFolder);
        using (var conn = Blind.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync($"VACUUM INTO '{Path.Combine(BackupFolder, BlindPackageFile.DbFileName).Replace("'", "''")}'");
        var setJson = await Blind.Services.GetRequiredService<RecoverySetBuilder>().BuildJsonAsync();
        await File.WriteAllTextAsync(Path.Combine(BackupFolder, "backup.recovery-set.json"), setJson);

        if (ResticAvailable())
        {
            ResticRepo = Path.Combine(Work, "repo");
            Restic("init");
            var data = Path.Combine(Work, "restic-data");
            Directory.CreateDirectory(data);
            File.Copy(Path.Combine(BackupFolder, BlindPackageFile.DbFileName), Path.Combine(data, BlindPackageFile.DbFileName));
            Restic("backup", data);
            await File.WriteAllTextAsync(ResticRepo + ".recovery-set.json", setJson);
        }
    }

    public Task DisposeAsync()
    {
        Blind.Dispose();
        Source.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// A blind node holding the network's state as it would after seeding and syncing: the PC's
    /// replicated state, the PC and an old laptop as superadmins in its whitelist, its pull positions,
    /// and the PC's signed anchor event in its log.
    /// </summary>
    private async Task BuildBlindNodeAsync()
    {

        var sourceSnapshots = Source.Services.GetRequiredService<SnapshotService>();
        var seed = await sourceSnapshots.CreateAsync(filterSecrets: true, sign: true, cpSequenceNum: 0, encryptDb: false);
        var seedPath = sourceSnapshots.GetSnapshotPath(seed.FileName);
        NodeIdentity pc;
        using (var scope = Source.Services.CreateScope())
            pc = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        SourceNodeId = pc.NodeId;
        await Blind.Services.GetRequiredService<SnapshotService>().RestoreForJoinAsync(
            seedPath, await File.ReadAllBytesAsync(seedPath + ".sig"), pc.Ed25519PublicKey);

        using var blindScope = Blind.Services.CreateScope();
        var whitelist = blindScope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var now = DateTime.UtcNow;
        await whitelist.CreateAsync(new WhitelistEntry
        {
            NodeId = pc.NodeId, DisplayName = "Source PC", Ed25519PublicKey = pc.Ed25519PublicKey,
            IsSuperadmin = true, Status = "A", CreatedAt = now, UpdatedAt = now
        });
        await whitelist.CreateAsync(new WhitelistEntry
        {
            NodeId = OldLaptopId, DisplayName = "Old laptop", Ed25519PublicKey = OldLaptopKey.PublicKey,
            LamportTs = OldLaptopRowVersion, SourceNodeId = pc.NodeId,
            ApiAddress = "https://laptop.test:5300", IsSuperadmin = true, Status = "A", CreatedAt = now, UpdatedAt = now
        });
        var positions = blindScope.ServiceProvider.GetRequiredService<ISyncPositionRepository>();
        await positions.UpsertAsync(new SyncPosition { RemoteNodeId = pc.NodeId, LastSequenceNum = 42, UpdatedAt = now });
        await positions.UpsertAsync(new SyncPosition { RemoteNodeId = OldLaptopId, LastSequenceNum = 7, UpdatedAt = now });

        // The PC's anchor event arrives at the blind node like any synced event.
        using var sourceScope = Source.Services.CreateScope();
        var applier = blindScope.ServiceProvider.GetRequiredService<BeeMemoryBank.Sync.EventApplier>();
        foreach (var evt in await sourceScope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetRecentAsync(100, 0, BeeMemoryBank.Sync.EventTypes.StateAnchor))
            await applier.ApplyAsync(evt);
    }

    private async Task<Guid> CreateArticleAsync(HttpClient client, string title, string content)
    {
        var resp = await client.PostAsJsonAsync("/api/articles", new { title, treePath = "/Restore", content });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await resp.Content.ReadFromJsonAsync<ArticleResponse>())!.Id;
        Bodies[id] = content;
        return id;
    }

    private async Task RotateAsync(HttpClient client, SessionService session)
    {
        var before = DekFingerprint.Of(session.GetMasterDek());
        var propose = await client.PostAsJsonAsync("/api/dek-rotation/propose", new { masterPassword = Password });
        propose.EnsureSuccessStatusCode();
        var commitEventId = (await propose.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("commitEventId").GetGuid().ToString();
        (await client.PostAsJsonAsync("/api/dek-rotation/accept", new { commitEventId, masterPassword = Password })).EnsureSuccessStatusCode();

        var connFactory = Source.Services.GetRequiredService<DbConnectionFactory>();
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            var now = DekFingerprint.Of(session.GetMasterDek());
            if (now != before)
            {
                using var conn = connFactory.CreateConnection();
                var strong = await conn.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM tbl_recovery_box WHERE kind = 'strong' AND status = 'A' AND dek_fingerprint = @F", new { F = now });
                var link = await conn.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM tbl_dek_retired_link WHERE old_fingerprint = @O AND new_fingerprint = @N", new { O = before, N = now });
                var resealed = await conn.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM tbl_sealed_secret WHERE dek_fingerprint = @F", new { F = now });
                if (strong == 1 && link == 1 && resealed == 1)
                {
                    AllFingerprints.Add(now);
                    return;
                }
            }
            await Task.Delay(250);
        }
        throw new TimeoutException("Rotation did not produce its strong box, link and re-seal in time.");
    }

    public static bool ResticAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("restic", "version") { RedirectStandardOutput = true, UseShellExecute = false });
            p!.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private void Restic(params string[] args)
    {
        var psi = new ProcessStartInfo("restic") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-r");
        psi.ArgumentList.Add(ResticRepo!);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["RESTIC_PASSWORD"] = ResticPassword;
        using var p = Process.Start(psi)!;
        p.WaitForExit(120_000);
        p.ExitCode.Should().Be(0, p.StandardError.ReadToEnd());
    }
}

[CollectionDefinition(Name)]
public sealed class RestoreScenarioCollection : ICollectionFixture<RestoreSourceFixture>
{
    public const string Name = "RestoreScenario";
}

/// <summary>
/// Restore (plan 6.7, 6.8): two rotations and a late body under the first key — everything opens;
/// from a backup with no blind node at all; over the network from a blind node, which then trusts the
/// restored device as its superadmin.
/// </summary>
[Collection(RestoreScenarioCollection.Name)]
public class RestoreScenarioTests(RestoreSourceFixture source, ITestOutputHelper output)
{
    private static readonly RestoreIdentity Who = new("admin", "Restored PC", RestoreSourceFixture.Password);

    // In-process the TLS layer is the test server's, so any well-formed pin gets through; every request
    // carrying the code's pin is checked below, the pin check itself is SpkiPinRegistry's (RestoreTlsPinTests).
    private const string TestPin = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    /// <summary>A well-formed restore code (no blind node issued it): by default naming the fixture's blind node.</summary>
    private string FakeCode(string secret = "AAAA-BBBB-CCCC-DDDD", byte[]? key = null, Guid? nodeId = null) =>
        new BlindRestoreCode(nodeId ?? source.BlindNodeIdentity, BlindRestoreCode.FingerprintOf(key ?? source.ProducerKey),
            "https://blind.test", TestPin, secret, DateTime.UtcNow.AddMinutes(15)).ToString();

    [Fact]
    public async Task FromPackage_TwoRotationsAndALateBody_EverythingOpens()
    {
        using var target = new RecoveryTestFactory();
        var result = await target.Services.GetRequiredService<RecoveryRestoreService>().RestoreFromPackageAsync(
            source.PackagePath, source.Signature, source.ProducerKey, Who, null, source.BlindEvents);

        result.RetiredKeys.Should().Be(2);
        result.Anchor.Confirmed.Should().BeTrue("the package's signed blind-manifest.json names the anchor's author a superadmin");
        await AssertEverythingOpensAsync(target);
        using var scope = target.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(source.OldLaptopId))!
            .IsSuperadmin.Should().BeTrue("the whitelist comes from the manifest");
    }

    [Fact]
    public async Task BlindStatus_CarriesTheRecoverySections_AndTheBackupGetsTheRealRecoverySet()
    {
        using var console = source.Blind.CreateClient();

        var json = await (await console.GetAsync("/api/blind/status")).Content.ReadFromJsonAsync<JsonElement>();

        var anchor = json.GetProperty("anchor");
        anchor.GetProperty("created_at").GetString().Should().NotBeNullOrEmpty();
        anchor.GetProperty("confirmed").GetBoolean().Should().BeTrue("the blind copy still reproduces the anchor's digest");
        var boxes = json.GetProperty("boxes");
        boxes.GetProperty("count").GetInt32().Should().BeGreaterThan(0);
        boxes.GetProperty("current_key").GetProperty("fingerprint").GetString().Should().Be(source.CurrentFingerprint);
        var set = await source.Blind.Services.GetRequiredService<BeeMemoryBank.Api.Services.BlindBackup.IRecoverySetSource>().BuildAsync(default);
        RecoverySet.Parse(set!).Boxes.Should().NotBeEmpty("the backup writes the real recovery set, not the stub's nothing");
    }

    /// <summary>
    /// The restore bootstrap writes the identity row first and the key slot, the admin and the
    /// sentinel after it. "Initialized" used to mean no more than "that first row exists", so a
    /// crash in between (a power cut, a killed container — the restore is not a transaction) left a
    /// node that called itself initialized, refused the next restore with "restore needs a fresh
    /// node" and had no slot to unlock with. Nothing cleared it but wiping the volume by hand.
    /// </summary>
    [Fact]
    public async Task ARestoreThatDiedAfterTheNodeRow_IsNotInitialized_AndTheNextAttemptSucceeds()
    {
        using var target = new RecoveryTestFactory();
        var nodeRepo = target.Services.GetRequiredService<INodeIdentityRepository>();
        // Exactly what the crash leaves behind: the row, and nothing else.
        await nodeRepo.CreateAsync(new NodeIdentity
        {
            NodeId = Guid.NewGuid(), DisplayName = "Half restored",
            Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey,
            Ed25519PrivateKey = [], Ed25519PrivateKeyIV = null, Ed25519PrivateKeyV = 1,
            CreatedAt = DateTime.UtcNow
        });
        using (var scope = target.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsInitializedAsync())
                .Should().BeFalse(
                    "there is no key slot, no admin and no sentinel — this node cannot be unlocked, "
                    + "and calling it initialized is what made the brick permanent");

        var result = await target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(source.BackupFolder, Who);

        result.NodeId.Should().NotBe(Guid.Empty);
        await AssertEverythingOpensAsync(target);
    }

    [Fact]
    public async Task FromBackupFolder_WithNoBlindNodeAtAll_EverythingOpens()
    {
        using var target = new RecoveryTestFactory();

        var result = await target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(source.BackupFolder, Who);

        result.RetiredKeys.Should().Be(2);
        result.Anchor.Confirmed.Should().BeTrue();
        await AssertEverythingOpensAsync(target);
    }

    [Fact]
    public async Task FromResticRepository_PasswordComesFromTheSealedSecret()
    {
        if (source.ResticRepo == null)
        {
            output.WriteLine("restic is not installed here; the restic restore path was not exercised.");
            return;
        }
        using var target = new RecoveryTestFactory();

        var result = await target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(source.ResticRepo, Who);

        result.RetiredKeys.Should().Be(2);
        await AssertEverythingOpensAsync(target);
    }

    [Fact]
    public async Task FromBackup_TakesOverWhitelistAndPositions_UnderItsOwnNewIdentity()
    {
        using var target = new RecoveryTestFactory();

        var result = await target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(source.BackupFolder, Who);

        using var scope = target.Services.CreateScope();
        var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        (await whitelist.GetByNodeIdAsync(source.SourceNodeId))!.IsSuperadmin.Should().BeTrue();
        var laptop = (await whitelist.GetByNodeIdAsync(source.OldLaptopId))!;
        laptop.IsSuperadmin.Should().BeTrue();
        laptop.ApiAddress.Should().Be("https://laptop.test:5300");
        (await whitelist.GetByNodeIdAsync(source.BlindNodeIdentity))!.IsSuperadmin.Should().BeFalse(
            "the node that wrote the copy is known, but a blind node is never a superadmin");
        result.NodeId.Should().NotBe(source.SourceNodeId).And.NotBe(source.BlindNodeIdentity);
        (await whitelist.GetByNodeIdAsync(result.NodeId, includeDeleted: true)).Should().BeNull("a node never lists itself");

        // The copy's pull positions (42, 7) are not taken over: each peer is pulled from its start.
        var positions = scope.ServiceProvider.GetRequiredService<ISyncPositionRepository>();
        ((await positions.GetAsync(source.SourceNodeId))?.LastSequenceNum ?? 0).Should().Be(0);
        ((await positions.GetAsync(source.OldLaptopId))?.LastSequenceNum ?? 0).Should().Be(0);
    }

    [Fact]
    public async Task OldBoxesKept_NewerLinksDropped_IsNotConfirmed_ButWritesUnderTheNewestKey()
    {
        // The old strong boxes put back as active and every chain link removed: the password opens three
        // unlinked keys. The genuine anchor under the newest one must not confirm, since nothing proves
        // which key is newest.
        var folder = await BackupCopyAsync(
            "UPDATE tbl_recovery_box SET status = 'A' WHERE kind = 'strong'; DELETE FROM tbl_dek_retired_link;");
        await RewriteRecoverySetAsync(folder);
        using var target = new RecoveryTestFactory();

        var result = await target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(folder, Who);

        result.Anchor.HeadProven.Should().BeFalse();
        result.Anchor.Confirmed.Should().BeFalse();
        result.Anchor.State.Should().Be("unconfirmed");
        await AssertEverythingOpensAsync(target); // current = the key that opens the newest body: the newest
    }

    [Fact]
    public async Task OldBoxesKept_WithTheLinks_TheHeadIsTheNewestKey_AndConfirms()
    {
        var folder = await BackupCopyAsync("UPDATE tbl_recovery_box SET status = 'A' WHERE kind = 'strong';");
        await RewriteRecoverySetAsync(folder);
        using var target = new RecoveryTestFactory();

        var result = await target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(folder, Who);

        result.Anchor.HeadProven.Should().BeTrue();
        result.Anchor.Confirmed.Should().BeTrue();
        result.Anchor.AsOf.Should().NotBeNull();
        await AssertEverythingOpensAsync(target);
    }

    /// <summary>The recovery set next to a tampered copy, rebuilt from that copy (what a tamperer would ship).</summary>
    private static async Task RewriteRecoverySetAsync(string folder, Action<RecoverySet>? edit = null)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(folder, BlindPackageFile.DbFileName)};Pooling=False");
        conn.Open();
        var set = await RecoverySetBuilder.BuildAsync(conn);
        edit?.Invoke(set);
        await File.WriteAllTextAsync(Directory.GetFiles(folder, "*.recovery-set.json").Single(), set.ToJson());
    }

    /// <summary>
    /// Old strong boxes active again, the newest key's strong box removed and its device box hidden behind
    /// junk boxes under its own fingerprint (sorted first): the default budget never tries it.
    /// </summary>
    private async Task<string> NewestBoxHiddenBehindJunkAsync()
    {
        var folder = await BackupCopyAsync("UPDATE tbl_recovery_box SET status = 'A' WHERE kind = 'strong';");
        await RewriteRecoverySetAsync(folder, set =>
        {
            set.Boxes.RemoveAll(b => b.Kind == "strong" && b.DekFingerprint == source.CurrentFingerprint);
            set.Boxes.AddRange(Enumerable.Range(0, RecoveryAttemptBudget.DefaultPerKey).Select(i =>
            {
                var wrapped = SecureRandom.GetBytes(49);
                wrapped[0] = 0x01;
                return new RecoverySetBox($"00000000-0000-0000-0000-{i:D12}", "device", Guid.NewGuid().ToString(), source.CurrentFingerprint, 9,
                    "d64t3", Convert.ToBase64String(SecureRandom.GetBytes(32)), Convert.ToBase64String(wrapped),
                    Convert.ToBase64String(SecureRandom.GetBytes(12)), DateTime.UtcNow.ToString("O"), 9, null);
            }));
        });
        return folder;
    }

    private static async Task<JsonElement> StopsAtRemainingBoxesAsync(RecoveryTestFactory target, HttpClient client, string folder)
    {
        (await client.PostAsJsonAsync("/api/restore/backup", new
        {
            path = folder, password = RestoreSourceFixture.Password, adminUsername = "admin", displayName = "PC"
        })).EnsureSuccessStatusCode();
        var progress = await WaitForRestoreAsync(client);
        progress.GetProperty("state").GetString().Should().Be("boxes_remaining", progress.ToString());
        progress.GetProperty("remainingBoxes").GetInt32().Should().Be(1);
        using var scope = target.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsInitializedAsync()).Should().BeFalse("nothing is written before the choice");
        return progress;
    }

    [Fact]
    public async Task BudgetReachedOnTheLastBox_StopsBeforeWriting_AndWithoutALimitTheHeadIsProven()
    {
        var dek = MasterKeyManager.GenerateMasterDek();
        var seal = RecoveryBoxCrypto.Wrap(dek, "BoundaryPass1", RecoveryBoxKdf.Device64);
        var box = new RecoverySetBox(Guid.NewGuid().ToString(), "device", Guid.NewGuid().ToString(), DekFingerprint.Of(dek), 1, "d64t3",
            Convert.ToBase64String(seal.Salt), Convert.ToBase64String(seal.Wrapped), Convert.ToBase64String(seal.Iv), DateTime.UtcNow.ToString("O"), 1, null);
        var set = new RecoverySet(RecoverySet.FormatV1, [box], [], [], [], DateTime.UtcNow.ToString("O"));

        var act = () => RecoveryRestoreService.ResolveKeysAsync(set, "BoundaryPass1", RestoreBoxPolicy.Default, default,
            new RecoveryAttemptBudget(maxHeavy: 0, maxLight: 1));

        (await act.Should().ThrowAsync<RecoveryBoxesRemainingException>()).Which.Remaining.Should().Be(0);
        using var all = await RecoveryRestoreService.ResolveKeysAsync(set, "BoundaryPass1", RestoreBoxPolicy.TryAll, default);
        all.HeadProven.Should().BeTrue();
    }

    [Fact]
    public async Task NewestBoxHiddenBehindJunk_StopsBeforeWriting_AndWithoutItIsNeverConfirmed()
    {
        var folder = await NewestBoxHiddenBehindJunkAsync();
        using var target = new RecoveryTestFactory();
        using var client = target.CreateClient();
        await StopsAtRemainingBoxesAsync(target, client, folder);

        (await client.PostAsJsonAsync("/api/restore/continue", new { boxes = "skip" })).StatusCode.Should().Be(HttpStatusCode.Accepted);

        var progress = await WaitForRestoreAsync(client);
        progress.GetProperty("state").GetString().Should().Be("done", progress.ToString());
        progress.GetProperty("remainingBoxes").GetInt32().Should().Be(1);
        progress.GetProperty("anchor").GetProperty("headProven").GetBoolean().Should().BeFalse();
        progress.GetProperty("anchor").GetProperty("confirmed").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task NewestBoxHiddenBehindJunk_TryingTheRemainingBoxes_FindsItAndConfirms()
    {
        var folder = await NewestBoxHiddenBehindJunkAsync();
        using var target = new RecoveryTestFactory();
        using var client = target.CreateClient();
        await StopsAtRemainingBoxesAsync(target, client, folder);

        (await client.PostAsJsonAsync("/api/restore/continue", new { boxes = "all" })).StatusCode.Should().Be(HttpStatusCode.Accepted);

        var progress = await WaitForRestoreAsync(client);
        progress.GetProperty("state").GetString().Should().Be("done", progress.ToString());
        progress.GetProperty("anchor").GetProperty("state").GetString().Should().Be("confirmed");
        await AssertEverythingOpensAsync(target);
    }

    [Fact]
    public async Task CancelWhileTheBoxesAreBeingOpened_FreesTheWizardAndTheGate_AndWritesNothing()
    {
        using var target = new RecoveryTestFactory();
        using var client = target.CreateClient();
        var start = new { path = source.BackupFolder, password = RestoreSourceFixture.Password, adminUsername = "admin", displayName = "PC" };

        (await client.PostAsJsonAsync("/api/restore/backup", start)).EnsureSuccessStatusCode();
        await Task.Delay(300); // the strong box's derivation is running now
        var clock = System.Diagnostics.Stopwatch.StartNew();
        (await client.PostAsync("/api/restore/cancel", null)).EnsureSuccessStatusCode();

        var progress = await WaitForRestoreAsync(client);
        progress.GetProperty("state").GetString().Should().Be("cancelled");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "the wizard does not wait for Argon2");
        using (var scope = target.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsInitializedAsync()).Should().BeFalse();

        // The gate is free: a new restore starts and completes.
        (await client.PostAsJsonAsync("/api/restore/backup", start)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await WaitForRestoreAsync(client)).GetProperty("state").GetString().Should().Be("done");
    }

    [Fact]
    public async Task WrongPassword_LeavesTheNodeUninitialized()
    {
        using var target = new RecoveryTestFactory();

        var act = () => target.Services.GetRequiredService<BlindRestoreClient>()
            .RestoreFromBackupAsync(source.BackupFolder, Who with { Password = "WrongPassword9" });

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        using var scope = target.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsInitializedAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task TamperedPackage_IsRefused()
    {
        using var target = new RecoveryTestFactory();
        var tampered = Path.Combine(source.Work, $"tampered-{Guid.NewGuid():N}.tar.gz");
        var bytes = await File.ReadAllBytesAsync(source.PackagePath);
        bytes[^10] ^= 0xFF;
        await File.WriteAllBytesAsync(tampered, bytes);

        var act = () => target.Services.GetRequiredService<RecoveryRestoreService>().RestoreFromPackageAsync(
            tampered, source.Signature, source.ProducerKey, Who, null, []);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ClockJumpFarBeyondTheLocalClock_EndsStrictlyAboveTheImportedState()
    {
        // A mesh that has been busy for years: Lamport values far past what a fresh node's clock
        // could reach in one capped step.
        var folder = await BackupCopyAsync("UPDATE tbl_article SET lamport_ts = 5000000 WHERE title = 'Under key 3'");
        using var target = new RecoveryTestFactory();

        var result = await target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(folder, Who);

        target.Services.GetRequiredService<ILamportClock>().Current.Should().BeGreaterThan(5_000_000);
        result.LamportTs.Should().BeGreaterThan(5_000_000);
    }

    [Fact]
    public async Task AbsurdLamportTime_IsRefused_BeforeAnythingIsWritten()
    {
        var folder = await BackupCopyAsync($"UPDATE tbl_article SET lamport_ts = {1L << 60} WHERE title = 'Under key 3'");
        using var target = new RecoveryTestFactory();

        var act = () => target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(folder, Who);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*Lamport*");
        using var scope = target.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsInitializedAsync()).Should().BeFalse();
    }

    /// <summary>A private copy of the backup folder with <paramref name="tamper"/> applied to its database.</summary>
    private async Task<string> BackupCopyAsync(string tamper)
    {
        var folder = Path.Combine(source.Work, "backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.GetFiles(source.BackupFolder))
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(folder, BlindPackageFile.DbFileName)};Pooling=False");
        conn.Open();
        await conn.ExecuteAsync(tamper);
        return folder;
    }

    /// <summary>
/// A restore that died in the bootstrap leaves rows behind, and <c>IsInitializedAsync</c> then says
/// exactly the right thing: this node is not initialized, restore it again. The retry therefore has
/// to be re-entrant — one identity row, one admin, and the identity that is persisted is the one the
/// restore returns, the one that signs and the one the blind node's claim trusts (review release-a2
/// spec#1, agy#3, sec#4).
/// </summary>
    [Fact]
    public async Task ARestoreRetriedAfterATornBootstrap_KeepsOneIdentity_ThatIsTheOneThatSignsAndClaims()
    {
        var blind = source.Blind;
        using var blindClient = blind.CreateClient();
        var issued = await blindClient.PostAsync("/api/blind/restore-code", null);
        issued.StatusCode.Should().Be(HttpStatusCode.OK, await issued.Content.ReadAsStringAsync());
        var code = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

        using var target = new RecoveryTestFactory();
        target.RouteOutboundHttpThrough(blind.Server.CreateHandler());

        // The crash: an identity row and nothing else — the first write of the bootstrap, before the
        // slot, the admin and the sentinel.
        var halfId = Guid.NewGuid();
        await target.Services.GetRequiredService<INodeIdentityRepository>().CreateAsync(new NodeIdentity
        {
            NodeId = halfId, DisplayName = "Half restored",
            Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey,
            Ed25519PrivateKey = [], Ed25519PrivateKeyIV = null, Ed25519PrivateKeyV = 1,
            CreatedAt = DateTime.UtcNow
        });
        using (var scope = target.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsInitializedAsync())
                .Should().BeFalse("the operator's next move is to restore again, not to set up a new vault");

        using var targetClient = target.CreateClient();
        var start = await targetClient.PostAsJsonAsync("/api/restore/blind", new
        {
            address = "https://blind.test", code, password = RestoreSourceFixture.Password,
            adminUsername = "admin", displayName = "Restored PC"
        });
        start.StatusCode.Should().Be(HttpStatusCode.Accepted, await start.Content.ReadAsStringAsync());
        var progress = await WaitForRestoreAsync(targetClient);
        progress.GetProperty("state").GetString().Should().Be("done", progress.ToString());

        Guid restoredId, storedKeyNodeId;
        byte[] storedKey;
        using (var conn = target.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
        {
            // One row, not two: every read of the identity is an unordered LIMIT 1, so a second row
            // would make "this node's identity" a coin toss between the returned id and the signer.
            (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_node_identity")).Should().Be(1);
            storedKeyNodeId = Guid.Parse(await conn.ExecuteScalarAsync<string>("SELECT node_id FROM tbl_node_identity LIMIT 1")!);
            (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_user WHERE username = 'admin' COLLATE NOCASE"))
                .Should().Be(1, "the retry updates the admin row instead of colliding with its UNIQUE username");
            (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_key_slot")).Should().Be(1,
                "a second slot would keep accepting the old password after the next password change");
        }
        using (var scope = target.Services.CreateScope())
        {
            var identity = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
            restoredId = identity.NodeId;
            storedKey = identity.Ed25519PublicKey;
        }

        // The identity on disk is the one every later read will serve (the progress JSON does not
        // carry it; the claim below and the signature below are what pin the reported result to it).
        storedKeyNodeId.Should().Be(restoredId);
        restoredId.Should().NotBe(halfId, "the torn row is replaced in place, not adopted as this node");

        // …and the stored seed really signs as the stored public key (the node's whole ability to
        // authenticate, pair and claim rests on those two being the same identity).
        var probe = "bmb-restore-retry-probe"u8.ToArray();
        using (var scope = target.Services.CreateScope())
        {
            var identity = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
            var session = scope.ServiceProvider.GetRequiredService<SessionService>();
            await session.UnlockAsync(RestoreSourceFixture.Password);
            var seed = NodeIdentityCrypto.GetDecryptedPrivateKey(
                identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV,
                identity.NodeId, session.GetMasterDek());
            try
            {
                Ed25519Signer.Verify(identity.Ed25519PublicKey, probe, Ed25519Signer.Sign(seed, probe))
                    .Should().BeTrue("the persisted seed is the private half of the persisted key");
            }
            finally { Array.Clear(seed); }
        }

        // …and the blind node's claim trusts that same id with that same key.
        using (var scope = blind.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(restoredId);
            row.Should().NotBeNull("the claim names the restored device");
            row!.Ed25519PublicKey.Should().Equal(storedKey);
            row.IsSuperadmin.Should().BeTrue();
        }
        (await blindClient.GetAsync("/api/blind/status")).StatusCode.Should().Be(HttpStatusCode.OK);

        await AssertEverythingOpensAsync(target);
    }

    [Fact]
    public async Task RestoredClock_IsAboveEverythingImported()
    {
        using var target = new RecoveryTestFactory();
        var result = await target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(source.BackupFolder, Who);

        using var conn = target.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        var maxImported = await conn.ExecuteScalarAsync<long>("SELECT MAX(lamport_ts) FROM tbl_article");
        target.Services.GetRequiredService<ILamportClock>().Current.Should().BeGreaterThan(maxImported);
        result.LamportTs.Should().BeGreaterThan(maxImported);
    }

    [Fact]
    public async Task Restore_PublishesTheNewDeviceBox()
    {
        using var target = new RecoveryTestFactory();
        var result = await target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(source.BackupFolder, Who);

        using var conn = target.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        (await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM tbl_recovery_box WHERE kind = 'device' AND status = 'A' AND author_node_id = @Me COLLATE NOCASE AND dek_fingerprint = @Fp",
            new { Me = result.NodeId.ToString(), Fp = source.CurrentFingerprint })).Should().Be(1);
    }

    [Fact]
    public async Task OverTheNetwork_FromABlindNode_ThenTheBlindNodeTrustsTheRestoredDevice()
    {
        var blind = source.Blind;
        using var blindClient = blind.CreateClient();
        var issued = await blindClient.PostAsync("/api/blind/restore-code", null);
        issued.StatusCode.Should().Be(HttpStatusCode.OK, await issued.Content.ReadAsStringAsync());
        var code = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

        using var target = new RecoveryTestFactory();
        target.RouteOutboundHttpThrough(blind.Server.CreateHandler());
        using var targetClient = target.CreateClient();
        var start = await targetClient.PostAsJsonAsync("/api/restore/blind", new
        {
            address = "https://blind.test", code, password = RestoreSourceFixture.Password, adminUsername = "admin", displayName = "Restored PC"
        });
        start.StatusCode.Should().Be(HttpStatusCode.Accepted, await start.Content.ReadAsStringAsync());

        var progress = await WaitForRestoreAsync(targetClient);
        progress.GetProperty("state").GetString().Should().Be("done", progress.ToString());
        progress.GetProperty("anchor").GetProperty("state").GetString().Should().Be("confirmed");
        await AssertEverythingOpensAsync(target);

        Guid restoredId;
        using (var scope = target.Services.CreateScope())
        {
            restoredId = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
            var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
            (await whitelist.GetByNodeIdAsync(source.OldLaptopId))!.IsSuperadmin.Should().BeTrue("the blind node's whitelist is taken over");
            (await whitelist.GetByNodeIdAsync(source.BlindNodeIdentity))!.ApiAddress.Should().Be("https://blind.test");
            (await whitelist.GetByNodeIdAsync(source.BlindNodeIdentity))!.TlsSpki.Should().Be(BlindRestoreCode.Parse(code).TlsSpki,
                "this device keeps dialing it pinned, with the pin from the code");
            var positions = scope.ServiceProvider.GetRequiredService<ISyncPositionRepository>();
            ((await positions.GetAsync(source.OldLaptopId))?.LastSequenceNum ?? 0).Should().Be(0, "the manifest's position is not taken over");
            (await positions.GetAsync(source.BlindNodeIdentity))!.LastSequenceNum.Should().BeGreaterThan(0,
                "the blind node's own signed checkpoint: what the package covered of its log");
        }
        using (var scope = blind.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(restoredId);
            row.Should().NotBeNull("the claim trusts the restored device on the blind node");
            row!.IsSuperadmin.Should().BeTrue();
        }

        using var reuse = new HttpRequestMessage(HttpMethod.Get, "/api/blind/restore/package");
        reuse.Headers.Add("X-Restore-Code", code);
        (await blind.CreateClient().SendAsync(reuse)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a code is single use");
    }

    [Fact]
    public async Task OverTheNetwork_EveryRequestCarriesTheTypedPin()
    {
        var blind = source.Blind;
        using var blindClient = blind.CreateClient();
        var code = (await (await blindClient.PostAsync("/api/blind/restore-code", null)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetString()!;
        var recorder = new PinRecorder(blind.Server.CreateHandler());
        using var target = new RecoveryTestFactory();
        target.RouteOutboundHttpThrough(recorder);
        using var targetClient = target.CreateClient();
        (await targetClient.PostAsJsonAsync("/api/restore/blind", new
        {
            address = "https://blind.test", code, password = RestoreSourceFixture.Password, adminUsername = "admin", displayName = "PC"
        })).EnsureSuccessStatusCode();

        (await WaitForRestoreAsync(targetClient)).GetProperty("state").GetString().Should().Be("done");

        // Only the restore's own requests: once restored, the node's sync may already talk to the blind node,
        // pinned through its whitelist row (which the test above checks) rather than an explicit pin.
        var restore = recorder.Seen.Where(s => s.Path.StartsWith("/api/blind/restore/", StringComparison.Ordinal) || s.Path == "/api/blind/claim").ToList();
        restore.Select(s => s.Path).Should().Contain(["/api/blind/restore/package", "/api/blind/restore/events", "/api/blind/claim"]);
        var pin = BlindRestoreCode.Parse(code).TlsSpki;
        restore.Should().OnlyContain(s => s.Pin == pin, "SpkiPinRegistry pins each request to the key in the restore code");
    }

    [Fact]
    public async Task AOneFileBackup_GoesToTheSourceThatHandlesIt_AFolderDoesNot()
    {
        var file = Path.Combine(source.Work, $"phone-{Guid.NewGuid():N}.bmbbackup");
        await File.WriteAllTextAsync(file, "stand-in for a one-file backup");
        var fake = new FakeFileSource(source);
        using var target = new RecoveryTestFactory(services: s => s.AddSingleton<IBackupFileRestoreSource>(fake));
        fake.Restore = target.Services.GetRequiredService<RecoveryRestoreService>();
        var client = target.Services.GetRequiredService<BlindRestoreClient>();

        var result = await client.RestoreFromBackupAsync(file, Who);

        fake.Paths.Should().Equal(Path.GetFullPath(file));
        result.RetiredKeys.Should().Be(2);
        using var other = new RecoveryTestFactory(services: s => s.AddSingleton<IBackupFileRestoreSource>(fake));
        fake.Restore = other.Services.GetRequiredService<RecoveryRestoreService>();
        await other.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(source.BackupFolder, Who);
        fake.Paths.Should().HaveCount(1, "a folder is not a one-file backup");
    }

    /// <summary>Handles *.bmbbackup files by restoring the fixture's backup folder in their place.</summary>
    private sealed class FakeFileSource(RestoreSourceFixture source) : IBackupFileRestoreSource
    {
        public List<string> Paths { get; } = [];
        public RecoveryRestoreService? Restore { get; set; }

        public bool Handles(string fullPath) => fullPath.EndsWith(".bmbbackup", StringComparison.Ordinal);

        public Task<RestoreResult> RestoreAsync(string fullPath, RestoreIdentity who, CancellationToken ct, RestoreBoxPolicy boxes)
        {
            Paths.Add(fullPath);
            var folder = source.BackupFolder;
            var set = RecoverySet.Parse(File.ReadAllText(Directory.GetFiles(folder, "*.recovery-set.json").Single()));
            return Restore!.RestoreFromDatabaseAsync(Path.Combine(folder, BlindPackageFile.DbFileName), set, who, ct, boxes);
        }
    }

    [Theory]
    [InlineData(false)] // from the blind node's package (signed manifest)
    [InlineData(true)]  // from a backup copy
    public async Task RestoredWhitelistRows_KeepTheirVersion_SoAnOlderWhitelistUpdateLoses(bool fromBackup)
    {
        using var target = new RecoveryTestFactory();
        if (fromBackup)
            await target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(source.BackupFolder, Who);
        else
            await target.Services.GetRequiredService<RecoveryRestoreService>().RestoreFromPackageAsync(
                source.PackagePath, source.Signature, source.ProducerKey, Who, null, source.BlindEvents);

        using var scope = target.Services.CreateScope();
        var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var row = (await whitelist.GetByNodeIdAsync(source.OldLaptopId))!;
        row.LamportTs.Should().Be(RestoreSourceFixture.OldLaptopRowVersion);
        row.SourceNodeId.Should().Be(source.SourceNodeId);

        // A genuine, signed, but OLDER update of that row arrives during catch-up.
        var stale = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = source.OldLaptopId, LamportTs = RestoreSourceFixture.OldLaptopRowVersion - 100,
            EventType = BeeMemoryBank.Sync.EventTypes.WhitelistUpdate, ProtocolVersion = BeeMemoryBank.Sync.SyncProtocolVersion.Current,
            CreatedAt = DateTime.UtcNow, EntityId = source.OldLaptopId.ToString(),
            Payload = System.Text.Json.JsonSerializer.Serialize(new BeeMemoryBank.Sync.WhitelistUpdatePayload(source.OldLaptopId, "https://stale.test:5300", null)),
        };
        stale.Signature = Ed25519Signer.Sign(source.OldLaptopKey.PrivateKey, BeeMemoryBank.Sync.EventSignature.BuildPayload(stale));
        await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Sync.EventApplier>().ApplyAsync(stale);

        (await whitelist.GetByNodeIdAsync(source.OldLaptopId))!.ApiAddress.Should().Be("https://laptop.test:5300",
            "the restored row's version is newer than the update");
    }

    [Fact]
    public async Task ASuperadminRowNoAnchorVouchesFor_IsRestoredAsAnOrdinaryPeer()
    {
        // A compromised blind node adds a superadmin of its own to its whitelist: it can sign that, it cannot
        // make the PC's anchor (MAC under the master DEK) cover it.
        // In a trust bucket of its own: a bucket is vouched for or not as a whole.
        int Bucket(Guid id) => StateDigest.BucketOf(new StateDigestEntry("peer", id.ToString(), 0, ""));
        var taken = new[] { source.SourceNodeId, source.OldLaptopId, source.BlindNodeIdentity }.Select(Bucket).ToHashSet();
        var intruder = Enumerable.Range(0, 10_000).Select(_ => Guid.NewGuid()).First(g => !taken.Contains(Bucket(g)));
        var folder = await BackupCopyAsync(
            $@"INSERT INTO tbl_whitelist (node_id, display_name, ed25519_public_key, status, is_superadmin, created_at, updated_at)
               VALUES ('{intruder}', 'Intruder', randomblob(32), 'A', 1, '2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z')");
        using var target = new RecoveryTestFactory();

        var result = await target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(folder, Who);

        result.Anchor.Confirmed.Should().BeTrue("the data still matches: only the trust metadata was touched");
        using var scope = target.Services.CreateScope();
        var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var row = (await whitelist.GetByNodeIdAsync(intruder, includeDeleted: true))!;
        row.IsSuperadmin.Should().BeFalse("no anchor under the master key names it a superadmin");
        row.Status.Should().Be(RestoredPeerStatus.Unconfirmed);
        result.UnconfirmedPeers!.Should().Contain(p => p.NodeId == intruder && p.WasSuperadmin);
        (await whitelist.GetByNodeIdAsync(source.SourceNodeId))!.IsSuperadmin.Should().BeTrue("the anchoring PC vouched for itself");
        (await whitelist.GetByNodeIdAsync(source.OldLaptopId))!.IsSuperadmin.Should().BeTrue("the PC's anchor lists it");
    }

    [Fact]
    public async Task WithoutAMatchingAnchor_EverySuperadminIsRestoredAsAnOrdinaryPeer()
    {
        var folder = await BackupCopyAsync("UPDATE tbl_article SET title = 'Altered' WHERE title = 'Under key 1'");
        using var target = new RecoveryTestFactory();

        var result = await target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(folder, Who);

        result.Anchor.MatchesAnchor.Should().BeFalse();
        using var scope = target.Services.CreateScope();
        var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        (await whitelist.GetAllActiveAsync()).Should().BeEmpty("a backup has no restore code naming any node: nothing is vouched for");
        result.UnconfirmedPeers!.Select(p => p.NodeId).Should().BeEquivalentTo([source.SourceNodeId, source.OldLaptopId, source.BlindNodeIdentity]);
    }

    [Fact]
    public async Task AnOrdinaryPeerRowNoAnchorVouchesFor_NeverBecomesAnActiveSyncPeer()
    {
        // A compromised blind node adds an ordinary peer of its own — its key, its endpoint.
        int Bucket(Guid id) => StateDigest.BucketOf(new StateDigestEntry("peer", id.ToString(), 0, ""));
        var taken = new[] { source.SourceNodeId, source.OldLaptopId, source.BlindNodeIdentity }.Select(Bucket).ToHashSet();
        var intruder = Enumerable.Range(0, 10_000).Select(_ => Guid.NewGuid()).First(g => !taken.Contains(Bucket(g)));
        var (intruderKey, intruderPrivate) = Ed25519Signer.GenerateKeyPair();
        var folder = await BackupCopyAsync(
            $@"INSERT INTO tbl_whitelist (node_id, display_name, ed25519_public_key, api_address, status, is_superadmin, created_at, updated_at)
               VALUES ('{intruder}', 'Intruder', X'{Convert.ToHexString(intruderKey)}', 'https://intruder.test', 'A', 0, '2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z')");
        using var target = new RecoveryTestFactory();

        var result = await target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(folder, Who);

        result.UnconfirmedPeers!.Should().Contain(p => p.NodeId == intruder && !p.WasSuperadmin);
        using var scope = target.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync())
            .Should().NotContain(w => w.NodeId == intruder);
        (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync())
            .Should().Contain(w => w.NodeId == source.OldLaptopId, "a row the anchor covers stays active");
        // And it cannot sign in for sync with its key.
        using var http = target.CreateClient();
        var act = () => BeeMemoryBank.Sync.PeerAuthenticator.AuthenticateAsync(new KeySigner(intruderPrivate), http,
            http.BaseAddress!.ToString().TrimEnd('/'), new NodeIdentity { NodeId = intruder, Ed25519PublicKey = intruderKey }, result.NodeId);
        await act.Should().ThrowAsync<Exception>("an inactive row authenticates nothing");
    }

    [Fact]
    public async Task AnUnvouchedPeer_IsListed_AndActiveOnlyOnceASuperadminConfirmsIt()
    {
        int Bucket(Guid id) => StateDigest.BucketOf(new StateDigestEntry("peer", id.ToString(), 0, ""));
        var taken = new[] { source.SourceNodeId, source.OldLaptopId, source.BlindNodeIdentity }.Select(Bucket).ToHashSet();
        var phone = Enumerable.Range(0, 10_000).Select(_ => Guid.NewGuid()).First(g => !taken.Contains(Bucket(g)));
        var folder = await BackupCopyAsync(
            $@"INSERT INTO tbl_whitelist (node_id, display_name, ed25519_public_key, status, is_superadmin, created_at, updated_at)
               VALUES ('{phone}', 'New phone', randomblob(32), 'A', 0, '2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z')");
        using var target = new RecoveryTestFactory();
        await target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(folder, Who);
        using var admin = target.CreateClient();

        var listed = await admin.GetFromJsonAsync<JsonElement>("/api/recovery/restored-peers");
        listed.EnumerateArray().Select(p => p.GetProperty("nodeId").GetGuid()).Should().Contain(phone);
        (await admin.PostAsync($"/api/recovery/restored-peers/{phone}/confirm", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = target.Services.CreateScope();
        var row = (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync()).Single(w => w.NodeId == phone);
        row.IsSuperadmin.Should().BeFalse("confirming makes it active, not a superadmin");
        (await admin.PostAsync($"/api/recovery/restored-peers/{phone}/confirm", null)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "only an unconfirmed row can be confirmed");
    }

    [Fact]
    public async Task AnEventFromAnUnconfirmedPeer_IsDeferred_AndAppliesOnceThePeerIsConfirmed()
    {
        // A relaying peer delivers an event the unconfirmed phone signed: not an answer, so not quarantined.
        int Bucket(Guid id) => StateDigest.BucketOf(new StateDigestEntry("peer", id.ToString(), 0, ""));
        var taken = new[] { source.SourceNodeId, source.OldLaptopId, source.BlindNodeIdentity }.Select(Bucket).ToHashSet();
        var phone = Enumerable.Range(0, 10_000).Select(_ => Guid.NewGuid()).First(g => !taken.Contains(Bucket(g)));
        var (phoneKey, phonePrivate) = Ed25519Signer.GenerateKeyPair();
        var folder = await BackupCopyAsync(
            $@"INSERT INTO tbl_whitelist (node_id, display_name, ed25519_public_key, status, is_superadmin, created_at, updated_at)
               VALUES ('{phone}', 'Phone', X'{Convert.ToHexString(phoneKey)}', 'A', 0, '2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z')");
        using var target = new RecoveryTestFactory();
        await target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(folder, Who);
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = phone, LamportTs = 5_000_000, EventType = BeeMemoryBank.Sync.EventTypes.FolderCreate,
            EntityId = "/From the phone", ProtocolVersion = BeeMemoryBank.Sync.SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow,
            Payload = System.Text.Json.JsonSerializer.Serialize(new BeeMemoryBank.Sync.FolderCreatePayload(
                Guid.NewGuid(), "/From the phone", "From the phone", "/", DateTime.UtcNow, DateTime.UtcNow)),
        };
        evt.Signature = Ed25519Signer.Sign(phonePrivate, BeeMemoryBank.Sync.EventSignature.BuildPayload(evt));

        Exception? deferred = null;
        using (var scope = target.Services.CreateScope())
            try { await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Sync.EventApplier>().ApplyAsync(evt); }
            catch (Exception ex) { deferred = ex; }
        deferred.Should().BeOfType<BeeMemoryBank.Sync.OriginatorUnconfirmedException>();
        BeeMemoryBank.Sync.SyncFailureClassifier.Classify(deferred!).Should().Be(BeeMemoryBank.Core.Interfaces.SyncFailureKind.Deferred,
            "the sync stops and retries it instead of quarantining it and moving on");

        using var admin = target.CreateClient();
        (await admin.PostAsync($"/api/recovery/restored-peers/{phone}/confirm", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        using (var scope = target.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Sync.EventApplier>().ApplyAsync(evt); // the retry
            (await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().ExistsAsync(evt.EventId)).Should().BeTrue();
        }
    }

    private sealed class KeySigner(byte[] privateKey) : INodeAuthSigner
    {
        public byte[] SignChallenge(NodeIdentity identity, byte[] challengePayload) => Ed25519Signer.Sign(privateKey, challengePayload);
    }

    [Fact]
    public async Task ARowAddedNextToTheSigner_NeitherVoidsTheAnchorNorDeactivatesThePeersItCovered()
    {
        // The old design re-hashed the signer's bucket: one unrelated row there (1 in 64 for a random id, a
        // chosen id for a hostile source) voided the anchor and every peer went inactive.
        int Bucket(Guid id) => StateDigest.BucketOf(new StateDigestEntry("peer", id.ToString(), 0, ""));
        var neighbour = Enumerable.Range(0, 10_000).Select(_ => Guid.NewGuid()).First(g => Bucket(g) == Bucket(source.SourceNodeId));
        var folder = await BackupCopyAsync(
            $@"INSERT INTO tbl_whitelist (node_id, display_name, ed25519_public_key, status, is_superadmin, created_at, updated_at)
               VALUES ('{neighbour}', 'Neighbour', randomblob(32), 'A', 0, '2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z')");
        using var target = new RecoveryTestFactory();

        var result = await target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(folder, Who);

        result.Anchor.Found.Should().BeTrue();
        result.Anchor.Confirmed.Should().BeTrue();
        result.UnconfirmedPeers!.Select(p => p.NodeId).Should().Equal([neighbour], "only the row no anchor lists stays inactive");
        using var scope = target.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync()).Select(w => w.NodeId)
            .Should().BeEquivalentTo([source.SourceNodeId, source.OldLaptopId, source.BlindNodeIdentity]);
    }

    [Fact]
    public async Task AnAnchorWhoseSignerWasDemotedLater_StillConfirmsWhatItCovered()
    {
        // The PC anchored as a superadmin; a later, correct whitelist_update demoted it before the copy was made.
        var folder = await BackupCopyAsync($"UPDATE tbl_whitelist SET is_superadmin = 0 WHERE node_id = '{source.SourceNodeId}' COLLATE NOCASE");
        using var target = new RecoveryTestFactory();

        var result = await target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(folder, Who);

        result.Anchor.Found.Should().BeTrue("at its cut the anchor's own trust section lists its signer as a superadmin");
        result.Anchor.Confirmed.Should().BeTrue();
        using var scope = target.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(source.OldLaptopId))!
            .IsSuperadmin.Should().BeTrue("the rows the anchor covered stay vouched for");
    }

    [Fact]
    public async Task APublisherRowWithATlsPin_IsStillVouchedFor()
    {
        var folder = await BackupCopyAsync(
            $"UPDATE tbl_whitelist SET tls_spki = 'CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC' WHERE node_id = '{source.SourceNodeId}' COLLATE NOCASE");
        using var target = new RecoveryTestFactory();

        var result = await target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(folder, Who);

        result.UnconfirmedPeers.Should().BeEmpty();
        using var scope = target.Services.CreateScope();
        var pc = (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(source.SourceNodeId))!;
        pc.IsSuperadmin.Should().BeTrue();
        pc.Status.Should().Be("A");
    }

    [Theory]
    [InlineData(false)] // the blind node's package: its signed manifest carries the positions
    [InlineData(true)]  // a backup copy: its tbl_sync_position
    public async Task AForgedHighPullPosition_IsNotTakenOver_SoThePeersEventsAreStillFetched(bool fromBackup)
    {
        // A source claiming it has pulled far more of the PC's log than it holds would make the restored node
        // ask the PC only for what comes after — the rest skipped for good. Every cursor starts at 0 instead.
        using var target = new RecoveryTestFactory();
        if (fromBackup)
        {
            var folder = await BackupCopyAsync(
                $"UPDATE tbl_sync_position SET last_sequence_num = 1000000 WHERE remote_node_id = '{source.SourceNodeId}' COLLATE NOCASE");
            await target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(folder, Who);
        }
        else
        {
            await target.Services.GetRequiredService<RecoveryRestoreService>().RestoreFromPackageAsync(
                source.PackagePath, source.Signature, source.ProducerKey, Who, null, source.BlindEvents);
        }

        using var scope = target.Services.CreateScope();
        var positions = scope.ServiceProvider.GetRequiredService<ISyncPositionRepository>();
        // SyncClient pulls with afterSequence = this cursor.
        ((await positions.GetAsync(source.SourceNodeId))?.LastSequenceNum ?? 0).Should().Be(0);
        ((await positions.GetAsync(source.OldLaptopId))?.LastSequenceNum ?? 0).Should().Be(0);
    }

    [Fact]
    public async Task AForgedHighLamportHeader_DoesNotMoveTheRestoredClock()
    {
        var blind = source.Blind;
        using var blindClient = blind.CreateClient();
        var code = (await (await blindClient.PostAsync("/api/blind/restore-code", null)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetString()!;
        using var target = new RecoveryTestFactory();
        target.RouteOutboundHttpThrough(new ForgeTheLamportHeader(blind.Server.CreateHandler()));
        using var targetClient = target.CreateClient();
        (await targetClient.PostAsJsonAsync("/api/restore/blind", new
        {
            address = "https://blind.test", code, password = RestoreSourceFixture.Password, adminUsername = "admin", displayName = "PC"
        })).EnsureSuccessStatusCode();

        (await WaitForRestoreAsync(targetClient)).GetProperty("state").GetString().Should().Be("done");

        // The fixture's state is a few hundred Lamport ticks; the forged header claims 2^53 - 1.
        var clock = target.Services.GetRequiredService<ILamportClock>().Current;
        clock.Should().BeLessThan(1_000_000, "an unsigned header says nothing about the clock");
        using var conn = target.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        clock.Should().BeGreaterThan(await conn.ExecuteScalarAsync<long>("SELECT MAX(lamport_ts) FROM tbl_article"));
    }

    /// <summary>Passes everything on, and puts a Lamport claim at the edge of the accepted range on the package response.</summary>
    private sealed class ForgeTheLamportHeader(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = await base.SendAsync(request, ct);
            if (request.RequestUri!.AbsolutePath == "/api/blind/restore/package")
            {
                resp.Headers.Remove("X-BMB-Snapshot-Lamport");
                resp.Headers.Add("X-BMB-Snapshot-Lamport", ((1L << 53) - 1).ToString());
            }
            return resp;
        }
    }

    [Fact]
    public async Task FromBackupFolder_TheWhitelistKeepsItsTlsPins()
    {
        const string laptopPin = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        var folder = await BackupCopyAsync($"UPDATE tbl_whitelist SET tls_spki = '{laptopPin}' WHERE node_id = '{source.OldLaptopId}' COLLATE NOCASE");
        using var target = new RecoveryTestFactory();

        await target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(folder, Who);

        using var scope = target.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(source.OldLaptopId))!
            .TlsSpki.Should().Be(laptopPin);
    }

    /// <summary>Records each request's path and its explicit pin (SpkiPinRegistry.ExplicitPin), passes it on.</summary>
    private sealed class PinRecorder(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<(string Path, string? Pin)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Options.TryGetValue(BeeMemoryBank.Sync.Blind.SpkiPinRegistry.ExplicitPin, out var pin);
            lock (Seen) Seen.Add((request.RequestUri!.AbsolutePath, pin));
            return base.SendAsync(request, ct);
        }
    }

    [Fact]
    public async Task OverTheNetwork_WrongCode_Fails_AndNothingIsWritten()
    {
        var blind = source.Blind;
        using var target = new RecoveryTestFactory();
        target.RouteOutboundHttpThrough(blind.Server.CreateHandler());
        using var targetClient = target.CreateClient();

        (await targetClient.PostAsJsonAsync("/api/restore/blind", new
        {
            address = "https://blind.test", code = FakeCode(), password = RestoreSourceFixture.Password, adminUsername = "admin", displayName = "PC"
        })).EnsureSuccessStatusCode();

        var progress = await WaitForRestoreAsync(targetClient);
        progress.GetProperty("state").GetString().Should().Be("failed");
        progress.GetProperty("error").GetString().Should().Contain("restore code");
        using var scope = target.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsInitializedAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData("http://blind.test", null, true)]                                           // plain HTTP
    [InlineData("https://blind.test", "not-a-pin", true)]                                   // a malformed pin
    [InlineData("https://blind.test", "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB", true)] // a pin the code does not carry
    [InlineData("https://blind.test", null, false)]                                         // not a restore code
    public async Task OverTheNetwork_PlainHttp_AnotherPin_OrNoRealCode_IsRefused(string address, string? pin, bool realCode)
    {
        using var target = new RecoveryTestFactory();
        using var targetClient = target.CreateClient();

        var start = await targetClient.PostAsJsonAsync("/api/restore/blind", new
        {
            address, code = realCode ? FakeCode() : "AAAA-BBBB-CCCC-DDDD", pin, password = RestoreSourceFixture.Password,
            adminUsername = "admin", displayName = "PC"
        });

        start.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(false)] // the response names the other node's key
    [InlineData(true)]  // the response claims the blind node's own key, the package is signed by another
    public async Task OverTheNetwork_APackageFromAnotherKey_IsRefused_AndNothingIsWritten(bool claimTheBlindNodesKey)
    {
        // A pinned endpoint that answers the package request with a package signed by some other key (its own
        // manifest free to name any superadmin it likes): the code names the blind node's key, so it is refused.
        var blind = source.Blind;
        using var blindClient = blind.CreateClient();
        var code = (await (await blindClient.PostAsync("/api/blind/restore-code", null)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetString()!;
        using var rogue = new RecoveryTestFactory(blind: true);
        await rogue.InitializeNodeAsync();
        var swap = new SwapThePackage(blind.Server.CreateHandler(), rogue, claimTheBlindNodesKey ? source : null);
        using var target = new RecoveryTestFactory();
        target.RouteOutboundHttpThrough(swap);
        using var targetClient = target.CreateClient();

        (await targetClient.PostAsJsonAsync("/api/restore/blind", new
        {
            address = "https://blind.test", code, password = RestoreSourceFixture.Password, adminUsername = "admin", displayName = "PC"
        })).EnsureSuccessStatusCode();

        var progress = await WaitForRestoreAsync(targetClient);
        progress.GetProperty("state").GetString().Should().Be("failed");
        progress.GetProperty("error").GetString().Should().Contain(claimTheBlindNodesKey ? "signature does not verify" : "another identity");
        using var scope = target.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsInitializedAsync()).Should().BeFalse();
    }

    /// <summary>
    /// Answers the package request with a package another (blind) node built and signed; with
    /// <c>claim</c> set, the headers name the fixture blind node's identity instead of the signer's.
    /// </summary>
    private sealed class SwapThePackage(HttpMessageHandler inner, RecoveryTestFactory signer, RestoreSourceFixture? claim) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath != "/api/blind/restore/package") return await base.SendAsync(request, ct);
            using var scope = signer.Services.CreateScope();
            var package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
                .BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin: false, ct);
            var identity = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(await File.ReadAllBytesAsync(package.FilePath, ct)) };
            resp.Headers.Add("X-BMB-Snapshot-Signature", Convert.ToBase64String(await File.ReadAllBytesAsync(package.FilePath + ".sig", ct)));
            resp.Headers.Add("X-BMB-Snapshot-Producer", (claim?.BlindNodeIdentity ?? identity.NodeId).ToString());
            resp.Headers.Add("X-BMB-Snapshot-Producer-Key", Convert.ToBase64String(claim?.ProducerKey ?? identity.Ed25519PublicKey));
            return resp;
        }
    }

    [Fact]
    public async Task OverTheNetwork_ARedirectIsNotFollowed()
    {
        var stub = new RedirectingHandler("https://attacker.example/api/blind/restore/package");
        using var target = new RecoveryTestFactory();
        target.RouteOutboundHttpThrough(stub);
        using var targetClient = target.CreateClient();

        (await targetClient.PostAsJsonAsync("/api/restore/blind", new
        {
            address = "https://blind.test", code = FakeCode(), password = RestoreSourceFixture.Password,
            adminUsername = "admin", displayName = "PC"
        })).EnsureSuccessStatusCode();

        var progress = await WaitForRestoreAsync(targetClient);
        progress.GetProperty("state").GetString().Should().Be("failed");
        progress.GetProperty("error").GetString().Should().Contain("redirect");
        stub.Hosts.Should().OnlyContain(h => h == "blind.test", "the redirect target is never contacted");
    }

    [Fact]
    public async Task RogueBlindNode_WithItsOwnKeyAndSidecar_IsReportedUnconfirmed()
    {
        // A node that serves the network's real material — boxes, the PC's genuine signed anchor, a
        // whitelist naming the PC superadmin — but altered data, signed with its own key. Its signature and
        // key list are self-consistent; only the anchor under the master DEK can tell, and it does.
        using var rogue = new RecoveryTestFactory(blind: true);
        await rogue.InitializeNodeAsync();
        await rogue.Services.GetRequiredService<SnapshotService>().RestoreForJoinAsync(source.PackagePath, source.Signature, source.ProducerKey);
        using (var scope = rogue.Services.CreateScope())
        {
            NodeIdentity pc;
            using (var sourceScope = source.Source.Services.CreateScope())
                pc = (await sourceScope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = pc.NodeId, DisplayName = "Source PC", Ed25519PublicKey = pc.Ed25519PublicKey, IsSuperadmin = true,
                Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            using var sourceEvents = source.Source.Services.CreateScope();
            foreach (var evt in await sourceEvents.ServiceProvider.GetRequiredService<IEventLogRepository>()
                         .GetRecentAsync(100, 0, BeeMemoryBank.Sync.EventTypes.StateAnchor))
                await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Sync.EventApplier>().ApplyAsync(evt);
            using var conn = rogue.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
            await conn.ExecuteAsync("UPDATE tbl_article SET title = 'Altered' WHERE title = 'Under key 1'");
        }
        using var rogueConsole = rogue.CreateClient();
        var code = (await (await rogueConsole.PostAsync("/api/blind/restore-code", null)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetString()!;

        using var target = new RecoveryTestFactory();
        target.RouteOutboundHttpThrough(rogue.Server.CreateHandler());
        using var targetClient = target.CreateClient();
        (await targetClient.PostAsJsonAsync("/api/restore/blind", new
        {
            address = "https://blind.test", code, password = RestoreSourceFixture.Password, adminUsername = "admin", displayName = "PC"
        })).EnsureSuccessStatusCode();

        var progress = await WaitForRestoreAsync(targetClient);
        progress.GetProperty("state").GetString().Should().Be("done", "a restore is still allowed");
        var anchor = progress.GetProperty("anchor");
        anchor.GetProperty("state").GetString().Should().Be("unconfirmed");
        anchor.GetProperty("confirmed").GetBoolean().Should().BeFalse();
        // No anchor vouches for its whitelist: only the node the restore code names stays active.
        Guid rogueId;
        using (var rogueScope = rogue.Services.CreateScope())
            rogueId = (await rogueScope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        using var targetScope = target.Services.CreateScope();
        var active = await targetScope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync();
        active.Select(w => w.NodeId).Should().Equal([rogueId]);
        active.Single().IsSuperadmin.Should().BeFalse();
    }

    [Fact]
    public async Task CancelWhileTheClaimIsInFlight_AfterTheCommit_FinishesTheClaim_NeverSaysCancelled()
    {
        var blind = source.Blind;
        using var blindClient = blind.CreateClient();
        var code = (await (await blindClient.PostAsync("/api/blind/restore-code", null)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetString()!;
        var hold = new HoldTheClaim(blind.Server.CreateHandler());
        using var target = new RecoveryTestFactory();
        target.RouteOutboundHttpThrough(hold);
        using var targetClient = target.CreateClient();
        (await targetClient.PostAsJsonAsync("/api/restore/blind", new
        {
            address = "https://blind.test", code, password = RestoreSourceFixture.Password, adminUsername = "admin", displayName = "PC"
        })).EnsureSuccessStatusCode();

        await hold.Arrived.Task.WaitAsync(TimeSpan.FromMinutes(3)); // the restore has committed; the claim is on the wire
        (await targetClient.PostAsync("/api/restore/cancel", null)).EnsureSuccessStatusCode();
        hold.Release.SetResult();

        var progress = await WaitForRestoreAsync(targetClient);
        progress.GetProperty("state").GetString().Should().Be("done", "the node is initialized; \"cancelled\" would be false");
        progress.GetProperty("claim").GetString().Should().Be("complete");
        using var scope = target.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsInitializedAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task ClaimThatGetsNoAnswer_IsReportedPending_TheRestoreIsDone()
    {
        var blind = source.Blind;
        using var blindClient = blind.CreateClient();
        var code = (await (await blindClient.PostAsync("/api/blind/restore-code", null)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetString()!;
        var hold = new HoldTheClaim(blind.Server.CreateHandler(), failWith: HttpStatusCode.ServiceUnavailable);
        using var target = new RecoveryTestFactory();
        target.RouteOutboundHttpThrough(hold);
        using var targetClient = target.CreateClient();
        (await targetClient.PostAsJsonAsync("/api/restore/blind", new
        {
            address = "https://blind.test", code, password = RestoreSourceFixture.Password, adminUsername = "admin", displayName = "PC"
        })).EnsureSuccessStatusCode();

        var progress = await WaitForRestoreAsync(targetClient);

        progress.GetProperty("state").GetString().Should().Be("done");
        progress.GetProperty("claim").GetString().Should().Be("pending");
        hold.Claims.Should().Be(3, "the claim is tried again after a failure");
    }

    /// <summary>Holds the claim until released (or answers it with <c>failWith</c>); passes everything else on.</summary>
    private sealed class HoldTheClaim(HttpMessageHandler inner, HttpStatusCode? failWith = null) : DelegatingHandler(inner)
    {
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Claims;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/api/blind/claim")
            {
                Interlocked.Increment(ref Claims);
                if (failWith is { } status) return new HttpResponseMessage(status);
                Arrived.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return await base.SendAsync(request, ct);
        }
    }

    /// <summary>Answers every request with a redirect and remembers which hosts were asked.</summary>
    private sealed class RedirectingHandler(string location) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Hosts) Hosts.Add(request.RequestUri!.Host);
            var resp = new HttpResponseMessage(HttpStatusCode.Found);
            resp.Headers.Location = new Uri(location);
            return Task.FromResult(resp);
        }
    }

    private static async Task<JsonElement> WaitForRestoreAsync(HttpClient client)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            var p = await client.GetFromJsonAsync<JsonElement>("/api/restore/progress");
            if (p.GetProperty("state").GetString() is "done" or "failed" or "cancelled" or "boxes_remaining") return p;
            await Task.Delay(250);
        }
        throw new TimeoutException("Restore did not finish.");
    }

    private async Task AssertEverythingOpensAsync(RecoveryTestFactory target)
    {
        var session = target.Services.GetRequiredService<SessionService>();
        (await session.UnlockAsync(RestoreSourceFixture.Password)).Should().BeTrue("the new slot opens with the master password");
        DekFingerprint.Of(session.GetMasterDek()).Should().Be(source.CurrentFingerprint, "the restored key is the newest one");

        using var scope = target.Services.CreateScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        foreach (var (id, body) in source.Bodies)
            (await articles.GetContentAsync(id)).Should().Be(body, id == source.LateArticle
                ? "the late body under the first key opens through the unwound chain"
                : "every article opens");
    }
}
