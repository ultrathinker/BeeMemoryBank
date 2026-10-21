using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindIos.Services;

/// <summary>One notification to hand to the notification center. <paramref name="Generation"/> names THIS add: only the answer that carries the newest generation of its request id counts.</summary>
public sealed record SilenceAdd(string RequestId, BlindSilenceNotice Notice, long Generation);

/// <summary>What the book found to do for a snapshot: notifications to add (each with its generation) and request ids to withdraw.</summary>
public sealed record SilencePlan(IReadOnlyList<SilenceAdd> Adds, IReadOnlyList<string> Removes)
{
    public bool IsEmpty => Adds.Count == 0 && Removes.Count == 0;
}

/// <summary>
/// The bookkeeping behind <c>IosSilenceNotifier</c>, without the notification center: what is planned (<see cref="BlindSilencePlanner"/>), when iOS will show each pending request, and which
/// ADD of a request id the answers still count for. The notification center answers an add some time later and not necessarily in the order the adds were made (two updates can add the same
/// identifier before either is answered), so every add gets a generation and an answer is taken only for the newest generation of its request id:
/// <list type="bullet">
/// <item>an older success cannot overwrite the time of a newer add, and an older error cannot erase the record of a newer success;</item>
/// <item>a failure of the CURRENT add withdraws the plan of that request, so that the next update - even for a snapshot that did not change - plans it again instead of believing that it is pending.</item>
/// </list>
/// Thread-safe: the answers come on threads of the system.
/// </summary>
public sealed class SilenceRequestBook
{
    private readonly object _gate = new();
    private readonly BlindSilencePlanner _planner = new();
    private readonly Dictionary<string, long> _current = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _showsAt = new(StringComparer.Ordinal);
    private long _counter;

    /// <summary>What to change for this snapshot. Every notification returned is a new add with a new generation; every id returned to withdraw forgets its answers.</summary>
    public SilencePlan Plan(BlindAccountsSnapshot snapshot, DateTimeOffset now)
    {
        lock (_gate)
        {
            var changes = _planner.Update(snapshot, now);
            foreach (var id in changes.Remove)
            {
                _current.Remove(id);
                _showsAt.Remove(id);
            }
            var adds = new List<SilenceAdd>();
            foreach (var (requestId, notice) in changes.Set)
            {
                var generation = ++_counter;
                _current[requestId] = generation;
                adds.Add(new SilenceAdd(requestId, notice, generation));
            }
            return new SilencePlan(adds, changes.Remove);
        }
    }

    /// <summary>
    /// The notification center answered an add. An answer for any but the newest add of the request id is ignored. A success records when iOS will show the request; a failure forgets that and the plan
    /// of the request, so that the next update plans it again. Returns whether the answer counted.
    /// </summary>
    public bool Answered(string requestId, long generation, bool success, DateTimeOffset showsAt)
    {
        lock (_gate)
        {
            if (!_current.TryGetValue(requestId, out var newest) || newest != generation) return false;
            if (success)
            {
                _showsAt[requestId] = showsAt;
            }
            else
            {
                _current.Remove(requestId);
                _showsAt.Remove(requestId);
                _planner.Invalidate(requestId);
            }
            return true;
        }
    }

    /// <summary>
    /// The notification center answered (or did not answer) a withdrawal. A withdrawal that was not confirmed forgets the plan of those requests, so that the next update withdraws them again
    /// even though nothing the plan depends on changed: a request that was meant to go must not stay pending for ever.
    /// </summary>
    public void RemoveAnswered(IEnumerable<string> requestIds, bool success)
    {
        if (success) return;
        lock (_gate)
        {
            foreach (var id in requestIds) _planner.Invalidate(id);
        }
    }

    /// <summary>The notification center says which requests are still pending: the ones that fired are forgotten, and the next update plans their successors (<see cref="BlindSilencePlanner.NoteFired"/>).</summary>
    public IReadOnlyList<string> NoteFired(IEnumerable<string> pendingIds, DateTimeOffset now)
    {
        lock (_gate)
        {
            var fired = _planner.NoteFired(pendingIds, now);
            foreach (var id in fired) _showsAt.Remove(id);
            return fired;
        }
    }

    /// <summary>Alerts were just allowed: when nothing is known to be pending, everything is planned again (nothing could be queued before).</summary>
    public void AlertsAllowed()
    {
        lock (_gate)
        {
            if (_showsAt.Count == 0) _planner.Forget();
        }
    }

    /// <summary>When iOS will show the request, as far as the notification center has confirmed an add; null when none is known to be pending.</summary>
    public DateTimeOffset? ShowsAt(string requestId)
    {
        lock (_gate) return _showsAt.TryGetValue(requestId, out var at) ? at : null;
    }
}
