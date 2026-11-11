using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// pair_verdict (plan 3.7): is a pair of notes a duplicate, a newer and an older version, an overlap or a contradiction.
/// The prompt is <c>pair-1</c>; the model sees exactly two notes, labelled A and B (it never sees or answers with an id,
/// so an id it makes up has nowhere to go), each inside an <c>&lt;article&gt;</c> element the system text calls data.
///
/// <para>The reply is untrusted. It must be one JSON object that passes the schema check (verdict from the list, keep
/// A or B where the verdict needs one, a score 0..100, a summary); on a miss the model gets the error once and one more
/// try, then the candidate is an error. Text fields are cut (summary 400 characters, a quote 200), a quote that is not in
/// what was sent is dropped, unknown fields are ignored, and the advice that comes out is built by this code from the
/// verdict alone: the kind, the action and the apply mode are fixed per verdict, the model chooses none of them.</para>
///
/// <para>A note under the second password never reaches the model: the corpus does not list it, its text is not read
/// (<see cref="IDreamCorpus.ReadBodyAsync"/> answers null) and, again here, a text that is a protected blob is refused
/// before anything is built.</para>
/// </summary>
public sealed class PairVerdictTask : IDreamModelTask
{
    /// <summary>The reply is one short JSON object; the cap keeps a model that rambles from running up the bill. It is
    /// generous on purpose: a model that reasons before it answers spends its reasoning against it (the client asks for
    /// low effort), and a cap of 700 was used up before any JSON was written.</summary>
    internal const int MaxOutputTokens = 3000;

    private static readonly string[] Verdicts = ["duplicate", "superseded", "overlap", "contradiction", "related", "unrelated"];
    private static readonly string[] Labels = ["A", "B"];

