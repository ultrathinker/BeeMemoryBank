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
    public bool HasIdentity => state.NodeId != null;
    public bool IsPaired => state.CallCode != null;

    /// <summary>True while a phone code can be answered: before pairing, or after <see cref="StartRePair"/>.</summary>
    public bool AwaitingAnswer
    {
        get
        {
            var secret = keys.LoadPairingSecret();
            if (secret is null) return false;
            CryptographicOperations.ZeroMemory(secret);
            return true;
        }
    }

    /// <summary>Creates the identity (once). Calling it again keeps the identity already made.</summary>
    public async Task CreateIdentityAsync(string displayName, CancellationToken ct = default)
    {
        if (HasIdentity) return;
        displayName = displayName.Trim();
        if (displayName.Length is 0 or > BlindPhoneCode.MaxDisplayNameLength)
            throw new ArgumentException($"A name of 1 to {BlindPhoneCode.MaxDisplayNameLength} characters is needed.", nameof(displayName));

        var nodeId = BlindNodeId.NewId();
        var (publicKey, seed) = Ed25519Signer.GenerateKeyPair();
        var backupKey = RandomNumberGenerator.GetBytes(32);
        var secret = BlindPairingSecret.New();
        try
        {
            keys.SaveIdentitySeed(seed);
            keys.SaveBackupKey(backupKey);
            keys.SavePairingSecret(secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
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

    /// <summary>
    /// The code the phone shows so Windows can add it; null when there is nothing to answer — no
    /// identity, or already paired and no re-pair started.
    /// </summary>
    public BlindPhoneCode? PhoneCode()
    {
        if (state.NodeId is not { } nodeId || state.PublicKey is not { } key || state.DisplayName is not { } name) return null;
        var secret = keys.LoadPairingSecret();
        var backupKey = keys.LoadBackupKey();
        if (secret is null || backupKey is null) return null;
        return new BlindPhoneCode(nodeId, key, secret, backupKey, name);
    }

    /// <summary>
    /// Starts pairing with another (or the same) computer: a fresh one-time secret, so the phone shows a
    /// new code. The current connection stays until a new answer is accepted.
    /// </summary>
    public void StartRePair()
    {
        if (!HasIdentity) throw new InvalidOperationException("This phone has no blind identity.");
        var secret = BlindPairingSecret.New();
        try { keys.SavePairingSecret(secret); }
        finally { CryptographicOperations.ZeroMemory(secret); }
        log.Add("pairing", "Re-pairing started: a new phone code was made.");
    }

    /// <summary>
    /// Accepts Windows' "where to call" code. Null on success, otherwise why not. Only a code made with
    /// this phone's CURRENT pairing secret is accepted, and accepting it spends the secret.
    /// </summary>
    public string? AcceptCallCode(string text)
    {
        if (!BlindCallCode.TryParse(text, out var code))
            return "This is not a connection code from the computer. Copy it again.";

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

        // Spend the secret before anything else: a second answer — replayed or freshly minted by
        // someone who saw the phone code — must find nothing to check against.
        keys.ClearPairingSecret();
        state.CallCode = code;
        log.Add("pairing", $"Paired: calls {code.Address} (node {code.NodeId}).");
        return null;
    }
}
