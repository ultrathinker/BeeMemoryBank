using System.Runtime.CompilerServices;
using BeeMemoryBank.Api.Dream;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>What a test wants the training detector and the training model task to do. One per node.</summary>
public sealed class DreamEngineScript
{
    /// <summary>The training detector's body; null yields nothing.</summary>
    public Func<IDreamCorpus, DreamRunContext, CancellationToken, IAsyncEnumerable<DreamAdviceDraft>>? Detect { get; set; }

    /// <summary>The pairs the training model task offers, for any tier; null offers none.</summary>
    public Func<IDreamCorpus, IReadOnlyList<(Guid A, Guid B)>>? Pairs { get; set; }

    /// <summary>What the training model task makes of a pair; null: an overlap advice.</summary>
    public Func<DreamModelCandidate, IDreamCorpus, DreamRunContext, Task<DreamTaskResult>>? Examine { get; set; }

    /// <summary>The caller scope the detector found itself under.</summary>
    public ICallerScope? ScopeSeen { get; set; }

    /// <summary>Set the moment the detector starts.</summary>
    public TaskCompletionSource DetectorStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>The detector of plan 4 P0b's acceptance list: whatever the test scripts, with whatever delays it scripts.</summary>
public sealed class TrainingDetector(DreamEngineScript script, CallerScopeHolder holder) : IDreamDetector
{
    public string Name => "Training";

    public async IAsyncEnumerable<DreamAdviceDraft> DetectAsync(IDreamCorpus corpus, DreamRunContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        script.ScopeSeen = holder.Scope;
        script.DetectorStarted.TrySetResult();
        if (script.Detect is null) yield break;
        await foreach (var draft in script.Detect(corpus, ctx, ct).WithCancellation(ct))
            yield return draft;
    }
}

/// <summary>A pair_verdict stand-in. It reads both bodies and sends them to the model, as the real task does.</summary>
public sealed class TrainingModelTask(DreamEngineScript script) : IDreamModelTask
{
    public string Name => DreamNames.PairVerdictTask;
    public string PromptVersion => DreamNames.PairPromptVersion;

    public async IAsyncEnumerable<DreamModelCandidate> CandidatesAsync(IDreamCorpus corpus, DreamRunContext ctx, int tier, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        if (script.Pairs is null) yield break;
        var lamport = corpus.Articles.ToDictionary(a => a.Id, a => a.LamportTs);
        foreach (var (a, b) in script.Pairs(corpus))
            yield return new DreamModelCandidate(Name, DreamFingerprint.CandidateKey(Name, [a, b]),
                DreamFingerprint.ContentMarker([(a, lamport[a]), (b, lamport[b])]), tier, [a, b], [], DreamNames.PairFamilyKey(a, b));
    }

    public async Task<DreamTaskResult> ExamineAsync(DreamModelCandidate c, IDreamCorpus corpus, IDreamModelClient model, DreamRunContext ctx, CancellationToken ct)
    {
        var bodies = new List<string?>();
        foreach (var id in c.ArticleIds) bodies.Add(await corpus.ReadBodyAsync(id, ct));
        var reply = await model.CompleteAsync(new DreamModelRequest(ctx.ModelId!, "system", string.Join("\n", bodies), 300, ctx.Budget), ct);

        if (script.Examine is not null) return await script.Examine(c, corpus, ctx);

        var a = corpus.Articles.Single(x => x.Id == c.ArticleIds[0]);
        var b = corpus.Articles.Single(x => x.Id == c.ArticleIds[1]);
        var draft = new DreamAdviceDraft
        {
            Kind = DreamAdviceKind.Overlap,
            Source = DreamAdviceSource.Model,
            ApplyMode = DreamApplyMode.Chat,
            Score = 77,
            Title = $"Overlap of {a.Title} and {b.Title}",
            Summary = "The two say much the same.",
            Action = DreamAction.ReviewPair(a.Id, b.Id),
            Objects = [DreamAdviceObject.ForArticle(a, DreamObjectRole.Subject), DreamAdviceObject.ForArticle(b, DreamObjectRole.Other)],
            FamilyKey = c.FamilyKey,
            CandidateKey = c.CandidateKey,
            Model = new DreamAdviceModelInfo(ctx.ModelId!, ctx.ModelLabel ?? "", PromptVersion, reply.PromptTokens, reply.CompletionTokens),
        };
        return new DreamTaskResult(DreamExaminedVerdict.Advice, [draft], "overlap", 77, "summary", null, 2);
    }
}

/// <summary>A model that answers at once, counts against the run's budget as the real client does, and remembers what it was sent.</summary>
public sealed class RecordingDreamModelClient : IDreamModelClient
{
    private readonly object _gate = new();
    private readonly List<DreamModelRequest> _requests = [];

    public long PromptTokens { get; set; } = 500;
    public long CompletionTokens { get; set; } = 100;

    public IReadOnlyList<DreamModelRequest> Requests
    {
        get
        {
            lock (_gate) return _requests.ToList();
        }
    }

    public Task<DreamModelReply> CompleteAsync(DreamModelRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!request.Budget.TryReserve(DreamRunBudget.EstimateTokens(request.System, request.User, request.MaxOutputTokens)))
            throw new DreamBudgetExceededException("The run's budget does not admit this call.");
        lock (_gate) _requests.Add(request);
        request.Budget.Record(PromptTokens, CompletionTokens);
        return Task.FromResult(new DreamModelReply("{}", PromptTokens, CompletionTokens, "fake/model"));
    }
}

/// <summary>A clock a test moves by hand; the node's local zone is UTC so that a schedule's window is plain arithmetic.</summary>
public sealed class ManualDreamClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    public void Set(DateTimeOffset at) => Interlocked.Exchange(ref _ticks, at.UtcTicks);
}

/// <summary>A node whose detector, model task and model client are the training ones. <paramref name="configure"/> adds
/// what a test needs besides (for example the Apply test seam, DreamApplyHooks).</summary>
public sealed class DreamEngineTestNode(DreamEngineOptions? options = null, bool keepRealServices = false, Action<IServiceCollection>? configure = null)
    : BmbWebApplicationFactory
{
    public DreamEngineScript Script { get; } = new();
    public RecordingDreamModelClient Model { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            if (options is not null) services.AddSingleton(options);
            configure?.Invoke(services);
            if (keepRealServices) return;
            services.RemoveAll<IDreamDetector>();
            services.RemoveAll<IDreamModelTask>();
            services.RemoveAll<IDreamModelClient>();
            services.AddSingleton(Script);
            services.AddScoped<IDreamDetector, TrainingDetector>();
            services.AddScoped<IDreamModelTask, TrainingModelTask>();
            services.AddSingleton<IDreamModelClient>(Model);
        });
    }
}
