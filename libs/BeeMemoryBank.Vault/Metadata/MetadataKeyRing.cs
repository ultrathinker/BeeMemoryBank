using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using Dapper;

namespace BeeMemoryBank.Vault.Metadata;

/// <summary>
/// The open MEKs of an unlocked full node (docs/design/step-a-spec.md 4.5, 4.8), in memory only. A key is opened when it is
/// first needed: its row is read, the session's candidate DEKs are tried by fingerprint (the current DEK, the retired ones in
/// memory and those loaded at unlock), and the MEK is kept until the vault locks. Nothing opened here is ever written down;
/// a locked node holds none by construction.
///
/// <para>Rows are immutable (spec 4.1), so an opened MEK never goes stale. A DEK rotation adds a row and keeps the old DEK
/// among the candidates; <see cref="Current"/> reads the rows each time and therefore sees the new generation at once.</para>
/// </summary>
public sealed class MetadataKeyRing : IMetadataKeyRing
{
    private readonly IDbConnectionFactory _connFactory;
    private readonly SessionService _session;
    private readonly object _lock = new();
    private readonly Dictionary<string, byte[]> _open = new(StringComparer.Ordinal);

    public MetadataKeyRing(IDbConnectionFactory connFactory, SessionService session)
    {
        _connFactory = connFactory;
        _session = session;
        _session.Locked += Clear;
    }

    public MetadataKeyInfo? Current
    {
        get
        {
            if (!_session.IsUnlocked) return null;
            // Highest generation this node can open now, ties broken by the ordinally greatest key id: every node holding
            // the same rows and DEKs makes the same choice.
            foreach (var row in ReadRows())
                if (TryGetOpen(row) is not null) return new MetadataKeyInfo(row.KeyId, row.Generation);
            return null;
        }
    }

    public bool CanOpen(string keyId) => _session.IsUnlocked && ReadRow(keyId) is { } row && TryGetOpen(row) is not null;

    public byte[] DeriveKey(string keyId, string info, ReadOnlySpan<byte> salt)
    {
        if (!_session.IsUnlocked || ReadRow(keyId) is not { } row) throw new MetadataKeyUnavailableException(keyId);
        lock (_lock)
        {
            var mek = TryGetOpen(row) ?? throw new MetadataKeyUnavailableException(keyId);
            return MetadataKeyCrypto.Derive(mek, info, salt);
        }
    }

    /// <summary>Every open MEK zeroed and forgotten: the vault locked.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var mek in _open.Values) CryptographicOperations.ZeroMemory(mek);
            _open.Clear();
        }
    }

    private byte[]? TryGetOpen(MetadataKeyRow row)
    {
        lock (_lock)
        {
            if (_open.TryGetValue(row.KeyId, out var cached)) return cached;
            var candidates = _session.GetCandidateDeks();
            try
            {
                // Locked between the check and here: no candidates, nothing opened.
                if (MetadataKeyCrypto.TryUnwrap(row, candidates) is not { } mek) return null;
                _open[row.KeyId] = mek;
                return mek;
            }
            finally
            {
                foreach (var dek in candidates) CryptographicOperations.ZeroMemory(dek);
            }
        }
    }

    private List<MetadataKeyRow> ReadRows()
    {
        using var conn = _connFactory.CreateConnection();
        // key_id is lowercase hex, so SQLite's binary order is the ordinal order spec 4.8 names.
        return conn.Query<KeyRowDto>(
                "SELECT key_id AS KeyId, generation AS Generation, dek_fingerprint AS Fp, wrapped AS Wrapped, iv AS Iv FROM tbl_metadata_key ORDER BY generation DESC, key_id DESC")
            .Select(r => r.ToRow()).ToList();
    }

    private MetadataKeyRow? ReadRow(string keyId)
    {
        using var conn = _connFactory.CreateConnection();
        return conn.Query<KeyRowDto>(
                "SELECT key_id AS KeyId, generation AS Generation, dek_fingerprint AS Fp, wrapped AS Wrapped, iv AS Iv FROM tbl_metadata_key WHERE key_id = @keyId",
                new { keyId })
            .Select(r => r.ToRow()).FirstOrDefault();
    }

    private sealed class KeyRowDto
    {
        public string KeyId { get; set; } = "";
        public long Generation { get; set; }
        public string Fp { get; set; } = "";
        public byte[] Wrapped { get; set; } = [];
        public byte[] Iv { get; set; } = [];
        public MetadataKeyRow ToRow() => new(KeyId, (int)Generation, Fp, Wrapped, Iv);
    }
}
