using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Recovery;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>Who the restored device becomes: its admin account (with the master password) and name.</summary>
public sealed record RestoreIdentity(string AdminUsername, string DisplayName, string Password);

/// <summary>The blind node a network restore came from; it becomes this device's first peer.</summary>
/// <param name="CpSeq">Its log head the package covers (the signed manifest's cp_sequence).</param>
/// <param name="TlsSpki">The pin the user typed with the code: the blind node's row keeps it.</param>
/// <param name="TlsTrust">How this node's TLS endpoint is trusted (<see cref="BlindTrust"/>); null with a pin reads as <c>pin</c>.
/// A hub on a public CA (an Android backup's listener) is <c>public-ca</c> with no pin.</param>
public sealed record RestoreBlindPeer(Guid NodeId, string DisplayName, byte[] PublicKey, string ApiAddress, long CpSeq, string? TlsSpki = null,
    string? TlsTrust = null);

/// <summary>What a restore did.</summary>
/// <param name="RemainingBoxes">Recovery boxes the user chose not to try (<see cref="RestoreBoxPolicy.SkipRemaining"/>).</param>
/// <param name="Claim">
/// A network restore's claim on the blind node, made after the restore committed: "complete", "pending"
/// (no answer within <see cref="BlindRestoreClient.ClaimTimeout"/>) or "refused"; null for a backup.
/// </param>
/// <param name="UnconfirmedPeers">
/// Peers the source named that no anchor under the master key vouches for: restored INACTIVE
/// (<see cref="RestoredPeerStatus.Unconfirmed"/>) until the user confirms or re-pairs them.
/// </param>
public sealed record RestoreResult(Guid NodeId, byte[] PublicKey, AnchorVerification Anchor, int RetiredKeys, long LamportTs,
    int RemainingBoxes = 0, string? Claim = null, IReadOnlyList<RestoredPeerRef>? UnconfirmedPeers = null);

/// <summary>A peer named in a restore result; <paramref name="WasSuperadmin"/>: as the source listed it.</summary>
public sealed record RestoredPeerRef(Guid NodeId, string DisplayName, bool WasSuperadmin);

/// <summary>The whitelist status of a restored row no anchor vouched for: kept, never used, until confirmed.</summary>
public static class RestoredPeerStatus
{
    public const string Unconfirmed = WhitelistStatuses.Unconfirmed;
}

/// <summary>
/// What a restore does about recovery boxes left untried by the attempt budget (<see cref="RecoveredKeys.RemainingBoxes"/>).
/// </summary>
public enum RestoreBoxPolicy
{
    /// <summary>Stop before anything is written (<see cref="RecoveryBoxesRemainingException"/>) and let the user choose.</summary>
    Default,
    /// <summary>The user chose to try every remaining box; cancellable like any restore.</summary>
    TryAll,
    /// <summary>The user chose to go on without them: the head stays unproven, nothing is confirmed.</summary>
    SkipRemaining,
}

