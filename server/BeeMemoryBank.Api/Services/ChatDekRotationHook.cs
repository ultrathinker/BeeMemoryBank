using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Before a DEK rotation is proposed and again before its rewrap, moves every chat.db row still
/// sealed directly under the master DEK onto the node chat key — and refuses the rotation if it
/// cannot finish.
///
/// <para>The rotation transaction re-wraps the chat key (the <c>'chat'</c> row of
/// <c>tbl_node_data_key</c>) but cannot reach chat.db, so a legacy row left under the outgoing DEK
/// would be readable afterwards only through the in-memory retired-DEK cache — and not at all after
/// the next restart. The background <see cref="ChatHistoryBackfillProcessor"/> normally finishes this
/// long before any rotation; this hook is what makes it a guarantee instead of a race. It creates the
/// chat key too, if the node had none yet, so the rotation carries it forward.</para>
///
/// <para>Mandatory (see <see cref="IDekRotationHook"/>): it returns only after re-counting and
/// finding zero legacy rows, and throws <see cref="DekRotationPreconditionException"/> otherwise —
/// vault locked, a migration error, or rows left over after the batch cap. Rows marked unreadable
/// (<c>-1</c>) do not count: nothing this node holds opens them, before or after the rotation.</para>
/// </summary>
public sealed class ChatDekRotationHook(
    IServiceScopeFactory scopeFactory,
    ILogger<ChatDekRotationHook> logger,
    int maxBatches = 10_000,
    int batchSize = 500) : IDekRotationHook
{
    public async Task BeforeRewrapAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<SessionService>();
        if (!session.IsUnlocked)
            throw new DekRotationPreconditionException(
                "DEK rotation was not started: the vault locked before chat history could be moved onto the node chat key. Unlock and retry.");

        // Make sure the chat key exists before the rotation, so the transaction re-wraps it rather
        // than a first chat write racing the sentinel change.
        using (await scope.ServiceProvider.GetRequiredService<ChatDataProtector>().AcquireAsync(ct)) { }

        // Each batch strictly shrinks the legacy set, so a drain ends when a batch comes back empty;
        // the cap only bounds a runaway, and the recount below is what decides.
        var total = 0;
        for (var i = 0; i < maxBatches; i++)
        {
            ct.ThrowIfCancellationRequested();
            var moved = await ChatHistoryBackfillProcessor.MigrateOneBatchAsync(scope.ServiceProvider, batchSize, logger, ct);
            if (moved == 0) break;
            total += moved;
        }

        var remaining = await ChatHistoryBackfillProcessor.CountLegacyAsync(scope.ServiceProvider);
        if (remaining > 0)
            throw new DekRotationPreconditionException(
                $"DEK rotation was not started: {remaining} chat record(s) are still sealed directly under the current master key "
                + $"({total} moved in this attempt). Rotating now would make them unreadable after a restart. "
                + "Nothing was changed; retry the rotation (the remaining records are moved first).");

        if (total > 0)
            logger.LogInformation("Before DEK rotation: moved {Count} legacy chat row(s) onto the node chat key", total);
    }
}
