using System.Text;
using BeeMemoryBank.Search.Indexing;

namespace BeeMemoryBank.Search.Tests.Indexing;

/// <summary>
/// The load-bearing correctness test for WP-10: a differential oracle comparing
/// <see cref="IndexBuilder.Lookup"/> against an independently-computed ground truth built by
/// tokenizing+stemming a synthetic corpus's plaintext directly, with the exact same
/// <see cref="ITokenizer"/>/<see cref="IStemmer"/> instances the <see cref="IndexBuilder"/> under
/// test uses internally.
///
/// <para>
/// The oracle invariant checked throughout: <c>IndexBuilder.Lookup(T)</c> == the set of articleIds
/// whose current plaintext, independently tokenized+stemmed by this test, contains term
/// <c>T</c> -- for every term ever seen in the corpus, not just terms currently present (so a term
/// whose only occurrences were later deleted/merged away must resolve to the empty set on both
/// sides, proving tombstoning/merging did not leave stale postings behind).
/// </para>
///
/// <para>
/// Run twice: once after only adding documents (hot buffer + whatever sealing that triggers, but no
/// updates/deletes), and once more after a churn phase of updates/deletes/re-additions sized to
/// force at least one seal and at least one merge, to prove the oracle still holds exactly after
/// that churn.
/// </para>
/// </summary>
public class IndexBuilderOracleTests
{
    // "A few thousand documents" per the brief; kept at the low end of that range plus modest
    // per-document word counts so the whole test (oracle build + IndexBuilder ingestion, run twice)
    // stays fast enough for routine `dotnet test` runs while still exercising realistic seal/merge
    // volumes.
    private const int InitialDocumentCount = 2500;
    private const int ChurnOperationCount = 1800;

    private readonly ITokenizer _tokenizer = new DefaultTokenizer();
    private readonly IStemmer _stemmer = new DefaultStemmer();

    [Fact]
    public void Lookup_MatchesIndependentOracle_AfterAddOnly_AndAfterChurnWithSealAndMerge()
    {
        var random = new Random(20260811);
        var corpus = new SyntheticCorpus(random);

        // Small thresholds relative to the corpus size: guarantees several seals happen during the
        // initial add-only phase, and the churn phase below (updates/deletes) will tombstone enough
        // of those sealed segments to force at least one merge on top of that.
        var builder = new IndexBuilder(_tokenizer, _stemmer, hotBufferSealThreshold: 150, mergeSegmentCountThreshold: 6, mergeTombstoneFractionThreshold: 0.25);

        // ground truth: articleId -> current plaintext. Only ever mutated in lockstep with calls
        // into `builder`, so at every checkpoint it reflects exactly what `builder` was told.
        var currentPlaintext = new Dictionary<Guid, string>();
        var liveArticleIds = new List<Guid>();

        for (int i = 0; i < InitialDocumentCount; i++)
        {
            Guid articleId = Guid.NewGuid();
            string body = corpus.GenerateBody(random);
            currentPlaintext[articleId] = body;
            liveArticleIds.Add(articleId);
            builder.AddOrUpdateDocument(articleId, Guid.NewGuid(), body);
        }

        builder.SealCount.Should().BeGreaterThan(0, "the corpus/threshold sizes above must force at least one seal during add-only ingestion");
        builder.SealedSegmentCount.Should().BeLessThanOrEqualTo(6, "the segment-count merge threshold must bound how many sealed segments ever coexist");

        var allTermsEverSeen = new HashSet<string>();
        AssertOracleMatches(builder, currentPlaintext, allTermsEverSeen);

        int sealCountBeforeChurn = builder.SealCount;
        int mergeCountBeforeChurn = builder.MergeCount;

        // Churn: a realistic mix of updates (re-add with different content), deletes, and fresh
        // additions. Updates/deletes on already-sealed articles tombstone their old segment entry,
        // which -- combined with the low merge thresholds above -- must force at least one merge on
        // top of the seals the add-only phase already forced.
        for (int i = 0; i < ChurnOperationCount; i++)
        {
            double roll = random.NextDouble();
            if (roll < 0.5 && liveArticleIds.Count > 0)
            {
                // Update an existing article's content.
                Guid articleId = liveArticleIds[random.Next(liveArticleIds.Count)];
                string body = corpus.GenerateBody(random);
                currentPlaintext[articleId] = body;
                builder.AddOrUpdateDocument(articleId, Guid.NewGuid(), body);
            }
            else if (roll < 0.75 && liveArticleIds.Count > 0)
            {
                // Delete an existing article.
                int index = random.Next(liveArticleIds.Count);
                Guid articleId = liveArticleIds[index];
                liveArticleIds.RemoveAt(index);
                currentPlaintext.Remove(articleId);
                builder.RemoveDocument(articleId);
            }
            else
            {
                // Add a brand-new article (including possibly re-using content shapes, but always a
                // fresh articleId).
                Guid articleId = Guid.NewGuid();
                string body = corpus.GenerateBody(random);
                currentPlaintext[articleId] = body;
                liveArticleIds.Add(articleId);
                builder.AddOrUpdateDocument(articleId, Guid.NewGuid(), body);
            }
        }

        builder.SealCount.Should().BeGreaterThan(sealCountBeforeChurn, "the churn phase's updates/re-additions must have triggered at least one more seal");
        builder.MergeCount.Should().BeGreaterThan(mergeCountBeforeChurn, "the churn phase's updates/deletes must have tombstoned enough sealed content to trigger at least one more merge");
        builder.SealedSegmentCount.Should().BeLessThanOrEqualTo(6, "the segment-count merge threshold must still bound live segments after churn");

        AssertOracleMatches(builder, currentPlaintext, allTermsEverSeen);
    }