/// <summary>
/// Brings up a new device from recovery material (plan 6.7 step 3, 6.8): the master password is tried
/// on the boxes, the chain is unwound, the key is chosen by the anchor — all BEFORE anything is written,
/// so a wrong password leaves nothing behind. Then the replicated state is imported, a fresh identity,
/// slot and admin are created under the current key, every older key is kept (late bodies under them
/// still open), the Lamport clock ends strictly above every imported Lamport value (a state claiming
/// more than <see cref="MaxRestoredLamport"/> is refused before anything is written),
/// the anchor is checked and the search index and embeddings are queued for a rebuild. The new slot is
/// published as this device's box; the strong box follows at the first login.
/// </summary>
public class RecoveryRestoreService(
    IServiceScopeFactory scopes,
    BeeMemoryBank.Sync.LamportClock clock,
    ILogger<RecoveryRestoreService> logger)
{
    /// <summary>
    /// The highest Lamport value a restore accepts. A fresh node has no clock of its own to measure a
    /// jump against, so the bound is absolute: 2^53 events is beyond any real mesh (one tick per event),
    /// yet far enough from long.MaxValue that a crafted package cannot push this node's clock to the
    /// edge of overflow, where every later event would collide.
    /// </summary>
    public const long MaxRestoredLamport = 1L << 53;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Test seam: awaited at the named points of the restore tail ("peers-written", "before-marker-lowered"); a test
    /// throws from it to cut the restore there, as a crash would. Always null in production.
    /// </summary>
    internal Func<string, Task>? StepForTests { get; set; }

    private Task StepAsync(string name) => StepForTests?.Invoke(name) ?? Task.CompletedTask;

    /// <summary>
    /// A blind package (BlindPackageBuilder). Read with the blind seed's own reader
    /// (<see cref="SnapshotService.ExtractVerifiedAsync"/>: every file against the signed manifest), the
    /// embedded signature checked against <paramref name="producerPublicKey"/>, and its signed
    /// blind-manifest.json taken as the evidence: whitelist (superadmin flags, TLS pins) and positions.
    /// </summary>
    /// <param name="events">The signed events that came with the package (the blind node's restore route).</param>
    public async Task<RestoreResult> RestoreFromPackageAsync(
        string packagePath, byte[] signature, byte[] producerPublicKey, RestoreIdentity who,
        RestoreBlindPeer? blind, IReadOnlyList<SyncEvent> events, CancellationToken ct = default,
        RestoreBoxPolicy boxes = RestoreBoxPolicy.Default)
    {
        // In the data folder and owner-only, not the OS temp folder: the extracted package holds the vault database in clear.
        string dataPath;
        using (var pathScope = scopes.CreateScope())
            dataPath = pathScope.ServiceProvider.GetRequiredService<SnapshotService>().DataPath;
        var dir = SnapshotStaging.NewDirectory(dataPath);
        try
        {
            VerifiedSnapshot package;
            BlindManifest manifest;
            using (var scope = scopes.CreateScope())
            {
                try
                {
                    package = await scope.ServiceProvider.GetRequiredService<SnapshotService>().ExtractVerifiedAsync(packagePath, dir);
                }
                catch (InvalidOperationException ex)
                {
                    // A file that does not match its manifest hash, an unknown layout: not a package to restore from.
                    throw new InvalidDataException($"Not a valid blind package: {ex.Message}", ex);
                }
                // Both signatures up front: the embedded one covers every file through the manifest, the sidecar
                // the whole archive (what the join import checks again), so nothing is resolved from a package
                // the import would refuse.
                if (!package.IsSignedBy(producerPublicKey)
                    || !Ed25519Signer.Verify(producerPublicKey,
                        await SnapshotService.ComputeSignaturePayloadAsync(package.ManifestBytes, packagePath, ct), signature))
                    throw new InvalidDataException("The package signature does not verify.");
                var manifestPath = Path.Combine(package.Directory, BlindManifest.FileName);
                if (!File.Exists(manifestPath))
                    throw new InvalidDataException("Not a blind package: blind-manifest.json is missing.");
                manifest = BlindManifest.Parse(await File.ReadAllBytesAsync(manifestPath, ct));
            }
            var producer = blind?.NodeId ?? manifest.ProducerNodeId;
            if (manifest.ProducerNodeId != producer)
                throw new InvalidDataException("The package was not built by the node it came from.");
            // One row per node, and the producer's own row naming the key that signed the package.
            if (manifest.Whitelist.GroupBy(p => p.NodeId).Any(g => g.Count() > 1))
                throw new InvalidDataException("The package's manifest lists a node twice.");
            var producerRow = manifest.Whitelist.SingleOrDefault(p => p.NodeId == producer);
            if (producerRow == null || !ProducerKeyMatches(producerRow.PublicKeyB64, producerPublicKey))
                throw new InvalidDataException("The package's manifest does not name its producer with the key that signed it.");

            var evidence = new RestoreEvidence(RestoreEvidenceFromManifest.Of(manifest), events);
            return await RestoreCoreAsync(package.DatabasePath, recoverySet: null, who, blind is null ? null : blind with { CpSeq = manifest.CpSequence },
                evidence, async sp =>
                {
                    await sp.GetRequiredService<SnapshotService>().RestoreForJoinAsync(packagePath, signature, producerPublicKey);
                    // Beyond the join snapshot: what lets the restored node refuse late events about
                    // something deleted for good (plan 3.6).
                    ImportTables(sp.GetRequiredService<DbConnectionFactory>(), package.DatabasePath, BlindPackageBuilder.ExtraTables);
                }, ct, boxes);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* a temp folder; the OS cleans the temp folder */ }
        }
    }

    /// <summary>
    /// A database copy from a backup (plan 6.8), with the recovery set that lay next to it. No producer
    /// signature here — the anchor is what vouches for the state. The whitelist, positions and signed
    /// events come from the copy itself.
    /// </summary>
    public async Task<RestoreResult> RestoreFromDatabaseAsync(
        string databasePath, RecoverySet? recoverySet, RestoreIdentity who, CancellationToken ct = default,
        RestoreBoxPolicy boxes = RestoreBoxPolicy.Default)
    {
        var evidence = await DatabaseRestoreEvidence.ReadAsync(databasePath);
        return await RestoreCoreAsync(databasePath, recoverySet, who, blind: null, evidence, sp =>
        {
            ImportTables(sp.GetRequiredService<DbConnectionFactory>(), databasePath,
                [.. SnapshotTables.Replicated, .. BlindPackageBuilder.ExtraTables]);
            return Task.CompletedTask;
        }, ct, boxes);
    }

    /// <summary>
    /// The keys, under <paramref name="boxes"/>: with <see cref="RestoreBoxPolicy.Default"/> a set whose boxes
    /// were not all tried, or whose search stopped at a budget limit, stops here
    /// (<see cref="RecoveryBoxesRemainingException"/>) — nothing is written yet.
    /// </summary>
    /// <param name="defaultBudget">The budget of a <see cref="RestoreBoxPolicy.Default"/> run (tests); null = the defaults.</param>
    public static async Task<RecoveredKeys> ResolveKeysAsync(RecoverySet set, string password, RestoreBoxPolicy boxes, CancellationToken ct,
        RecoveryAttemptBudget? defaultBudget = null)
    {
        RecoveredKeys? keys;
        try
        {
            keys = await RecoveryKeyResolver.ResolveAsync(set, password, ct,
                boxes == RestoreBoxPolicy.TryAll ? RecoveryAttemptBudget.Unlimited() : defaultBudget);
        }
        catch (RecoveryBoxesRemainingException) when (boxes == RestoreBoxPolicy.SkipRemaining)
        {
            keys = null;
        }
        if (keys == null)
            throw new UnauthorizedAccessException("The master password opens none of the recovery boxes.");
        if ((keys.RemainingBoxes > 0 || keys.BudgetExhausted) && boxes == RestoreBoxPolicy.Default)
        {
            var remaining = keys.RemainingBoxes;
            keys.Dispose();
            throw new RecoveryBoxesRemainingException(remaining);
        }
        return keys;
    }

    private async Task<RestoreResult> RestoreCoreAsync(
        string databaseForKeys, RecoverySet? recoverySet, RestoreIdentity who, RestoreBlindPeer? blind,
        RestoreEvidence evidence, Func<IServiceProvider, Task> import, CancellationToken ct,
        RestoreBoxPolicy boxes)
    {
        if (string.IsNullOrWhiteSpace(who.AdminUsername) || string.IsNullOrWhiteSpace(who.DisplayName))
            throw new ArgumentException("Admin username and device name are required.");

        await Gate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            if (await sp.GetRequiredService<InitializationService>().IsInitializedAsync())
                throw new InvalidOperationException("This node is already initialized; restore needs a fresh node.");

            // 1. Keys first, from the copy, before anything is written here.
            if (recoverySet == null)
            {
                using var source = new SqliteConnection($"Data Source={databaseForKeys};Mode=ReadOnly;Pooling=False");
                source.Open();
                recoverySet = await RecoverySetBuilder.BuildAsync(source);
            }
            using var keys = await ResolveKeysAsync(recoverySet, who.Password, boxes, ct);
            // Several unlinked keys: which is newest is not proven, so nothing will be confirmed. The key to
            // write under is still best taken from the data itself: the one that opens the newest body.
            if (!keys.HeadProven && NewestBodyHead(databaseForKeys, keys) is { } byData)
                keys.UseHead(byData);

            // The clock the restored node must start above, checked before anything is written.
            long maxImported;
            using (var source = new SqliteConnection($"Data Source={databaseForKeys};Mode=ReadOnly;Pooling=False"))
            {
                source.Open();
                // What is imported, and nothing a producer says about its clock: the restored node only has to
                // end above the rows it takes over (no event is imported), and an unsigned claim would let a
                // hostile source push its clock to the edge.
                maxImported = await MaxLamportAsync(source);
            }
            if (maxImported is < 0 or >= MaxRestoredLamport)
                throw new InvalidDataException(
                    $"The recovery material claims Lamport time {maxImported}, beyond the {MaxRestoredLamport} a restore accepts; refusing it.");

            // Everything that can refuse this restore without writing anything (the gate above, a
            // password that opens no box, a source claiming impossible Lamport time) has now
            // refused. From here the bootstrap writes, so it raises the marker that says "this node
            // is halfway through a restore": without it, a crash in the middle of the bootstrap
            // leaves an identity row that IsInitializedAsync reads as a finished node, and the
            // operator's next attempt is refused with "restore needs a fresh node" (review
            // release-a2 batch 2; the marker is lowered with the last write of the restore,
            // see step 8).
            await sp.GetRequiredService<RestoreBootstrapMarker>().SetAsync(ct);

            // 2. The replicated state.
            await import(sp);

            // 3. Identity, slot and admin under the current key — RESUMED, never repeated.
            //
            // This is the step a crash interrupts most cheaply (it writes the identity row first),
            // and IsInitializedAsync then reports the node as NOT initialized, which is exactly the
            // instruction the operator needs: restore again. So the retry has to be re-entrant, and
            // the two things it must never do are add a SECOND identity row and insert the admin a
            // second time (review release-a2 spec#1, agy#3, sec#4):
            //
            //   * tbl_node_identity is read with an unordered LIMIT 1 by GetAsync AND by
            //     StoreSentinelAsync, so two rows make "this node's identity" ambiguous — the id
            //     returned here could be served by one row while the other one signs. The row a
            //     torn attempt left is ADOPTED when its seed opens under the DEK this run derived
            //     (a retry with the same recovery material does), and otherwise replaced IN PLACE
            //     by its primary key: one row either way.
            //   * tbl_user.username is UNIQUE, so a repeated insert fails the retry outright and
            //     leaves a vault that can neither initialize nor unlock.
            var connFactory = sp.GetRequiredService<DbConnectionFactory>();
            var nodeRepo = sp.GetRequiredService<INodeIdentityRepository>();
            var userRepo = sp.GetRequiredService<IUserRepository>();
            var keySlotRepo = sp.GetRequiredService<IKeySlotRepository>();
            var adminUsername = who.AdminUsername.Trim();
            var now = DateTime.UtcNow;

            var partial = await nodeRepo.GetAsync();
            Guid nodeId;
            byte[] publicKey, wrappedPk, pkIv, privateKey;
            if (partial is not null && OpensUnder(partial, keys.Current))
            {
                nodeId = partial.NodeId;
                publicKey = partial.Ed25519PublicKey;
                wrappedPk = partial.Ed25519PrivateKey;
                pkIv = partial.Ed25519PrivateKeyIV!;
            }
            else
            {
                nodeId = Guid.NewGuid();
                (publicKey, privateKey) = Ed25519Signer.GenerateKeyPair();
                (wrappedPk, pkIv) = NodeIdentityVault.EncryptPrivateKey(privateKey, keys.Current, nodeId);
                Array.Clear(privateKey);
            }

            if (partial is null)
            {
                await nodeRepo.CreateAsync(new NodeIdentity
                {
                    NodeId = nodeId, DisplayName = who.DisplayName.Trim(), Ed25519PublicKey = publicKey,
                    Ed25519PrivateKey = wrappedPk, Ed25519PrivateKeyIV = pkIv, Ed25519PrivateKeyV = 1,
                    CreatedAt = now
                });
            }
            else
            {
                // In place, by the row that is there — replacing whatever a previous attempt left,
                // whether that is a half-written row or one sealed under a DEK this run cannot open.
                using var conn = connFactory.CreateConnection();
                await conn.ExecuteAsync(
                    @"UPDATE tbl_node_identity
                         SET node_id = @NodeId, display_name = @Name, ed25519_public_key = @Pk,
                             ed25519_private_key = @Wrapped, ed25519_private_key_iv = @Iv,
                             ed25519_private_key_v = 1, created_at = @Now
                       WHERE node_id = @Old",
                    new
                    {
                        NodeId = nodeId, Name = who.DisplayName.Trim(), Pk = publicKey, Wrapped = wrappedPk,
                        Iv = pkIv, Now = now.ToString("O"), Old = partial.NodeId
                    });
            }

            // The slot and the admin: the rows a torn attempt left are reused, not duplicated. A
            // second slot wrapping the same DEK would be invisible to the operator and would still
            // accept the OLD password after the next password change (ChangePasswordAsync rotates
            // the one slot the user points at), so reusing the row matters, not just the row count.
            var existingAdmin = await userRepo.GetByUsernameAsync(adminUsername);
            var existingSlot = existingAdmin?.KeySlotId is { } id
                ? (await keySlotRepo.GetAllAsync()).FirstOrDefault(s => s.SlotId == id)
                : null;

            // A reused slot keeps its own salt: the KEK must be derived from what is on the row, or
            // the wrap and the salt would disagree and the vault would open for nobody.
            var salt = existingSlot?.Salt ?? KeyDerivation.GenerateSalt();
            var kek = KeyDerivation.DeriveKek(who.Password, salt);
            MasterKeyStore slot;
            try
            {
                var (encDek, iv) = MasterKeyManager.WrapMasterDek(keys.Current, kek);
                slot = new MasterKeyStore
                {
                    SlotType = "user", EncryptedMasterDek = encDek, IV = iv, Salt = salt,
                    ArgonMemory = CryptoConstants.DefaultArgonMemory, ArgonIterations = CryptoConstants.DefaultArgonIterations,
                    ArgonParallelism = CryptoConstants.DefaultArgonParallelism, CreatedAt = existingSlot?.CreatedAt ?? now
                };
            }
            finally
            {
                Array.Clear(kek);
            }

            var passwordHash = UserService.HashPassword(who.Password);
            if (existingSlot is not null)
            {
                slot.SlotId = existingSlot.SlotId;
                await keySlotRepo.UpdateSlotKeyAsync(existingSlot.SlotId, slot.EncryptedMasterDek, slot.IV);
            }
            else
            {
                slot.SlotId = await keySlotRepo.CreateAsync(slot);
            }

            if (existingAdmin is null)
            {
                await userRepo.CreateAsync(new User
                {
                    Username = adminUsername, DisplayName = adminUsername,
                    PasswordHash = passwordHash, Role = UserRoles.Superadmin,
                    KeySlotId = slot.SlotId, IsActive = true, CreatedAt = now
                });
            }
            else
            {
                // Raw, so the row keeps everything this restore does not own (its id, its chat
                // access, its last login) and the UNIQUE username is never touched.
                using var conn = connFactory.CreateConnection();
                await conn.ExecuteAsync(
                    @"UPDATE tbl_user
                         SET display_name = @Name, password_hash = @Hash, role = @Role,
                             key_slot_id = @Slot, is_active = 1
                       WHERE username = @Username COLLATE NOCASE",
                    new
                    {
                        Name = adminUsername, Hash = passwordHash, Role = UserRoles.Superadmin,
                        Slot = slot.SlotId, Username = adminUsername
                    });
            }
            await nodeRepo.StoreSentinelAsync(MasterKeyManager.ComputeSentinel(keys.Current));

            await VerifyBootstrappedIdentityAsync(nodeRepo, nodeId, publicKey, keys.Current);

            using (var conn = connFactory.CreateConnection())
            {
                // The last writes of the bootstrap, together. The marker stays up past them: it is lowered only
                // when the whole restore is done (step 8) - the peers, the clock floor and this device's box
                // included - so that a cut anywhere before that is a restore to run again, never a node that
                // reports itself finished over a half-done one (review release-a2 A2-a).
                using var tx = conn.BeginTransaction();
                await conn.ExecuteAsync(
                    "INSERT OR IGNORE INTO tbl_migration_marker (key, value, set_at) VALUES ('legacy_password_unified', '1', @T)",
                    new { T = DateTime.UtcNow.ToString("O") }, tx);
                await conn.ExecuteAsync("UPDATE tbl_node_identity SET dek_epoch = @E", new { E = Math.Max(1, keys.EpochHint) }, tx);

                // Older keys, sealed under the current one like DekRewrapper keeps them: a body that
                // arrives late under one of them, or already sits in the state, still opens.
                foreach (var (fp, oldDek) in keys.Retired)
                {
                    var name = IRetiredMasterDekStore.KeyNamePrefix + (recoverySet.Links
                        .FirstOrDefault(l => l.OldFingerprint == fp)?.CommitId ?? "fp-" + fp);
                    var (wrapped, iv) = NodeDataKeyEnvelope.Wrap(name, oldDek, keys.Current);
                    await conn.ExecuteAsync(
                        $"INSERT OR IGNORE INTO {NodeDataKeyEnvelope.TableName} (key_name, wrapped_key, iv, created_at) VALUES (@N, @W, @I, @T)",
                        new { N = name, W = wrapped, I = iv, T = DateTime.UtcNow.ToString("O") }, tx);
                }

                // Search index and embeddings are node-local: rebuild them from the imported content.
                await conn.ExecuteAsync("UPDATE tbl_article SET embedding_pending = 1, index_pending = 1 WHERE status = 'A'", transaction: tx);

                tx.Commit();
            }

            // 4. Strictly above every imported Lamport value, so this node's next write wins over them.
            clock.RaiseTo(maxImported);
            var lamport = clock.Tick();

            // 5. The anchor: date, whether the state at it is intact - and, from its trust section, which of the
            // rows the source names it vouches for. Checked BEFORE any peer row exists (the check reads no
            // whitelist row for that; see AnchorVerification.VouchesRow).
            AnchorVerification anchor;
            using (var conn = connFactory.CreateConnection())
                anchor = await StateAnchorService.VerifyAsync(conn, keys.Current, evidence.Events,
                    evidence.Manifest?.Keys ?? new Dictionary<Guid, byte[]>(), keys.HeadProven);

            // 6. Who it trusts and how far it has pulled from the blind node, each peer with its FINAL status, and the
            // clock floor - one transaction. A peer is active only where the anchor vouches for its row: what the
            // source says about who is in the mesh is as unverified as its data otherwise. The old order wrote every
            // peer active (superadmin as the source claimed) and demoted the unvouched ones afterwards, so a cut in
            // between left them trusted. The floor makes the clock above durable: no imported row carries an event,
            // and every start rebuilds the clock from the event log and the floor (LamportFloor).
            var unconfirmedPeers = await WritePeersAsync(connFactory, evidence.Manifest, blind, nodeId, anchor, lamport);
            sp.GetService<SpkiPinRegistry>()?.Invalidate();
            await StepAsync("peers-written");

            // 7. This device's box, like every other slot change - before the marker comes down, so a restore cut
            // before the box is out is run again (the box is published again; the newer one supersedes).
            await sp.GetRequiredService<IRecoveryBoxPublisher>().PublishDeviceBoxAsync(slot, keys.Current);
            await StepAsync("before-marker-lowered");

            // 8. Done: the initial sync marked and the marker lowered, together.
            using (var conn = connFactory.CreateConnection())
            {
                using var tx = conn.BeginTransaction();
                await conn.ExecuteAsync(
                    "UPDATE tbl_node_identity SET initial_sync_completed = 1 WHERE rowid = (SELECT rowid FROM tbl_node_identity LIMIT 1)",
                    transaction: tx);
                RestoreBootstrapMarker.Clear(conn, tx);
                tx.Commit();
            }

            logger.LogInformation(
                "Restore complete: node {Node}, {Retired} older key(s) kept, anchor {Anchor} ({Confirmed})",
                nodeId, keys.Retired.Count, anchor.AnchorId ?? "none", anchor.State);
            // Without its trust comparison: it was made before the peer rows existed, so Vouches would read it as "every
            // node vouched for". The per-row answer is UnconfirmedPeers (and the rows' status).
            return new RestoreResult(nodeId, publicKey, anchor with { Trust = null }, keys.Retired.Count, lamport, keys.RemainingBoxes,
                UnconfirmedPeers: unconfirmedPeers);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// True when the identity row a previous restore attempt left still holds a seed this run can
    /// open — a v=1 row whose wrapped seed decrypts under the DEK derived from the recovery material.
    /// A row that is not (a half-write, or one sealed by a different recovery set) is not adopted:
    /// the bootstrap replaces it in place instead, so the node never signs with a key nobody holds.
    /// </summary>
    private static bool OpensUnder(NodeIdentity identity, byte[] dek)
    {
        if (identity.Ed25519PrivateKeyV != 1 || identity.Ed25519PrivateKeyIV is null
            || identity.Ed25519PrivateKey.Length == 0)
            return false;
        byte[]? seed = null;
        try
        {
            seed = NodeIdentityVault.GetDecryptedPrivateKey(
                identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV,
                identity.NodeId, dek);
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // Malformed row (the version/IV pair does not describe a wrapped seed) — not adopted.
            return false;
        }
        finally
        {
            if (seed is not null) Array.Clear(seed);
        }
    }

    /// <summary>
    /// Before the restore claims success: the identity that was PERSISTED is the one this method
    /// returns, and its stored seed really signs as the returned public key.
    ///
    /// <para>The reviewers' failure mode was exactly the gap between those three facts: a retry
    /// could leave two rows, report one id and have the other one sign (release-a2 spec#1), and a
    /// node whose returned identity is not its signing identity cannot pair, claim a blind node or
    /// authenticate anywhere — while the restore says it succeeded. This turns that into a failed
    /// restore instead.</para>
    /// </summary>
    private static async Task VerifyBootstrappedIdentityAsync(
        INodeIdentityRepository nodeRepo, Guid nodeId, byte[] publicKey, byte[] dek)
    {
        var persisted = await nodeRepo.GetAsync();
        if (persisted is null || persisted.NodeId != nodeId
            || !persisted.Ed25519PublicKey.AsSpan().SequenceEqual(publicKey))
            throw new InvalidOperationException(
                "The restore bootstrap left an identity that is not the one being returned; refusing to report a restore "
                + "whose node would authenticate as a different key. Nothing else was changed — restore again.");

        var probe = "bmb-restore-identity-probe"u8.ToArray();
        byte[]? seed = null;
        try
        {
            seed = NodeIdentityVault.GetDecryptedPrivateKey(
                persisted.Ed25519PrivateKey, persisted.Ed25519PrivateKeyIV, persisted.Ed25519PrivateKeyV,
                persisted.NodeId, dek);
            if (!Ed25519Signer.Verify(persisted.Ed25519PublicKey, probe, Ed25519Signer.Sign(seed, probe)))
                throw new InvalidOperationException(
                    "The restored identity's stored seed does not sign as its public key; the node could not "
                    + "authenticate to any peer. Restore again.");
        }
        finally
        {
            if (seed is not null) Array.Clear(seed);
        }
    }

    private static bool ProducerKeyMatches(string? rowKeyB64, byte[] producerPublicKey)
    {
        try { return rowKeyB64 != null && Convert.FromBase64String(rowKeyB64).AsSpan().SequenceEqual(producerPublicKey); }
        catch (FormatException) { return false; }
    }

    /// <summary>
    /// The peers the source names (and the blind node restored from), each written with its final status in ONE
    /// transaction, together with the pull position for the blind node and the clock floor. Returns the peers kept
    /// inactive. A row the matching anchor vouches for (<see cref="AnchorVerification.VouchesRow"/>: key and superadmin
    /// flag as written) is active as the source has it; the blind node restored from stays active - the restore code the
    /// user typed names its key - but never as a superadmin; every other row is kept but inactive
    /// (<see cref="RestoredPeerStatus.Unconfirmed"/>, no superadmin flag): it cannot authenticate, sync or author
    /// anything here until the user confirms it or pairs the node again. Never a row for itself. A restore run again
    /// after a cut replaces everything the cut run wrote to the whitelist and the pull positions, not only the rows it names itself.
    /// </summary>
    internal static async Task<IReadOnlyList<RestoredPeerRef>> WritePeersAsync(
        DbConnectionFactory connFactory, RestoreManifest? manifest, RestoreBlindPeer? blind, Guid self, AnchorVerification anchor,
        long lamportFloor)
    {
        var now = DateTime.UtcNow;
        var peers = (manifest?.Whitelist ?? []).Where(p => p.NodeId != self).GroupBy(p => p.NodeId).Select(g => g.First()).ToList();
        if (blind != null && peers.All(p => p.NodeId != blind.NodeId))
            peers.Add(new RestorePeer(blind.NodeId, blind.DisplayName, blind.PublicKey, null, IsSuperadmin: false, null));

        var unconfirmed = new List<RestoredPeerRef>();
        using var conn = connFactory.CreateConnection();
        using var tx = conn.BeginTransaction();
        // While the marker is up this restore owns both tables (the node is not initialized and nothing else writes them), so what an
        // earlier, cut attempt left in them is its own: a re-run from another source must not keep a peer, a superadmin or a pull
        // position that only the cut run named and the finishing run never checked against its anchor.
        await conn.ExecuteAsync("DELETE FROM tbl_whitelist", transaction: tx);
        await conn.ExecuteAsync("DELETE FROM tbl_sync_position", transaction: tx);
        foreach (var peer in peers)
        {
            var isBlindSource = blind != null && peer.NodeId == blind.NodeId;
            var tlsSpki = isBlindSource ? blind!.TlsSpki ?? peer.TlsSpki : peer.TlsSpki;
            var tlsTrust = isBlindSource ? blind!.TlsTrust ?? peer.TlsTrust : peer.TlsTrust;
            var vouched = anchor.VouchesRow(peer.NodeId, peer.PublicKey, peer.IsSuperadmin);
            if (!vouched && !isBlindSource)
                unconfirmed.Add(new RestoredPeerRef(peer.NodeId, peer.DisplayName, peer.IsSuperadmin));

            await WhitelistRepository.InsertAsync(conn, tx, new WhitelistEntry
            {
                NodeId = peer.NodeId, DisplayName = peer.DisplayName, Ed25519PublicKey = peer.PublicKey,
                ApiAddress = isBlindSource ? blind!.ApiAddress.TrimEnd('/') : peer.ApiAddress,
                // The blind node we came from: the pin the user typed. Everyone else: the pin the manifest carries.
                TlsSpki = BlindTrust.PinOf(tlsTrust, tlsSpki),
                TlsTrust = BlindTrust.Effective(tlsTrust, tlsSpki),
                IsSuperadmin = vouched && peer.IsSuperadmin,
                Status = vouched || isBlindSource ? "A" : RestoredPeerStatus.Unconfirmed,
                CreatedAt = now, UpdatedAt = now,
                // The row's LWW version as the source held it: an older whitelist_update must still lose.
                LamportTs = peer.LamportTs, SourceNodeId = peer.SourceNodeId
            });
        }

        // The source's pull positions are NOT taken over. They count in each peer's own log, differ from node to
        // node (no anchor can cover them), and nothing in the package shows how far a peer's log really is
        // represented in it: a position set too high would skip that peer's events for good, one set too low
        // only replays events the appliers already resolve (LWW, idempotent). So every peer starts at 0 — a
        // peer whose log was compacted below that answers 410 and asks for a snapshot, never silence.

        // What this node pulled from the blind node: everything the package covered (its own signed checkpoint —
        // the one position the producer speaks for itself about; whatever it withheld comes from the peers).
        if (blind != null)
            await SyncPositionRepository.UpsertAsync(conn, tx, new SyncPosition
            {
                RemoteNodeId = blind.NodeId, LastSequenceNum = manifest?.IncludesUpTo ?? blind.CpSeq, UpdatedAt = now
            });

        LamportFloor.Raise(conn, tx, lamportFloor);
        tx.Commit();
        return unconfirmed;
    }

    // Of several unproven heads, the one that opens the most recently written body (by content version).
    private static string? NewestBodyHead(string databasePath, RecoveredKeys keys)
    {
        using var conn = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        var bodies = conn.Query<(string Id, byte[] Dek, byte[] Iv)>(
            @"SELECT a.id, b.encrypted_dek, b.dek_iv FROM tbl_article_body b JOIN tbl_article a ON a.id = b.article_id
              ORDER BY a.lamport_ts DESC LIMIT 20");
        var heads = keys.Heads.Select(fp => (fp, Key: fp == keys.CurrentFingerprint ? keys.Current : keys.Retired[fp])).ToList();
        foreach (var body in bodies)
        {
            if (!Guid.TryParse(body.Id, out var id)) continue;
            foreach (var (fp, key) in heads)
            {
                try
                {
                    Array.Clear(EnvelopeFraming.Article.UnwrapDek(id, body.Dek, body.Iv, key));
                    return fp;
                }
                catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException) { }
            }
        }
        return null;
    }

    // Every Lamport column of the imported replicated state (a copy from an older schema may lack some).
    private static readonly (string Table, string Column)[] LamportColumns =
    [
        ("tbl_article", "lamport_ts"), ("tbl_folder", "lamport_ts"), ("tbl_media", "lamport_ts"),
        ("tbl_comment", "lamport_ts"), ("tbl_comment", "delete_lamport_ts"), ("tbl_tombstone", "lamport_ts"),
        ("tbl_conflict_version", "lamport_ts"), ("tbl_recovery_box", "lamport_ts"),
        ("tbl_state_anchor", "lamport_ts"), ("tbl_sealed_secret", "lamport_ts"), ("tbl_whitelist", "lamport_ts"),
    ];

    private static async Task<long> MaxLamportAsync(SqliteConnection conn)
    {
        var tables = (await conn.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table'"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        long max = 0;
        foreach (var (table, column) in LamportColumns.Where(c => tables.Contains(c.Table)))
            max = Math.Max(max, await conn.ExecuteScalarAsync<long?>($"SELECT MAX({column}) FROM {table}") ?? 0);
        return max;
    }

    // The join path's import, from a plain database file: the given tables, by column name.
    private static void ImportTables(DbConnectionFactory connFactory, string databasePath, IEnumerable<string> tables)
    {
        using var conn = (SqliteConnection)connFactory.CreateConnection();
        using (var off = conn.CreateCommand()) { off.CommandText = "PRAGMA foreign_keys = OFF"; off.ExecuteNonQuery(); }
        using (var attach = conn.CreateCommand())
        {
            attach.CommandText = $"ATTACH DATABASE '{databasePath.Replace("'", "''")}' AS snap";
            attach.ExecuteNonQuery();
        }
        try
        {
            using var tx = conn.BeginTransaction();
            foreach (var table in tables)
            {
                if (!SnapshotTableImport.SnapshotHasTable(conn, tx, table)) continue;
                SnapshotTableImport.CopyTable(conn, tx, table, orIgnore: true);
            }
            SnapshotTableImport.AdoptLegacyInlineCiphertext(conn, tx);
            tx.Commit();
        }
        finally
        {
            using (var detach = conn.CreateCommand()) { detach.CommandText = "DETACH DATABASE snap"; detach.ExecuteNonQuery(); }
            using var on = conn.CreateCommand();
            on.CommandText = "PRAGMA foreign_keys = ON";
            on.ExecuteNonQuery();
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* a temp file; the OS cleans the temp folder */ }
    }
}
