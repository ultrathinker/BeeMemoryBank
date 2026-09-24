using System.Security.Cryptography;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Dapper;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Owns the node's chat data key: the one AES-256 key everything in chat.db is encrypted under
/// (message content, tool-call arguments, attachment blobs, LLM provider API keys).
///
/// <para><b>Why a separate key.</b> chat.db is its own SQLite file. Its rows used to be sealed
/// directly under the master DEK, and a DEK rotation — one transaction in the MAIN database — could
/// not re-encrypt them, so every rotation left all chat history, attachments and stored provider
/// keys undecryptable. The chat key is stored wrapped under the master DEK in the main database
/// (the <c>'chat'</c> row of <c>tbl_node_data_key</c>, migration 026), and <c>DekRewrapper</c>
/// re-wraps that row inside the rotation transaction on the initiator and on every applying peer.
/// The chat key itself never
/// changes, so chat.db never needs touching during a rotation, and it opens after a restart with
/// nothing but the post-rotation master DEK.</para>
///
/// <para><b>Row markers.</b> Every chat.db ciphertext column has a sibling key-version column:
/// <see cref="ChatKeyVersion"/> (1) = sealed under the chat key; NULL = legacy (sealed under the
/// master DEK, or still plaintext from before chat encryption existed); <see cref="LegacyUnreadable"/>
/// (-1) = a legacy ciphertext that opened under no available master DEK when the background
/// migration reached it, left as-is and excluded from further migration scans. Legacy rows are moved
/// onto the chat key by <see cref="ChatHistoryBackfillProcessor"/>, and forcibly before every DEK
/// rotation by <see cref="ChatDekRotationHook"/>; until then readers open them with the current or a
/// retired master DEK (<see cref="SessionService.TryUnwrapWithCandidates{T}"/>).</para>
///
/// <para><b>Caching.</b> Once opened, the plaintext chat key is cached in memory while the vault is
/// unlocked and wiped on <see cref="SessionService.Locked"/> — which every path that replaces the
/// main database (snapshot restore, network restore, node reset) raises. A rotation does not change
/// the chat key, so the cache survives <see cref="SessionService.SwapMasterDek"/> by design.</para>
///
/// <para><b>Restored or foreign main database.</b> A restored snapshot carries the chat key row as
/// it was when the snapshot was taken. A snapshot from before the key existed has no row, and a new
/// key is created on next use; chat rows sealed under the previous key then read as placeholders.
/// Such a mismatch always degrades to placeholders, never to an exception.</para>
/// </summary>
public sealed class ChatDataProtector : IDisposable
{
    /// <summary>Key-version marker: this column is sealed under the node chat key.</summary>
    public const int ChatKeyVersion = 1;

    /// <summary>Key-version marker: legacy master-DEK ciphertext that no available key opened.</summary>
    public const int LegacyUnreadable = -1;

    private readonly IDbConnectionFactory _mainDb;
    private readonly SessionService _session;
    private readonly ILogger<ChatDataProtector> _logger;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly object _cacheLock = new();
    private byte[]? _cachedKey;
    // Bumped on every wipe, so a load that was already in flight when the vault locked cannot put
    // the key it produced back into the cache afterwards.
    private long _generation;

    public ChatDataProtector(IDbConnectionFactory mainDb, SessionService session, ILogger<ChatDataProtector> logger)
    {
        _mainDb = mainDb;
        _session = session;
        _logger = logger;
        _session.Locked += WipeCache;
    }

