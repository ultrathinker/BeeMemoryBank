using BeeMemoryBank.Api.Dream;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Package D2 (plan 3.5, 3.6): the neighbour index, exact and near duplicates, the misplaced article and the pair
/// source by tiers. The similarity matrix is written by hand, so every test states the values it reasons about; no
/// embeddings model and no node is involved.
/// </summary>
public sealed class DreamSimilarityDetectorTests
{
    private static DateTime DaysAgo(double days) => DreamTestRuns.DefaultNow.UtcDateTime.AddDays(-days);

    // ── The neighbour index ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Neighbours_are_ranked_by_cosine_and_z_is_taken_over_the_whole_row()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var index = new DreamNeighbourIndex(new Dictionary<Guid, float[]>
        {
            [a] = [1f, 0f],
            [b] = [0f, 1f],
            [c] = [1f, 1f],
        });

        var row = index.NeighboursOf(a, 2);

        row.Should().HaveCount(2);
        row[0].Id.Should().Be(c);
        row[0].Similarity.Should().BeApproximately(1 / Math.Sqrt(2), 1e-9);
        row[0].Z.Should().BeApproximately(1, 1e-9);
        row[1].Id.Should().Be(b);
        row[1].Similarity.Should().BeApproximately(0, 1e-9);
        row[1].Z.Should().BeApproximately(-1, 1e-9);
    }

    [Fact]
    public void A_shorter_request_returns_only_the_nearest_and_a_later_longer_one_is_not_served_from_a_cut_row()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var index = new DreamNeighbourIndex(new Dictionary<Guid, float[]>
        {
            [a] = [1f, 0f],
            [b] = [0f, 1f],
            [c] = [1f, 1f],
        });

        index.NeighboursOf(a, 1).Should().ContainSingle().Which.Id.Should().Be(c);
        index.NeighboursOf(a, 2).Should().HaveCount(2);
        index.NeighboursOf(a, 10).Should().HaveCount(2);
    }

    [Fact]
    public void A_cached_prefix_is_not_mistaken_for_the_whole_row()
    {
        var hub = Guid.NewGuid();
        var vectors = new Dictionary<Guid, float[]>
        {
            [hub] = [1f, 0f],
        };
        for (var i = 0; i < 40; i++) vectors[Guid.NewGuid()] = [1f, 0.001f * (i + 1)];
        var index = new DreamNeighbourIndex(vectors);

        index.NeighboursOf(hub, 40).Should().HaveCount(40);
        // The second full request must not be served by the 32-entry cached prefix.
        index.NeighboursOf(hub, 40).Should().HaveCount(40);
        index.NeighboursOf(hub, 10).Should().HaveCount(10);
    }

    [Fact]
    public void An_article_without_a_vector_has_no_neighbours_and_is_not_one()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var index = new DreamNeighbourIndex(new Dictionary<Guid, float[]>
        {
            [a] = [1f, 0f],
            [b] = [],
        });

        index.NeighboursOf(a, 5).Should().BeEmpty();
        index.NeighboursOf(b, 5).Should().BeEmpty();
        index.NeighboursOf(Guid.NewGuid(), 5).Should().BeEmpty();
    }

    [Fact]
    public void A_zero_vector_is_treated_like_no_vector()
    {
        var a = Guid.NewGuid();
        var zero = Guid.NewGuid();
        var index = new DreamNeighbourIndex(new Dictionary<Guid, float[]>
        {
            [a] = [1f, 0f],
            [zero] = [0f, 0f],
        });

        index.NeighboursOf(a, 5).Should().BeEmpty();
    }

    [Fact]
    public void Two_articles_with_the_same_direction_have_similarity_one_and_z_zero()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var index = new DreamNeighbourIndex(new Dictionary<Guid, float[]>
        {
            [a] = [1f, 0f],
            [b] = [2f, 0f],
        });

        var row = index.NeighboursOf(a, 5);

        row.Should().ContainSingle();
        row[0].Similarity.Should().BeApproximately(1, 1e-9);
        row[0].Z.Should().Be(0);
    }

    [Fact]
    public void Vectors_of_different_dimensions_do_not_compare()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var index = new DreamNeighbourIndex(new Dictionary<Guid, float[]>
        {
            [a] = [1f, 0f],
            [b] = [1f, 0f, 0f],
        });

        index.NeighboursOf(a, 5).Should().BeEmpty();
    }

    [Fact]
    public void CandidatesOf_takes_every_qualifying_neighbour_not_just_the_nearest_twenty()
    {
        var hub = Guid.NewGuid();
        var far = Guid.NewGuid();
        var vectors = new Dictionary<Guid, float[]>
        {
            [hub] = [1f, 0f],
        };
        for (var i = 0; i < 25; i++)
        {
            var similarity = 0.999f - i * 0.0001f;
            vectors[Guid.NewGuid()] =
                [similarity, MathF.Sqrt(1f - similarity * similarity)];
        }
        vectors[far] = [0.96f, MathF.Sqrt(1f - 0.96f * 0.96f)];
        var index = new DreamNeighbourIndex(vectors);

        index.NeighboursOf(hub, 20).Should().NotContain(n => n.Id == far);
        var candidates = index.CandidatesOf(hub, minSimilarity: 0.95, minZ: double.PositiveInfinity);

        candidates.Should().HaveCount(26);
        candidates.Should().Contain(n => n.Id == far);
        candidates.Select(n => n.Similarity).Should().BeInDescendingOrder();
    }

    [Fact]
    public void CandidatesOf_can_select_by_z_when_similarity_alone_would_not()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var index = new DreamNeighbourIndex(new Dictionary<Guid, float[]>
        {
            [a] = [1f, 0f],
            [b] = [0f, 1f],
            [c] = [1f, 1f],
        });

        var candidates = index.CandidatesOf(a, minSimilarity: 2.0, minZ: 0.5);

        candidates.Should().ContainSingle().Which.Id.Should().Be(c);
    }

    // ── Exact duplicates ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Same_title_and_same_normalized_text_is_an_exact_duplicate()
    {
        var corpus = new HandMadeDreamCorpus();
        var older = corpus.AddArticle("Notes", "/A", DaysAgo(5), DaysAgo(5));
        var newer = corpus.AddArticle("  notes ", "/B", DaysAgo(2), DaysAgo(2));
        corpus.WithBody(older, "Hello  World\n").WithBody(newer, "hello world");

        var drafts = await DreamDetectorRunner.CollectAsync(
            new ExactDuplicateDetector(), corpus, DreamTestRuns.Context());

        var draft = drafts.Should().ContainSingle().Subject;
        draft.Kind.Should().Be(DreamAdviceKind.DuplicateExact);
        draft.Source.Should().Be(DreamAdviceSource.Rule);
        draft.Score.Should().Be(95);
        draft.Action.Type.Should().Be(DreamActionType.TrashArticle);
        draft.Action.ArticleId.Should().Be(newer.Id);
        draft.Action.KeepId.Should().Be(older.Id);
        draft.ApplyMode.Should().Be(DreamApplyMode.Code);
        draft.FamilyKey.Should().Be(DreamNames.PairFamilyKey(older.Id, newer.Id));
        draft.Preconditions.Should().HaveCount(2)
            .And.OnlyContain(p => p.Type == DreamPreconditionType.ArticleUnchanged);
    }

    [Fact]
    public async Task The_copy_with_fewer_references_goes_to_the_trash()
    {
        var corpus = new HandMadeDreamCorpus();
        var older = corpus.AddArticle("Notes", "/A", DaysAgo(5), DaysAgo(5));
        var newer = corpus.AddArticle("Notes", "/B", DaysAgo(2), DaysAgo(2));
        corpus.WithBody(older, "same text").WithBody(newer, "same text")
            .WithReferences(older, 3).WithReferences(newer, 0);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new ExactDuplicateDetector(), corpus, DreamTestRuns.Context());

        var draft = drafts.Should().ContainSingle().Subject;
        draft.Action.ArticleId.Should().Be(newer.Id);
        draft.Action.KeepId.Should().Be(older.Id);
        draft.ApplyMode.Should().Be(DreamApplyMode.Code);
        draft.References.Should().NotBeNull();
        draft.References!.Checked.Should().BeTrue();
        draft.References.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_copy_with_references_is_handed_to_the_chat_instead_of_the_code()
    {
        var corpus = new HandMadeDreamCorpus();
        var older = corpus.AddArticle("Notes", "/A", DaysAgo(5), DaysAgo(5));
        var newer = corpus.AddArticle("Notes", "/B", DaysAgo(2), DaysAgo(2));
        corpus.WithBody(older, "same text").WithBody(newer, "same text")
            .WithReferences(older, 3).WithReferences(newer, 2);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new ExactDuplicateDetector(), corpus, DreamTestRuns.Context());

        var draft = drafts.Should().ContainSingle().Subject;
        draft.Action.ArticleId.Should().Be(newer.Id);
        draft.ApplyMode.Should().Be(DreamApplyMode.Chat);
        draft.Evidence.Should().Contain(e => e.Type == DreamEvidenceType.Count && e.Value == 2);
    }

    [Fact]
    public async Task An_unavailable_reference_check_makes_it_a_chat_advice()
    {
        var corpus = new HandMadeDreamCorpus();
        var older = corpus.AddArticle("Notes", "/A", DaysAgo(5), DaysAgo(5));
        var newer = corpus.AddArticle("Notes", "/B", DaysAgo(2), DaysAgo(2));
        corpus.WithBody(older, "same text").WithBody(newer, "same text")
            .WithReferences(older, -1).WithReferences(newer, -1);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new ExactDuplicateDetector(), corpus, DreamTestRuns.Context());

        var draft = drafts.Should().ContainSingle().Subject;
        draft.ApplyMode.Should().Be(DreamApplyMode.Chat);
        draft.References!.Checked.Should().BeFalse();
    }

    [Fact]
    public async Task Same_title_but_different_text_is_not_an_exact_duplicate()
    {
        var corpus = new HandMadeDreamCorpus();
        var a = corpus.AddArticle("Notes", "/A", DaysAgo(5), DaysAgo(5));
        var b = corpus.AddArticle("Notes", "/B", DaysAgo(2), DaysAgo(2));
        corpus.WithBody(a, "one text").WithBody(b, "another text");

        var drafts = await DreamDetectorRunner.CollectAsync(
            new ExactDuplicateDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_high_similarity_pair_with_different_titles_and_the_same_text_is_an_exact_duplicate()
    {
        var corpus = new HandMadeDreamCorpus();
        var a = corpus.AddArticle("First", "/A", DaysAgo(5), DaysAgo(5));
        var b = corpus.AddArticle("Second", "/B", DaysAgo(2), DaysAgo(2));
        corpus.WithBody(a, "the same body").WithBody(b, "the same body");
        corpus.Link(a, b, similarity: 0.99, zFromA: 5);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new ExactDuplicateDetector(), corpus, DreamTestRuns.Context());

        var draft = drafts.Should().ContainSingle().Subject;
        draft.Action.Type.Should().Be(DreamActionType.TrashArticle);
        draft.Evidence.Should().Contain(e => e.Type == DreamEvidenceType.Similarity);
    }

    [Fact]
    public async Task A_pair_below_the_similarity_threshold_and_with_another_title_is_not_examined()
    {
        var corpus = new HandMadeDreamCorpus();
        var a = corpus.AddArticle("First", "/A", DaysAgo(5), DaysAgo(5));
        var b = corpus.AddArticle("Second", "/B", DaysAgo(2), DaysAgo(2));
        corpus.WithBody(a, "the same body").WithBody(b, "the same body");
        corpus.Link(a, b, similarity: 0.90, zFromA: 5);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new ExactDuplicateDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unreadable_body_cannot_prove_an_exact_duplicate()
    {
        var corpus = new HandMadeDreamCorpus();
        corpus.AddArticle("Notes", "/A", DaysAgo(5), DaysAgo(5));
        corpus.AddArticle("Notes", "/B", DaysAgo(2), DaysAgo(2));
        corpus.WithBody(corpus.Articles[0], "only one body");

        var drafts = await DreamDetectorRunner.CollectAsync(
            new ExactDuplicateDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_large_same_title_group_reads_and_hashes_each_body_once()
    {
        var corpus = new HandMadeDreamCorpus();
        for (var i = 0; i < 100; i++)
        {
            var article = corpus.AddArticle("Untitled", "/Inbox");
            corpus.WithBody(article, $"unique body {i}");
        }

        var drafts = await DreamDetectorRunner.CollectAsync(
            new ExactDuplicateDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().BeEmpty();
        corpus.BodyReads.Should().HaveCount(100);
        corpus.BodyReads.Values.Should().OnlyContain(count => count == 1);
    }

    [Fact]
    public async Task Only_articles_with_the_same_title_and_body_digest_are_paired()
    {
        var corpus = new HandMadeDreamCorpus();
        var first = corpus.AddArticle("Notes", "/A", DaysAgo(5), DaysAgo(5));
        var twin = corpus.AddArticle(" notes ", "/B", DaysAgo(2), DaysAgo(2));
        var different = corpus.AddArticle("Notes", "/C", DaysAgo(1), DaysAgo(1));
        corpus.WithBody(first, "same text").WithBody(twin, "SAME  text").WithBody(different, "other text");

        var drafts = await DreamDetectorRunner.CollectAsync(
            new ExactDuplicateDetector(), corpus, DreamTestRuns.Context());

        var draft = drafts.Should().ContainSingle().Subject;
        draft.Action.ArticleId.Should().Be(twin.Id);
        draft.Action.KeepId.Should().Be(first.Id);
        corpus.BodyReads.Should().HaveCount(3);
        corpus.BodyReads.Values.Should().OnlyContain(count => count == 1);
    }

    [Fact]
    public async Task An_exact_duplicate_beyond_the_twentieth_neighbour_is_still_found()
    {
        var corpus = new HandMadeDreamCorpus();
        var original = corpus.AddArticle("Original", "/A");
        corpus.WithBody(original, "shared body");
        var far = corpus.AddArticle("Copy", "/B");
        corpus.WithBody(far, "shared body");
        corpus.Link(original, far, similarity: 0.98, zFromA: 2, zFromB: 1);
        for (var i = 0; i < 24; i++)
        {
            var neighbour = corpus.AddArticle($"Other {i}", "/C");
            corpus.WithBody(neighbour, $"another body {i}");
            corpus.Link(original, neighbour, similarity: 0.999 - i * 0.0001, zFromA: 3, zFromB: 1);
        }

        corpus.NeighboursOf(original.Id, 20).Should().NotContain(n => n.Id == far.Id);
        var drafts = await DreamDetectorRunner.CollectAsync(
            new ExactDuplicateDetector(), corpus, DreamTestRuns.Context());

        var draft = drafts.Should().ContainSingle().Subject;
        new[] { draft.Action.ArticleId!.Value, draft.Action.KeepId!.Value }
            .Should().BeEquivalentTo(new[] { original.Id, far.Id });
    }

    // ── Near duplicates ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Without_the_model_a_candidate_that_is_not_exact_is_a_near_duplicate()
    {
        var corpus = new HandMadeDreamCorpus();
        var a = corpus.AddArticle("First", "/A");
        var b = corpus.AddArticle("Second", "/B");
        corpus.WithBody(a, "alpha").WithBody(b, "beta");
        corpus.Link(a, b, similarity: 0.96, zFromA: 3.5);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new NearDuplicateDetector(), corpus, DreamTestRuns.Context(useModel: false));

        var draft = drafts.Should().ContainSingle().Subject;
        draft.Kind.Should().Be(DreamAdviceKind.NearDuplicate);
        draft.Source.Should().Be(DreamAdviceSource.Rule);
        draft.ApplyMode.Should().Be(DreamApplyMode.Chat);
        draft.Score.Should().Be(71);
        draft.Action.Type.Should().Be(DreamActionType.ReviewPair);
        draft.FamilyKey.Should().Be(DreamNames.PairFamilyKey(a.Id, b.Id));
    }

    [Fact]
    public async Task An_exact_pair_is_not_offered_again_as_a_near_duplicate()
    {
        var corpus = new HandMadeDreamCorpus();
        var a = corpus.AddArticle("Same title", "/A");
        var b = corpus.AddArticle("same  title", "/B");
        corpus.WithBody(a, "same body").WithBody(b, "SAME BODY");
        corpus.Link(a, b, similarity: 0.96, zFromA: 3.5);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new NearDuplicateDetector(), corpus, DreamTestRuns.Context(useModel: false));

        drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_high_similarity_pair_with_different_text_is_still_a_near_duplicate()
    {
        var corpus = new HandMadeDreamCorpus();
        var a = corpus.AddArticle("First", "/A");
        var b = corpus.AddArticle("Second", "/B");
        corpus.WithBody(a, "alpha").WithBody(b, "beta");
        corpus.Link(a, b, similarity: 0.99, zFromA: 3.5);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new NearDuplicateDetector(), corpus, DreamTestRuns.Context(useModel: false));

        drafts.Should().ContainSingle().Subject.Score.Should().Be(74);
    }

    [Fact]
    public async Task With_the_model_on_the_near_duplicate_detector_is_silent()
    {
        var corpus = new HandMadeDreamCorpus();
        var a = corpus.AddArticle("First", "/A");
        var b = corpus.AddArticle("Second", "/B");
        corpus.Link(a, b, similarity: 0.96, zFromA: 3.5);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new NearDuplicateDetector(), corpus, DreamTestRuns.Context(useModel: true));

        drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_pair_that_only_stands_out_by_z_is_a_near_duplicate_at_the_lowest_score()
    {
        var corpus = new HandMadeDreamCorpus();
        var a = corpus.AddArticle("First", "/A");
        var b = corpus.AddArticle("Second", "/B");
        corpus.Link(a, b, similarity: 0.60, zFromA: 4.5, zFromB: 1.0);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new NearDuplicateDetector(), corpus, DreamTestRuns.Context(useModel: false));

        drafts.Should().ContainSingle().Subject.Score.Should().Be(50);
    }

    [Fact]
    public async Task A_pair_that_is_neither_similar_enough_nor_standing_out_is_not_a_candidate()
    {
        var corpus = new HandMadeDreamCorpus();
        var a = corpus.AddArticle("First", "/A");
        var b = corpus.AddArticle("Second", "/B");
        corpus.Link(a, b, similarity: 0.90, zFromA: 1.5, zFromB: 1.5);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new NearDuplicateDetector(), corpus, DreamTestRuns.Context(useModel: false));

        drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_near_duplicate_beyond_the_twentieth_neighbour_is_still_found()
    {
        var corpus = new HandMadeDreamCorpus();
        var original = corpus.AddArticle("Original", "/A");
        corpus.WithBody(original, "shared body");
        var far = corpus.AddArticle("Different", "/B");
        corpus.WithBody(far, "another body");
        corpus.Link(original, far, similarity: 0.96, zFromA: 2, zFromB: 1);
        for (var i = 0; i < 24; i++)
        {
            var neighbour = corpus.AddArticle($"Other {i}", "/C");
            corpus.WithBody(neighbour, "shared body");
            corpus.Link(original, neighbour, similarity: 0.999 - i * 0.0001, zFromA: 3, zFromB: 1);
        }

        corpus.NeighboursOf(original.Id, 20).Should().NotContain(n => n.Id == far.Id);
        var drafts = await DreamDetectorRunner.CollectAsync(
            new NearDuplicateDetector(), corpus, DreamTestRuns.Context(useModel: false));

        var draft = drafts.Should().ContainSingle().Subject;
        new[] { draft.Action.AId!.Value, draft.Action.BId!.Value }
            .Should().BeEquivalentTo(new[] { original.Id, far.Id });
    }

    // ── Misplaced articles ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_article_whose_strong_neighbours_share_another_folder_is_offered_a_move()
    {
        var corpus = new HandMadeDreamCorpus().AddFolder("/Inbox").AddFolder("/Kids");
        var article = corpus.AddArticle("Puzzle", "/Inbox");
        AddNeighbours(corpus, article, "/Kids", 8, z: 2.5);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new MisplacedArticleDetector(), corpus, DreamTestRuns.Context());

        var draft = drafts.Should().ContainSingle().Subject;
        draft.Kind.Should().Be(DreamAdviceKind.MoveArticle);
        draft.Source.Should().Be(DreamAdviceSource.Rule);
        draft.ApplyMode.Should().Be(DreamApplyMode.Code);
        draft.Score.Should().Be(90);
        draft.Action.Type.Should().Be(DreamActionType.MoveArticle);
        draft.Action.ArticleId.Should().Be(article.Id);
        draft.Action.ToPath.Should().Be("/Kids");
        draft.Preconditions.Should().Contain(p =>
            p.Type == DreamPreconditionType.ArticleInPath && p.Path == "/Inbox");
        draft.Preconditions.Should().Contain(p => p.Type == DreamPreconditionType.FolderExists && p.Path == "/Kids");
    }

    [Fact]
    public async Task Seven_strong_neighbours_are_not_enough()
    {
        var corpus = new HandMadeDreamCorpus().AddFolder("/Inbox").AddFolder("/Kids");
        var article = corpus.AddArticle("Puzzle", "/Inbox");
        AddNeighbours(corpus, article, "/Kids", 7, z: 2.5);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new MisplacedArticleDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_neighbour_below_the_z_threshold_is_not_a_strong_neighbour()
    {
        var corpus = new HandMadeDreamCorpus().AddFolder("/Inbox").AddFolder("/Kids");
        var article = corpus.AddArticle("Puzzle", "/Inbox");
        AddNeighbours(corpus, article, "/Kids", 8, z: 1.9);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new MisplacedArticleDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_share_of_sixty_percent_is_the_boundary_and_scores_seventy()
    {
        var corpus = new HandMadeDreamCorpus().AddFolder("/Inbox").AddFolder("/Kids").AddFolder("/Other");
        var article = corpus.AddArticle("Puzzle", "/Inbox");
        AddNeighbours(corpus, article, "/Kids", 6, z: 2.5);
        AddNeighbours(corpus, article, "/Other", 4, z: 2.5);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new MisplacedArticleDetector(), corpus, DreamTestRuns.Context());

        var draft = drafts.Should().ContainSingle().Subject;
        draft.Action.ToPath.Should().Be("/Kids");
        draft.Score.Should().Be(70);
    }

    [Fact]
    public async Task A_share_below_sixty_percent_does_not_point_at_any_folder()
    {
        var corpus = new HandMadeDreamCorpus().AddFolder("/Inbox").AddFolder("/Kids").AddFolder("/Other");
        var article = corpus.AddArticle("Puzzle", "/Inbox");
        AddNeighbours(corpus, article, "/Kids", 5, z: 2.5);
        AddNeighbours(corpus, article, "/Other", 5, z: 2.5);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new MisplacedArticleDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task Too_many_strong_neighbours_in_the_articles_own_folder_stop_the_move()
    {
        var corpus = new HandMadeDreamCorpus().AddFolder("/Inbox").AddFolder("/Kids");
        var article = corpus.AddArticle("Puzzle", "/Inbox");
        AddNeighbours(corpus, article, "/Inbox", 2, z: 2.5);
        AddNeighbours(corpus, article, "/Kids", 6, z: 2.5);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new MisplacedArticleDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task An_own_folder_share_of_exactly_fifteen_percent_is_allowed()
    {
        var corpus = new HandMadeDreamCorpus().AddFolder("/Inbox").AddFolder("/Kids");
        var article = corpus.AddArticle("Puzzle", "/Inbox");
        AddNeighbours(corpus, article, "/Inbox", 3, z: 2.5);
        AddNeighbours(corpus, article, "/Kids", 17, z: 2.5);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new MisplacedArticleDetector(), corpus, DreamTestRuns.Context());

        var draft = drafts.Should().ContainSingle().Subject;
        draft.Action.ToPath.Should().Be("/Kids");
        draft.Score.Should().Be(83);
    }

    [Fact]
    public async Task A_folder_that_contains_the_articles_own_folder_is_not_a_target()
    {
        var corpus = new HandMadeDreamCorpus().AddFolder("/A").AddFolder("/A/B");
        var article = corpus.AddArticle("Puzzle", "/A/B");
        AddNeighbours(corpus, article, "/A", 8, z: 2.5);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new MisplacedArticleDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_system_folder_is_not_a_target()
    {
        var corpus = new HandMadeDreamCorpus().AddFolder("/Inbox").AddFolder("/_Drafts", isSystem: true);
        var article = corpus.AddArticle("Puzzle", "/Inbox");
        AddNeighbours(corpus, article, "/_Drafts", 8, z: 2.5);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new MisplacedArticleDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_folder_missing_from_the_corpus_is_not_a_target()
    {
        var corpus = new HandMadeDreamCorpus().AddFolder("/Inbox");
        var article = corpus.AddArticle("Puzzle", "/Inbox");
        AddNeighbours(corpus, article, "/Ghost", 8, z: 2.5);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new MisplacedArticleDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task An_article_changed_within_the_last_three_days_is_left_alone()
    {
        var corpus = new HandMadeDreamCorpus().AddFolder("/Inbox").AddFolder("/Kids");
        var article = corpus.AddArticle("Puzzle", "/Inbox", updatedAt: DaysAgo(2));
        AddNeighbours(corpus, article, "/Kids", 8, z: 2.5);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new MisplacedArticleDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task An_article_at_the_quiet_boundary_is_still_proposed()
    {
        var corpus = new HandMadeDreamCorpus().AddFolder("/Inbox").AddFolder("/Kids");
        var article = corpus.AddArticle("Puzzle", "/Inbox", updatedAt: DaysAgo(3));
        AddNeighbours(corpus, article, "/Kids", 8, z: 2.5);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new MisplacedArticleDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().ContainSingle();
    }

    [Fact]
    public async Task A_neighbour_that_is_not_in_the_corpus_does_not_count_as_strong()
    {
        var corpus = new HandMadeDreamCorpus().AddFolder("/Inbox").AddFolder("/Kids");
        var article = corpus.AddArticle("Puzzle", "/Inbox");
        AddNeighbours(corpus, article, "/Kids", 7, z: 2.5);
        corpus.WithNeighbour(article.Id, Guid.NewGuid(), 0.99, 5);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new MisplacedArticleDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task The_score_follows_the_share_rounded_half_away_from_zero()
    {
        var corpus = new HandMadeDreamCorpus().AddFolder("/Inbox").AddFolder("/Kids").AddFolder("/Other");
        var article = corpus.AddArticle("Puzzle", "/Inbox");
        AddNeighbours(corpus, article, "/Kids", 13, z: 2.5);
        AddNeighbours(corpus, article, "/Other", 7, z: 2.5);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new MisplacedArticleDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().ContainSingle().Subject.Score.Should().Be(73);
    }

    // ── The pair source by tiers ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Tier_one_takes_every_duplicate_candidate()
    {
        var corpus = new HandMadeDreamCorpus();
        var a = corpus.AddArticle("A", "/A");
        var b = corpus.AddArticle("B", "/A");
        var c = corpus.AddArticle("C", "/A");
        var d = corpus.AddArticle("D", "/A");
        corpus.Link(a, b, similarity: 0.96, zFromA: 2, zFromB: 1);
        corpus.Link(a, c, similarity: 0.90, zFromA: 4.5, zFromB: 1);
        corpus.Link(a, d, similarity: 0.90, zFromA: 1, zFromB: 1);

        var pairs = new PairCandidateSource().PairsOf(corpus, DreamTestRuns.Context(), tier: 1);

        pairs.Should().HaveCount(2);
        pairs.Should().Contain(p => IsPair(p, a, b));
        pairs.Should().Contain(p => IsPair(p, a, c));
        pairs[0].Similarity.Should().BeGreaterThanOrEqualTo(pairs[1].Similarity);
    }

    [Fact]
    public void Tier_one_finds_a_candidate_beyond_the_twentieth_neighbour()
    {
        var corpus = new HandMadeDreamCorpus();
        var hub = corpus.AddArticle("Hub", "/A");
        var far = corpus.AddArticle("Far", "/B");
        corpus.Link(hub, far, similarity: 0.96, zFromA: 2, zFromB: 1);
        for (var i = 0; i < 24; i++)
        {
            var neighbour = corpus.AddArticle($"Near {i}", "/C");
            corpus.Link(hub, neighbour, similarity: 0.999 - i * 0.0001, zFromA: 3, zFromB: 1);
        }

        corpus.NeighboursOf(hub.Id, 20).Should().NotContain(n => n.Id == far.Id);
        var pairs = new PairCandidateSource().PairsOf(corpus, DreamTestRuns.Context(), tier: 1);

        pairs.Should().HaveCount(25);
        pairs.Should().Contain(p => IsPair(p, hub, far));
    }

    [Fact]
    public void Tier_two_takes_the_three_nearest_with_z_at_least_three()
    {
        var corpus = new HandMadeDreamCorpus();
        var hub = corpus.AddArticle("Hub", "/A");
        var best = corpus.AddArticle("Best", "/A");
        var second = corpus.AddArticle("Second", "/A");
        var third = corpus.AddArticle("Third", "/A");
        var fourth = corpus.AddArticle("Fourth", "/A");
        corpus.Link(hub, best, similarity: 0.99, zFromA: 5, zFromB: 1);
        corpus.Link(hub, second, similarity: 0.95, zFromA: 3.5, zFromB: 1);
        corpus.Link(hub, third, similarity: 0.94, zFromA: 3.2, zFromB: 1);
        corpus.Link(hub, fourth, similarity: 0.93, zFromA: 3.1, zFromB: 1);

        var pairs = new PairCandidateSource().PairsOf(corpus, DreamTestRuns.Context(), tier: 2);

        pairs.Should().HaveCount(3);
        pairs.Should().Contain(p => IsPair(p, hub, best));
        pairs.Should().Contain(p => IsPair(p, hub, second));
        pairs.Should().Contain(p => IsPair(p, hub, third));
        pairs.Should().NotContain(p => IsPair(p, hub, fourth));
    }

    [Fact]
    public void Tier_three_takes_the_five_nearest_with_z_at_least_two()
    {
        var corpus = new HandMadeDreamCorpus();
        var hub = corpus.AddArticle("Hub", "/A");
        var n1 = corpus.AddArticle("N1", "/A");
        var n2 = corpus.AddArticle("N2", "/A");
        var n3 = corpus.AddArticle("N3", "/A");
        var n4 = corpus.AddArticle("N4", "/A");
        var lowZ = corpus.AddArticle("LowZ", "/A");
        var tooFar = corpus.AddArticle("TooFar", "/A");
        corpus.Link(hub, n1, similarity: 0.96, zFromA: 5, zFromB: 1);
        corpus.Link(hub, n2, similarity: 0.95, zFromA: 4, zFromB: 1);
        corpus.Link(hub, n3, similarity: 0.94, zFromA: 3, zFromB: 1);
        corpus.Link(hub, n4, similarity: 0.93, zFromA: 2.5, zFromB: 1);
        corpus.Link(hub, lowZ, similarity: 0.92, zFromA: 1.9, zFromB: 1);
        corpus.Link(hub, tooFar, similarity: 0.91, zFromA: 5, zFromB: 1);

        var pairs = new PairCandidateSource().PairsOf(corpus, DreamTestRuns.Context(), tier: 3);

        pairs.Should().HaveCount(4);
        pairs.Should().Contain(p => IsPair(p, hub, n1));
        pairs.Should().Contain(p => IsPair(p, hub, n2));
        pairs.Should().Contain(p => IsPair(p, hub, n3));
        pairs.Should().Contain(p => IsPair(p, hub, n4));
        pairs.Should().NotContain(p => IsPair(p, hub, lowZ));
        pairs.Should().NotContain(p => IsPair(p, hub, tooFar));
    }

    [Fact]
    public void Every_unordered_pair_appears_once_with_the_smaller_id_first()
    {
        var corpus = new HandMadeDreamCorpus();
        var a = corpus.AddArticle("A", "/A");
        var b = corpus.AddArticle("B", "/A");
        var c = corpus.AddArticle("C", "/A");
        corpus.Link(a, b, similarity: 0.99, zFromA: 5, zFromB: 5);
        corpus.Link(a, c, similarity: 0.99, zFromA: 5, zFromB: 5);
        corpus.Link(b, c, similarity: 0.99, zFromA: 5, zFromB: 5);

        var pairs = new PairCandidateSource().PairsOf(corpus, DreamTestRuns.Context(), tier: 1);

        pairs.Should().HaveCount(3);
        pairs.Should().OnlyContain(p =>
            string.CompareOrdinal(p.AId.ToString("D"), p.BId.ToString("D")) < 0);
    }

    [Fact]
    public void A_tier_outside_the_configured_range_gives_no_pairs()
    {
        var corpus = new HandMadeDreamCorpus();
        var a = corpus.AddArticle("A", "/A");
        var b = corpus.AddArticle("B", "/A");
        corpus.Link(a, b, similarity: 0.99, zFromA: 5);

        var source = new PairCandidateSource();

        source.PairsOf(corpus, DreamTestRuns.Context(), tier: 0).Should().BeEmpty();
        source.PairsOf(corpus, DreamTestRuns.Context(), tier: 4).Should().BeEmpty();
    }

    private static List<DreamArticle> AddNeighbours(
        HandMadeDreamCorpus corpus, DreamArticle article, string folder, int count, double z, double similarity = 0.9)
    {
        var neighbours = new List<DreamArticle>();
        for (var i = 0; i < count; i++)
        {
            var neighbour = corpus.AddArticle($"Neighbour {folder} {i}", folder);
            corpus.Link(article, neighbour, similarity, zFromA: z, zFromB: 1);
            neighbours.Add(neighbour);
        }
        return neighbours;
    }

    private static bool IsPair(DreamPairCandidate pair, DreamArticle a, DreamArticle b) =>
        (pair.AId == a.Id && pair.BId == b.Id) || (pair.AId == b.Id && pair.BId == a.Id);
}
