using System.Globalization;
using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sealed;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>
/// Keeps the boot store's copies in step with the open sealed database (docs/design/step-b-spec.md 4.3): the doors (key slots and the
/// wrapped DEK of auto-unlock agents), the database-key wrappers (3.4), and the identity and whitelist mirrors. Every member except
/// <see cref="WriteNewBootStoreAsync"/> is a no-op unless the gate says the node is sealed, so a plain node and a blind host never
/// create or touch a boot store through it.
/// </summary>
public sealed class UnlockMaterialMirror : IUnlockMaterialMirror
{
    private readonly IDbConnectionFactory _vault;
    private readonly BootStore _boot;
    private readonly BootDoorStore _doors;
    private readonly BootDbKeyStore _wrappers;
    private readonly BootStoreWriter _writer;
    private readonly IVaultGate? _gate;
    private readonly ISealedVaultKeys? _vaultKeys;
    private readonly Func<byte[][]>? _candidateDeks;
    private readonly ILogger? _logger;

    // One rewrite or wrapper change at a time: they all end in the same boot transaction rules.
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <param name="gate">Null on a host without a gate: the mirror then acts as on a plain node (does nothing).</param>
    /// <param name="vaultKeys">The open vault's database key, for the wrappers; null in tests that do not create wrappers.</param>
    /// <param name="candidateDeks">Clones of every DEK the session holds, current first (<c>SessionService.GetCandidateDeks</c>); the mirror clears them.</param>
    public UnlockMaterialMirror(IDbConnectionFactory vault, BootStore boot, BootDoorStore doors, BootDbKeyStore wrappers, BootStoreWriter writer,
        IVaultGate? gate = null, ISealedVaultKeys? vaultKeys = null, Func<byte[][]>? candidateDeks = null, ILogger<UnlockMaterialMirror>? logger = null)
    {
        _vault = vault;
        _boot = boot;
        _doors = doors;
        _wrappers = wrappers;
        _writer = writer;
        _gate = gate;
        _vaultKeys = vaultKeys;
        _candidateDeks = candidateDeks;
        _logger = logger;
    }

    private bool Sealed => _gate is { IsSealed: true };

