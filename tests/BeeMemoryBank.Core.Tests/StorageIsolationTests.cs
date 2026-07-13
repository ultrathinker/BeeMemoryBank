using System.Runtime.Versioning;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Multi-account isolation guard tests (Этап 6, §6 пункт 1). Each profile/vault is, by
/// construction, an INDEPENDENT data directory with its own SQLite database file and its own
/// sibling state files. These tests prove that independence explicitly and freeze it as a
/// regression boundary: enabling a feature or writing a key slot in vault A must NEVER be
/// observable from vault B.
///
/// <para>Unlike <see cref="OsAutoUnlockServiceTests"/> / <see cref="TestFixture"/>, which use a
/// single shared in-memory database, these tests build TWO genuinely separate file-backed
/// vaults (A and B) — each with its own <see cref="DbConnectionFactory"/>, migration run, and
/// initialized key-slot table — exactly as the desktop node does for two different profiles.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public class StorageIsolationTests : IAsyncDisposable
{
    private readonly string _root;
    private readonly string _dirA;
    private readonly string _dirB;
    private readonly List<DbConnectionFactory> _factories = new();

    public StorageIsolationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "bmb-iso-" + Guid.NewGuid().ToString("N"));
        _dirA = Path.Combine(_root, "vaultA");
        _dirB = Path.Combine(_root, "vaultB");
        Directory.CreateDirectory(_dirA);
        Directory.CreateDirectory(_dirB);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var f in _factories)
        {
            try { f.Dispose(); } catch { }
        }
        try { Directory.Delete(_root, recursive: true); } catch { }
        await ValueTask.CompletedTask;
    }

    // ── Vault builder ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a fully initialized, unlocked vault rooted at <paramref name="dataDir"/>, exactly
    /// mirroring how the desktop host stands up a per-profile storage: its own SQLite file, its
    /// own migration run, and its own initialized key-slot table. Two calls with different
    /// <paramref name="dataDir"/>s therefore produce two physically isolated vaults.
    /// </summary>
    private async Task<Vault> BuildVaultAsync(string dataDir, string password)
    {
        var factory = new DbConnectionFactory(dataDir);
        _factories.Add(factory);

        var runner = new MigrationRunner(factory);
        await runner.RunMigrationsAsync();

        var keySlots = new KeySlotRepository(factory);
        var nodeRepo = new NodeIdentityRepository(factory);
        var userRepo = new UserRepository(factory);

        var session = new SessionService(keySlots);
        var init = new InitializationService(nodeRepo, keySlots, userRepo, factory);
        await init.InitializeAsync("admin", "IsoNode", password);
        await session.UnlockAsync(password);

        return new Vault(factory, keySlots, nodeRepo, session, dataDir);
    }

    private sealed record Vault(
        DbConnectionFactory Factory,
        KeySlotRepository KeySlots,
        NodeIdentityRepository NodeRepo,
        SessionService Session,
        string DataDir);

    // ── Task 1.1 — os_auto_unlock per-vault isolation ──────────────────────────

    [Fact]
    public async Task OsAutoUnlock_EnabledInVaultA_IsInvisibleFromVaultB()
    {
        if (!OperatingSystem.IsWindows()) return;

        var a = await BuildVaultAsync(_dirA, "passwordA");
        var b = await BuildVaultAsync(_dirB, "passwordB");

        // Enable OS auto-unlock ONLY in vault A.
        var svcA = new OsAutoUnlockService(a.KeySlots, a.Session, a.DataDir);
        await svcA.EnableAsync();

        var svcB = new OsAutoUnlockService(b.KeySlots, b.Session, b.DataDir);

        // (a) Vault B's service (its own DB + its own dataPath) reports the feature disabled.
        (await svcB.IsEnabledAsync()).Should().BeFalse(
            "vault B was never enabled, so IsEnabledAsync must be false");

        // (b) The DPAPI secret file physically exists ONLY in A's data directory, never in B's.
        var secretInA = Path.Combine(a.DataDir, "os-auto-unlock.dat");
        var secretInB = Path.Combine(b.DataDir, "os-auto-unlock.dat");
        File.Exists(secretInA).Should().BeTrue("EnableAsync must write the secret into A's data dir");
        File.Exists(secretInB).Should().BeFalse("no secret file may be created in B's data dir");

        // (c) Edge case: B's service is pointed at A's dataPath (a misconfiguration), but because
        // B's OWN database has no os_auto_unlock slot, it still must NOT report enabled — i.e. B
        // does not "pull" A's slot through its own key-slot repository.
        var svcBWithADataPath = new OsAutoUnlockService(b.KeySlots, b.Session, a.DataDir);
        (await svcBWithADataPath.IsEnabledAsync()).Should().BeFalse(
            "B's DB has no os_auto_unlock slot, so even with A's dataPath it must not report enabled");

        // Symmetric edge: A's service pointed at B's dataPath. A's slot exists in A's DB, but the
        // matching secret file is NOT present at B's dataPath -> still disabled. Proves the file is
        // looked up strictly relative to the dataPath passed at construction, not A's.
        var svcAWithBDataPath = new OsAutoUnlockService(a.KeySlots, a.Session, b.DataDir);
        (await svcAWithBDataPath.IsEnabledAsync()).Should().BeFalse(
            "A's slot exists in A's DB, but the secret file is absent at B's dataPath, so it must be disabled");
    }

    // ── Task 1.4 — key-slot tables do not cross-contaminate across vaults ──────

    [Fact]
    public async Task KeySlotTables_PerVault_DoNotCrossContaminate()
    {
        if (!OperatingSystem.IsWindows()) return;

        var a = await BuildVaultAsync(_dirA, "passwordA");
        var b = await BuildVaultAsync(_dirB, "passwordB");

        // Each freshly-initialized vault has exactly one password ("user") slot in its own DB.
        var aSlotsBefore = await a.KeySlots.GetAllAsync();
        var bSlotsBefore = await b.KeySlots.GetAllAsync();

        aSlotsBefore.Should().ContainSingle(s => s.SlotType == "user");
        bSlotsBefore.Should().ContainSingle(s => s.SlotType == "user");

        var aInitialSlotId = aSlotsBefore.Single(s => s.SlotType == "user").SlotId;
        var bInitialSlotId = bSlotsBefore.Single(s => s.SlotType == "user").SlotId;
        aInitialSlotId.Should().Be(bInitialSlotId,
            "sanity: both DBs start from an empty tbl_key_slot so the first row id coincides — " +
            "which is exactly why a naive shared-state bug would be invisible without this test");

        // Add a SECOND, distinguishable slot to vault A only.
        var extraASlotId = await a.KeySlots.CreateAsync(new MasterKeyStore
        {
            SlotType = "password",
            EncryptedMasterDek = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
            IV = new byte[] { 0x11, 0x22 },
            Salt = new byte[] { 0xAA },
            ArgonMemory = 64,
            ArgonIterations = 1,
            ArgonParallelism = 1,
            CreatedAt = DateTime.UtcNow
        });

        // Re-query both vaults through their OWN repositories.
        var aSlotsAfter = await a.KeySlots.GetAllAsync();
        var bSlotsAfter = await b.KeySlots.GetAllAsync();

        aSlotsAfter.Should().HaveCount(2, "vault A received one extra slot");
        bSlotsAfter.Should().HaveCount(1,
            "vault B's DB file is physically separate; A's new slot must never leak into B");

        // The extra slot id created in A must never be found in B's slot list.
        bSlotsAfter.Should().NotContain(s => s.SlotId == extraASlotId,
            "the slot id created in A's DB must not exist in B's DB");

        // Content-level disjointness: B's slots must never carry A's distinctive encrypted DEK
        // bytes, even though both DBs independently number their first row as slot id 1 (which
        // is precisely why an id-only check would be blind to a shared-state bug).
        bSlotsAfter.Should().NotContain(s => s.EncryptedMasterDek.SequenceEqual(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }),
            "A's distinctive encrypted DEK must never appear among B's slots");
        aSlotsAfter.Should().Contain(s => s.EncryptedMasterDek.SequenceEqual(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }),
            "sanity: the distinctive slot really did land in A");

        // Confirm the on-disk DB files are genuinely distinct (the structural root of isolation).
        File.Exists(Path.Combine(_dirA, "beememorybank.db")).Should().BeTrue();
        File.Exists(Path.Combine(_dirB, "beememorybank.db")).Should().BeTrue();
        var aFileInfo = new FileInfo(Path.Combine(_dirA, "beememorybank.db"));
        var bFileInfo = new FileInfo(Path.Combine(_dirB, "beememorybank.db"));
        aFileInfo.FullName.Should().NotBe(bFileInfo.FullName);
    }
}
