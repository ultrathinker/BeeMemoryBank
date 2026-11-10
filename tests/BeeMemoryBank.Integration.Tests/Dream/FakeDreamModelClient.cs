using BeeMemoryBank.Api.Dream;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The model client of every Dream test (plan 3.7): no network, no key. It hands out the replies a test queued, in
/// order, and records every request it was asked to send, so a test can check what would have left the node. It keeps
/// the real client's budget protocol (the run's budget admits the call before it is answered, and the usage is counted
/// after), so a test of the limits behaves as in production.
///
/// <para>A reply is queued with <see cref="Enqueue"/> (text) or <see cref="EnqueueError"/> (the call throws); when the
/// queue is empty the <see cref="Responder"/> answers, and without one the call throws, so a test that expected fewer
/// calls than were made fails instead of getting a made-up answer.</para>
/// </summary>
public sealed class FakeDreamModelClient : IDreamModelClient
{
    private readonly object _lock = new();
    private readonly Queue<Func<DreamModelRequest, DreamModelReply>> _queue = new();
    private readonly List<DreamModelRequest> _requests = [];

    /// <summary>Answers a call when nothing is queued: the reply text for this request.</summary>
    public Func<DreamModelRequest, string>? Responder { get; set; }

    /// <summary>How long each call takes, so a test can stop or time out a run in the middle of one.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>The model the provider says answered.</summary>
    public string ModelName { get; set; } = "fake/model";

    /// <summary>Every request seen so far, in order (a request the budget refused is not among them).</summary>
    public IReadOnlyList<DreamModelRequest> Requests
    {
        get
        {
            lock (_lock) return _requests.ToList();
        }
    }

    public int CallCount
    {
        get
        {
            lock (_lock) return _requests.Count;
        }
    }

    /// <summary>The system and user text of every request, for "this string never left the node" checks.</summary>
    public string AllSentText
    {
        get
        {
            lock (_lock) return string.Join("\n----\n", _requests.Select(r => r.System + "\n" + r.User));
        }
    }

    /// <summary>Queues a reply. The token counts default to what the real count would roughly be (characters / 3).
    /// <paramref name="cutOff"/> marks a reply the output limit stopped (a model that reasoned away its cap).</summary>
    public FakeDreamModelClient Enqueue(string content, long? promptTokens = null, long? completionTokens = null, bool cutOff = false)
    {
        lock (_lock)
            _queue.Enqueue(r => new DreamModelReply(content,
                promptTokens ?? (r.System.Length + r.User.Length + 2) / 3,
                completionTokens ?? (content.Length + 2) / 3,
                ModelName,
                cutOff));
        return this;
    }

    /// <summary>Queues a failure: the call throws <paramref name="error"/> (a <see cref="DreamProviderException"/>, say).</summary>
    public FakeDreamModelClient EnqueueError(Exception error)
    {
        lock (_lock) _queue.Enqueue(_ => throw error);
        return this;
    }

    public async Task<DreamModelReply> CompleteAsync(DreamModelRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var estimate = DreamRunBudget.EstimateTokens(request.System, request.User, request.MaxOutputTokens);
        if (!request.Budget.TryReserve(estimate))
            throw new DreamBudgetExceededException("The model budget of this run does not admit another call.");

        Func<DreamModelRequest, DreamModelReply> next;
        lock (_lock)
        {
            _requests.Add(request);
            if (_queue.Count > 0)
                next = _queue.Dequeue();
            else if (Responder is { } responder)
                next = r => new DreamModelReply(responder(r), (r.System.Length + r.User.Length + 2) / 3, 100, ModelName);
            else
                throw new InvalidOperationException("FakeDreamModelClient has no reply queued for this call.");
        }

        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);

        var reply = next(request);
        request.Budget.Record(reply.PromptTokens, reply.CompletionTokens);
        return reply;
    }
}
