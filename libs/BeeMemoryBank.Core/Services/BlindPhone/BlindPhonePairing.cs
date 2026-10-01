using System.Security.Cryptography;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Services.BlindPhone;

/// <summary>
/// Makes a phone an Android blind node and pairs it (plan section 10): a blind NodeId, an Ed25519 key
/// outside any DEK, a backup key of its own, and a ONE-TIME pairing secret; then the code the phone
/// shows (→ Windows) and the check of the code Windows answers with (→ phone).
///
/// <para>The pairing secret is spent by the first answer the phone accepts. Whoever saw the phone's
/// code — over a shoulder, in a screenshot — could otherwise mint valid answers forever and move the
/// phone's sync and backups to a server of their choosing. Pointing the phone elsewhere later takes
/// an explicit <see cref="StartRePair"/> on the phone, which makes a fresh secret for a fresh code.
/// The backup key does not change with it: backups made before stay openable.</para>
/// </summary>
public sealed class BlindPhonePairing(
    BlindPhoneState state, IBlindPhoneKeys keys, IBlindIdentityRecorder identity, BlindPhoneLog log)
{
    private readonly SemaphoreSlim _identityGate = new(1, 1);
    private readonly object _pairGate = new();

    public bool HasIdentity => state.NodeId != null;
    public bool IsPaired => state.CallCode != null;

    /// <summary>True while a phone code can be answered: before pairing, or after <see cref="StartRePair"/>.</summary>
    public bool AwaitingAnswer
    {
        get
        {
            lock (_pairGate)
            {
                CleanupOrphanedSecretIfCommitted();
                var secret = keys.LoadPairingSecret();
                if (secret is null) return false;
                CryptographicOperations.ZeroMemory(secret);
                return true;
            }
        }
    }

    /// <summary>Creates the identity (once). Calling it again keeps the identity already made.</summary>
    public async Task CreateIdentityAsync(string displayName, CancellationToken ct = default)
    {
        await _identityGate.WaitAsync(ct);
        try
        {
            if (HasIdentity) return;
            displayName = displayName.Trim();
            if (displayName.Length is 0 or > BlindPhoneCode.MaxDisplayNameLength)
                throw new ArgumentException($"A name of 1 to {BlindPhoneCode.MaxDisplayNameLength} characters is needed.", nameof(displayName));

            // Check if an identity is already recorded in the database (e.g. crash after database write before state write)
            var existing = await identity.GetRecordedAsync(ct);
            if (existing is not null)
            {
                // Invariants validation (Finding 2):
                // Must be a blind node ID, v=2 external key, with empty private key & IV.
                if (!BlindNodeId.IsBlind(existing.NodeId) ||
                    existing.PrivateKeyV != NodeIdentityCrypto.ExternalKeyVersion ||
                    (existing.PrivateKey is { Length: > 0 }) ||
                    (existing.PrivateKeyIV is { Length: > 0 }))
                {
                    throw new InvalidOperationException(
                        $"Existing identity {existing.NodeId} in database is not a valid blind v=2 external-key row. Refusing to adopt invalid identity.");
                }

                var seed = keys.LoadIdentitySeed();
                if (seed is not null)
                {
                    try
                    {
                        var derivedPubKey = DerivePublicKeyFromSeed(seed);
                        if (CryptographicOperations.FixedTimeEquals(derivedPubKey, existing.PublicKey))
                        {
                            // Recover existing identity: Keystore seed matches the database row
                            state.PublicKey = existing.PublicKey;
                            state.DisplayName = string.IsNullOrWhiteSpace(existing.DisplayName) ? displayName : existing.DisplayName;
                            state.NodeId = existing.NodeId;

                            if (keys.LoadBackupKey() is null)
                            {
                                var bk = RandomNumberGenerator.GetBytes(32);
                                try { keys.SaveBackupKey(bk); }
                                finally { CryptographicOperations.ZeroMemory(bk); }
                            }
                            if (keys.LoadPairingSecret() is null && !IsPaired)
                            {
                                var sec = BlindPairingSecret.New();
                                try { keys.SavePairingSecret(sec); }
                                finally { CryptographicOperations.ZeroMemory(sec); }
                            }

                            log.Add("pairing", $"Recovered existing blind identity: {existing.NodeId}");
                            return;
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(seed);
                    }
                }

                // Database row exists without matching Keystore key: FAIL CLOSED (Finding 1)!
                // Do NOT delete or replace existing identity automatically.
                // Do NOT clear keys.
                // The old replica can no longer authenticate; preserve data and require explicit recovery/wipe.
                throw new InvalidOperationException(
                    $"Existing node identity {existing.NodeId} exists in database but Keystore seed is missing or mismatched. Disconnect and wipe required.");
            }

            // No database row: create fresh identity.
            // Do NOT call keys.Clear() here (Finding 3): doing so in the ordinary app wipes ingest.Clear(),
            // destroying the hardware-backed signing key (bmb_ingest.bin).
            var nodeId = BlindNodeId.NewId();
            var (publicKey, newSeed) = Ed25519Signer.GenerateKeyPair();
            var backupKey = RandomNumberGenerator.GetBytes(32);
            var secret = BlindPairingSecret.New();
            try
            {
                keys.SaveIdentitySeed(newSeed);
                keys.SaveBackupKey(backupKey);
                keys.SavePairingSecret(secret);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(newSeed);
                CryptographicOperations.ZeroMemory(backupKey);
                CryptographicOperations.ZeroMemory(secret);
            }

            await identity.RecordAsync(nodeId, publicKey, displayName, ct);

            state.PublicKey = publicKey;
            state.DisplayName = displayName;
            // Last: NodeId is what "has an identity" means, so a crash before this leaves no half-made one.
            state.NodeId = nodeId;
            log.Add("pairing", $"Blind identity created: {nodeId}");
        }
        finally
        {
            _identityGate.Release();
        }
    }

    /// <summary>
    /// The code the phone shows so Windows can add it; null when there is nothing to answer — no
    /// identity, or already paired and no re-pair started.
    /// </summary>
    public BlindPhoneCode? PhoneCode()
    {
        lock (_pairGate)
        {
            CleanupOrphanedSecretIfCommitted();
            if (state.NodeId is not { } nodeId || state.PublicKey is not { } key || state.DisplayName is not { } name) return null;
            var secret = keys.LoadPairingSecret();
            var backupKey = keys.LoadBackupKey();
            if (secret is null || backupKey is null) return null;
            return new BlindPhoneCode(nodeId, key, secret, backupKey, name);
        }
    }

    /// <summary>
    /// Starts pairing with another (or the same) computer: a fresh one-time secret, so the phone shows a
    /// new code. The current connection stays until a new answer is accepted.
    /// </summary>
    public void StartRePair()
    {
        lock (_pairGate)
        {
            if (!HasIdentity) throw new InvalidOperationException("This phone has no blind identity.");
            var secret = BlindPairingSecret.New();
            try { keys.SavePairingSecret(secret); }
            finally { CryptographicOperations.ZeroMemory(secret); }
            log.Add("pairing", "Re-pairing started: a new phone code was made.");
        }
    }

    /// <summary>
    /// Accepts Windows' "where to call" code. Null on success, otherwise why not. Only a code made with
    /// this phone's CURRENT pairing secret is accepted, and accepting it spends the secret.
    /// </summary>
    public string? AcceptCallCode(string text)
    {
        if (!BlindCallCode.TryParse(text, out var code))
            return "This is not a connection code from the computer. Copy it again.";

        lock (_pairGate)
        {
            CleanupOrphanedSecretIfCommitted();

            var secret = keys.LoadPairingSecret();
            if (secret is null)
                return IsPaired
                    ? "This phone is already paired. To connect it to another computer, choose Re-pair first."
                    : "This phone has no pairing secret. Disconnect and set it up again.";

            try
            {
                if (!code.IsAuthenticBy(secret))
                    return "This code was not made for this phone. Add the phone on the computer with the code shown here, then use the code the computer shows back.";
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }

            // Durable commit protocol:
            // 1. Commit the accepted call code to persistent state first.
            //    If this fails (e.g. Preferences/IO error), the secret is NOT consumed and retry is possible.
            state.CallCode = code;

            // 2. Consume the secret atomically with the accepted state.
            //    If process dies right here, on restart CleanupOrphanedSecretIfCommitted() will detect that
            //    state.CallCode is authentic by this secret, and will spend it safely without losing the connection.
            keys.ClearPairingSecret();

            log.Add("pairing", $"Paired: calls {code.Address} (node {code.NodeId}).");
            return null;
        }
    }

    private void CleanupOrphanedSecretIfCommitted()
    {
        if (state.CallCode is not { } callCode) return;
        var secret = keys.LoadPairingSecret();
        if (secret is null) return;
        try
        {
            if (callCode.IsAuthenticBy(secret))
            {
                keys.ClearPairingSecret();
                log.Add("pairing", "Cleaned up pairing secret committed in previous session.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static byte[] DerivePublicKeyFromSeed(byte[] seed)
    {
        var privateKeyParams = new Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(seed);
        return privateKeyParams.GeneratePublicKey().GetEncoded();
    }
}