    /// <summary>
    /// Returns the chat key (a private copy the caller must dispose), creating it on first use.
    /// Requires an unlocked vault — throws <see cref="SessionLockedException"/> otherwise. Throws
    /// <see cref="CryptographicException"/> only in the brief window of a DEK rotation where the
    /// stored row has already moved to a master DEK this process has not swapped to yet.
    /// </summary>
    public async Task<ChatKeyLease> AcquireAsync(CancellationToken ct = default)
    {
        if (TryCloneCached(out var cachedGeneration) is { } cached) return IssueLease(cached, cachedGeneration);

        await _loadGate.WaitAsync(ct);
        try
        {
            if (TryCloneCached(out var racedGeneration) is { } raced) return IssueLease(raced, racedGeneration);

            long generation;
            lock (_cacheLock) generation = _generation;

            var key = LoadOrCreate();
            lock (_cacheLock)
            {
                if (generation == _generation && _session.IsUnlocked)
                {
                    if (_cachedKey != null) Array.Clear(_cachedKey);
                    _cachedKey = (byte[])key.Clone();
                }
            }
            return IssueLease(key, generation);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>
    /// The last gate before a key copy leaves this class. SessionService.Lock() clears the master
    /// DEK first and raises <see cref="SessionService.Locked"/> (which wipes the cache) only
    /// afterwards, so a copy taken from the cache in between would otherwise be handed out while the
    /// vault is already locked. Checking <see cref="SessionService.IsUnlocked"/> AFTER the copy is
    /// held closes that: if locking had begun by the time of the check, the copy is wiped and the
    /// caller gets <see cref="SessionLockedException"/>; if it had not, the lease predates the lock,
    /// exactly like any in-flight operation holding a master DEK clone. The generation check covers
    /// a full lock → unlock in between (a restore swapping the database, say): the Locked handler
    /// bumped the generation, so a key copied or loaded before it is refused too.
    /// </summary>
    private ChatKeyLease IssueLease(byte[] key, long generation)
    {
        bool current;
        lock (_cacheLock)
            current = generation == _generation && _session.IsUnlocked;
        if (current)
            return new ChatKeyLease(key);

        Array.Clear(key);
        WipeCache();
        throw new SessionLockedException("The vault was locked while the chat key was being obtained. Unlock and retry.");
    }

    /// <summary>
    /// Decrypts one text column. <paramref name="keyVersion"/> selects the key: the chat key for
    /// <see cref="ChatKeyVersion"/>, otherwise the current-then-retired master DEKs (legacy row).
    /// Returns null when nothing opens it, so callers can substitute a placeholder.
    /// <paramref name="lease"/> may be null when the batch had no chat-key rows or the key could not
    /// be obtained; a chat-key row then simply fails to open.
    /// </summary>
    public string? TryDecryptText(ChatKeyLease? lease, byte[] ciphertext, byte[] iv, int? keyVersion, byte[] aad)
    {
        try
        {
            if (keyVersion == ChatKeyVersion)
                return lease?.DecryptText(ciphertext, iv, aad);
            return _session.TryUnwrapWithCandidates(dek => ArticleEncryptor.Decrypt(ciphertext, iv, dek, aad));
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Byte-payload counterpart of <see cref="TryDecryptText"/>.</summary>
    public byte[]? TryDecryptBytes(ChatKeyLease? lease, byte[] ciphertext, byte[] iv, int? keyVersion, byte[] aad)
    {
        try
        {
            if (keyVersion == ChatKeyVersion)
                return lease?.DecryptBytes(ciphertext, iv, aad);
            return _session.TryUnwrapWithCandidates(dek => MediaEncryptor.Decrypt(ciphertext, iv, dek, aad));
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// <see cref="AcquireAsync"/> for readers: a key that cannot be obtained right now yields null
    /// (chat-key rows then read as placeholders) instead of failing the whole transcript. A locked
    /// vault still throws — reading encrypted chat content while locked is a caller bug.
    /// </summary>
    public async Task<ChatKeyLease?> TryAcquireForReadAsync(CancellationToken ct = default)
    {
        try
        {
            return await AcquireAsync(ct);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Chat data key is not available right now; affected chat rows read as placeholders");
            return null;
        }
    }

    private byte[]? TryCloneCached(out long generation)
    {
        lock (_cacheLock)
        {
            generation = _generation;
            return _cachedKey is null ? null : (byte[])_cachedKey.Clone();
        }
    }

    private void WipeCache()
    {
        lock (_cacheLock)
        {
            if (_cachedKey != null) Array.Clear(_cachedKey);
            _cachedKey = null;
            _generation++;
        }
    }

    private byte[] LoadOrCreate()
    {
        var candidates = _session.GetCandidateDeks();
        if (candidates.Length == 0)
            throw new SessionLockedException("Session is locked. Call UnlockAsync first.");

        try
        {
            var current = candidates[0];
            using var conn = _mainDb.CreateConnection();

            // Fast path, no write lock: the row exists and opens under the current master DEK.
            var row = conn.QuerySingleOrDefault<KeyRow>(SelectSql);
            if (row != null && ChatDataKeyEnvelope.TryUnwrap(row.Wrapped, row.Iv, current) is { } opened)
                return opened;

            // Everything else writes, and must be decided against a stable view of the row AND the
            // sentinel. A non-deferred transaction is BEGIN IMMEDIATE, so this serializes with
            // DekRewrapper's rotation transaction: either the rotation has not started (whatever is
            // written here is re-wrapped by it), or it has committed (the sentinel already names
            // the new DEK and the check below sees that). Requested explicitly rather than relied
            // on as the provider default.
            using var tx = conn is Microsoft.Data.Sqlite.SqliteConnection sqlite
                ? sqlite.BeginTransaction(deferred: false)
                : conn.BeginTransaction();
            row = conn.QuerySingleOrDefault<KeyRow>(SelectSql, transaction: tx);
            var sentinel = conn.ExecuteScalar<byte[]?>(
                "SELECT sentinel_value FROM tbl_node_identity LIMIT 1", transaction: tx);

            // Is the session's current DEK the one this database is keyed under right now? False in
            // exactly one situation: a rotation committed and this process has not swapped its
            // in-memory DEK yet (the microseconds between DekRewrapper's commit and SwapMasterDek).
            // Nothing may be created or replaced then — it would be sealed under a DEK that is
            // already retired.
            var currentIsAuthoritative = sentinel is not { Length: > 0 }
                || MasterKeyManager.VerifySentinel(sentinel, current);

            if (row != null)
            {
                for (var i = 0; i < candidates.Length; i++)
                {
                    if (ChatDataKeyEnvelope.TryUnwrap(row.Wrapped, row.Iv, candidates[i]) is not { } key)
                        continue;

                    // Opened under a retired DEK: a key created in a rotation's window. Re-seal it
                    // under the current DEK now, while the retired one is still in memory — after a
                    // restart it would be gone.
                    if (i > 0 && currentIsAuthoritative)
                    {
                        var (wrapped, iv) = ChatDataKeyEnvelope.Wrap(key, current);
                        conn.Execute($"UPDATE {NodeDataKeyEnvelope.TableName} SET wrapped_key = @wrapped, iv = @iv WHERE key_name = @name",
                            new { wrapped, iv, name = ChatDataKeyEnvelope.KeyName }, tx);
                        _logger.LogInformation("Chat data key was sealed under a retired master key; re-sealed under the current one");
                    }
                    tx.Commit();
                    return key;
                }

                if (!currentIsAuthoritative)
                    throw new CryptographicException("The chat data key is between master keys (DEK rotation in progress); retry shortly.");

                // The database's own current DEK cannot open its own chat key row. Nothing ever
                // will: the key it held is unrecoverable. Replace it so chat keeps working; whatever
                // was sealed under the old key reads as placeholders from now on.
                _logger.LogError(
                    "The chat data key does not open under the current master key (damaged row or a "
                    + "rotation that could not carry it forward). Replacing it; chat history, attachments "
                    + "and LLM provider keys sealed under the previous chat key are no longer readable.");
                var replacement = ChatDataKeyEnvelope.Generate();
                var (repWrapped, repIv) = ChatDataKeyEnvelope.Wrap(replacement, current);
                conn.Execute(
                    $"UPDATE {NodeDataKeyEnvelope.TableName} SET wrapped_key = @wrapped, iv = @iv, created_at = @now WHERE key_name = @name",
                    new { wrapped = repWrapped, iv = repIv, now = DateTime.UtcNow.ToString("O"), name = ChatDataKeyEnvelope.KeyName }, tx);
                tx.Commit();
                return replacement;
            }

            if (!currentIsAuthoritative)
                throw new CryptographicException("The master key is changing (DEK rotation in progress); retry shortly.");

            var created = ChatDataKeyEnvelope.Generate();
            var (newWrapped, newIv) = ChatDataKeyEnvelope.Wrap(created, current);
            conn.Execute(
                $"INSERT INTO {NodeDataKeyEnvelope.TableName} (key_name, wrapped_key, iv, created_at) VALUES (@name, @wrapped, @iv, @now)",
                new { wrapped = newWrapped, iv = newIv, now = DateTime.UtcNow.ToString("O"), name = ChatDataKeyEnvelope.KeyName }, tx);
            tx.Commit();
            return created;
        }
        finally
        {
            foreach (var c in candidates) Array.Clear(c);
        }
    }

    private static readonly string SelectSql =
        $"SELECT wrapped_key AS Wrapped, iv AS Iv FROM {NodeDataKeyEnvelope.TableName} WHERE key_name = '{ChatDataKeyEnvelope.KeyName}'";

    private sealed class KeyRow
    {
        public byte[]? Wrapped { get; set; }
        public byte[]? Iv { get; set; }
    }

    public void Dispose()
    {
        _session.Locked -= WipeCache;
        WipeCache();
    }
}

/// <summary>
/// A private copy of the chat data key for the duration of one repository operation. Dispose wipes
/// it; nothing outside the lease ever sees the key bytes.
/// </summary>
public sealed class ChatKeyLease : IDisposable
{
    private readonly byte[] _key;

    internal ChatKeyLease(byte[] key) => _key = key;

    public (byte[] ciphertext, byte[] iv) EncryptText(string plaintext, byte[] aad)
        => ArticleEncryptor.Encrypt(plaintext, _key, aad);

    public (byte[] ciphertext, byte[] iv) EncryptBytes(byte[] plaintext, byte[] aad)
        => MediaEncryptor.Encrypt(plaintext, _key, aad);

    public string DecryptText(byte[] ciphertext, byte[] iv, byte[] aad)
        => ArticleEncryptor.Decrypt(ciphertext, iv, _key, aad);

    public byte[] DecryptBytes(byte[] ciphertext, byte[] iv, byte[] aad)
        => MediaEncryptor.Decrypt(ciphertext, iv, _key, aad);

    public void Dispose() => Array.Clear(_key);
}
