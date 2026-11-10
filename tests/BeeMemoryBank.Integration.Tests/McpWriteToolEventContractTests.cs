using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The guard against the whole class of bug behind BMB-343: an MCP write tool that changes the vault and
/// leaves no event in the sync log. Such a change exists on one node only, is silently overwritten by the next
/// <c>article_update</c> from another node, and makes the node's state impossible to rebuild from events.
///
/// <para>
/// <see cref="RepositoryWriteGuardrailTests"/> (Core.Tests) already forbids a tool from calling a repository
/// write directly. It cannot see a write that hides inside a service method — which is exactly how
/// <c>bee_add_tags</c> did it (<c>ConceptTagService.AddToArticleAsync</c>, a bare tag-table write behind a
/// service-looking name). So this contract looks at the one thing every such bug has in common, whatever it
/// calls: what the tool call left in the event log.
/// </para>
///
/// <para>
/// Two halves, both without parsing any source:
/// (1) every registered tool is declared here, on purpose, either as touching nothing that replicates or as a
/// writer with the event type(s) it must leave — a NEW tool fails <see cref="EveryRegisteredTool_IsDeclaredOnPurpose"/>
/// until someone decides which; (2) every declared writer is called for real, on a node with a real event
/// logger, and must leave one of its events (<see cref="EveryWriterTool_LeavesItsEventInTheLog"/>).
/// </para>
/// </summary>
public class McpWriteToolEventContractTests : IAsyncLifetime
{
    private const string MinimalPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    /// <summary>Tools that change nothing that replicates: reads, searches, and per-session paging/settings.</summary>
    private static readonly HashSet<string> NoReplicatedWrite = new(StringComparer.Ordinal)
    {
        "bee_get_log", "bee_get_related", "bee_search_by_tag", "bee_list_tags", "bee_list_articles", "bee_get_article",
        "bee_get_tree", "bee_get_article_versions", "bee_get_article_version", "bee_get_article_diff", "bee_get_image",
        "bee_get_file", "bee_search", "bee_search_content", "bee_get_upload_script",
        // Per-connection response handling, kept in memory: nothing in the vault.
        "bee_set_max_tokens", "bee_continue",
    };

    /// <summary>What a writer does once its data is set up: the call itself, which is what the log is read around.</summary>
    private delegate Task<string> Call();

    private sealed record Writer(string[] Events, Func<McpToolRig, Task<Call>> Prepare);

