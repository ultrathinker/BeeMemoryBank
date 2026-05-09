using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Services;

/// <summary>
/// Decides where to navigate after the session becomes unlocked (cold start,
/// unlock page, init, or join). If the local node has never completed its
/// initial sync, routes to the gate page; otherwise goes straight to status.
/// The gate's verdict must survive backgrounding, so it lives in
/// tbl_node_identity.initial_sync_completed, not in-memory state.
/// </summary>
public class PostUnlockRouter
{
    private readonly IServiceProvider _sp;

    // Cached view of tbl_node_identity.initial_sync_completed so the shell
    // navigation guard never has to block the UI thread on a SQLite read.
    // null = not yet resolved (cold start before first RouteAsync).
    private bool? _initialSyncCompletedCache;

    public PostUnlockRouter(IServiceProvider sp) => _sp = sp;

    public bool? CachedInitialSyncCompleted => _initialSyncCompletedCache;

    public void MarkInitialSyncCompleted() => _initialSyncCompletedCache = true;

    public async Task RouteAsync()
    {
        var target = await ResolveTargetAsync();

        if (target == "//initialSync")
        {
            // Don't start the sync foreground service until the initial gate
            // passes — the gate drives sync itself and must keep the UI blocked.
            await Shell.Current.GoToAsync(target);
        }
        else
        {
            await Shell.Current.GoToAsync(target);
            App.StartSyncService();
        }
    }

    public async Task<string> ResolveTargetAsync()
    {
        using var scope = _sp.CreateScope();
        var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
        var identity = await nodeRepo.GetAsync();
        if (identity == null)
        {
            _initialSyncCompletedCache = null;
            return "//setup";
        }
        _initialSyncCompletedCache = identity.InitialSyncCompleted;
        return identity.InitialSyncCompleted ? "//status" : "//initialSync";
    }
}
