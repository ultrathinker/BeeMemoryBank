namespace BeeMemoryBank.Mobile.Services;

/// <summary>
/// Hardware-backed (Android Keystore) store for the node's Ed25519 identity seed, used by the
/// background "ingest" path so the encrypted backup replica can authenticate and keep receiving
/// events WITHOUT the master DEK — i.e. while the vault is locked and across device reboots.
///
/// The key is wrapped by a non-exportable Keystore key that does NOT require screen unlock, so
/// the foreground service can use it unattended. It only enables sync AUTH + receiving ciphertext;
/// it can never decrypt article content (that still needs the password-derived master DEK).
///
/// Enrolled once, right after the first successful unlock (when the master DEK is available to
/// decrypt the identity seed). A stolen, copied database file is useless: the wrapping key lives
/// in the TEE and never leaves the device.
/// </summary>
public interface IIngestKeyStore
{
    /// <summary>True if the ingest seed has been enrolled and is usable.</summary>
    bool HasEnrolledKey();

    /// <summary>
    /// Wraps the raw 32-byte Ed25519 identity seed under the Keystore key and persists it.
    /// Idempotent-safe to call again (overwrites). Caller owns clearing <paramref name="rawNodePrivateKey"/>.
    /// </summary>
    void Enroll(byte[] rawNodePrivateKey);

    /// <summary>
    /// Returns the raw 32-byte Ed25519 identity seed. Caller MUST clear the returned buffer.
    /// </summary>
    byte[] UnwrapNodePrivateKey();

    /// <summary>Deletes the enrolled seed and the Keystore key.</summary>
    void Clear();
}
