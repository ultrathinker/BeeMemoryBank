using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>
/// The moments the master password is in memory on this node, and what recovery work each one starts
/// (plan 6.3, 6.5). Every method returns at once; the work runs in the background and its task is
/// returned only so a caller that wants to (a test) can wait for it. Nothing here is awaited by a
/// request handler.
/// </summary>
public class RecoveryTriggers(
    IServiceScopeFactory scopes,
    StrongBoxService strongBoxes,
    RecoveryCleanupService cleanup,
    SessionService session,
    ILogger<RecoveryTriggers> logger)
{
    /// <summary>
    /// Stops the login-time recovery work (a test that counts events must not see a device box published on the background's own clock, as
    /// <c>NodeStateAnnouncer.PauseBackground</c> does for announcements). Off by default: a test that wants the work awaits the returned task.
    /// </summary>
    public bool PauseBackground { get; set; }

    /// <summary>
    /// A superadmin signed in and the vault is open: the device box when this node's newest one does not name the current key (2.12: a rotation
    /// publishes a v2 box only once every peer opens one, so until then the old shape is published here, from the password), strong box for the
    /// current key, then cleanup.
    /// </summary>
    public Task OnSuperadminLogin(User user, string password)
    {
        if (PauseBackground || user.Role != UserRoles.Superadmin || user.KeySlotId is not { } slotId || !session.IsUnlocked)
            return Task.CompletedTask;

        return RunDetached(async () =>
        {
            await EnsureDeviceBoxForCurrentKeyAsync(slotId, password);
            await strongBoxes.EnsureForCurrentKeyAsync(password);
            await cleanup.RunAsync(slotId, password);
        }, "Recovery work at login failed; it runs again at the next login");
    }

    internal Task RunDetached(Func<Task> work, string failureMessage)
    {
        var flowControl = ExecutionContext.IsFlowSuppressed() ? (AsyncFlowControl?)null : ExecutionContext.SuppressFlow();
        try
        {
            return Task.Run(async () =>
            {
                try
                {
                    await work().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    // Host/provider torn down while detached background work was running.
                }
                catch (Exception ex)
                {
                    try
                    {
                        logger.LogWarning(ex, "{Message}", failureMessage);
                    }
                    catch (Exception)
                    {
                        // The host's loggers are already disposed (shutdown). The logging pipeline reports that as an AggregateException
                        // around the provider's ObjectDisposedException, not as the bare one; any failure to log ends here, so this
                        // detached task never faults (it is nobody's to observe).
                    }
                }
            });
        }
        finally
        {
            flowControl?.Undo();
        }
    }

    private async Task EnsureDeviceBoxForCurrentKeyAsync(int slotId, string password)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var slot = (await sp.GetRequiredService<IKeySlotRepository>().GetAllAsync()).FirstOrDefault(s => s.SlotId == slotId);
        if (slot?.Salt is null || slot.ArgonMemory is null || slot.ArgonIterations is null || slot.ArgonParallelism is null) return;
        var identity = await sp.GetRequiredService<INodeIdentityRepository>().GetAsync();
        if (identity is null) return;
        var dek = session.GetMasterDek();
        byte[]? kek = null;
        try
        {
            var currentFp = BeeMemoryBank.Crypto.DekFingerprint.Of(dek);
            if (slot.DekFingerprint != currentFp) return;   // the door holds an older key: its lazy rewrap publishes
            using (var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection())
            {
                var newest = await Dapper.SqlMapper.QueryFirstOrDefaultAsync<string?>(conn,
                    @"SELECT dek_fingerprint FROM tbl_recovery_box WHERE kind = 'device' AND author_node_id = @me COLLATE NOCASE
                       ORDER BY lamport_ts DESC, created_at DESC LIMIT 1", new { me = identity.NodeId.ToString() });
                if (newest == currentFp) return;
            }
            kek = BeeMemoryBank.Crypto.KeyDerivation.DeriveKek(password, slot.Salt, slot.ArgonMemory.Value, slot.ArgonIterations.Value, slot.ArgonParallelism.Value);
            await sp.GetRequiredService<IRecoveryBoxPublisher>().PublishDeviceBoxAsync(slot, dek, kek);
        }
        finally
        {
            Array.Clear(dek);
            if (kek is not null) Array.Clear(kek);
        }
    }

    /// <summary>This node's own master password was just changed to <paramref name="newPassword"/>.</summary>
    public Task OnPasswordChanged(string newPassword) => strongBoxes.RequestBuild(newPassword);

    /// <summary>
    /// A rotation started here was accepted (after the rewrap committed; the session holds the new
    /// DEK). The initiator's slot was rewrapped inside the rotation, so it is published like any other
    /// slot change; links and seals are brought up to date; a strong box for the new key is built.
    /// </summary>
    public Task OnRotationAccepted(int initiatorSlotId, string masterPassword) => RunDetached(async () =>
    {
        using (var scope = scopes.CreateScope())
        {
            var slot = (await scope.ServiceProvider.GetRequiredService<IKeySlotRepository>().GetAllAsync())
                .FirstOrDefault(s => s.SlotId == initiatorSlotId);
            if (slot != null)
            {
                var dek = session.GetMasterDek();
                try
                {
                    // 2.12 (plan WS-A4): the door this node's device box should copy now, sealed to the new key; nothing while a peer
                    // cannot open a v2 box. An initiator whose own door is still v1 (the legacy single slot) is copied as before.
                    var boxes = scope.ServiceProvider.GetRequiredService<IRecoveryBoxPublisher>();
                    if (slot.DoorVersion == BeeMemoryBank.Crypto.Doors.V2) await boxes.RepublishForDoorsAsync(dek);
                    else await boxes.PublishDeviceBoxAsync(slot, dek);
                    // The initiator holds its password: when the box above was not published (a peer cannot open a v2 box yet), the
                    // old shape goes out from it, as at a sign-in.
                    await EnsureDeviceBoxForCurrentKeyAsync(initiatorSlotId, masterPassword);
                }
                finally
                {
                    Array.Clear(dek);
                }
            }
            await scope.ServiceProvider.GetRequiredService<IRecoveryReconciler>().ReconcileAsync();
        }
        await strongBoxes.RequestBuild(masterPassword);
    }, "Recovery work after the rotation failed; reconciliation retries at the next unlock");
}