    private static readonly Regex ArticleTag = new(@"<(\s*/?\s*article)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly Func<IDreamCorpus, DreamRunContext, int, IReadOnlyList<DreamPairCandidate>> _pairsOf;

    // Pairs already offered in this run (a pair that is in tier 1 may be in tier 2 as well), and the article lookup of a
    // corpus. Both live as long as the object that keys them.
    private readonly ConditionalWeakTable<DreamRunContext, HashSet<string>> _offered = new();
    private readonly ConditionalWeakTable<IDreamCorpus, Dictionary<Guid, DreamArticle>> _index = new();

    public PairVerdictTask(PairCandidateSource? pairs = null)
    {
        var source = pairs ?? new PairCandidateSource();
        _pairsOf = source.PairsOf;
    }

    /// <summary>A test's own source of pairs.</summary>
    internal PairVerdictTask(Func<IDreamCorpus, DreamRunContext, int, IReadOnlyList<DreamPairCandidate>> pairsOf)
    {
        _pairsOf = pairsOf;
    }

    public string Name => DreamNames.PairVerdictTask;

    public string PromptVersion => DreamNames.PairPromptVersion;

    public async IAsyncEnumerable<DreamModelCandidate> CandidatesAsync(IDreamCorpus corpus, DreamRunContext ctx, int tier,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        var byId = IndexOf(corpus);
        var offered = _offered.GetOrCreateValue(ctx);
        foreach (var pair in _pairsOf(corpus, ctx, tier))
        {
            ct.ThrowIfCancellationRequested();
            if (pair.AId == pair.BId) continue;
            if (!byId.TryGetValue(pair.AId, out var a) || !byId.TryGetValue(pair.BId, out var b)) continue;

            var key = DreamFingerprint.CandidateKey(Name, [a.Id, b.Id]);
            lock (offered)
            {
                if (!offered.Add(key)) continue;
            }

            yield return new DreamModelCandidate(
                Task: Name,
                CandidateKey: key,
                ContentMarker: DreamFingerprint.ContentMarker([(a.Id, a.LamportTs), (b.Id, b.LamportTs)]),
                Tier: tier,
                ArticleIds: [a.Id, b.Id],
                FolderIds: [],
                FamilyKey: DreamNames.PairFamilyKey(a.Id, b.Id),
                Data: new JsonObject
                {
                    ["similarity"] = Math.Round(pair.Similarity, 4),
                    ["z"] = Math.Round(pair.Z, 2),
                });
        }
    }

    public async Task<DreamTaskResult> ExamineAsync(DreamModelCandidate c, IDreamCorpus corpus, IDreamModelClient model,
        DreamRunContext ctx, CancellationToken ct)
    {
        if (c.ArticleIds.Count != 2 || c.ArticleIds[0] == c.ArticleIds[1])
            return DreamTaskResult.Failed("The candidate is not a pair of two different notes.");
        if (string.IsNullOrWhiteSpace(ctx.ModelId))
            return DreamTaskResult.Failed("No analysis model is set for this run.");

        var byId = IndexOf(corpus);
        if (!byId.TryGetValue(c.ArticleIds[0], out var a) || !byId.TryGetValue(c.ArticleIds[1], out var b))
            return DreamTaskResult.Nothing("unreadable");

        // Nothing is built, and nothing leaves, before both texts are in hand and neither is under the second password.
        var bodyA = await corpus.ReadBodyAsync(a.Id, ct);
        var bodyB = await corpus.ReadBodyAsync(b.Id, ct);
        if (bodyA == null || bodyB == null || ProtectedContentCodec.IsProtected(bodyA) || ProtectedContentCodec.IsProtected(bodyB))
            return DreamTaskResult.Nothing("unreadable");
        if (string.IsNullOrWhiteSpace(bodyA) || string.IsNullOrWhiteSpace(bodyB))
            return DreamTaskResult.Nothing("empty");

        var t = ctx.Thresholds;
        var sentA = Truncate(bodyA, t.PairBodyHeadChars, t.PairBodyTailChars);
        var sentB = Truncate(bodyB, t.PairBodyHeadChars, t.PairBodyTailChars);
        var system = DreamPromptResources.Load(PromptVersion);
        var user = BuildUserText(a, sentA, corpus.TagsOf(a.Id), b, sentB, corpus.TagsOf(b.Id));

        long promptTokens = 0, completionTokens = 0;
        PairReply? reply = null;
        string? error = null;
        for (var attempt = 0; attempt < DreamModelExchange.MaxTries && reply == null; attempt++)
        {
            var text = attempt == 0 ? user : DreamModelExchange.RetryUserText(user, error);
            var answer = await model.CompleteAsync(new DreamModelRequest(ctx.ModelId, system, text, MaxOutputTokens, ctx.Budget, ctx.ModelRowId), ct);
            promptTokens += answer.PromptTokens;
            completionTokens += answer.CompletionTokens;
            await ctx.Log.WriteAsync(DreamEventKind.ModelCall, "pair_verdict model call", [a.Id, b.Id], new JsonObject
            {
                ["tier"] = c.Tier,
                ["attempt"] = attempt + 1,
                ["promptTokens"] = answer.PromptTokens,
                ["completionTokens"] = answer.CompletionTokens,
                // Which model of which provider answered (a fallback's when the analysis model's provider failed); names, never text.
                ["model"] = answer.Model,
                ["provider"] = answer.Provider,
            }, ct);

            reply = ReadReply(answer.Content, sentA, sentB, t, out error);
            // A reply the output limit stopped says so, instead of "empty" or "no JSON object".
            if (reply == null && answer.CutOff) error = DreamJsonReader.CutOffError;
            if (reply == null)
                await ctx.Log.WriteAsync(DreamEventKind.Error, $"pair_verdict reply rejected: {error}", [a.Id, b.Id], null, ct);
        }

        if (reply == null)
            return DreamTaskResult.Failed(DreamModelExchange.FailureText(error), articlesSent: 2);

        if (reply.Verdict is "related" or "unrelated")
            return DreamTaskResult.Nothing(reply.Verdict, reply.Score, reply.Summary, articlesSent: 2);

        var draft = BuildDraft(reply, c, a, b, ctx, promptTokens, completionTokens);
        return new DreamTaskResult(DreamExaminedVerdict.Advice, [draft], reply.Verdict, reply.Score, reply.Summary, null, 2);
    }

    // ── The reply ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A reply that passed the schema check.</summary>
    internal sealed record PairReply(string Verdict, string? Keep, int? Score, string? Summary, IReadOnlyList<(string Label, string Text)> Quotes);

    /// <summary>The schema check of a pair_verdict reply: the reply read, or null with <paramref name="error"/> naming the
    /// field that failed (never its value). <paramref name="sentA"/> and <paramref name="sentB"/> are the texts the model
    /// was shown, against which each quote is checked.</summary>
    internal static PairReply? ReadReply(string? content, string sentA, string sentB, DreamThresholds t, out string? error)
    {
        if (!DreamJsonReader.TryReadObject(content, out var obj, out error)) return null;

        var f = new DreamJsonFields(obj);
        var verdict = f.OneOf("verdict", Verdicts, required: true);
        var makesAdvice = verdict is "duplicate" or "superseded" or "overlap" or "contradiction";
        var needsKeep = verdict is "duplicate" or "superseded";

        string? keep = needsKeep ? f.OneOf("keep", Labels, required: true) : null;
        int? score;
        string? summary;
        if (makesAdvice)
        {
            score = f.Integer("score", 0, 100, required: true);
            summary = f.Text("summary", t.SummaryMaxLength, required: true);
        }
        else
        {
            // No advice comes of related and unrelated, so a sloppy score or summary there costs nothing: read what is
            // usable and ignore the rest (a throwaway reader, so a miss records no error).
            score = new DreamJsonFields(obj).Integer("score", 0, 100, required: false);
            summary = new DreamJsonFields(obj).Text("summary", t.SummaryMaxLength, required: false);
        }
        if (!f.Ok)
        {
            error = f.Error;
            return null;
        }

        error = null;
        return new PairReply(verdict!, keep, score, summary, ReadQuotes(f, sentA, sentB, t));
    }

    /// <summary>Up to <see cref="DreamThresholds.PairMaxQuotes"/> quotes. A quote is evidence, so a bad one is dropped, not
    /// a reason to refuse the verdict; one that is not in the text the model was shown is dropped too.</summary>
    private static List<(string Label, string Text)> ReadQuotes(DreamJsonFields f, string sentA, string sentB, DreamThresholds t)
    {
        var quotes = new List<(string, string)>();
        var array = new DreamJsonFields(f.Object).Array("quotes", required: false);
        if (array == null) return quotes;

        var seenA = NormalizeForSearch(sentA);
        var seenB = NormalizeForSearch(sentB);
        foreach (var item in array)
        {
            if (quotes.Count >= t.PairMaxQuotes) break;
            if (item is not JsonObject o) continue;
            var q = new DreamJsonFields(o);
            var label = q.OneOf("article", Labels, required: true);
            var whole = q.Text("text", 10_000, required: true);
            if (!q.Ok || label == null || whole == null || whole.Length < 3) continue;
            if (!(label == "A" ? seenA : seenB).Contains(NormalizeForSearch(whole), StringComparison.Ordinal)) continue;
            quotes.Add((label, DreamJsonReader.Cut(whole, t.QuoteMaxLength)));
        }
        return quotes;
    }

    private static string NormalizeForSearch(string text) =>
        DreamJsonReader.CleanText(text, int.MaxValue / 4).ToLowerInvariant();

    // ── The advice ──────────────────────────────────────────────────────────────────────────────────────────────

    private static DreamAdviceDraft BuildDraft(PairReply r, DreamModelCandidate c, DreamArticle a, DreamArticle b,
        DreamRunContext ctx, long promptTokens, long completionTokens)
    {
        var t = ctx.Thresholds;
        var keepArticle = r.Keep == "B" ? b : a;
        var otherArticle = ReferenceEquals(keepArticle, a) ? b : a;

        var (kind, mode, action, title, first, second) = r.Verdict switch
        {
            "duplicate" => (DreamAdviceKind.Duplicate, DreamApplyMode.Chat,
                DreamAction.MergeArticles(keepArticle.Id, otherArticle.Id),
                $"Duplicate: {Quote(keepArticle)} and {Quote(otherArticle)}",
                (keepArticle, DreamObjectRole.Target), (otherArticle, DreamObjectRole.Other)),
            "superseded" => (DreamAdviceKind.Superseded, DreamApplyMode.Chat,
                DreamAction.MarkSuperseded(keepArticle.Id, otherArticle.Id),
                $"Outdated: {Quote(otherArticle)} is replaced by {Quote(keepArticle)}",
                (keepArticle, DreamObjectRole.Target), (otherArticle, DreamObjectRole.Other)),
            "overlap" => (DreamAdviceKind.Overlap, DreamApplyMode.Chat,
                DreamAction.ReviewPair(a.Id, b.Id),
                $"Overlap: {Quote(a)} and {Quote(b)}",
                (a, DreamObjectRole.Subject), (b, DreamObjectRole.Other)),
            _ => (DreamAdviceKind.Contradiction, DreamApplyMode.Prompt,
                DreamAction.ReviewPair(a.Id, b.Id),
                $"Contradiction: {Quote(a)} and {Quote(b)}",
                (a, DreamObjectRole.Subject), (b, DreamObjectRole.Other)),
        };

        var evidence = new List<DreamEvidence>();
        if (c.Data?["similarity"] is JsonValue sv && sv.TryGetValue<double>(out var sim))
            evidence.Add(new DreamEvidence(DreamEvidenceType.Similarity,
                $"Vector similarity of the two notes: {sim.ToString("0.00", CultureInfo.InvariantCulture)}.", sim));
        foreach (var (label, text) in r.Quotes)
            evidence.Add(new DreamEvidence(DreamEvidenceType.Quote, text, null, label == "A" ? a.Id : b.Id));

        return new DreamAdviceDraft
        {
            Kind = kind,
            Source = DreamAdviceSource.Model,
            ApplyMode = mode,
            Score = DreamScore.Clamp(r.Score ?? 0),
            Title = DreamJsonReader.Cut(title, t.TitleMaxLength),
            Summary = r.Summary ?? "",
            Action = action,
            Evidence = evidence,
            Objects = [DreamAdviceObject.ForArticle(first.Item1, first.Item2), DreamAdviceObject.ForArticle(second.Item1, second.Item2)],
            Preconditions =
            [
                DreamPrecondition.ArticleUnchanged(a.Id, a.LamportTs),
                DreamPrecondition.ArticleUnchanged(b.Id, b.LamportTs),
            ],
            FamilyKey = c.FamilyKey ?? DreamNames.PairFamilyKey(a.Id, b.Id),
            CandidateKey = c.CandidateKey,
            Model = new DreamAdviceModelInfo(ctx.ModelId!, ctx.ModelLabel ?? ctx.ModelId!, DreamNames.PairPromptVersion,
                promptTokens, completionTokens),
        };
    }

    private static string Quote(DreamArticle a) => $"\"{DreamJsonReader.CleanText(a.Title, 70)}\"";

    // ── The input ───────────────────────────────────────────────────────────────────────────────────────────────

    private static string BuildUserText(DreamArticle a, string textA, IReadOnlyList<string> tagsA,
        DreamArticle b, string textB, IReadOnlyList<string> tagsB)
    {
        var sb = new StringBuilder();
        sb.Append("Compare the two notes below and answer with the JSON object described in the instructions.\n\n");
        AppendArticle(sb, "A", a, textA, tagsA);
        sb.Append('\n');
        AppendArticle(sb, "B", b, textB, tagsB);
        return sb.ToString();
    }

    private static void AppendArticle(StringBuilder sb, string label, DreamArticle article, string text, IReadOnlyList<string> tags)
    {
        sb.Append("<article id=\"").Append(label).Append("\">\n");
        sb.Append("Title: ").Append(Line(article.Title, 200)).Append('\n');
        sb.Append("Folder: ").Append(Line(article.TreePath, 300)).Append('\n');
        if (tags.Count > 0)
            sb.Append("Tags: ").Append(string.Join(", ", tags.Take(8).Select(x => Line(x, 40)))).Append('\n');
        sb.Append("Created: ").Append(article.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("Updated: ").Append(article.UpdatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("Text:\n").Append(Neutralize(text)).Append('\n');
        sb.Append("</article>\n");
    }

    private static string Line(string text, int max) => Neutralize(DreamJsonReader.CleanText(text, max));

    /// <summary>A note must not be able to close its own <c>&lt;article&gt;</c> element, or open another one, and so step
    /// out of the data: the opening angle bracket of any such tag is written as an entity.</summary>
    internal static string Neutralize(string text)
    {
        try
        {
            return ArticleTag.Replace(text, "&lt;$1");
        }
        catch (RegexMatchTimeoutException)
        {
            return text.Replace("<", "&lt;", StringComparison.Ordinal);
        }
    }

    /// <summary>The text to send: all of it when it fits in head + tail characters, else the first head characters, a note
    /// of how much was left out, and the last tail characters (plan 3.7: 6000 and 2000).</summary>
    internal static string Truncate(string body, int head, int tail)
    {
        var text = body.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (text.Length <= head + tail) return text;

        var headEnd = head;
        if (headEnd > 0 && char.IsHighSurrogate(text[headEnd - 1])) headEnd--;
        var tailStart = text.Length - tail;
        if (tailStart < text.Length && char.IsLowSurrogate(text[tailStart])) tailStart++;
        var omitted = tailStart - headEnd;
        return $"{text[..headEnd]}\n[... {omitted} characters omitted ...]\n{text[tailStart..]}";
    }

    private Dictionary<Guid, DreamArticle> IndexOf(IDreamCorpus corpus) => _index.GetValue(corpus, static c =>
    {
        var map = new Dictionary<Guid, DreamArticle>();
        foreach (var article in c.Articles) map.TryAdd(article.Id, article);
        return map;
    });
}
