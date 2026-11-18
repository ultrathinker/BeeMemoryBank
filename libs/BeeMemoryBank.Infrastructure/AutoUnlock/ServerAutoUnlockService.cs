using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Infrastructure.AutoUnlock;

/// <summary>The four states of the server checkbox at start (docs/design/step-b-spec.md 11.2 "At start").</summary>
public enum ServerAutoUnlockState
{
    /// <summary>No slot and no record: every node that never used the checkbox. Nothing is read, nothing is written.</summary>
    Off,

    /// <summary>The slot exists and the enable record matches it: the key file is read and the unlock is tried.</summary>
    Armed,

    /// <summary>A slot exists and no record matches it: it came with restored or copied data, or a crash left a half-done enable. Never used.</summary>
    StaleSlot,

    /// <summary>A record exists and no slot (or one it does not match): the record is removed.</summary>
    StaleRecord,
}

/// <param name="Slots">The <c>server_auto_unlock</c> slots found (the boot doors of a closed sealed node, <c>tbl_key_slot</c> otherwise).</param>
/// <param name="Record">The parsed enable record, or null (missing, unreadable, or not ours).</param>
/// <param name="RecordFileExists">Whether a file with the record's name exists at all.</param>
public sealed record ServerAutoUnlockEvaluation(ServerAutoUnlockState State, IReadOnlyList<ServerSlotView> Slots, ServerEnableRecord? Record, bool RecordFileExists)
{
    /// <summary>The slot the record names, when armed.</summary>
    public ServerSlotView? MatchingSlot => MatchingSlots.FirstOrDefault();

    /// <summary>
    /// Every slot the record names, the one it names as next first. Mid-rotation (a crash between the commit and the rewrite of the boot store)
    /// two doors match: the old one and the re-sealed one, and which of them holds the committed key only the database can say.
    /// </summary>
    public IReadOnlyList<ServerSlotView> MatchingSlots => Record is null
        ? []
        : Slots.Where(s => s.Matches(Record)).OrderByDescending(s => s.IsNext(Record)).ToList();
}

public sealed record ServerSlotView(long SlotId, string ContentSha256, byte[] EncryptedMasterDek, byte[] Iv, string? DekFingerprint, BootDoor? Door,
    int DoorVersion = 1, byte[]? DoorPublicKey = null)
{
    public bool Matches(ServerEnableRecord record) =>
        record.Names(ContentSha256) && (DekFingerprint is null || record.NamesDek(DekFingerprint));

    /// <summary>True for the re-sealed door of a rotation the record was prepared for.</summary>
    public bool IsNext(ServerEnableRecord record) =>
        string.Equals(ContentSha256, record.NextSlotSha256, StringComparison.Ordinal)
        || (DekFingerprint is not null && string.Equals(DekFingerprint, record.NextDekFingerprint, StringComparison.Ordinal));

    /// <summary>Opens the slot with the secret of the key file, whatever its door format (ADR 0012). Throws <see cref="CryptographicException"/> for another secret.</summary>
    public byte[] Open(byte[] secret) => Doors.Open(DoorVersion, ServerAutoUnlockService.SlotType, EncryptedMasterDek, Iv, secret, null, DoorPublicKey);
}