    public async Task RewriteAsync(CancellationToken ct = default)
    {
        if (!Sealed) return;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RewriteCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The backstop of spec 4.3: rewrites when the sealed database's <c>unlock_material_version</c> differs from the one the boot store last
    /// mirrored (a writer this specification did not foresee, a restore, a trigger-only change). True when it rewrote.
    /// </summary>
    public async Task<bool> RewriteIfBehindAsync(CancellationToken ct = default)
    {
        if (!Sealed) return false;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sealedVersion = await ReadSealedVersionAsync().ConfigureAwait(false);
            if (string.Equals(sealedVersion, await _boot.GetMetaAsync(BootMetaKeys.UnlockMaterialVersion, ct).ConfigureAwait(false), StringComparison.Ordinal))
                return false;
            await RewriteCoreAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddPendingDoorAsync(PendingDoor door, CancellationToken ct = default)
    {
        if (!Sealed) return;
        ArgumentNullException.ThrowIfNull(door);
        var hash = BootDoorHash.Of(door.Kind, door.SourceId, door.SlotType, door.KeyHash, door.KdfVersion, door.ArgonMemory,
            door.ArgonIterations, door.ArgonParallelism, door.Salt, door.EncryptedMasterDek, door.Iv);
        await _doors.AddPendingAsync(new BootDoor(0, door.Kind, door.SourceId, door.SlotType, door.EncryptedMasterDek, door.Iv, door.Salt,
            door.ArgonMemory, door.ArgonIterations, door.ArgonParallelism, door.KeyHash, door.KdfVersion, hash, door.DekFingerprint, true), ct)
            .ConfigureAwait(false);
    }

    public async Task RetireDoorAsync(string kind, long sourceId, CancellationToken ct = default)
    {
        if (!Sealed) return;
        await _doors.RetireAsync(kind, sourceId, ct).ConfigureAwait(false);
    }

    public async Task AddDbKeyWrapperAsync(byte[] masterDek, CancellationToken ct = default)
    {
        if (!Sealed) return;
        ArgumentNullException.ThrowIfNull(masterDek);
        if (_vaultKeys is null) throw new InvalidOperationException("The mirror has no source of the open database key.");
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var nodeId = await ReadNodeIdAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The boot store carries no node id.");
            var dbk = _vaultKeys.GetDatabaseKey();
            try
            {
                await _wrappers.AddIfMissingAsync(DatabaseKeyCrypto.Wrap(dbk, masterDek, nodeId, _vaultKeys.DbkGeneration), ct).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dbk);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task WriteNewBootStoreAsync(string bootPath, IDbConnectionFactory vault, byte[] databaseKey, int dbkGeneration,
        IReadOnlyList<byte[]> masterDeks, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(bootPath);
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(databaseKey);
        ArgumentNullException.ThrowIfNull(masterDeks);
        if (dbkGeneration < 1) throw new ArgumentOutOfRangeException(nameof(dbkGeneration));
        if (masterDeks.Count == 0) throw new ArgumentException("A boot store without a master DEK could never be opened.", nameof(masterDeks));

        var store = new BootStore(bootPath);
        if (store.Exists && await store.GetMetaAsync(BootMetaKeys.NodeId, ct).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("A boot store already exists at the target: the node id of a store is written once, so a new one needs a new file.");
        await store.EnsureCreatedAsync(ct).ConfigureAwait(false);

        var sealedState = await ReadSealedStateAsync(vault).ConfigureAwait(false);
        var nodeId = sealedState.Identity?.NodeId ?? Guid.NewGuid();
        var wrappers = masterDeks.Select(dek => DatabaseKeyCrypto.Wrap(databaseKey, dek, nodeId, dbkGeneration)).ToList();
        var fingerprints = wrappers.Select(w => w.DekFingerprint).ToList();

        var plan = new BootRewrite(sealedState.Doors, fingerprints[0], fingerprints, wrappers, sealedState.Identity, sealedState.Whitelist, sealedState.Version);
        await new BootStoreWriter(store.Connections).RewriteAsync(plan, ct).ConfigureAwait(false);

        await store.SetMetaAsync(BootMetaKeys.Format, "1", ct).ConfigureAwait(false);
        await store.SetMetaAsync(BootMetaKeys.NodeId, nodeId.ToString("D"), ct).ConfigureAwait(false);
        await store.SetMetaAsync(BootMetaKeys.DbkGeneration, dbkGeneration.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await store.SetMetaAsync(BootMetaKeys.SealedAt, DateTime.UtcNow.ToString("o"), ct).ConfigureAwait(false);
        await store.SetMetaAsync(BootMetaKeys.Activated, "1", ct).ConfigureAwait(false);
        await store.SetMetaAsync(BootMetaKeys.SeqFloor, (sealedState.MaxSequence + 1).ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await store.SetMetaAsync(BootMetaKeys.SealedHead, "", ct).ConfigureAwait(false);
        await store.SetMetaAsync(BootMetaKeys.CleanClose, "0", ct).ConfigureAwait(false);
        await store.CheckpointAsync(ct).ConfigureAwait(false);
    }

    // --- the rewrite ---------------------------------------------------------------------------------------------------------------

    private async Task RewriteCoreAsync(CancellationToken ct)
    {
        var state = await ReadSealedStateAsync(_vault).ConfigureAwait(false);
        var nodeId = await ReadNodeIdAsync(ct).ConfigureAwait(false);

        var candidates = _candidateDeks?.Invoke() ?? [];
        try
        {
            var candidateFingerprints = candidates.Select(DekFingerprint.Of).ToList();
            var existing = (await _wrappers.ListAsync(ct).ConfigureAwait(false)).Select(w => (w.DekFingerprint, w.DbkGeneration)).ToHashSet();

            var add = new List<BootDbKeyRow>();
            if (nodeId is { } id && _vaultKeys is not null && candidates.Length > 0)
            {
                var generation = _vaultKeys.DbkGeneration;
                byte[]? dbk = null;
                try
                {
                    for (var i = 0; i < candidates.Length; i++)
                    {
                        if (existing.Contains((candidateFingerprints[i], generation))) continue;
                        dbk ??= _vaultKeys.GetDatabaseKey();
                        add.Add(DatabaseKeyCrypto.Wrap(dbk, candidates[i], id, generation));
                    }
                }
                catch (VaultSealedException)
                {
                    // The database closed under us: the doors are still rewritten, the wrappers wait for the next open.
                    add.Clear();
                    _logger?.LogDebug("The database closed while the wrappers were prepared; they are written at the next rewrite.");
                }
                finally
                {
                    if (dbk is not null) CryptographicOperations.ZeroMemory(dbk);
                }
            }

            var plan = new BootRewrite(state.Doors, candidateFingerprints.Count > 0 ? candidateFingerprints[0] : null, candidateFingerprints, add,
                state.Identity, state.Whitelist, state.Version);
            var result = await _writer.RewriteAsync(plan, ct).ConfigureAwait(false);
            _logger?.LogDebug("The boot store was rewritten: {Doors} doors, {Wrappers} wrappers kept, {Pruned} pruned.",
                result.Doors, result.WrapperFingerprints.Count, result.WrappersPruned);
        }
        finally
        {
            foreach (var candidate in candidates) CryptographicOperations.ZeroMemory(candidate);
        }
    }

    private async Task<Guid?> ReadNodeIdAsync(CancellationToken ct)
    {
        var text = await _boot.GetMetaAsync(BootMetaKeys.NodeId, ct).ConfigureAwait(false);
        return Guid.TryParseExact(text, "D", out var id) ? id : null;
    }

    private async Task<string> ReadSealedVersionAsync()
    {
        using var connection = _vault.CreateConnection();
        return await connection.ExecuteScalarAsync<string?>("SELECT value FROM tbl_migration_marker WHERE key = 'unlock_material_version'")
            .ConfigureAwait(false) ?? "0";
    }

    /// <summary>Everything the boot store copies, read from one vault connection.</summary>
    private sealed record SealedState(IReadOnlyList<BootDoor> Doors, BootIdentity? Identity,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Whitelist, string Version, long MaxSequence);

    private static async Task<SealedState> ReadSealedStateAsync(IDbConnectionFactory vault)
    {
        using var connection = vault.CreateConnection();
        // One read transaction: the version, the doors and the mirrors are one consistent picture of the database.
        using var transaction = connection.BeginTransaction();

        var doors = new List<BootDoor>();
        foreach (var s in await connection.QueryAsync<SlotRecord>(
                     @"SELECT slot_id AS SlotId, slot_type AS SlotType, encrypted_master_dek AS EncryptedMasterDek, iv AS Iv, salt AS Salt,
                              argon_memory AS ArgonMemory, argon_iterations AS ArgonIterations, argon_parallelism AS ArgonParallelism
                       FROM tbl_key_slot ORDER BY slot_id", transaction: transaction).ConfigureAwait(false))
            doors.Add(new BootDoor(0, BootDoorKinds.Slot, s.SlotId, s.SlotType, s.EncryptedMasterDek, s.Iv, s.Salt, (int?)s.ArgonMemory,
                (int?)s.ArgonIterations, (int?)s.ArgonParallelism, null, null,
                BootDoorHash.Of(BootDoorKinds.Slot, s.SlotId, s.SlotType, null, null, (int?)s.ArgonMemory, (int?)s.ArgonIterations, (int?)s.ArgonParallelism,
                    s.Salt, s.EncryptedMasterDek, s.Iv), null, false));
        foreach (var a in await connection.QueryAsync<AgentRecord>(
                     @"SELECT id AS Id, key_hash AS KeyHash, salt AS Salt, kdf_version AS KdfVersion, encrypted_dek AS EncryptedDek, dek_iv AS DekIv
                       FROM tbl_agent WHERE encrypted_dek IS NOT NULL AND dek_iv IS NOT NULL AND status = 'A' ORDER BY id", transaction: transaction).ConfigureAwait(false))
            doors.Add(new BootDoor(0, BootDoorKinds.Agent, a.Id, null, a.EncryptedDek, a.DekIv, a.Salt, null, null, null, a.KeyHash, (int?)a.KdfVersion,
                BootDoorHash.Of(BootDoorKinds.Agent, a.Id, null, a.KeyHash, (int?)a.KdfVersion, null, null, null, a.Salt, a.EncryptedDek, a.DekIv), null, false));

        BootIdentity? identity = null;
        var row = await connection.QuerySingleOrDefaultAsync<IdentityRecord>(
            "SELECT node_id AS NodeId, display_name AS DisplayName, ed25519_public_key AS PublicKey FROM tbl_node_identity LIMIT 1", transaction: transaction)
            .ConfigureAwait(false);
        if (row is not null && Guid.TryParse(row.NodeId, out var nodeId)) identity = new BootIdentity(nodeId, row.DisplayName, row.PublicKey);

        var whitelist = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var w in await connection.QueryAsync("SELECT * FROM tbl_whitelist", transaction: transaction).ConfigureAwait(false))
            whitelist.Add(new Dictionary<string, object?>(((IDictionary<string, object>)w).Select(p => new KeyValuePair<string, object?>(p.Key, p.Value)), StringComparer.Ordinal));

        var version = await connection.ExecuteScalarAsync<string?>("SELECT value FROM tbl_migration_marker WHERE key = 'unlock_material_version'", transaction: transaction)
            .ConfigureAwait(false) ?? "0";
        var max = await connection.ExecuteScalarAsync<long?>("SELECT MAX(sequence_num) FROM tbl_event", transaction: transaction).ConfigureAwait(false) ?? 0;
        return new SealedState(doors, identity, whitelist, version, max);
    }

    private sealed class SlotRecord
    {
        public long SlotId { get; set; }
        public string SlotType { get; set; } = "";
        public byte[] EncryptedMasterDek { get; set; } = [];
        public byte[] Iv { get; set; } = [];
        public byte[]? Salt { get; set; }
        public long? ArgonMemory { get; set; }
        public long? ArgonIterations { get; set; }
        public long? ArgonParallelism { get; set; }
    }

    private sealed class AgentRecord
    {
        public long Id { get; set; }
        public string KeyHash { get; set; } = "";
        public byte[]? Salt { get; set; }
        public long? KdfVersion { get; set; }
        public byte[] EncryptedDek { get; set; } = [];
        public byte[] DekIv { get; set; } = [];
    }

    private sealed class IdentityRecord
    {
        public string NodeId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public byte[] PublicKey { get; set; } = [];
    }
}