    /// <summary>
    /// Builds the ground-truth term -&gt; articleIds index directly from <paramref name="currentPlaintext"/>
    /// (independently of anything <see cref="IndexBuilder"/> does internally, other than sharing the
    /// same tokenizer/stemmer instances), merges every term ever observed (across this and prior
    /// checkpoints) into <paramref name="allTermsEverSeen"/>, then asserts
    /// <see cref="IndexBuilder.Lookup"/> agrees with the oracle for every one of those terms --
    /// including ones no longer present in any current document, which must resolve to empty on
    /// both sides.
    /// </summary>
    private void AssertOracleMatches(IndexBuilder builder, Dictionary<Guid, string> currentPlaintext, HashSet<string> allTermsEverSeen)
    {
        var oracle = new Dictionary<string, HashSet<Guid>>();

        foreach ((Guid articleId, string text) in currentPlaintext)
        {
            var termsInThisDoc = new HashSet<string>();
            foreach (string token in _tokenizer.Tokenize(text))
            {
                string stem = _stemmer.Stem(token);
                if (stem.Length == 0)
                {
                    continue;
                }

                termsInThisDoc.Add(stem);
            }

            foreach (string term in termsInThisDoc)
            {
                allTermsEverSeen.Add(term);
                if (!oracle.TryGetValue(term, out HashSet<Guid>? articleIds))
                {
                    articleIds = new HashSet<Guid>();
                    oracle[term] = articleIds;
                }

                articleIds.Add(articleId);
            }
        }

        foreach (string term in allTermsEverSeen)
        {
            // Deliberately not FluentAssertions' BeEquivalentTo here: its structural-equivalence
            // matching is unordered-collection-generic and gets very slow on the hundreds of
            // thousand-plus-element comparisons this test does (one per distinct term). A direct
            // HashSet.SetEquals check is exactly the "same set of articleIds" comparison this test
            // needs, and stays fast regardless of collection size; a descriptive diff is only built
            // if the sets actually differ.
            var actual = new HashSet<Guid>(builder.Lookup(term));
            HashSet<Guid> expected = oracle.TryGetValue(term, out HashSet<Guid>? set) ? set : [];

            if (!actual.SetEquals(expected))
            {
                List<Guid> missing = expected.Except(actual).ToList();
                List<Guid> extra = actual.Except(expected).ToList();
                true.Should().BeFalse(
                    $"index lookup for term '{term}' must exactly match the independently-computed oracle: " +
                    $"missing {missing.Count} expected articleId(s), {extra.Count} unexpected extra articleId(s)");
            }
        }
    }