/// <summary>
/// The local enable record <c>&lt;data&gt;/server-auto-unlock.enabled</c> (spec 11.2 part 3): says "the owner enabled exactly this slot on this server";
/// holds no secret. <see cref="NextSlotSha256"/> is set only while the slot is being sealed again as a v2 door (ADR 0012): the record then names the
/// door before and the door after, so that a crash between the two writes leaves the server armed rather than "stale". An older build ignores it.
/// </summary>
public sealed record ServerEnableRecord(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("slot_sha256")] string SlotSha256,
    [property: JsonPropertyName("dek_fp")] string DekFingerprint,
    [property: JsonPropertyName("enabled_at")] string EnabledAt,
    [property: JsonPropertyName("next_slot_sha256"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextSlotSha256 = null,
    [property: JsonPropertyName("next_dek_fp"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextDekFingerprint = null)
{
    /// <summary>True when the record names the slot content <paramref name="slotSha256"/> (the slot, or the replacement in progress).</summary>
    public bool Names(string slotSha256) =>
        string.Equals(slotSha256, SlotSha256, StringComparison.Ordinal)
        || (NextSlotSha256 is { Length: 64 } next && string.Equals(slotSha256, next, StringComparison.Ordinal));

    /// <summary>
    /// True when the record names the DEK <paramref name="dekFingerprint"/>: its own, or, while a rotation re-seals the door (2.12), the new one
    /// (<see cref="NextDekFingerprint"/>).
    /// </summary>
    public bool NamesDek(string dekFingerprint) =>
        string.Equals(dekFingerprint, DekFingerprint, StringComparison.Ordinal)
        || (NextDekFingerprint is { Length: 64 } next && string.Equals(dekFingerprint, next, StringComparison.Ordinal));

    /// <summary>The record once the change it names is done: the next door (and DEK) become the door; nothing is "next" any more.</summary>
    public ServerEnableRecord Settled(bool toNext) => toNext && NextSlotSha256 is { Length: 64 } next
        ? this with { SlotSha256 = next, DekFingerprint = NextDekFingerprint ?? DekFingerprint, NextSlotSha256 = null, NextDekFingerprint = null }
        : this with { NextSlotSha256 = null, NextDekFingerprint = null };
}

/// <summary>What the status route and the status of the vault report (spec 6.7, 11.2).</summary>
/// <param name="Enabled">The effective state: true only when the slot, the key file and the enable record agree.</param>
/// <param name="KeyFileConfigured"><c>BMB_AUTO_UNLOCK_KEY_FILE</c> is set (the operator's file, which the node never writes, moves or deletes).</param>
/// <param name="Notice">A one-time notice for the owner, or null.</param>
public sealed record ServerAutoUnlockStatus(HostRole Role, bool Enabled, ServerAutoUnlockState State, bool KeyFileConfigured, string? Notice);

public enum ServerAutoUnlockStartOutcome
{
    /// <summary>Nothing to do (the state is off, or the role is not a server's).</summary>
    NotApplicable,
    Unlocked,
    /// <summary>A stale slot or record was found and repaired; the node stays locked.</summary>
    Repaired,
    /// <summary>Armed, but the unlock did not happen (a missing or unsafe key file, a secret that does not open the slot, a sentinel that does not match): locked, state unchanged.</summary>
    StayedLocked,
}

/// <summary>A refusal the routes map to a status code; its message names no path and no key.</summary>
public sealed class ServerAutoUnlockException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// "Start unlocked after a restart" for servers (docs/design/step-b-spec.md 11.2): the same feature on plain and on sealed servers, working
/// the same on Windows, macOS, Linux and Docker. Three parts must agree before anything is read or unlocked: the key slot (data, per server),
/// the secret in a key file (never DPAPI or the Keychain), and the local enable record (never in a backup, a snapshot or a package). With the
/// checkbox off - every node after every upgrade - the call at start costs one read of the slot table (plain node) or of the boot store
/// (sealed node) and one <c>File.Exists</c>: no key file is read, no boot store is created.
/// </summary>
public sealed class ServerAutoUnlockService : IServerAutoUnlockLocalState, IUnlockedDoorUpgrade
{
    public const string SlotType = "server_auto_unlock";
    public const string KeyFileEnvironmentVariable = "BMB_AUTO_UNLOCK_KEY_FILE";
    public const string RecordFileName = "server-auto-unlock.enabled";
    public const string NodeKeyFileName = "server-auto-unlock.key";
    public const string PhraseRequired = "START UNLOCKED";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly IKeySlotRepository _slots;
    private readonly SessionService _session;
    private readonly string _dataPath;
    private readonly IDbConnectionFactory? _factory;
    private readonly IUnlockMaterialMirror? _mirror;
    private readonly ISealedAutoUnlockHost? _sealedHost;
    private readonly BootStore? _boot;
    private readonly IServiceScopeFactory? _scopes;
    private readonly ILogger? _logger;
    private readonly IServerKeyFileStore _keyFiles;
    private readonly Func<string?> _configuredKeyFile;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile string? _notice;

    public ServerAutoUnlockService(IKeySlotRepository slots, SessionService session, string dataPath, HostRole role,
        IDbConnectionFactory? factory = null, IUnlockMaterialMirror? mirror = null, ISealedAutoUnlockHost? sealedHost = null, BootStore? bootStore = null,
        IServiceScopeFactory? scopes = null, ILogger<ServerAutoUnlockService>? logger = null, IServerKeyFileStore? keyFiles = null,
        Func<string?>? configuredKeyFile = null)
    {
        _slots = slots;
        _session = session;
        _dataPath = dataPath;
        Role = role;
        _factory = factory;
        _mirror = mirror;
        _sealedHost = sealedHost;
        _boot = bootStore;
        _scopes = scopes;
        _logger = logger;
        _keyFiles = keyFiles ?? new PhysicalServerKeyFileStore();
        _configuredKeyFile = configuredKeyFile ?? (() => Environment.GetEnvironmentVariable(KeyFileEnvironmentVariable));
    }

    public HostRole Role { get; }

    public string RecordPath => Path.Combine(_dataPath, RecordFileName);

    public string NodeKeyFilePath => Path.Combine(_dataPath, NodeKeyFileName);

    /// <summary>The operator's key file when <c>BMB_AUTO_UNLOCK_KEY_FILE</c> is set and not empty, else null.</summary>
    public string? ConfiguredKeyFilePath => _configuredKeyFile() is { Length: > 0 } configured ? configured : null;

    /// <summary>The key file the unlock reads: the configured one, else the node's own.</summary>
    public string KeyFilePath => ConfiguredKeyFilePath ?? NodeKeyFilePath;

    private bool SealedAndClosed => _sealedHost is { IsSealed: true, IsUsable: false };

    // --- evaluation ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Looks at the three parts and answers one of the four states, before any key file is read and before any unlock is tried
    /// (spec 11.2 "At start"). Reads the slots (the boot store of a closed sealed node, <c>tbl_key_slot</c> otherwise) and the record file;
    /// never the key file, never the secret.
    /// </summary>
    public async Task<ServerAutoUnlockEvaluation> EvaluateAsync(CancellationToken ct = default)
    {
        var slots = await ReadServerSlotsAsync(ct).ConfigureAwait(false);
        var recordFileExists = File.Exists(RecordPath);
        if (slots.Count == 0 && !recordFileExists) return new ServerAutoUnlockEvaluation(ServerAutoUnlockState.Off, slots, null, false);

        var record = recordFileExists ? ReadRecord() : null;
        if (slots.Count == 0) return new ServerAutoUnlockEvaluation(ServerAutoUnlockState.StaleRecord, slots, record, recordFileExists);
        var state = record is not null && slots.Any(s => s.Matches(record)) ? ServerAutoUnlockState.Armed : ServerAutoUnlockState.StaleSlot;
        return new ServerAutoUnlockEvaluation(state, slots, record, recordFileExists);
    }

    private async Task<List<ServerSlotView>> ReadServerSlotsAsync(CancellationToken ct)
    {
        if (SealedAndClosed)
            return (await _sealedHost!.ListDoorsAsync(ct).ConfigureAwait(false))
                .Where(d => d.Kind == BootDoorKinds.Slot && d.SlotType == SlotType)
                .Select(d => new ServerSlotView(d.SourceId, d.ContentSha256, d.EncryptedMasterDek, d.Iv, d.DekFingerprint, d, d.DoorVersion, d.DoorPublicKey))
                .ToList();

        return (await _slots.GetAllAsync().ConfigureAwait(false))
            .Where(s => s.SlotType == SlotType)
            // The DEK the slot names, as on the closed path (null only for a v1 slot nobody has opened since the update).
            .Select(s => new ServerSlotView(s.SlotId, SlotHash(s), s.EncryptedMasterDek, s.IV, s.DekFingerprint, null, s.DoorVersion, s.DoorPublicKey))
            .ToList();
    }

    /// <summary>The content hash of a slot row as the boot store and the enable record name it (a v2 door includes its format and public key).</summary>
    private static string SlotHash(MasterKeyStore s) =>
        BootDoorHash.OfSlot(s.SlotId, s.SlotType, s.EncryptedMasterDek, s.IV, s.Salt, s.ArgonMemory, s.ArgonIterations, s.ArgonParallelism, s.DoorVersion,
            s.DoorPublicKey);

    private ServerEnableRecord? ReadRecord()
    {
        try
        {
            if (new FileInfo(RecordPath).LinkTarget is not null) return null;
            var record = JsonSerializer.Deserialize<ServerEnableRecord>(File.ReadAllText(RecordPath), Json);
            return record is { Version: 1, SlotSha256.Length: 64, DekFingerprint.Length: 64 } ? record : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    // --- start -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The step of the start (spec 5.2 step 6): evaluate, repair what is stale, and for an armed server try the unlock. Never throws for an
    /// ordinary failure; a failure after "armed" is a warning without a key and without a path, the node stays locked and the state stays armed.
    /// </summary>
    public async Task<ServerAutoUnlockStartOutcome> RunAtStartAsync(INodeIdentityRepository nodeRepo, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var evaluation = await EvaluateAsync(ct).ConfigureAwait(false);
            switch (evaluation.State)
            {
                case ServerAutoUnlockState.Off:
                    // A key file named in the environment without a slot is inert: the node says so and reads nothing (spec 11.2 item 4).
                    if (ConfiguredKeyFilePath is not null)
                        _logger?.LogInformation("{Variable} is set, but 'start unlocked after a restart' is off on this server: the key file is not read.",
                            KeyFileEnvironmentVariable);
                    return ServerAutoUnlockStartOutcome.NotApplicable;
                case ServerAutoUnlockState.StaleSlot:
                    await RepairStaleSlotAsync(evaluation, ct).ConfigureAwait(false);
                    return ServerAutoUnlockStartOutcome.Repaired;
                case ServerAutoUnlockState.StaleRecord:
                    RepairStaleRecord();
                    return ServerAutoUnlockStartOutcome.Repaired;
            }

            // Armed. Only a server unlocks by itself; a desktop never uses this slot (spec 11.0).
            if (Role != HostRole.Server) return ServerAutoUnlockStartOutcome.NotApplicable;
            if (_session.IsUnlocked) return ServerAutoUnlockStartOutcome.NotApplicable;
            return await TryUnlockAsync(evaluation, nodeRepo, ct).ConfigureAwait(false)
                ? ServerAutoUnlockStartOutcome.Unlocked
                : ServerAutoUnlockStartOutcome.StayedLocked;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<bool> TryUnlockAsync(ServerAutoUnlockEvaluation evaluation, INodeIdentityRepository nodeRepo, CancellationToken ct)
    {
        var record = evaluation.Record!;
        if (ConfiguredKeyFilePath is { } configured && ConfiguredKeyFileAliasesNodeKeyFile(configured))
        {
            _logger?.LogWarning("Start unlocked after a restart: the configured key file cannot be used; the vault stays locked.");
            return false;
        }
        var secret = _keyFiles.ReadSecret(KeyFilePath, out var problem);
        if (secret is null)
        {
            // The configured file is named by its variable, never by its path.
            _logger?.LogWarning("Start unlocked after a restart: the key file ({Source}) is not usable ({Problem}); the vault stays locked.",
                ConfiguredKeyFilePath is null ? "node-created" : KeyFileEnvironmentVariable, problem);
            return false;
        }

        try
        {
            // Every door the record names, the next one first: after a crash between a rotation's commit and the rewrite of the
            // boot store the old door and the re-sealed one both match, and only the unlock (sentinel, door check) knows which key was committed.
            // A crash before the commit leaves the same pair the other way round; the order only saves an attempt.
            string? refusal = null;
            foreach (var slot in evaluation.MatchingSlots)
            {
                var outcome = await TryUnlockThroughAsync(slot, record, secret, nodeRepo, ct).ConfigureAwait(false);
                if (outcome is null) return true;
                refusal ??= outcome;
            }
            _logger?.LogWarning("Start unlocked after a restart: {Refusal}; the vault stays locked.", refusal ?? "the enable record names no slot");
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>One door of <see cref="TryUnlockAsync"/>: null when it unlocked, otherwise the fixed sentence of the refusal (no key, no path).</summary>
    private async Task<string?> TryUnlockThroughAsync(ServerSlotView slot, ServerEnableRecord record, byte[] secret, INodeIdentityRepository nodeRepo,
        CancellationToken ct)
    {
        byte[]? masterDek = null;
        try
        {
            try
            {
                masterDek = slot.Open(secret);
            }
            catch (CryptographicException)
            {
                return "the secret in the key file does not open the slot";
            }

            if (!record.NamesDek(DekFingerprint.Of(masterDek)))
                return "the slot does not hold the key the enable record names";

            if (SealedAndClosed)
            {
                if (!await OpenSealedWithoutTheLockAsync(masterDek, slot.Door, ct).ConfigureAwait(false))
                    return "the sealed unlock refused";
                if (slot.Door is not null)
                    try { await _sealedHost!.SetDekFingerprintAsync(slot.Door.DoorId, DekFingerprint.Of(masterDek), ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { /* the next rewrite learns it from the door that was used */ }
                return null;
            }

            var sentinel = await nodeRepo.GetSentinelAsync().ConfigureAwait(false);
            if (!MasterKeyManager.IsSentinelMissing(sentinel) && !MasterKeyManager.VerifySentinel(sentinel!, masterDek))
                return "the key does not match this vault's sentinel";

            _session.UnlockWithDek(masterDek);   // ownership of the buffer moves to the session
            masterDek = null;
            return null;
        }
        finally
        {
            if (masterDek is not null) CryptographicOperations.ZeroMemory(masterDek);
        }
    }

    /// <summary>
    /// Opens the sealed database with <see cref="_lock"/> released, and takes the lock back before returning; called only from
    /// <see cref="RunAtStartAsync"/>, which holds it. Opening runs the open steps before it returns, and one of them is this service's own
    /// <see cref="StaleServerSlotOpenStep"/>, which takes the same lock (<see cref="RetireStaleSlotsInOpenDatabaseAsync"/>). A SemaphoreSlim is
    /// not re-entrant: in 2.10.1 the start waited for itself and the API of a sealed server with the checkbox on never started (BMB-374).
    /// The slot, the record and the key were read under the lock already; the open step reads them again under the lock and leaves the
    /// slot the record names alone.
    /// </summary>
    private async Task<bool> OpenSealedWithoutTheLockAsync(byte[] masterDek, BootDoor? door, CancellationToken ct)
    {
        _lock.Release();
        try
        {
            return await _sealedHost!.TryUnlockWithDekAsync(masterDek, door, ct).ConfigureAwait(false);
        }
        finally
        {
            await _lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    // --- stale states ----------------------------------------------------------------------------------------------------------

    private async Task RepairStaleSlotAsync(ServerAutoUnlockEvaluation evaluation, CancellationToken ct)
    {
        _notice = "Start unlocked after a restart was switched off because this data came from a backup or a copy; turn it on again if you want it.";
        if (SealedAndClosed)
        {
            // The door goes at once (nothing here needs the key); the row in the sealed database goes at the first open, by the open step.
            foreach (var slot in evaluation.Slots)
                if (_mirror is not null) await _mirror.RetireDoorAsync(BootDoorKinds.Slot, slot.SlotId, ct).ConfigureAwait(false);
            if (_boot is not null && _boot.Exists) await _boot.SetMetaAsync(BootMetaKeys.ServerAutoUnlock, "0", ct).ConfigureAwait(false);
        }
        else
        {
            foreach (var slot in evaluation.Slots)
                await SlotDoors.DeleteSlotAsync(_slots, _mirror, (int)slot.SlotId).ConfigureAwait(false);
            await SlotDoors.RewriteAsync(_mirror, _logger).ConfigureAwait(false);
            await AuditAsync("server_auto_unlock_retired_stale").ConfigureAwait(false);
            if (_boot is { Exists: true }) await _boot.SetMetaAsync(BootMetaKeys.ServerAutoUnlock, "0", ct).ConfigureAwait(false);
        }
        DeleteRecordFile();
        WipeNodeKeyFile();
        _logger?.LogWarning("Start unlocked after a restart was switched off: a server slot without a matching enable record came with restored or copied data.");
    }

    private void RepairStaleRecord()
    {
        DeleteRecordFile();
        WipeNodeKeyFile();
        _logger?.LogInformation("Start unlocked after a restart: an enable record without a slot was removed.");
    }

    /// <summary>
    /// The open step <c>StaleServerSlot</c> (order 50, every open): the sealed database is open now, so a server slot that no enable record
    /// matches is deleted and the audit entry is written. A slot the record names is left alone.
    /// </summary>
    public async Task RetireStaleSlotsInOpenDatabaseAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var slots = (await _slots.GetAllAsync().ConfigureAwait(false)).Where(s => s.SlotType == SlotType).ToList();
            if (slots.Count == 0) return;
            var record = File.Exists(RecordPath) ? ReadRecord() : null;
            var stale = slots.Where(s => record is null || !record.Names(SlotHash(s))).ToList();
            if (stale.Count == 0) return;

            foreach (var slot in stale)
                await SlotDoors.DeleteSlotAsync(_slots, _mirror, slot.SlotId).ConfigureAwait(false);
            await SlotDoors.RewriteAsync(_mirror, _logger).ConfigureAwait(false);
            await AuditAsync("server_auto_unlock_retired_stale").ConfigureAwait(false);
            _notice ??= "Start unlocked after a restart was switched off because this data came from a backup or a copy; turn it on again if you want it.";
            if (record is not null && slots.Count == stale.Count) DeleteRecordFile();
            if (slots.Count == stale.Count) WipeNodeKeyFile();
            if (_boot is { Exists: true }) await _boot.SetMetaAsync(BootMetaKeys.ServerAutoUnlock, "0", ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    // --- status ----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>Enabled</c> is true only when the slot, the key file and the enable record agree; for an <c>off</c> node nothing is read beyond the
    /// evaluation. The one-time notice is returned once.
    /// </summary>
    public async Task<ServerAutoUnlockStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var evaluation = await EvaluateAsync(ct).ConfigureAwait(false);
        var enabled = false;
        if (evaluation.State == ServerAutoUnlockState.Armed && Role == HostRole.Server)
        {
            var configured = ConfiguredKeyFilePath;
            var secret = configured is not null && ConfiguredKeyFileAliasesNodeKeyFile(configured)
                ? null
                : _keyFiles.ReadSecret(KeyFilePath, out _);
            if (secret is not null)
            {
                try
                {
                    // Any door the record names that the secret opens to a key the record names (its current or, mid-rotation, its next one).
                    foreach (var slot in evaluation.MatchingSlots)
                    {
                        byte[] dek;
                        try { dek = slot.Open(secret); }
                        catch (CryptographicException) { continue; }
                        enabled = evaluation.Record!.NamesDek(DekFingerprint.Of(dek));
                        CryptographicOperations.ZeroMemory(dek);
                        if (enabled) break;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(secret);
                }
            }
        }
        var notice = Interlocked.Exchange(ref _notice, null);
        return new ServerAutoUnlockStatus(Role, enabled, evaluation.State, ConfiguredKeyFilePath is not null, notice);
    }

    // --- enable and disable ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Turns the checkbox on (spec 11.2 "Turning it on"). The caller (the route) has checked the role, the password and the phrase. The writes
    /// happen in this order so that every prefix of them is inert: the key file (node-created case), the slot, its door (sealed node), the enable
    /// record, the boot meta mirror. The session must be unlocked.
    /// </summary>
    public async Task EnableAsync(CancellationToken ct = default)
    {
        if (Role != HostRole.Server) throw new ServerAutoUnlockException("role_desktop", "On a desktop use the OS auto-unlock card.");
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        byte[]? masterDek = null;
        byte[]? secret = null;
        var createdKeyFile = false;
        var recordWritten = false;
        try
        {
            masterDek = _session.GetMasterDek();   // throws SessionLockedException when locked

            var configured = ConfiguredKeyFilePath;
            if (configured is not null)
            {
                if (ConfiguredKeyFileAliasesNodeKeyFile(configured))
                    throw new ServerAutoUnlockException("key_file_invalid", "The configured key file must not be the node-owned key file.");
                // The operator's file: read, never written, moved or deleted. It must exist, be owner-only and hold 64 hex characters.
                secret = _keyFiles.ReadSecret(configured, out var problem)
                    ?? throw new ServerAutoUnlockException("key_file_unusable",
                        $"The key file named by {KeyFileEnvironmentVariable} cannot be used ({problem}). It must exist, be readable only by the user the node runs as, and hold 64 hex characters.");
            }
            else
            {
                // A leftover node-created file (a half-done enable) is not trusted: it is replaced.
                _keyFiles.WipeAndDelete(NodeKeyFilePath);
                secret = RandomNumberGenerator.GetBytes(32);
                _keyFiles.WriteNew(NodeKeyFilePath, secret);
                createdKeyFile = true;
            }

            // One slot at most: any earlier one goes (its door with it).
            foreach (var existing in (await _slots.GetAllAsync().ConfigureAwait(false)).Where(s => s.SlotType == SlotType))
                await SlotDoors.DeleteSlotAsync(_slots, _mirror, existing.SlotId).ConfigureAwait(false);
            EnsureUniqueIndex();

            // A sealed (v2) door (ADR 0012): the secret is the KEK, there is no salt.
            var door = Doors.Seal(SlotType, secret, null, masterDek);
            var slot = new MasterKeyStore
            {
                SlotType = SlotType,
                EncryptedMasterDek = door.Box,
                IV = door.Iv,
                // No salt and no Argon2 columns: the password path never tries this slot, the secret is used directly as the KEK.
                Salt = null,
                ArgonMemory = null,
                ArgonIterations = null,
                ArgonParallelism = null,
                CreatedAt = DateTime.UtcNow,
                DoorVersion = Doors.V2,
                DoorPublicKey = door.PublicKey,
                DekFingerprint = door.DekFingerprint,
            };
            await SlotDoors.CreateSlotAsync(_slots, _factory, _mirror, slot, masterDek).ConfigureAwait(false);
            await SlotDoors.RewriteAsync(_mirror, _logger).ConfigureAwait(false);

            // The record is written last: until it exists the slot is stale and nothing unlocks.
            var record = new ServerEnableRecord(1, SlotHash(slot), DekFingerprint.Of(masterDek), DateTime.UtcNow.ToString("o"));
            WriteRecord(record);
            recordWritten = true;

            if (_boot is { Exists: true }) await _boot.SetMetaAsync(BootMetaKeys.ServerAutoUnlock, "1", ct).ConfigureAwait(false);
            await AuditAsync("server_auto_unlock_enabled").ConfigureAwait(false);
        }
        catch
        {
            // Every prefix is inert; tidy the key file this call made so that a retry starts clean. Once the record exists the choice is made
            // and the key file stays with it.
            if (createdKeyFile && !recordWritten) WipeNodeKeyFile();
            throw;
        }
        finally
        {
            if (masterDek is not null) CryptographicOperations.ZeroMemory(masterDek);
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
            _lock.Release();
        }
    }

    /// <summary>
    /// Turns the checkbox off (spec 11.2 "Turning it off"): the enable record first (every prefix is inert), then the door, the slot and the
    /// node-created key file. Never an operator's key file. True when there was something to turn off.
    /// </summary>
    public async Task<bool> DisableAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var didAnything = File.Exists(RecordPath);
            DeleteRecordFile();
            foreach (var slot in (await _slots.GetAllAsync().ConfigureAwait(false)).Where(s => s.SlotType == SlotType))
            {
                await SlotDoors.DeleteSlotAsync(_slots, _mirror, slot.SlotId).ConfigureAwait(false);
                didAnything = true;
            }
            await SlotDoors.RewriteAsync(_mirror, _logger).ConfigureAwait(false);
            if (File.Exists(NodeKeyFilePath)) { WipeNodeKeyFile(); didAnything = true; }
            if (_boot is { Exists: true }) await _boot.SetMetaAsync(BootMetaKeys.ServerAutoUnlock, "0", ct).ConfigureAwait(false);
            if (didAnything) await AuditAsync("server_auto_unlock_disabled").ConfigureAwait(false);
            return didAnything;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The local half of turning it off, for the DEK rotation (the commit deleted the slot) and the node reset: the enable record first, then a
    /// node-created key file. Never an operator's file. A failure is the caller's to log: the stale rule of 11.2 removes what is left at the next start.
    /// </summary>
    public Task DisableLocalAsync(CancellationToken ct = default)
    {
        DeleteRecordFile();
        WipeNodeKeyFile();
        return Task.CompletedTask;
    }

    // --- the quiet rotation (2.12) ---------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task PrepareRotationAsync(ServerRotationDoor change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Only the record file: the rotation transaction is open, and the slot is read from it by the caller.
            if (!File.Exists(RecordPath) || ReadRecord() is not { } record || !record.Names(change.OldSlotSha256)) return;
            await ReplaceRecordAsync(record with
            {
                SlotSha256 = change.OldSlotSha256, NextSlotSha256 = change.NewSlotSha256, NextDekFingerprint = change.NewDekFingerprint,
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task OnDekRotatedAsync(bool committed, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(RecordPath) || ReadRecord() is not { NextSlotSha256: not null } record) return;
            await ReplaceRecordAsync(record.Settled(toNext: committed), ct).ConfigureAwait(false);
            if (committed)
                _logger?.LogInformation("Start unlocked after a restart stays on: the server door was sealed to the new master key by the rotation.");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// A record that still names two doors (a crash between a rotation's commit and its settle, or during the upgrade below) is settled on the
    /// door the slot holds now. Called under the lock with the node open.
    /// </summary>
    private async Task SettleRecordAsync(ServerAutoUnlockEvaluation evaluation, CancellationToken ct)
    {
        if (evaluation.Record is not { NextSlotSha256: not null } record) return;
        var row = (await _slots.GetAllAsync().ConfigureAwait(false)).FirstOrDefault(s => s.SlotType == SlotType && record.Names(SlotHash(s)));
        if (row is null) return;
        var toNext = string.Equals(SlotHash(row), record.NextSlotSha256, StringComparison.Ordinal);
        await ReplaceRecordAsync(record.Settled(toNext), ct).ConfigureAwait(false);
    }

    // --- the upgrade of ADR 0012 -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Seals an armed server's v1 slot again as a v2 door, as soon as the node is open and the key file is readable (ADR 0012), and rewrites the
    /// enable record in the same step so the slot is never "stale": first the record names the door before AND the door after, then the door is
    /// replaced in place (pending boot door first, the row, the rewrite), then the record names only the new door. A crash at any point leaves a
    /// record that names the slot there is. Only when the slot yields the session's DEK and the record names that DEK. Best effort: logged, tried
    /// again at the next unlock.
    /// </summary>
    public async Task UpgradeAsync(CancellationToken ct = default)
    {
        if (Role != HostRole.Server || !_session.IsUnlocked || SealedAndClosed) return;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        byte[]? secret = null;
        byte[]? dek = null;
        byte[]? current = null;
        try
        {
            var evaluation = await EvaluateAsync(ct).ConfigureAwait(false);
            if (evaluation.State != ServerAutoUnlockState.Armed) return;
            if (evaluation.Record!.NextSlotSha256 is not null)
            {
                await SettleRecordAsync(evaluation, ct).ConfigureAwait(false);
                evaluation = await EvaluateAsync(ct).ConfigureAwait(false);
                if (evaluation.State != ServerAutoUnlockState.Armed) return;
            }
            var row = (await _slots.GetAllAsync().ConfigureAwait(false))
                .FirstOrDefault(s => s.SlotType == SlotType && s.SlotId == evaluation.MatchingSlot!.SlotId);
            if (row is null || !Doors.NeedsUpgrade(row)) return;
            var configured = ConfiguredKeyFilePath;
            if (configured is not null && ConfiguredKeyFileAliasesNodeKeyFile(configured)) return;
            secret = _keyFiles.ReadSecret(KeyFilePath, out _);
            if (secret is null) return;
            try { dek = Doors.OpenSlot(row, secret); }
            catch (CryptographicException) { return; }
            current = _session.GetMasterDek();
            var record = evaluation.Record!;
            if (!CryptographicOperations.FixedTimeEquals(dek, current)
                || !string.Equals(DekFingerprint.Of(current), record.DekFingerprint, StringComparison.Ordinal))
                return;   // the slot holds another key than the session's: it stays as it is (a rotation deals with it)

            var replacement = Doors.WithDoor(row, Doors.Seal(SlotType, secret, null, current));
            var oldHash = SlotHash(row);
            var newHash = SlotHash(replacement);
            await ReplaceRecordAsync(record with { SlotSha256 = oldHash, NextSlotSha256 = newHash }, ct).ConfigureAwait(false);
            if (!await SlotDoors.ReplaceSlotDoorAsync(_slots, _mirror, replacement, row.EncryptedMasterDek, _logger).ConfigureAwait(false))
            {
                await ReplaceRecordAsync(record with { SlotSha256 = oldHash, NextSlotSha256 = null }, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            await ReplaceRecordAsync(record with { SlotSha256 = newHash, NextSlotSha256 = null }, CancellationToken.None).ConfigureAwait(false);
            _logger?.LogInformation("Start unlocked after a restart: the server slot was sealed again in the door format of 2.12.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning("Start unlocked after a restart: the server slot could not be upgraded ({Type}); it is tried again at the next unlock.",
                ex.GetType().Name);
        }
        finally
        {
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
            if (dek is not null) CryptographicOperations.ZeroMemory(dek);
            if (current is not null) CryptographicOperations.ZeroMemory(current);
            _lock.Release();
        }
    }

    // --- files and helpers -----------------------------------------------------------------------------------------------------

    private void WriteRecord(ServerEnableRecord record)
    {
        DeleteRecordFile();
        OwnerOnlyFile.WriteNew(RecordPath, JsonSerializer.SerializeToUtf8Bytes(record, Json));
    }

    /// <summary>Replaces an existing record in one atomic rename (never deleted first: between a delete and a write a crash would leave the slot "stale").</summary>
    private Task ReplaceRecordAsync(ServerEnableRecord record, CancellationToken ct) =>
        OwnerOnlyFile.ReplaceAsync(RecordPath, JsonSerializer.SerializeToUtf8Bytes(record, Json), ct);

    private void DeleteRecordFile()
    {
        try
        {
            if (File.Exists(RecordPath)) File.Delete(RecordPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning("The enable record of the server checkbox could not be removed; it is removed at the next start.");
            throw;
        }
    }

    /// <summary>Removes the key file the node created (never the operator's). A leftover that cannot be removed is a warning: it is inert without a slot and a record.</summary>
    private void WipeNodeKeyFile()
    {
        if (ConfiguredKeyFilePath is { } configured && ConfiguredKeyFileAliasesNodeKeyFile(configured))
        {
            _logger?.LogWarning("The node-created key file was not removed because it may be the configured operator key file.");
            return;
        }
        try
        {
            _keyFiles.WipeAndDelete(NodeKeyFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning("The key file the node created could not be removed; it is inert without a slot and an enable record.");
        }
    }

    private bool IsNodeKeyFilePath(string path)
    {
        try
        {
            return string.Equals(Path.GetFullPath(path), Path.GetFullPath(NodeKeyFilePath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    /// <summary>
    /// The configured operator key must not be the node-owned key under another name. Path comparison catches ordinary aliases;
    /// file identity catches hard links. If both names exist but either identity cannot be read, fail closed: cleanup must not risk
    /// overwriting an operator's secret.
    /// </summary>
    private bool ConfiguredKeyFileAliasesNodeKeyFile(string path)
    {
        if (IsNodeKeyFilePath(path)) return true;
        if (!File.Exists(NodeKeyFilePath)) return false;

        var configured = FileIdentity.TryGet(path);
        var nodeKey = FileIdentity.TryGet(NodeKeyFilePath);
        return configured is null || nodeKey is null || configured.Value.IsSameFileAs(nodeKey.Value);
    }

    /// <summary>At most one server slot, enforced by an index created in code (as the OS slot's, <c>KeySlotRepository.EnsureOsAutoUnlockUniqueIndexAsync</c>).</summary>
    private void EnsureUniqueIndex()
    {
        if (_factory is null) return;
        using var connection = _factory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS ux_key_slot_server_auto_unlock ON tbl_key_slot(slot_type) WHERE slot_type = 'server_auto_unlock'";
        command.ExecuteNonQuery();
    }

    private async Task AuditAsync(string action)
    {
        if (_scopes is null) return;
        try
        {
            using var scope = _scopes.CreateScope();
            var audit = scope.ServiceProvider.GetService<IAuditLogRepository>();
            if (audit is not null) await audit.LogAsync("key_slot", SlotType, action, "system", "server auto-unlock").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning("The audit entry {Action} could not be written.", action);
        }
    }
}
