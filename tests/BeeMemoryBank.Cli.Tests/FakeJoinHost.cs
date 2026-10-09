using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// What a host answers to the rest of a <c>bmb join</c> after <c>/api/join</c> (BMB-81): the sync challenge, the
/// authentication, and a for-join snapshot of an empty vault, signed with the host's key the way the real endpoint signs
/// it. For the fake hosts of the <c>JoinCommand*Tests</c>, which test what happens around the join, not the snapshot.
/// </summary>
internal sealed class FakeJoinHost
{
    // Must match SnapshotService / SnapshotJoinClient byte for byte.
    private static readonly byte[] DomainTagSidecar = "BMB-MANIFEST-FILE-V1\0"u8.ToArray();

    private static readonly Lazy<(byte[] Db, int MigrationVersion)> EmptyVault = new(BuildEmptyVault);

    /// <param name="badSignature">Serve the snapshot with a signature that does not verify (a damaged or forged one).</param>
    public FakeJoinHost(Guid hostId, long cpSeq = 5, long lamportTs = 77, bool badSignature = false)
    {
        HostId = hostId;
        (PublicKey, _privateKey) = Ed25519Signer.GenerateKeyPair();
        CpSeq = cpSeq;
        (_snapshot, _signatureB64) = BuildSnapshot(cpSeq, lamportTs);
        if (badSignature) _signatureB64 = Convert.ToBase64String(new byte[64]);
    }

    private readonly byte[] _privateKey;
    private readonly byte[] _snapshot;
    private readonly string _signatureB64;

    /// <summary>The <c>/api/join</c> answer of this host: a key slot for <paramref name="password"/>, and the other peers given (none by default).</summary>
    public string JoinResponseJson(string password, int? protocolVersion = null, IEnumerable<IReadOnlyDictionary<string, object>>? whitelist = null)
    {
        var dek = MasterKeyManager.GenerateMasterDek();
        var salt = KeyDerivation.GenerateSalt();
        var (encDek, iv) = MasterKeyManager.WrapMasterDek(dek, KeyDerivation.DeriveKek(password, salt));
        return JsonSerializer.Serialize(new
        {
            remoteNode = new
            {
                nodeId = HostId, displayName = "Host", ed25519PublicKeyB64 = PublicKeyB64,
                protocolVersion = protocolVersion ?? BeeMemoryBank.Sync.SyncProtocolVersion.Current
            },
            keySlot = new
            {
                encryptedMasterDekB64 = Convert.ToBase64String(encDek),
                ivB64 = Convert.ToBase64String(iv),
                saltB64 = Convert.ToBase64String(salt),
                argonMemory = CryptoConstants.DefaultArgonMemory,
                argonIterations = CryptoConstants.DefaultArgonIterations,
                argonParallelism = CryptoConstants.DefaultArgonParallelism
            },
            whitelist = (whitelist ?? []).ToArray()
        });
    }

    /// <summary>
    /// A join answer with one string field of its <paramref name="section"/> (<c>remoteNode</c> or <c>keySlot</c>) replaced: a host
    /// whose answer parses but holds something the joiner cannot use (a base64 field that is not base64).
    /// </summary>
    public static string WithField(string joinResponseJson, string section, string field, string value)
    {
        var node = JsonNode.Parse(joinResponseJson)!;
        node[section]![field] = value;
        return node.ToJsonString();
    }

    /// <summary>
    /// A whitelist entry of the host's <c>/api/join</c> answer. A field that is null is left out altogether, which is what a host
    /// of a version that does not know it sends (<paramref name="tlsTrust"/> and <paramref name="tlsSpki"/> are 2.5.1's).
    /// </summary>
    public static Dictionary<string, object> Peer(
        Guid nodeId, string? apiAddress, string? tlsTrust = null, string? tlsSpki = null, bool isSuperadmin = false)
    {
        var entry = new Dictionary<string, object>
        {
            ["nodeId"] = nodeId,
            ["displayName"] = "Peer " + nodeId.ToString("N")[..6],
            ["ed25519PublicKeyB64"] = Convert.ToBase64String(Ed25519Signer.GenerateKeyPair().publicKey),
            ["isSuperadmin"] = isSuperadmin
        };
        if (apiAddress != null) entry["apiAddress"] = apiAddress;
        if (tlsTrust != null) entry["tlsTrust"] = tlsTrust;
        if (tlsSpki != null) entry["tlsSpki"] = tlsSpki;
        return entry;
    }

    /// <summary>A well-formed TLS pin (32 random bytes, base64url) of a key nobody holds.</summary>
    public static string NewPin() => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public Guid HostId { get; }
    public byte[] PublicKey { get; }
    public string PublicKeyB64 => Convert.ToBase64String(PublicKey);
    public long CpSeq { get; }

    /// <summary>The answer to a sync request of the join, or null for any other path.</summary>
    public (int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body)? Answer(string path)
    {
        var none = new Dictionary<string, string>();
        return path switch
        {
            "/api/sync/challenge" => (200, none, Json(new
            {
                challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                serverNodeId = HostId
            })),
            "/api/sync/authenticate" => (200, none, Json(new { token = "fake-host-token" })),
            "/api/sync/snapshot/for-join" => (200, new Dictionary<string, string>
            {
                ["X-BMB-Snapshot-Signature"] = _signatureB64,
                ["X-BMB-Snapshot-CP-Seq"] = CpSeq.ToString()
            }, _snapshot),
            _ => null
        };
    }

    private static byte[] Json(object value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));

    private (byte[] TarGz, string SignatureB64) BuildSnapshot(long cpSeq, long lamportTs)
    {
        var (db, migrationVersion) = EmptyVault.Value;
        var manifest = Json(new
        {
            version = 3,
            cpSequenceNum = cpSeq,
            lamportTsAtCp = lamportTs,
            migrationVersion,
            files = new Dictionary<string, string> { ["beememorybank.db"] = Convert.ToHexStringLower(SHA256.HashData(db)) }
        });

        using var tarGz = new MemoryStream();
        using (var gz = new GZipStream(tarGz, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: true))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "manifest.json") { DataStream = new MemoryStream(manifest) });
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "beememorybank.db") { DataStream = new MemoryStream(db) });
        }
        var bytes = tarGz.ToArray();

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(DomainTagSidecar);
        hasher.AppendData(manifest);
        hasher.AppendData(bytes);
        return (bytes, Convert.ToBase64String(Ed25519Signer.Sign(_privateKey, hasher.GetHashAndReset())));
    }

    /// <summary>A freshly migrated, empty vault database, as one self-contained file.</summary>
    private static (byte[] Db, int MigrationVersion) BuildEmptyVault()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb_cli_fakehost_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var live = Path.Combine(dir, "live.db");
            var copy = Path.Combine(dir, "snapshot.db");
            int version;
            using (var factory = new DbConnectionFactory(live))
            {
                new MigrationRunner(factory).RunMigrationsAsync().GetAwaiter().GetResult();
                using var conn = factory.CreateConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM tbl_migration";
                version = Convert.ToInt32(cmd.ExecuteScalar());
                cmd.CommandText = $"VACUUM INTO '{copy.Replace("'", "''")}'";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            return (File.ReadAllBytes(copy), version);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* the test's own temp folder */ }
        }
    }
}