    /// <summary>
    /// Generates realistic-ish, mixed Russian/English prose from a small curated word pool sampled
    /// on a Zipf (power-law) distribution -- the same idea <c>tools/BeeMemoryBank.SeedGen</c> uses
    /// for its synthetic vault content, reimplemented standalone here (no project reference) since
    /// that tool produces content for an encrypted vault, not plain in-memory strings for a unit
    /// test, and this library stays dependency-free per the design constraints.
    /// </summary>
    private sealed class SyntheticCorpus
    {
        private static readonly string[] EnglishWords =
        [
            "server", "system", "database", "network", "service", "client", "project", "report",
            "document", "release", "version", "update", "feature", "function", "module", "error",
            "issue", "request", "response", "security", "access", "policy", "account", "password",
            "session", "token", "folder", "article", "search", "index", "query", "result", "engine",
            "memory", "storage", "segment", "merge", "buffer", "thread", "process", "config",
            "settings", "backup", "restore", "schedule", "monitor", "status", "alert", "metric",
            "dashboard", "team", "meeting", "deadline", "budget", "customer", "vendor", "contract",
            "invoice", "payment", "order", "shipment", "warehouse", "inventory", "product", "catalog",
            "price", "discount", "review", "comment", "message", "notification", "calendar", "task",
            "priority", "assignment", "approval", "workflow", "record", "history", "archive", "export",
            "import", "format", "encoding", "language", "translation", "summary", "analysis",
            "insight", "trend", "forecast", "strategy", "goal", "objective", "milestone", "risk",
            "audit",
        ];

        private static readonly string[] RussianWords =
        [
            "сервер", "система", "база", "сеть", "сервис", "клиент", "проект", "отчёт", "документ",
            "релиз", "версия", "обновление", "функция", "модуль", "ошибка", "проблема", "запрос",
            "ответ", "безопасность", "доступ", "политика", "аккаунт", "пароль", "сессия", "ключ",
            "папка", "статья", "поиск", "индекс", "результат", "движок", "память", "хранилище",
            "сегмент", "слияние", "буфер", "поток", "процесс", "настройка", "резервный", "монитор",
            "статус", "оповещение", "метрика", "панель", "команда", "встреча", "срок", "бюджет",
            "клиентура", "поставщик", "договор", "счёт", "оплата", "заказ", "склад", "товар",
            "каталог", "цена", "скидка", "отзыв", "комментарий", "сообщение", "уведомление",
            "календарь", "задача", "приоритет", "назначение", "утверждение", "процедура", "запись",
            "история", "архив", "экспорт", "импорт", "формат", "кодировка", "язык", "перевод",
            "сводка", "анализ", "тенденция", "прогноз", "стратегия", "цель", "этап", "риск",
            "аудит",
        ];

        private readonly ZipfPool _english = new(EnglishWords);
        private readonly ZipfPool _russian = new(RussianWords);

        public SyntheticCorpus(Random seedSource)
        {
            // Constructor kept for symmetry/future extension; no per-instance state depends on the
            // seed today (word pools are static), but accepting it documents that generation is
            // driven entirely by the caller-supplied Random, not any hidden internal entropy.
            _ = seedSource;
        }

        /// <summary>Generates one document body: a primary-language-dominant run of words with an occasional word from the other language, punctuated into sentences.</summary>
        public string GenerateBody(Random rng)
        {
            ZipfPool primary = rng.Next(2) == 0 ? _english : _russian;
            ZipfPool secondary = ReferenceEquals(primary, _english) ? _russian : _english;

            int wordCount = 20 + rng.Next(40); // 20..59 words: enough for realistic multi-sentence bodies.
            var sb = new StringBuilder();
            for (int i = 0; i < wordCount; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                ZipfPool pool = rng.NextDouble() < 0.85 ? primary : secondary;
                sb.Append(pool.Sample(rng));

                if (i > 0 && i < wordCount - 1 && rng.NextDouble() < 0.08)
                {
                    sb.Append(',');
                }

                if (i > 0 && i % 10 == 9)
                {
                    sb.Append('.');
                }
            }

            sb.Append('.');
            return sb.ToString();
        }

        /// <summary>
        /// Inverse-CDF sampler over a Zipf (power-law) distribution: rank-0 is the most popular
        /// item, P(rank) proportional to 1/(rank+1). Deterministic given the supplied
        /// <see cref="Random"/> sequence.
        /// </summary>
        private sealed class ZipfPool
        {
            private readonly string[] _items;
            private readonly double[] _cumulative;
            private readonly double _total;

            public ZipfPool(string[] items)
            {
                _items = items;
                _cumulative = new double[items.Length];
                double sum = 0;
                for (int i = 0; i < items.Length; i++)
                {
                    sum += 1.0 / (i + 1);
                    _cumulative[i] = sum;
                }

                _total = sum;
            }

            public string Sample(Random rng)
            {
                double target = rng.NextDouble() * _total;
                int lo = 0;
                int hi = _cumulative.Length - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (_cumulative[mid] > target)
                    {
                        hi = mid;
                    }
                    else
                    {
                        lo = mid + 1;
                    }
                }

                return _items[lo];
            }
        }
    }
}
