using BeeMemoryBank.Api.Dream;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Package D1 (plan 3.6): tag variants, unused tags, empty folders and test leftovers. Pure detector logic over a
/// hand-written corpus; no node, no database and no embedding model is involved.
/// </summary>
public sealed class DreamTagAndFolderDetectorTests
{
    // ── Tag variants ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Tags_that_normalize_to_one_name_merge_into_the_most_used_one()
    {
        var corpus = new HandMadeDreamCorpus()
            .AddTag("How To", 1)
            .AddTag("how-to", 2)
            .AddTag("howto", 5)
            .AddTag("unrelated", 9);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new TagVariantsDetector(), corpus, DreamTestRuns.Context());

        var draft = drafts.Should().ContainSingle().Subject;
        draft.Kind.Should().Be(DreamAdviceKind.MergeTags);
        draft.Source.Should().Be(DreamAdviceSource.Rule);
        draft.ApplyMode.Should().Be(DreamApplyMode.Code);
        draft.Score.Should().Be(85);
        draft.Action.Type.Should().Be(DreamActionType.MergeTags);
        draft.Action.Target.Should().Be("howto");
        draft.Action.Sources.Should().Equal("How To", "how-to");
        draft.Preconditions.Should().HaveCount(3)
            .And.OnlyContain(p => p.Type == DreamPreconditionType.TagExists);
        draft.Objects.Should().HaveCount(3);
        draft.Objects.Should().Contain(o => o.Name == "howto" && o.Role == DreamObjectRole.Target);
    }

    [Fact]
    public async Task A_tie_between_equally_used_variants_is_broken_by_the_ordinal_name()
    {
        var corpus = new HandMadeDreamCorpus()
            .AddTag("beta", 3)
            .AddTag("Beta", 3);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new TagVariantsDetector(), corpus, DreamTestRuns.Context());

        var draft = drafts.Should().ContainSingle().Subject;
        draft.Action.Target.Should().Be("Beta");
        draft.Action.Sources.Should().Equal("beta");
    }

    [Fact]
    public async Task Different_words_that_only_look_alike_are_not_variants()
    {
        // "tech-debt" folds to "techdebt" and "technical debt" to "technicaldebt": the rule of 3.6 removes
        // separators, it does not expand abbreviations.
        var corpus = new HandMadeDreamCorpus()
            .AddTag("tech-debt", 4)
            .AddTag("technical debt", 2);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new TagVariantsDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_single_tag_is_never_a_variant()
    {
        var corpus = new HandMadeDreamCorpus().AddTag("howto", 4);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new TagVariantsDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().BeEmpty();
    }

    // ── Unused tags ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task All_unused_tags_become_one_advice_that_requires_them_to_stay_unused()
    {
        var corpus = new HandMadeDreamCorpus()
            .AddTag("unused-b", 0)
            .AddTag("used", 7)
            .AddTag("unused-a", 0);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new UnusedTagsDetector(), corpus, DreamTestRuns.Context());

        var draft = drafts.Should().ContainSingle().Subject;
        draft.Kind.Should().Be(DreamAdviceKind.DeleteUnusedTags);
        draft.Score.Should().Be(60);
        draft.ApplyMode.Should().Be(DreamApplyMode.Code);
        draft.Action.Type.Should().Be(DreamActionType.DeleteTags);
        draft.Action.Names.Should().Equal("unused-a", "unused-b");
        draft.Preconditions.Should().HaveCount(2)
            .And.OnlyContain(p => p.Type == DreamPreconditionType.TagUnused);
        draft.Objects.Should().HaveCount(2);
    }

    [Fact]
    public async Task At_most_fifty_unused_tag_names_go_into_one_advice()
    {
        var corpus = new HandMadeDreamCorpus();
        for (var i = 0; i < 51; i++) corpus.AddTag($"unused-{i:00}", 0);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new UnusedTagsDetector(), corpus, DreamTestRuns.Context());

        var draft = drafts.Should().ContainSingle().Subject;
        draft.Action.Names.Should().HaveCount(50);
        draft.Action.Names![0].Should().Be("unused-00");
        draft.Action.Names![49].Should().Be("unused-49");
        draft.Evidence.Should().Contain(e => e.Type == DreamEvidenceType.Count && e.Value == 51);
    }

    [Fact]
    public async Task Without_unused_tags_there_is_no_advice()
    {
        var corpus = new HandMadeDreamCorpus().AddTag("used", 1);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new UnusedTagsDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().BeEmpty();
    }

    // ── Empty folders ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_leaf_folder_without_articles_is_proposed_for_deletion()
    {
        var id = Guid.NewGuid();
        var corpus = new HandMadeDreamCorpus().AddFolder("/Old", id: id);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new EmptyFolderDetector(), corpus, DreamTestRuns.Context());

        var draft = drafts.Should().ContainSingle().Subject;
        draft.Kind.Should().Be(DreamAdviceKind.DeleteEmptyFolder);
        draft.Score.Should().Be(55);
        draft.Action.Type.Should().Be(DreamActionType.DeleteFolder);
        draft.Action.Path.Should().Be("/Old");
        draft.Action.FolderId.Should().Be(id);
        draft.Preconditions.Should().Contain(p => p.Type == DreamPreconditionType.FolderEmpty && p.Path == "/Old");
        draft.Preconditions.Should().Contain(p => p.Type == DreamPreconditionType.FolderExists && p.Path == "/Old");
    }

    [Fact]
    public async Task An_empty_folder_with_a_test_name_scores_seventy()
    {
        var corpus = new HandMadeDreamCorpus().AddFolder("/_Tmp");

        var drafts = await DreamDetectorRunner.CollectAsync(
            new EmptyFolderDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().ContainSingle().Subject.Score.Should().Be(70);
    }

    [Fact]
    public async Task A_folder_with_articles_is_not_empty()
    {
        var corpus = new HandMadeDreamCorpus().AddFolder("/Work", directArticles: 2, subtreeArticles: 2);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new EmptyFolderDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_folder_with_a_subfolder_is_not_empty_even_when_both_hold_no_articles()
    {
        var corpus = new HandMadeDreamCorpus()
            .AddFolder("/A")
            .AddFolder("/A/B");

        var drafts = await DreamDetectorRunner.CollectAsync(
            new EmptyFolderDetector(), corpus, DreamTestRuns.Context());

        var draft = drafts.Should().ContainSingle().Subject;
        draft.Action.Path.Should().Be("/A/B");
    }

    [Fact]
    public async Task A_system_folder_is_never_proposed_for_deletion()
    {
        var corpus = new HandMadeDreamCorpus().AddFolder("/_Drafts", isSystem: true);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new EmptyFolderDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task The_root_folder_is_never_proposed_for_deletion()
    {
        var corpus = new HandMadeDreamCorpus().AddFolder("/");

        var drafts = await DreamDetectorRunner.CollectAsync(
            new EmptyFolderDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().BeEmpty();
    }

    // ── Test leftovers ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/test")]
    [InlineData("/Test")]
    [InlineData("/tests")]
    [InlineData("/_tmp")]
    [InlineData("/_TEMP")]
    [InlineData("/Archive/tests")]
    public async Task Test_named_folders_on_the_first_two_levels_ask_for_a_review(string path)
    {
        var corpus = new HandMadeDreamCorpus().AddFolder(path, subtreeArticles: 3);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new TestLeftoversDetector(), corpus, DreamTestRuns.Context());

        var draft = drafts.Should().ContainSingle().Subject;
        draft.Kind.Should().Be(DreamAdviceKind.TestLeftovers);
        draft.Source.Should().Be(DreamAdviceSource.Rule);
        draft.ApplyMode.Should().Be(DreamApplyMode.Prompt);
        draft.Score.Should().Be(50);
        draft.Action.Type.Should().Be(DreamActionType.ReviewFolder);
        draft.Action.Path.Should().Be(path);
    }

    [Fact]
    public async Task A_test_named_folder_below_the_second_level_is_not_a_test_leftover()
    {
        var corpus = new HandMadeDreamCorpus().AddFolder("/Archive/Old/test", subtreeArticles: 3);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new TestLeftoversDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_name_that_only_starts_with_test_is_not_a_test_folder()
    {
        var corpus = new HandMadeDreamCorpus().AddFolder("/Testing", subtreeArticles: 2);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new TestLeftoversDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task The_largest_allowed_test_folder_is_thirty_articles()
    {
        var corpus = new HandMadeDreamCorpus()
            .AddFolder("/test", subtreeArticles: 30)
            .AddFolder("/tests", subtreeArticles: 31);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new TestLeftoversDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().ContainSingle().Subject.Action.Path.Should().Be("/test");
    }

    [Fact]
    public async Task An_empty_test_folder_is_the_empty_folder_detectors_advice_not_a_test_leftover()
    {
        var corpus = new HandMadeDreamCorpus().AddFolder("/test");

        var drafts = await DreamDetectorRunner.CollectAsync(
            new TestLeftoversDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task The_russian_spellings_of_test_are_test_folder_names()
    {
        // Built from escapes so this source file stays plain ASCII (public-repository hygiene).
        var corpus = new HandMadeDreamCorpus().AddFolder("/\u0442\u0435\u0441\u0442", subtreeArticles: 1);

        var drafts = await DreamDetectorRunner.CollectAsync(
            new TestLeftoversDetector(), corpus, DreamTestRuns.Context());

        drafts.Should().ContainSingle();
    }
}
