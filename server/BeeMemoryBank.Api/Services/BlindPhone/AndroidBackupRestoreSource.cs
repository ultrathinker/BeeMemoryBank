using System.Text.Json;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
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
            // A node that has activated Step A hands the phone a format-2 package (docs/design/step-a-spec.md 12): the baseline is sealed
            // and there is no database to check the pairing against, so the pairing is checked against the package's signed whitelist
            // and the anchors the phone holds; the restore then verifies the package and opens the baseline with the recovered keys.
            if (await RecoveryRestoreService.IsFormat2PackageAsync(parts.PackagePath, ct))
                await CheckPairingV2Async(seal, opened.NodeId, parts.PackagePath, events, keys!, Path.Combine(work, "check"), ct);
            else
                await CheckPairingAsync(seal, opened.NodeId, parts.PackagePath, events, keys!, Path.Combine(work, "check"), ct);

            // The node the phone calls becomes this device's first peer, dialed pinned as the pairing recorded it.
            return await restore.RestoreFromBlindPackageAsync(parts.PackagePath, parts.Signature, seal.ProducerPublicKey, who,
                new RestoreBlindPeer(seal.ProducerNodeId, "Listening node", seal.ProducerPublicKey, seal.ProducerAddress,
                    CpSeq: 0, TlsSpki: string.IsNullOrEmpty(seal.ProducerTlsSpki) ? null : seal.ProducerTlsSpki,
                    TlsTrust: seal.ProducerTrust),
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
        // The pairing node's key in the package, or one it retired since the pairing (plan WS-D D4; a re-key also signs the record again).
        IReadOnlyList<BeeMemoryBank.Core.Models.NodeKeyHistoryEntry> carried;
        using (var historyConn = new SqliteConnection($"Data Source={package.DatabasePath};Mode=ReadOnly;Pooling=False"))
        {
            historyConn.Open();
            carried = await BeeMemoryBank.Storage.Sqlite.NodeKeyHistoryStore.ListAllAsync(historyConn);
        }
        if (pairedBy is not [{ IsSuperadmin: true } signer]
            || !BeeMemoryBank.Sync.NodeEventVerifier.VerifyPackageSignature(seal.PairedBy, signer.PublicKey,
                BeeMemoryBank.Sync.NodeEventVerifier.ChainedTo(seal.PairedBy, signer.PublicKey, carried),
                seal.PairingStatement(phoneId), seal.PairingSignature, signedAtLamport: null))
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

    /// <summary>
    /// The pairing record's authority for a format-2 package: signed by the node that paired the phone, that node a superadmin in the
    /// package's whitelist, and an anchor under the master key, found among the PHONE's events, whose own trust section lists that node
    /// as a superadmin and was signed by one. The whole-state match of the format-1 check is not available before the baseline is
    /// opened; the restore makes it after the replay and reports it (<see cref="RestoreResult.Anchor"/>). The package's own signature
    /// is verified by the restore against the producer key the pairing record holds, before anything of it is read.
    /// </summary>
    private async Task CheckPairingV2Async(BlindPhoneBackupSeal seal, Guid phoneId, string packagePath,
        IReadOnlyList<SyncEvent> events, RecoveredKeys keys, string dir, CancellationToken ct)
    {
        BlindManifestV2 manifest;
        try
        {
            manifest = BlindManifestV2.Parse(await Format2Package.ReadEntryAsync(packagePath, BlindManifestV2.FileName, 4L * 1024 * 1024, ct)
                ?? throw new InvalidDataException("Not a blind package: blind-manifest.json is missing."));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Not a blind package: its manifest is not readable.", ex);
        }
        var publicKeys = new Dictionary<Guid, byte[]>();
        foreach (var peer in manifest.Whitelist)
        {
            try { publicKeys[peer.NodeId] = Convert.FromBase64String(peer.PublicKeyB64); }
            catch (FormatException) { /* a row without a usable key vouches for nothing */ }
        }
        var pairedBy = manifest.Whitelist.Where(p => p.NodeId == seal.PairedBy).ToList();
        // Format 2: the manifest carries no key history before the package is opened; the record is signed again at every re-key (plan WS-D D4),
        // so the current key of the pairing node is the one to check (the helper's current-key check).
        if (pairedBy is not [{ IsSuperadmin: true } signer] || !publicKeys.TryGetValue(signer.NodeId, out var signerKey)
            || !BeeMemoryBank.Sync.NodeEventVerifier.SignatureVerifies(signerKey, seal.PairingStatement(phoneId), seal.PairingSignature))
            throw new InvalidDataException("The phone's pairing record is not signed by a superadmin of this network; pair the phone again.");
        // The authors' retired keys, carried in the package's control.db, as the format-1 path reads them from the package's
        // database: an anchor signed before its author re-keyed still vouches. The package's signature is not verified yet (the restore does that
        // first thing); a forged history only adds links whose two proofs verify, so it cannot make a key that never signed an anchor vouch.
        IReadOnlyList<BeeMemoryBank.Core.Models.NodeKeyHistoryEntry> carried = [];
        Directory.CreateDirectory(dir);
        var control = Path.Combine(dir, BlindManifestV2.ControlDbFileName);
        if (await Format2Package.ExtractEntryAsync(packagePath, BlindManifestV2.ControlDbFileName, control, _limits.MaxBodyBytes, ct))
            carried = BlindPackageV2Reader.ReadKeyHistory(control);
        if (!keys.HeadProven || !AnchorVouchesFor(events, publicKeys, keys.Current, seal.PairedBy, carried))
            throw new InvalidDataException(
                "No anchor under the master key vouches for the superadmin that paired this phone; its backup cannot say who produced it.");
    }

    /// <summary>
    /// Is there a state anchor among <paramref name="events"/> that a known node signed, that verifies under <paramref name="dek"/>, whose
    /// signer was a superadmin at its cut and whose trust section lists <paramref name="nodeId"/> as a superadmin with the key held?
    /// </summary>
    internal static bool AnchorVouchesFor(IReadOnlyList<SyncEvent> events, IReadOnlyDictionary<Guid, byte[]> publicKeys, byte[] dek, Guid nodeId,
        IReadOnlyList<BeeMemoryBank.Core.Models.NodeKeyHistoryEntry>? history = null)
    {
        if (!publicKeys.TryGetValue(nodeId, out var nodeKey)) return false;
        history ??= [];
        var fingerprint = DekFingerprint.Of(dek);
        // A node's keys: the one the package names and those it retired, as the carried history leads from them to it (plan WS-D D4).
        IEnumerable<byte[]> KeysOf(Guid node, byte[] current) =>
            BeeMemoryBank.Sync.NodeEventVerifier.ChainedTo(node, current, history).Select(r => r.PublicKey).Prepend(current);
        foreach (var evt in events)
        {
            if (evt.EventType != EventTypes.StateAnchor || !publicKeys.TryGetValue(evt.NodeId, out var key)) continue;
            StateAnchorPayload? a;
            try { a = JsonSerializer.Deserialize<StateAnchorPayload>(evt.Payload); }
            catch (JsonException) { continue; }
            if (a is not { PositionVector: not null } || a.DekFingerprint != fingerprint
                || !BeeMemoryBank.Sync.NodeEventVerifier.Verify(evt, key, history, BeeMemoryBank.Sync.NodeKeyMode.Package)
                || !StateAnchorCrypto.Verify(dek, a.AnchorId, a.DekFingerprint, a.PositionVector, a.Digest, a.CreatedAt, a.Hmac)
                || StateDigest.FormatOf(a.Digest) != StateDigest.FormatVersion)
                continue;
            // The trust section names each node with the key it held at the anchor's cut: the current one, or one retired since.
            if (KeysOf(evt.NodeId, key).Any(k => StateDigest.TrustProves(a.Digest, evt.NodeId, k, superadmin: true))
                && KeysOf(nodeId, nodeKey).Any(k => StateDigest.TrustProves(a.Digest, nodeId, k, superadmin: true)))
                return true;
        }
        return false;
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
