using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Mobile.Services;

/// <summary>
/// Enrols the node's Ed25519 seed into the hardware-backed ingest store so background backup-sync
/// can authenticate while the vault is locked. Enrolment must happen while the session is unlocked
/// (the master DEK is needed to decrypt the seed once). Called from every unlock AND from the main
/// page appearing, so a single transient failure at first setup self-heals on the next app open
/// instead of silently leaving background sync dead.
/// </summary>
public sealed class IngestKeyEnroller(IServiceProvider sp, ILogger<IngestKeyEnroller> logger)
{
    // Non-blocking guard: if two callers (e.g. PostUnlockRouter + StatusPage) fire at once, only
    // one enrols; the other skips instead of racing on the seed derivation / Keystore / tmp file.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>True if background backup is "armed" (ingest key present). True on platforms with
    /// no ingest store (the concept doesn't apply there).</summary>
    public bool IsArmed()
    {
        var ingest = sp.GetService<IIngestKeyStore>();
        return ingest is null || ingest.HasEnrolledKey();
    }

    /// <summary>Best-effort: enrol the ingest key if it isn't already and the session is unlocked.</summary>
    public async Task TryEnrollAsync()
    {
        if (!await _gate.WaitAsync(0)) return; // an enrolment is already in flight
        try
        {
            var ingest = sp.GetService<IIngestKeyStore>();
            if (ingest is null) return;

            var session = sp.GetRequiredService<SessionService>();
            if (!session.IsUnlocked) return;

            using var scope = sp.CreateScope();
            var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
            var identity = await nodeRepo.GetAsync();
            if (identity is null) return;

            // An enrolled seed that is not the seed of the node's current key (the node re-keyed, release 2.12 plan WS-D D4) would sign the background
            // sessions with a key the peers no longer accept: it is enrolled again with the current one.
            if (ingest.HasEnrolledKey())
            {
                byte[]? enrolled = null;
                try
                {
                    enrolled = ingest.UnwrapNodePrivateKey();
                    if (NodeIdentityCrypto.IsSeedOf(enrolled, identity.Ed25519PublicKey)) return;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "The enrolled ingest key could not be read; enrolling the current one");
                }
                finally
                {
                    if (enrolled != null) Array.Clear(enrolled);
                }
                logger.LogInformation("The enrolled ingest key is not this node's current identity key; enrolling the current one.");
            }

            var dek = session.GetMasterDek();
            byte[]? seed = null;
            try
            {
                seed = NodeIdentityVault.GetDecryptedPrivateKey(
                    identity.Ed25519PrivateKey,
                    identity.Ed25519PrivateKeyIV,
                    identity.Ed25519PrivateKeyV,
                    identity.NodeId,
                    dek);
                ingest.Enroll(seed);
            }
            finally
            {
                if (seed != null) Array.Clear(seed);
                Array.Clear(dek);
            }
        }
        catch (Exception ex)
        {
            // Best-effort: the next unlock / page-open retries; the StatusPage banner surfaces a
            // persistent failure so it's never silent. Log so it's diagnosable.
            logger.LogWarning(ex, "Ingest key enrolment failed; background backup inactive until retry.");
        }
        finally
        {
            _gate.Release();
        }
    }
}
