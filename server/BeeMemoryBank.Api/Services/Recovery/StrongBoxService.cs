using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Recovery;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>Where a strong box is being built: the machine kind and how much memory it has free.</summary>
public interface IRecoveryHost
{
    RecoveryHostKind Kind { get; }
    long AvailableMemoryBytes();
}

/// <summary>The real machine.</summary>
public sealed class CurrentRecoveryHost : IRecoveryHost
{
    public RecoveryHostKind Kind { get; } = StrongBoxPolicy.CurrentHost();
    public long AvailableMemoryBytes() => StrongBoxPolicy.AvailableMemoryBytes();
}

/// <summary>
/// Builds this node's strong box (plan 6.2, 6.3): the current DEK under the master password with a
/// heavy Argon2id preset, so a thief of the blind node or its backups has to pay 1 GiB per guess.
/// It is built only while the password is in memory — after a password change here, after accepting a
/// rotation started here, at login when no strong box of this node holds the current key — always on
/// the heavy-derivation queue in the background: the caller gets a task and never waits for it inside
/// a request. After the build the box is opened again and its fingerprint compared before anything is
/// published: a box that does not give back the key must never supersede one that does. Only built
/// while the whitelist holds a blind node — nothing else ever reads a strong box.
/// </summary>
public class StrongBoxService(
    IServiceScopeFactory scopes,
    SessionService session,
    IRecoveryHost host,
    ILogger<StrongBoxService> logger)
{
    /// <summary>Queues a build for <paramref name="password"/> and the current DEK. Never throws.</summary>
    public Task RequestBuild(string password)
    {
        var preset = StrongBoxPolicy.ChoosePreset(host.Kind, host.AvailableMemoryBytes());
        if (preset == null)
        {
            logger.LogInformation("No strong recovery box built here ({Host}, {Available} bytes available)",
                host.Kind, host.AvailableMemoryBytes());
            return Task.CompletedTask;
        }

        byte[] dek;
        try { dek = session.GetMasterDek(); }
        catch (SessionLockedException) { return Task.CompletedTask; }

        return Task.Run(async () =>
        {
            // A strong box exists for blind nodes and the backups they make. A mesh without one gets
            // nothing from it but two gigabyte-sized derivations at every trigger; once a blind node
            // is added, the next login builds it.
            if (!await HasBlindPeerAsync())
            {
                Array.Clear(dek);
                return;
            }
            await BuildAsync(dek, password, preset);
        });
    }

    private async Task<bool> HasBlindPeerAsync()
    {
        using var scope = scopes.CreateScope();
        var peers = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync();
        return peers.Any(p => BlindNodeId.IsBlind(p.NodeId));
    }

    /// <summary>Builds one unless this node already has an active strong box holding the current DEK.</summary>
    public async Task EnsureForCurrentKeyAsync(string password)
    {
        if (!session.IsUnlocked) return;
        using (var scope = scopes.CreateScope())
        {
            var identity = await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync();
            if (identity == null) return;
            var dek = session.GetMasterDek();
            string fingerprint;
            try { fingerprint = DekFingerprint.Of(dek); }
            finally { Array.Clear(dek); }
            if (await scope.ServiceProvider.GetRequiredService<RecoveryBoxQueries>().OwnStrongBoxAsync(identity.NodeId, fingerprint) != null)
                return;
        }
        await RequestBuild(password);
    }

    private async Task BuildAsync(byte[] dek, string password, string preset)
    {
        try
        {
            var seal = await HeavyDerivationQueue.RunAsync(() => RecoveryBoxCrypto.Wrap(dek, password, preset));

            var fingerprint = DekFingerprint.Of(dek);
            var reopened = await HeavyDerivationQueue.RunAsync(() =>
                RecoveryBoxCrypto.TryUnwrap(password, seal.KdfPreset, seal.Salt, seal.Wrapped, seal.Iv));
            var verified = reopened != null && DekFingerprint.Of(reopened) == fingerprint;
            if (reopened != null) Array.Clear(reopened);
            if (!verified)
            {
                logger.LogError("Strong recovery box failed its self-check; not published");
                return;
            }

            using var scope = scopes.CreateScope();
            var identity = await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync()
                ?? throw new InvalidOperationException("Node is not initialized.");
            await scope.ServiceProvider.GetRequiredService<RecoveryEventPublisher>().PublishAsync(
                EventTypes.RecoveryBoxSet,
                new RecoveryBoxSetPayload(
                    BoxId: Guid.NewGuid().ToString(),
                    Kind: RecoveryBoxKdf.KindStrong,
                    AuthorNodeId: identity.NodeId.ToString(),
                    DekFingerprint: fingerprint,
                    EpochHint: identity.DekEpoch,
                    KdfPreset: seal.KdfPreset,
                    Salt: Convert.ToBase64String(seal.Salt),
                    Wrapped: Convert.ToBase64String(seal.Wrapped),
                    Iv: Convert.ToBase64String(seal.Iv)),
                dek);
            logger.LogInformation("Published a strong recovery box ({Preset})", seal.KdfPreset);
        }
        catch (Exception ex)
        {
            // KdfBusyException included: the next trigger (login, password change) tries again.
            logger.LogWarning(ex, "Building the strong recovery box failed");
        }
        finally
        {
            Array.Clear(dek);
        }
    }
}
