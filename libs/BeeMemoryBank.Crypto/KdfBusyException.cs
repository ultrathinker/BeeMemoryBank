namespace BeeMemoryBank.Crypto;

/// <summary>
/// Thrown by <see cref="KeyDerivation.DeriveKek"/> when too many Argon2id derivations are already
/// running or queued. It is a "try again later" signal (the API answers 503), never a verdict on the
/// password — callers that treat a failed derivation as "wrong password" must let this one through.
/// </summary>
public sealed class KdfBusyException()
    : Exception("Too many password checks are in progress. Try again in a moment.");
