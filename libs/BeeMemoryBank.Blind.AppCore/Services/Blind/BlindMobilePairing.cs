using System.Security.Cryptography;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using Org.BouncyCastle.Crypto.Parameters;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// Manages identity initialization and two-code pairing for the standalone Android blind node app.
/// 
/// Invariants:
/// 1. Identity is a v=2 node identity (<see cref="NodeIdentityCrypto.ExternalKeyVersion"/>)
///    with UUIDv8 blind marker (<see cref="BlindNodeId"/>).
/// 2. Private signing key seed is kept in Android Keystore under isolated alias "bmb_blind_seed_v1"
///    via <see cref="IBlindNodeKeys"/>, completely independent of the ordinary app's ingest store.
/// 3. If an identity exists in SQLite but the Keystore seed is missing or mismatched, fails closed
///    preserving database and keys to prevent data loss or identity hijacking.
/// 4. Pairing secret is strictly one-time and spent immediately upon accepting a valid call code.
/// 5. Durable commit: accepted call code is persisted before spending the secret, with crash-recovery cleanup.
/// </summary>
public sealed class BlindMobilePairing(
    BlindPhoneState state,
    IBlindSecretStore keys,
    SqliteBlindIdentityRecorder identity,
    BlindPhoneLog log)
{
    private static readonly SemaphoreSlim _identityGate = new(1, 1);
    private readonly object _pairGate = new();

    public bool HasIdentity => state.NodeId != null;
    public bool IsPaired => state.CallCode != null;

    /// <summary>
    /// True when this phone has an identity but the Keystore no longer holds its backup key. The computer keeps the
    /// key's sealed copy, so a new one is never made silently (backups under it could not be opened): the screen
    /// says to disconnect and pair again. A phone with no identity yet has nothing to lose.
    /// </summary>
    public bool BackupKeyLost
    {
        get
        {
            if (!HasIdentity) return false;
            var key = keys.LoadBackupKey();
            if (key is null) return true;
            CryptographicOperations.ZeroMemory(key);
            return false;
        }
    }

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

    /// <summary>
    /// Creates or recovers the blind node identity. If an existing valid row exists in SQLite,
    /// verifies the Keystore seed matches and adopts it; otherwise creates a fresh v=2 identity.
    /// </summary>
    public async Task CreateIdentityAsync(string displayName, CancellationToken ct = default)
    {
        await _identityGate.WaitAsync(ct);
        try
        {
            if (HasIdentity) return;
            displayName = displayName.Trim();
            if (displayName.Length is 0 or > BlindPhoneCode.MaxDisplayNameLength || displayName.Any(char.IsControl))
                throw new ArgumentException($"A name of 1 to {BlindPhoneCode.MaxDisplayNameLength} characters without control characters is needed.", nameof(displayName));

            // Check if an identity is already recorded in the SQLite database
            var existing = await identity.GetRecordedAsync(ct);
            if (existing is not null)
            {
                // Invariants validation:
                // Must be a blind node ID, v=2 external key, empty private key/IV, and cannot generate embeddings.
                if (!BlindNodeId.IsBlind(existing.NodeId) ||
                    existing.PrivateKeyV != NodeIdentityCrypto.ExternalKeyVersion ||
                    (existing.PrivateKey is { Length: > 0 }) ||
                    (existing.PrivateKeyIV is { Length: > 0 }) ||
                    existing.CanGenerateEmbeddings)
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
                            // Seed matches: verify backup key is also present.
                            // If the backup key is lost, a new one must NOT be generated:
                            // the paired node still holds the old android-backup:<node> key
                            // sealed under the DEK; new backups with a different key would be
                            // unreadable by the Windows recovery path.
                            var backupKey = keys.LoadBackupKey();
                            if (backupKey is null)
                            {
                                throw new InvalidOperationException(
                                    $"Existing identity {existing.NodeId}: Keystore seed matches but backup key is missing. " +
                                    "The paired node holds the original backup key sealed under the DEK. " +
                                    "Disconnect and re-pair to create a new backup key safely.");
                            }

                            try
                            {
                                // Recover existing identity: Keystore seed and backup key both present
                                state.PublicKey = existing.PublicKey;
                                state.DisplayName = string.IsNullOrWhiteSpace(existing.DisplayName) ? displayName : existing.DisplayName;
                                state.NodeId = existing.NodeId;

                                var existingSecret = keys.LoadPairingSecret();
                                try
                                {
                                    if (existingSecret is null && !IsPaired)
                                    {
                                        var sec = BlindPairingSecret.New();
                                        try { keys.SavePairingSecret(sec); }
                                        finally { CryptographicOperations.ZeroMemory(sec); }
                                    }
                                }
                                finally
                                {
                                    if (existingSecret is not null)
                                    {
                                        CryptographicOperations.ZeroMemory(existingSecret);
                                    }
                                }

                                log.Add("pairing", $"Recovered existing blind identity: {existing.NodeId}");
                                return;
                            }
                            finally
                            {
                                CryptographicOperations.ZeroMemory(backupKey);
                            }
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(seed);
                    }
                }

                // Database row exists without matching Keystore key: FAIL CLOSED!
                // Do NOT delete or replace existing identity automatically.
                // Do NOT clear keys.
                // Preserve data and require explicit wipe.
                throw new InvalidOperationException(
                    $"Existing node identity {existing.NodeId} exists in database but Keystore seed is missing or mismatched. Disconnect and wipe required.");
            }

            // No database row: create fresh identity.
            var nodeId = BlindNodeId.NewId();
            var (publicKey, newSeed) = Ed25519Signer.GenerateKeyPair();
            var backupKeyInitial = RandomNumberGenerator.GetBytes(32);
            var secret = BlindPairingSecret.New();
            try
            {
                keys.SaveIdentitySeed(newSeed);
                keys.SaveBackupKey(backupKeyInitial);
                keys.SavePairingSecret(secret);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(newSeed);
                CryptographicOperations.ZeroMemory(backupKeyInitial);
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
    /// Executes an action with the phone code and guarantees that the loaded secret and backupKey
    /// buffers are wiped with CryptographicOperations.ZeroMemory as soon as the callback finishes,
    /// or if an exception is thrown during key loading or execution.
    /// </summary>
    public TResult? WithPhoneCode<TResult>(Func<BlindPhoneCode, TResult> consume)
    {
        lock (_pairGate)
        {
            CleanupOrphanedSecretIfCommitted();
            if (state.NodeId is not { } nodeId || state.PublicKey is not { } key || state.DisplayName is not { } name) return default;
            byte[]? secret = null;
            byte[]? backupKey = null;
            try
            {
                secret = keys.LoadPairingSecret();
                backupKey = keys.LoadBackupKey();
                if (secret is null || backupKey is null) return default;
                var code = new BlindPhoneCode(nodeId, key, secret, backupKey, name);
                return consume(code);
            }
            finally
            {
                if (secret is not null) CryptographicOperations.ZeroMemory(secret);
                if (backupKey is not null) CryptographicOperations.ZeroMemory(backupKey);
            }
        }
    }

    /// <summary>
    /// Executes an action with the phone code and guarantees that the loaded secret and backupKey
    /// buffers are wiped with CryptographicOperations.ZeroMemory as soon as the callback finishes,
    /// or if an exception is thrown during key loading or execution.
    /// </summary>
    public void WithPhoneCode(Action<BlindPhoneCode> consume)
    {
        WithPhoneCode(code =>
        {
            consume(code);
            return 0;
        });
    }

    /// <summary>
    /// Formats the phone code as text (for display, QR code, or clipboard) and ensures
    /// all loaded key material is zeroed immediately.
    /// </summary>
    public string? PhoneCodeText() => WithPhoneCode(code => code.ToString());

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
            //    If this fails, the secret is NOT consumed and retry is possible.
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
        var privateKeyParams = new Ed25519PrivateKeyParameters(seed);
        return privateKeyParams.GeneratePublicKey().GetEncoded();
    }
}
