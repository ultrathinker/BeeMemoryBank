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
    public ServerSlotView? MatchingSlot => Record is null ? null : Slots.FirstOrDefault(s => s.Matches(Record));
}

public sealed record ServerSlotView(long SlotId, string ContentSha256, byte[] EncryptedMasterDek, byte[] Iv, string? DekFingerprint, BootDoor? Door)
{
    public bool Matches(ServerEnableRecord record) =>
        string.Equals(ContentSha256, record.SlotSha256, StringComparison.Ordinal)
        && (DekFingerprint is null || string.Equals(DekFingerprint, record.DekFingerprint, StringComparison.Ordinal));
}

/// <summary>The local enable record <c>&lt;data&gt;/server-auto-unlock.enabled</c> (spec 11.2 part 3): says "the owner enabled exactly this slot on this server"; holds no secret.</summary>
public sealed record ServerEnableRecord(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("slot_sha256")] string SlotSha256,
    [property: JsonPropertyName("dek_fp")] string DekFingerprint,
    [property: JsonPropertyName("enabled_at")] string EnabledAt);

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
public sealed class ServerAutoUnlockService : IServerAutoUnlockLocalState
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
                .Select(d => new ServerSlotView(d.SourceId, d.ContentSha256, d.EncryptedMasterDek, d.Iv, d.DekFingerprint, d))
                .ToList();

        return (await _slots.GetAllAsync().ConfigureAwait(false))
            .Where(s => s.SlotType == SlotType)
            .Select(s => new ServerSlotView(s.SlotId, BootDoorHash.OfSlot(s.SlotId, s.SlotType, s.EncryptedMasterDek, s.IV, s.Salt, s.ArgonMemory,
                s.ArgonIterations, s.ArgonParallelism), s.EncryptedMasterDek, s.IV, null, null))
            .ToList();
    }

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
        var slot = evaluation.MatchingSlot!;
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

        byte[]? masterDek = null;
        try
        {
            try
            {
                masterDek = MasterKeyManager.UnwrapMasterDek(slot.EncryptedMasterDek, slot.Iv, secret);
            }
            catch (CryptographicException)
            {
                _logger?.LogWarning("Start unlocked after a restart: the secret in the key file does not open the slot; the vault stays locked.");
                return false;
            }

            if (!string.Equals(DekFingerprint.Of(masterDek), record.DekFingerprint, StringComparison.Ordinal))
            {
                _logger?.LogWarning("Start unlocked after a restart: the slot does not hold the key the enable record names; the vault stays locked.");
                return false;
            }

            if (SealedAndClosed)
            {
                if (!await _sealedHost!.TryUnlockWithDekAsync(masterDek, slot.Door, ct).ConfigureAwait(false))
                {
                    _logger?.LogWarning("Start unlocked after a restart: the sealed unlock refused; the vault stays locked.");
                    return false;
                }
                if (slot.Door is not null)
                    try { await _sealedHost.SetDekFingerprintAsync(slot.Door.DoorId, record.DekFingerprint, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { /* the next rewrite learns it from the door that was used */ }
                return true;
            }

            var sentinel = await nodeRepo.GetSentinelAsync().ConfigureAwait(false);
            if (!MasterKeyManager.IsSentinelMissing(sentinel) && !MasterKeyManager.VerifySentinel(sentinel!, masterDek))
            {
                _logger?.LogWarning("Start unlocked after a restart: the key does not match this vault's sentinel; the vault stays locked.");
                return false;
            }

            _session.UnlockWithDek(masterDek);   // ownership of the buffer moves to the session
            masterDek = null;
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            if (masterDek is not null) CryptographicOperations.ZeroMemory(masterDek);
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
            var stale = slots.Where(s => record is null
                || !string.Equals(BootDoorHash.OfSlot(s.SlotId, s.SlotType, s.EncryptedMasterDek, s.IV, s.Salt, s.ArgonMemory, s.ArgonIterations, s.ArgonParallelism),
                    record.SlotSha256, StringComparison.Ordinal)).ToList();
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
                    var slot = evaluation.MatchingSlot!;
                    var dek = MasterKeyManager.UnwrapMasterDek(slot.EncryptedMasterDek, slot.Iv, secret);
                    enabled = string.Equals(DekFingerprint.Of(dek), evaluation.Record!.DekFingerprint, StringComparison.Ordinal);
                    CryptographicOperations.ZeroMemory(dek);
                }
                catch (CryptographicException)
                {
                    enabled = false;
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

            var (encryptedDek, iv) = MasterKeyManager.WrapMasterDek(masterDek, secret);
            var slot = new MasterKeyStore
            {
                SlotType = SlotType,
                EncryptedMasterDek = encryptedDek,
                IV = iv,
                // No salt and no Argon2 columns: the password path never tries this slot, the secret is used directly as the KEK.
                Salt = null,
                ArgonMemory = null,
                ArgonIterations = null,
                ArgonParallelism = null,
                CreatedAt = DateTime.UtcNow,
            };
            await SlotDoors.CreateSlotAsync(_slots, _factory, _mirror, slot, masterDek).ConfigureAwait(false);
            await SlotDoors.RewriteAsync(_mirror, _logger).ConfigureAwait(false);

            // The record is written last: until it exists the slot is stale and nothing unlocks.
            var record = new ServerEnableRecord(1,
                BootDoorHash.OfSlot(slot.SlotId, slot.SlotType, slot.EncryptedMasterDek, slot.IV, slot.Salt, slot.ArgonMemory, slot.ArgonIterations, slot.ArgonParallelism),
                DekFingerprint.Of(masterDek), DateTime.UtcNow.ToString("o"));
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

    // --- files and helpers -----------------------------------------------------------------------------------------------------

    private void WriteRecord(ServerEnableRecord record)
    {
        DeleteRecordFile();
        OwnerOnlyFile.WriteNew(RecordPath, JsonSerializer.SerializeToUtf8Bytes(record, Json));
    }

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
