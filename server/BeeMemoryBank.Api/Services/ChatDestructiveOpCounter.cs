using System.Collections.Concurrent;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Phase 3 guardrail: a small per-conversation cap on destructive chat tool calls
/// (<c>bee_delete_article</c>, <c>bee_replace_in_article</c>), so a single chat session cannot
/// spiral into mass deletions/replacements even if the user keeps clicking Allow (plan §2 Phase 3:
/// "per-session destructive-op cap ... a hard cap is enough").
///
/// <para>SIMPLICITY TRADE-OFF (documented): the counter is an in-memory singleton keyed by
/// conversation id. It is correct across the confirm-gate (each Allow is a separate HTTP request to
/// the confirm endpoint, but the singleton survives across requests within the process lifetime),
/// and it resets on application restart. That is acceptable per the plan ("in-memory OR chat.db-
/// persisted ... keep this simple") because (a) the human-in-the-loop gate already bounds every
/// destructive op one-by-one, and (b) after a restart the conversation context is reloaded from
/// chat.db and the model would have to re-request destructive ops, re-triggering confirmations.</para>
///
/// <para>The count reflects EXECUTED destructive ops (incremented only after a successful execution
/// on Allow), checked at execution time in the confirm endpoint so an over-cap op refuses with a
/// graceful tool-result error fed back to the model.</para>
/// </summary>
public sealed class ChatDestructiveOpCounter
{
    /// <summary>Maximum destructive (delete/replace) tool executions allowed per conversation.</summary>
    public const int CapPerConversation = 5;

    private readonly ConcurrentDictionary<Guid, int> _counts = new();

    /// <summary>True if executing one more destructive op in this conversation would exceed the cap
    /// (i.e. the conversation has already reached <see cref="CapPerConversation"/> executions).</summary>
    public bool WouldExceed(Guid conversationId)
        => _counts.GetValueOrDefault(conversationId) >= CapPerConversation;

    /// <summary>The configured per-conversation cap.</summary>
    public int Cap => CapPerConversation;

    /// <summary>Record one executed destructive op for a conversation. Called only after a successful
    /// execution (on user Allow).</summary>
    public void Record(Guid conversationId)
        => _counts.AddOrUpdate(conversationId, 1, (_, c) => c + 1);

    /// <summary>Atomically reserve a destructive-op slot: checks the cap AND increments in a single
    /// CAS loop, so two concurrent Allows (e.g. two browser tabs, or a rapid double-click that slips
    /// past the per-call in-flight guard because it targets a DIFFERENT pending destructive call)
    /// cannot both pass the cap (plan §2 Phase 3 "per-session destructive-op cap"). Returns false
    /// when the conversation is already at <see cref="CapPerConversation"/>; the caller should then
    /// refuse the op. Pair with <see cref="Release"/> when the reserved op does not actually execute.</summary>
    public bool TryReserve(Guid conversationId)
    {
        while (true)
        {
            if (_counts.TryGetValue(conversationId, out var current))
            {
                if (current >= CapPerConversation) return false;
                if (_counts.TryUpdate(conversationId, current + 1, current)) return true;
            }
            else if (_counts.TryAdd(conversationId, 1))
            {
                return true;
            }
            // Concurrent modification between the read and the write — retry the CAS loop.
        }
    }

    /// <summary>Release a previously-<see cref="TryReserve"/>d slot when the op did not actually
    /// execute (parse failure / ACL-denial / error result), so the count reflects EXECUTED destructive
    /// ops rather than attempted ones. Safe to call when nothing was reserved (no-op).</summary>
    public void Release(Guid conversationId)
    {
        while (true)
        {
            if (!_counts.TryGetValue(conversationId, out var current) || current <= 0) return;
            if (_counts.TryUpdate(conversationId, current - 1, current)) return;
        }
    }
}
