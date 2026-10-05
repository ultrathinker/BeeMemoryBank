using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// Extends <see cref="IBlindPhoneKeys"/> with isolated access to the blind node's identity seed.
/// Stored in Android Keystore under alias "bmb_blind_seed_v1", completely separated from the
/// ordinary app's ingest key store.
/// </summary>
public interface IBlindNodeKeys : IBlindSecretStore
{ }
