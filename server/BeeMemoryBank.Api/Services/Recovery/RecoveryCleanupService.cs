using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Recovery;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>
/// Cleanup at login on a superadmin PC (plan 6.3). In the quiet state the blind nodes should hold one
/// strong box; device boxes exist only while some device's password differs or no strong box exists.
/// With the password in memory, this tries it on every active device box (64 MiB each, once per box
/// and slot — the answer is remembered) and retires the ones that open to the same key as this PC's
/// strong box: same password, same key, nothing lost. A box that does not open means that device uses
/// another password; it stays, and the admin is told (<see cref="RecoveryStatusService"/>).
/// </summary>
public class RecoveryCleanupService(
    IServiceScopeFactory scopes,
    SessionService session,
    IRecoveryHost host,
    ILogger<RecoveryCleanupService> logger)
{
    /// <returns>The ids of the boxes retired (empty when nothing was to do).</returns>
    public async Task<IReadOnlyList<string>> RunAsync(int slotId, string password)
    {
        if (host.Kind != RecoveryHostKind.Pc || !session.IsUnlocked) return [];

        using var scope = scopes.CreateScope();
        var queries = scope.ServiceProvider.GetRequiredService<RecoveryBoxQueries>();
        var identity = await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync();
        if (identity == null) return [];

        var dek = session.GetMasterDek();
        string fingerprint;
        try { fingerprint = DekFingerprint.Of(dek); }
        finally { Array.Clear(dek); }

        // Nothing may be retired without this PC's own strong box for the current key to name as
        // the covering box: that is what guarantees the key stays recoverable.
        var covering = await queries.OwnStrongBoxAsync(identity.NodeId, fingerprint);
        if (covering == null) return [];

        var retire = new List<string>();
        foreach (var box in (await queries.ActiveBoxesAsync()).Where(b => b.Kind == RecoveryBoxKdf.KindDevice))
        {
            var check = await queries.CheckedAsync(box.BoxId, slotId);
            if (check == null)
            {
                byte[]? opened;
                try
                {
                    opened = RecoveryBoxCrypto.TryUnwrap(password, box.KdfPreset, box.Salt, box.Wrapped, box.Iv);
                }
                catch (KdfBusyException)
                {
                    return []; // try again at the next login
                }
                string? openedFp = null;
                if (opened != null)
                {
                    openedFp = DekFingerprint.Of(opened);
                    Array.Clear(opened);
                }
                await queries.RememberCheckAsync(box.BoxId, slotId, opened != null, openedFp);
                check = (opened != null, openedFp);
            }

            if (check.Value.Opens && check.Value.Fingerprint == covering.DekFingerprint)
                retire.Add(box.BoxId);
        }

        if (retire.Count == 0) return [];

        await scope.ServiceProvider.GetRequiredService<RecoveryEventPublisher>().PublishAsync(
            EventTypes.RecoveryBoxRetire, new RecoveryBoxRetirePayload(retire, covering.BoxId));
        logger.LogInformation("Retired {Count} device recovery box(es) covered by strong box {Covering}", retire.Count, covering.BoxId);
        return retire;
    }
}
