using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Recovery;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>
/// After this node re-keyed (release 2.12, plan WS-D D4): the sealed pairing records <c>android-backup:&lt;phone id&gt;</c> that name this node
/// carry its new key. A record whose producer (the node the phone calls) is this node gets the new producer key, so that a restored phone dials
/// and verifies the producer by the key it signs with now; a record this node signed (or whose statement changed because the producer key in it
/// changed) is signed again by this node with the new key, so that a restore checks it against the key the network now knows this node by.
/// Records that do not name this node are left as they are. Each changed record is sealed again under the current master key and published.
/// </summary>
public static class AndroidPairingRekey
{
    public const string NamePrefix = "android-backup:";

    /// <summary>
    /// Re-seals the records naming <paramref name="self"/>: the number published, and the number that could not be (a record that does not open,
    /// does not decode or does not name this node is skipped, not failed). <paramref name="newSeed"/> is not kept or cleared here.
    /// </summary>
    public static async Task<(int Published, int Failed)> ResealAsync(IDbConnectionFactory db, SessionService session, SealedSecretService secrets, Guid self,
        byte[] newPublicKey, byte[] newSeed, ILogger? logger = null)
    {
        var candidates = session.GetCandidateDeks();
        var published = 0;
        var failed = 0;
        try
        {
            if (candidates.Length == 0) return (0, 0);
            var byFingerprint = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var dek in candidates) byFingerprint.TryAdd(DekFingerprint.Of(dek), dek);

            List<(string Name, string Fp, byte[] Wrapped, byte[] Iv)> rows;
            using (var conn = db.CreateConnection())
                rows = (await conn.QueryAsync<(string Name, string Fp, byte[] Wrapped, byte[] Iv)>(
                    "SELECT name, dek_fingerprint, wrapped, iv FROM tbl_sealed_secret WHERE status = 'A' AND name LIKE 'android-backup:%' ORDER BY name")).ToList();

            foreach (var row in rows)
            {
                if (!byFingerprint.TryGetValue(row.Fp, out var dek)) continue;
                var value = SealedSecretCrypto.TryOpen(row.Name, row.Wrapped, row.Iv, dek);
                if (value is null) continue;
                byte[]? encoded = null;
                try
                {
                    if (!BlindPhoneBackupSeal.TryDecode(value, out var seal)) continue;
                    if (seal.ProducerNodeId != self && seal.PairedBy != self) continue;
                    if (!Guid.TryParse(row.Name[NamePrefix.Length..], out var phoneId)) continue;
                    // Already carried over (the steps after an interrupted re-key run again at the next unlock): left alone, nothing published twice.
                    if (seal.PairedBy == self && (seal.ProducerNodeId != self || seal.ProducerPublicKey.AsSpan().SequenceEqual(newPublicKey))
                        && NodeEventVerifier.SignatureVerifies(newPublicKey, seal.PairingStatement(phoneId), seal.PairingSignature))
                    {
                        CryptoZero(seal.BackupKey);
                        continue;
                    }
                    var moved = seal.ProducerNodeId == self ? seal with { ProducerPublicKey = newPublicKey } : seal;
                    // The statement names the producer's key: when it changed, the old signature no longer covers it, and this node (a superadmin, the
                    // re-key is a superadmin's operation) signs the record again.
                    var signed = moved with { PairedBy = self, PairingSignature = Ed25519Signer.Sign(newSeed, moved.PairingStatement(phoneId)) };
                    encoded = signed.Encode();
                    await secrets.PublishAsync(row.Name, encoded);
                    published++;
                    CryptoZero(signed.BackupKey, moved.BackupKey, seal.BackupKey);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    logger?.LogWarning("The phone pairing record {Name} could not be signed again after the re-key ({Type}); it is tried again at the next unlock",
                        row.Name, ex.GetType().Name);
                }
                finally
                {
                    Array.Clear(value);
                    if (encoded is not null) Array.Clear(encoded);
                }
            }
        }
        finally
        {
            foreach (var dek in candidates) Array.Clear(dek);
        }
        return (published, failed);
    }

    private static void CryptoZero(params byte[][] buffers)
    {
        foreach (var b in buffers.Distinct()) Array.Clear(b);
    }
}
