using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// The deterministic candidate order of a trial run that carries a <c>sample</c> (plan 3.8, package C1). Several trial runs of
/// different models with the same seed must look at the same candidates in the same order, so that the comparison table has
/// a common part to compare and a quick model simply goes further than a slow one.
///
/// <para>The order is a hash order: a candidate's sort key is SHA-256 over <c>seed|candidateKey</c>. The candidate key is a
/// hash of the task and the sorted ids (DreamFingerprint.CandidateKey), so the order does not depend on how a task happens to
/// enumerate its candidates, on the model, on the time or on the run id; only on the seed and on which candidates exist. A
/// different seed gives a different, equally stable order. The cap (<c>pairs</c>, <c>folders</c>) takes the first candidates
/// of that order, across every tier of the run.</para>
///
/// <para>Nothing here reads a note or calls a model: it only reorders and cuts what a task already offered. The runner wraps
/// each task with <see cref="Wrap"/> when the run has a sample; a run without one gets the task itself.</para>
/// </summary>
public static class DreamSampler
{
    /// <summary>The largest <c>pairs</c> or <c>folders</c> a request may ask for.</summary>
    public const int MaxSampleCount = 10_000;

    /// <summary>The bytes candidates sort by for a seed. Equal keys of different candidates are not a worry (256 bits).</summary>
    public static byte[] SortKey(int seed, string candidateKey) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}|{candidateKey}"));

    /// <summary>The items in the seed's order, whatever order they came in. A repeated key keeps its first item only.</summary>
    public static IReadOnlyList<T> Order<T>(IEnumerable<T> items, int seed, Func<T, string> candidateKeyOf)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keyed = new List<(byte[] Hash, string Key, T Item)>();
        foreach (var item in items)
        {
            var key = candidateKeyOf(item);
            if (seen.Add(key)) keyed.Add((SortKey(seed, key), key, item));
        }
        keyed.Sort(static (a, b) =>
        {
            var byHash = a.Hash.AsSpan().SequenceCompareTo(b.Hash);
            return byHash != 0 ? byHash : string.CompareOrdinal(a.Key, b.Key);
        });
        return keyed.ConvertAll(static k => k.Item);
    }

    /// <summary>
    /// The text of the first thing wrong with a requested sample, or null when it is fine (POST /runs answers 400 invalid_request
    /// with it). A sample belongs to a trial run: a normal run skips what the examined registry already holds after the candidates
    /// have been counted, so a cap would be used up on candidates that were never examined. A seed is any integer; a count, when
    /// given, is 1..<see cref="MaxSampleCount"/> (a sample of nothing is not a sample).
    /// </summary>
    public static string? Validate(DreamSampleRequest? sample, DreamRunMode mode)
    {
        if (sample == null) return null;
        if (mode != DreamRunMode.Trial) return "sample is only for a trial run.";
        if (sample.Pairs is { } pairs && (pairs < 1 || pairs > MaxSampleCount))
            return $"sample.pairs must be between 1 and {MaxSampleCount}.";
        if (sample.Folders is { } folders && (folders < 1 || folders > MaxSampleCount))
            return $"sample.folders must be between 1 and {MaxSampleCount}.";
        return null;
    }

    /// <summary>How many candidates of the task the sample takes in all; null for no cap (and for tasks a sample does not cut).</summary>
    public static int? CapOf(DreamSampleRequest sample, string task) => task switch
    {
        DreamNames.PairVerdictTask => sample.Pairs,
        DreamNames.FolderReviewTask => sample.Folders,
        _ => null,
    };

    /// <summary>The task as the run sees it: sampled when the run has a sample, else the task itself. Calling it again for
    /// the same run (once per tier, say) is fine: what was taken so far is remembered per run, not per wrapper.</summary>
    public static IDreamModelTask Wrap(IDreamModelTask task, DreamRunContext ctx) =>
        ctx.Sample == null || task is DreamSampledTask ? task : new DreamSampledTask(task);

    internal enum Take { Taken, Repeat, CapReached }

    internal sealed class RunState
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _offered = new(StringComparer.Ordinal);
        private int _taken;

        public bool CapReached(int? cap)
        {
            lock (_gate) return cap is { } limit && _taken >= limit;
        }

        public Take TryTake(string candidateKey, int? cap)
        {
            lock (_gate)
            {
                if (cap is { } limit && _taken >= limit) return Take.CapReached;
                if (!_offered.Add(candidateKey)) return Take.Repeat;
                _taken++;
                return Take.Taken;
            }
        }
    }

    // What each run has taken so far, by task name: the cap holds across the tiers of one run.
    internal static readonly ConditionalWeakTable<DreamRunContext, ConcurrentDictionary<string, RunState>> States = new();
}

/// <summary>
/// A model task whose candidates come out in the sample's order and stop at the sample's cap (<see cref="DreamSampler"/>).
/// Examining is the inner task's own; only the choice and order of candidates change.
/// </summary>
public sealed class DreamSampledTask(IDreamModelTask inner) : IDreamModelTask
{
    public string Name => inner.Name;

    public string PromptVersion => inner.PromptVersion;

    public async IAsyncEnumerable<DreamModelCandidate> CandidatesAsync(IDreamCorpus corpus, DreamRunContext ctx, int tier,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var sample = ctx.Sample;
        if (sample == null)
        {
            await foreach (var c in inner.CandidatesAsync(corpus, ctx, tier, ct).WithCancellation(ct)) yield return c;
            yield break;
        }

        var cap = DreamSampler.CapOf(sample, Name);
        var state = DreamSampler.States.GetOrCreateValue(ctx).GetOrAdd(Name, _ => new DreamSampler.RunState());
        if (state.CapReached(cap)) yield break;

        // The whole tier is looked at before the first candidate goes out: a hash order needs every candidate.
        var offered = new List<DreamModelCandidate>();
        await foreach (var c in inner.CandidatesAsync(corpus, ctx, tier, ct).WithCancellation(ct)) offered.Add(c);

        foreach (var candidate in DreamSampler.Order(offered, sample.Seed, static c => c.CandidateKey))
        {
            ct.ThrowIfCancellationRequested();
            switch (state.TryTake(candidate.CandidateKey, cap))
            {
                case DreamSampler.Take.CapReached: yield break;
                case DreamSampler.Take.Repeat: continue;   // the same candidate in a later tier counts once
            }
            yield return candidate;
        }
    }

    public Task<DreamTaskResult> ExamineAsync(DreamModelCandidate c, IDreamCorpus corpus, IDreamModelClient model, DreamRunContext ctx,
        CancellationToken ct) => inner.ExamineAsync(c, corpus, model, ctx, ct);
}