    private static readonly Dictionary<string, Writer> Writers = new(StringComparer.Ordinal)
    {
        ["bee_save_article"] = new([EventTypes.ArticleCreate],
            r => Task.FromResult<Call>(() => r.WriteTools.SaveArticle("New", "/Contract/Save", "body", ["t"]))),

        ["bee_update_article"] = new([EventTypes.ArticleUpdate], async r =>
        {
            var a = await NewArticle(r);
            return () => r.WriteTools.UpdateArticle(a.Id, title: "Retitled");
        }),
        ["bee_append_to_article"] = new([EventTypes.ArticleUpdate], async r =>
        {
            var a = await NewArticle(r);
            return () => r.WriteTools.AppendToArticle(a.Id, "more");
        }),
        ["bee_prepend_to_article"] = new([EventTypes.ArticleUpdate], async r =>
        {
            var a = await NewArticle(r);
            return () => r.WriteTools.PrependToArticle(a.Id, "less");
        }),
        ["bee_replace_in_article"] = new([EventTypes.ArticleUpdate], async r =>
        {
            var a = await NewArticle(r);
            return () => r.WriteTools.ReplaceInArticle(a.Id, "body", "BODY");
        }),
        ["bee_delete_article"] = new([EventTypes.ArticleDelete], async r =>
        {
            var a = await NewArticle(r);
            return () => r.WriteTools.DeleteArticle(a.Id, confirm: true);
        }),
        ["bee_copy_to"] = new([EventTypes.ArticleCreate], async r =>
        {
            var a = await NewArticle(r);
            return () => r.WriteTools.CopyTo(a.Id, "/Contract/Copy");
        }),

        // The two that were missing: a tag change is an article_update carrying the tag set.
        ["bee_add_tags"] = new([EventTypes.ArticleUpdate], async r =>
        {
            var a = await NewArticle(r);
            return () => r.ConceptTools.AddTags(a.Id, ["added"]);
        }),
        ["bee_remove_tag"] = new([EventTypes.ArticleUpdate], async r =>
        {
            var a = await NewArticle(r);
            return () => r.ConceptTools.RemoveTag(a.Id, "kept");
        }),

        ["bee_rename_tag"] = new([EventTypes.ConceptTagRename], async r =>
        {
            await NewArticle(r);
            return () => r.ConceptTools.RenameTag("kept", "renamed");
        }),
        ["bee_merge_tags"] = new([EventTypes.ConceptTagMerge], async r =>
        {
            await r.ArticleService.CreateAsync("Merge", "/Contract", ["source-tag", "target-tag"], "body");
            return () => r.ConceptTools.MergeTags("source-tag", "target-tag");
        }),
        ["bee_delete_tag"] = new([EventTypes.ConceptTagDelete], async r =>
        {
            await NewArticle(r);
            return () => r.ConceptTools.DeleteTag("kept");
        }),

        ["bee_rename_folder"] = new([EventTypes.FolderRename], async r =>
        {
            await r.ArticleService.CreateAsync("In folder", "/Contract/Old", [], "body");
            return () => r.WriteTools.RenameFolder("/Contract/Old", "NewName");
        }),
        ["bee_move_folder"] = new([EventTypes.FolderRename], async r =>
        {
            await r.ArticleService.CreateAsync("In folder", "/Contract/Moving", [], "body");
            await r.ArticleService.CreateAsync("In target", "/Contract/Target", [], "body");
            return () => r.WriteTools.MoveFolder("/Contract/Moving", "/Contract/Target");
        }),
        ["bee_delete_folder"] = new([EventTypes.FolderDelete], async r =>
        {
            var a = await r.ArticleService.CreateAsync("Gone soon", "/Contract/Empty", [], "body");
            await r.ArticleService.DeleteAsync(a.Id);
            return () => r.WriteTools.DeleteFolder("/Contract/Empty", confirm: true);
        }),

        ["bee_save_media"] = new([EventTypes.MediaCreate], async r =>
        {
            var a = await NewArticle(r);
            return () => r.UploadTools.SaveMedia("pic.png", MinimalPngBase64, a.Id);
        }),
        ["bee_delete_file"] = new([EventTypes.MediaDelete], async r =>
        {
            var a = await NewArticle(r);
            var saved = JsonDocument.Parse(await r.UploadTools.SaveMedia("pic.png", MinimalPngBase64, a.Id)).RootElement;
            var mediaId = Guid.Parse(saved.GetProperty("mediaId").GetString()!);
            return () => r.UploadTools.DeleteFile(mediaId, confirm: true);
        }),
    };

    private static Task<Article> NewArticle(McpToolRig r) =>
        r.ArticleService.CreateAsync("Subject", "/Contract", ["kept"], "body text");

    private McpToolRig _rig = null!;

    public async Task InitializeAsync() => _rig = await McpToolRig.CreateAsync();

    public async Task DisposeAsync() => await _rig.DisposeAsync();

    // ── half one: nothing is left undeclared ────────────────────────────────

    [Fact]
    public void EveryRegisteredTool_IsDeclaredOnPurpose()
    {
        var registered = McpToolRegistryTests.AllToolNames();

        var undeclared = registered
            .Where(n => !NoReplicatedWrite.Contains(n) && !Writers.ContainsKey(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        undeclared.Should().BeEmpty(
            "a new MCP tool must be declared in this file: in NoReplicatedWrite if it changes nothing that replicates, " +
            "or in Writers with the event it must leave and a call that proves it. A write that leaves no event exists " +
            "on one node only and is silently erased by the next article_update from another (BMB-343)");

        var both = NoReplicatedWrite.Where(Writers.ContainsKey).ToList();
        both.Should().BeEmpty("a tool is either a writer or it is not");

        var stale = NoReplicatedWrite.Concat(Writers.Keys)
            .Where(n => !registered.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        stale.Should().BeEmpty("these names are declared here but no longer registered as tools");
    }

    // ── half two: every writer is run for real ──────────────────────────────

    public static IEnumerable<object[]> WriterNames() => Writers.Keys.OrderBy(n => n, StringComparer.Ordinal).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(WriterNames))]
    public async Task EveryWriterTool_LeavesItsEventInTheLog(string tool)
    {
        var writer = Writers[tool];
        var call = await writer.Prepare(_rig);
        var mark = await _rig.LogMarkAsync();

        var result = await call();

        result.Should().NotStartWith("Error").And.NotStartWith("Access denied").And.NotStartWith("Warning",
            "the call has to be a real change, not a refusal or a dry run, or the log proves nothing");
        var events = await _rig.EventsSinceAsync(mark);
        events.Select(e => e.EventType).Should().Contain(
            writer.Events,
            "{0} changed the vault, so the log must say so, or no other node will ever hear of it (BMB-343). It left: [{1}]",
            tool, string.Join(", ", events.Select(e => e.EventType)));
    }
}
