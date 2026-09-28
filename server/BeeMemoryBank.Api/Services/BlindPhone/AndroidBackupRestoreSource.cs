using System.Text.Json;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Recovery;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Api.Services.BlindPhone;

/// <summary>
/// A one-file restore source (<see cref="IBackupFileRestoreSource"/>): an Android blind node's backup file
/// (plan 6.8, section 10). Only the file and the master password are needed:
/// <list type="number">
/// <item><description>the file's open header carries the phone's recovery set;</description></item>
/// <item><description>the master password opens a box in it and the chain gives every DEK
/// (<see cref="RecoveryRestoreService.ResolveKeysAsync"/>, under the user's box policy);</description></item>
/// <item><description>one of those DEKs opens the sealed pairing record <c>android-backup:&lt;node id&gt;</c>
/// (<see cref="BlindPhoneBackupSeal"/>): the phone's backup key and the node it calls;</description></item>
/// <item><description>the key opens the body (<see cref="BlindPhoneBackupBody"/>, within
/// <see cref="BlindPhoneBackupLimits"/>): the blind package that node built and signed, its signature and the
/// phone's signed events;</description></item>
/// <item><description>the pairing record counts only if the superadmin that paired the phone signed it and a
/// DEK anchor among the phone's events vouches for that superadmin — the sealed secret itself is an any-peer
/// event every full node could replace;</description></item>
/// <item><description>then the same package restore a blind node's network restore gets
/// (<see cref="RecoveryRestoreService.RestoreFromPackageAsync"/>): both signatures against that producer, the
/// signed blind manifest's whitelist and positions, the media, anchor-vouched peers.</description></item>
/// </list>
/// </summary>
public sealed class AndroidBackupRestoreSource(
    RecoveryRestoreService restore, IServiceScopeFactory scopes, BlindPhoneBackupLimits? limits = null) : IBackupFileRestoreSource
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly BlindPhoneBackupLimits _limits = limits ?? BlindPhoneBackupLimits.Default;

    public bool Handles(string fullPath) => AndroidBackupRestore.LooksLikeBackup(fullPath);

    public async Task<RestoreResult> RestoreAsync(string fullPath, RestoreIdentity who, CancellationToken ct,
        RestoreBoxPolicy boxes = RestoreBoxPolicy.Default)
    {
        var work = Path.Combine(Path.GetTempPath(), $"bmb-android-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        var seals = new List<BlindPhoneBackupSeal>();
        RecoveredKeys? keys = null;
        try
        {
            var opened = await AndroidBackupRestore.OpenAsync(fullPath, async (header, token) =>
            {
                var set = RecoverySet.Parse(header.RecoverySet.ToJsonString());
                keys = await RecoveryRestoreService.ResolveKeysAsync(set, who.Password, boxes, token);
                seals.AddRange(OpenSeals(set, header.BackupKeyName, keys));
                return seals.Select(s => s.BackupKey.ToArray()).ToList();
            }, Path.Combine(work, "body.tar"), ct, maxBodyBytes: _limits.MaxBodyBytes);

            // Every copy of the record says the same (the same value re-sealed under each DEK).
            if (seals.Select(s => Convert.ToBase64String(s.Encode())).Distinct().Count() != 1)
                throw new InvalidDataException("The phone's pairing record is ambiguous; pair the phone again.");
            var seal = seals[0];

            var parts = await BlindPhoneBackupBody.ReadAsync(opened.PackagePath, Path.Combine(work, "body"), _limits, ct);
            var events = await ReadEventsAsync(parts.EventsPath, ct);
            await CheckPairingAsync(seal, opened.NodeId, parts.PackagePath, events, keys!, Path.Combine(work, "check"), ct);

            // The node the phone calls becomes this device's first peer, dialed pinned as the pairing recorded it.
            return await restore.RestoreFromPackageAsync(parts.PackagePath, parts.Signature, seal.ProducerPublicKey, who,
                new RestoreBlindPeer(seal.ProducerNodeId, "Listening node", seal.ProducerPublicKey, seal.ProducerAddress,
                    CpSeq: 0, TlsSpki: seal.ProducerTlsSpki),
                events, ct, boxes);
        }
        finally
        {
            keys?.Dispose();
            foreach (var s in seals) Array.Clear(s.BackupKey);
            try { Directory.Delete(work, recursive: true); } catch (IOException) { /* a temp folder */ }
        }
    }

    /// <summary>
    /// The pairing record's authority: signed by the node that paired the phone, that node a superadmin in the
    /// package's signed manifest, and a DEK anchor — found among the PHONE's events, which its applier took only
    /// from superadmins — vouching for that row. Checked on a scratch copy of the package's database with the
    /// manifest's whitelist in place, as the restore will lay it out, before anything is written here.
    /// </summary>
    private async Task CheckPairingAsync(BlindPhoneBackupSeal seal, Guid phoneId, string packagePath,
        IReadOnlyList<SyncEvent> events, RecoveredKeys keys, string dir, CancellationToken ct)
    {
        VerifiedSnapshot package;
        using (var scope = scopes.CreateScope())
        {
            try
            {
                package = await scope.ServiceProvider.GetRequiredService<SnapshotService>().ExtractVerifiedAsync(packagePath, dir);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidDataException($"Not a valid blind package: {ex.Message}", ex);
            }
        }
        var manifestPath = Path.Combine(package.Directory, BlindManifest.FileName);
        if (!File.Exists(manifestPath))
            throw new InvalidDataException("Not a blind package: blind-manifest.json is missing.");
        var manifest = RestoreEvidenceFromManifest.Of(BlindManifest.Parse(await File.ReadAllBytesAsync(manifestPath, ct)));

        var pairedBy = manifest.Whitelist.Where(p => p.NodeId == seal.PairedBy).ToList();
        if (pairedBy is not [{ IsSuperadmin: true } signer]
            || !Ed25519Signer.Verify(signer.PublicKey, seal.PairingStatement(phoneId), seal.PairingSignature))
            throw new InvalidDataException("The phone's pairing record is not signed by a superadmin of this network; pair the phone again.");

        AnchorVerification anchor;
        using (var conn = new SqliteConnection($"Data Source={package.DatabasePath};Pooling=False"))
        {
            conn.Open();
            await conn.ExecuteAsync("DELETE FROM tbl_whitelist");
            // A package carries no identity; the digest only skips "this node's" row, and the restored device's
            // new id is in no row — an empty table reads the same.
            await conn.ExecuteAsync("CREATE TABLE IF NOT EXISTS tbl_node_identity (node_id TEXT PRIMARY KEY, ed25519_public_key BLOB)");
            var now = DateTime.UtcNow.ToString("O");
            foreach (var p in manifest.Whitelist.GroupBy(p => p.NodeId).Select(g => g.First()))
                await conn.ExecuteAsync(
                    @"INSERT INTO tbl_whitelist (node_id, display_name, ed25519_public_key, api_address, is_superadmin, tls_spki,
                          status, created_at, updated_at, lamport_ts, source_node_id)
                      VALUES (@Id, @Name, @Key, @Address, @Super, @Pin, 'A', @Now, @Now, @Lamport, @Source)",
                    new
                    {
                        Id = p.NodeId.ToString(), Name = p.DisplayName, Key = p.PublicKey, Address = p.ApiAddress,
                        Super = p.IsSuperadmin ? 1 : 0, Pin = p.TlsSpki, Now = now, Lamport = p.LamportTs,
                        Source = p.SourceNodeId?.ToString()
                    });
            anchor = await StateAnchorService.VerifyAsync(conn, keys.Current, events, manifest.Keys, keys.HeadProven);
        }
        if (!anchor.Vouches(seal.PairedBy))
            throw new InvalidDataException(
                "No anchor under the master key vouches for the superadmin that paired this phone; its backup cannot say who produced it.");
    }

    /// <summary>The phone's events, read one at a time and stopped at the limit — never a whole oversized list in memory.</summary>
    private async Task<List<SyncEvent>> ReadEventsAsync(string path, CancellationToken ct)
    {
        var events = new List<SyncEvent>();
        try
        {
            await using var file = File.OpenRead(path);
            await foreach (var evt in JsonSerializer.DeserializeAsyncEnumerable<SyncEvent>(file, JsonOpts, ct))
            {
                if (evt == null) throw new InvalidDataException("The backup body's events hold an empty entry.");
                if (events.Count == _limits.MaxEvents)
                    throw new InvalidDataException("The backup body holds more events than a restore takes.");
                events.Add(evt);
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The backup body's events are not readable.", ex);
        }
        return events;
    }

    /// <summary>Every pairing record the sealed secret <paramref name="name"/> opens to under a recovered key.</summary>
    private static List<BlindPhoneBackupSeal> OpenSeals(RecoverySet set, string name, RecoveredKeys keys)
    {
        var deks = new[] { keys.Current }.Concat(keys.Retired.Values).ToList();
        var opened = new List<BlindPhoneBackupSeal>();
        foreach (var secret in set.SealedSecrets.Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            byte[] wrapped, iv;
            try
            {
                wrapped = Convert.FromBase64String(secret.Wrapped);
                iv = Convert.FromBase64String(secret.Iv);
            }
            catch (FormatException) { continue; }
            foreach (var dek in deks)
            {
                var value = SealedSecretCrypto.TryOpen(secret.Name, wrapped, iv, dek);
                // A record that opens but is not a signed pairing record (a bare key from an older pairing) names
                // no producer: it cannot vouch for a package.
                if (BlindPhoneBackupSeal.TryDecode(value, out var seal)) opened.Add(seal);
                if (value != null) Array.Clear(value);
            }
        }
        return opened;
    }
}
