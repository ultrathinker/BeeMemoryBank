using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Right before a DEK rotation's rewrap, moves every chat.db row still sealed directly under the
/// master DEK onto the node chat key.
///
/// <para>The rotation transaction re-wraps the chat key (the <c>'chat'</c> row of
/// <c>tbl_node_data_key</c>) but cannot reach chat.db, so a legacy row left under the outgoing DEK
/// would be readable afterwards only through
/// the in-memory retired-DEK cache — and not at all after the next restart. The background
/// <see cref="ChatHistoryBackfillProcessor"/> normally finishes this long before any rotation; this
/// hook is what makes it a guarantee instead of a race. It creates the chat key too, if the node had
/// none yet, so the rotation carries it forward.</para>
///
/// <para>Rows that open under no available master DEK are left marked unreadable (they already
/// were); a failure here is logged by <c>DekRewrapper.RunPreRewrapHooksAsync</c> and the rotation
/// continues — stragglers still open through the retired-DEK cache until restart, and the periodic
/// tick moves them.</para>
/// </summary>
public sealed class ChatDekRotationHook(IServiceScopeFactory scopeFactory, ILogger<ChatDekRotationHook> logger)
    : IDekRotationHook
{
    // A hard stop, not a target: each batch strictly shrinks the legacy set, so a drain ends when a
    // batch comes back empty. 10,000 x 500 rows is far beyond any real chat.db.
    private const int MaxBatches = 10_000;
    private const int BatchSize = 500;

    public async Task BeforeRewrapAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<SessionService>();
        if (!session.IsUnlocked) return;

        // Make sure the chat key exists before the rotation, so the transaction re-wraps it rather
        // than a concurrent first chat write racing the sentinel change.
        using (await scope.ServiceProvider.GetRequiredService<ChatDataProtector>().AcquireAsync(ct)) { }

        var total = 0;
        for (var i = 0; i < MaxBatches && !ct.IsCancellationRequested; i++)
        {
            var moved = await ChatHistoryBackfillProcessor.MigrateOneBatchAsync(scope.ServiceProvider, BatchSize, logger, ct);
            if (moved == 0) break;
            total += moved;
        }

        if (total > 0)
            logger.LogInformation("Before DEK rotation: moved {Count} legacy chat row(s) onto the node chat key", total);
    }
}
