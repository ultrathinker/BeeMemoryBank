using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>
/// The parity part 1 operations on a real vault, through the same core services the pages call:
/// folder rename/move/delete (subfolders go, articles to the root), article rename/move, tags
/// (set, list, rename, merge, delete, articles by tag) and comments (add, read, delete).
/// </summary>
public class ParityOpsTests
{
    [Fact]
    public async Task Folder_Rename_KeepsChildrenAndNotes()
    {
        var (test, _) = await TestVault.CreateAsync("folder-rename");
        await using var _2 = test;
        var notes = test.Notes;
        await notes.CreateFolderAsync("/", "Work");
        await notes.CreateFolderAsync("/Work", "Sub");
        var id = await notes.SaveAsync(null, "Inside", "/Work/Sub", "x");

        await notes.RenameFolderAsync("/Work", "Projects");

        (await notes.ListFolderAsync("/")).Where(i => i.IsFolder).Select(i => i.Name).Should().Equal("Projects");
        (await notes.ListFolderAsync("/Projects")).Select(i => i.Name).Should().Equal("Sub");
        (await notes.GetAsync(id))!.Path.Should().Be("/Projects/Sub");
    }

    [Fact]
    public async Task Folder_Move_CarriesTheWholeSubtree()
    {
        var (test, _) = await TestVault.CreateAsync("folder-move");
        await using var _2 = test;
        var notes = test.Notes;
        await notes.CreateFolderAsync("/", "Work");
        await notes.CreateFolderAsync("/Work", "Sub");
        await notes.CreateFolderAsync("/", "Archive");
        var id = await notes.SaveAsync(null, "Inside", "/Work/Sub", "x");

        await notes.MoveFolderAsync("/Work", "/Archive");

        (await notes.ListFolderAsync("/Archive")).Select(i => i.Name).Should().Equal("Work");
        (await notes.GetAsync(id))!.Path.Should().Be("/Archive/Work/Sub");
    }

    [Fact]
    public async Task Folder_Delete_TakesSubfolders_AndMovesArticlesToRoot()
    {
        var (test, _) = await TestVault.CreateAsync("folder-delete");
        await using var _2 = test;
        var notes = test.Notes;
        await notes.CreateFolderAsync("/", "Work");
        await notes.CreateFolderAsync("/Work", "Sub");
        var id = await notes.SaveAsync(null, "Inside", "/Work/Sub", "x");

        await notes.DeleteFolderAsync("/Work");

        (await notes.ListFolderAsync("/")).Select(i => i.Name).Should().Equal("Inside");
        (await notes.GetAsync(id))!.Path.Should().Be("/");
    }

    [Fact]
    public async Task Article_RenameAndMove_GoThroughTheCore()
    {
        var (test, _) = await TestVault.CreateAsync("article-rename-move");
        await using var _2 = test;
        var notes = test.Notes;
        var id = await notes.SaveAsync(null, "Plan", "/", "x");
        await notes.CreateFolderAsync("/", "Work");

        await notes.RenameArticleAsync(id, "Plan B");
        await notes.MoveArticleAsync(id, "/Work");

        var note = await notes.GetAsync(id);
        note!.Title.Should().Be("Plan B");
        note.Path.Should().Be("/Work");
    }

    [Fact]
    public async Task Tags_SetListRenameMergeDelete_AndArticlesByTag()
    {
        var (test, _) = await TestVault.CreateAsync("tags");
        await using var _2 = test;
        var notes = test.Notes;
        var a = await notes.SaveAsync(null, "A", "/", "x", tags: ["travel", "plans"]);
        var b = await notes.SaveAsync(null, "B", "/", "y", tags: ["travel"]);

        (await notes.TagsOfAsync(a)).Should().BeEquivalentTo(["travel", "plans"]);
        (await notes.AllTagsAsync(null)).Select(t => t.Name).Should().BeEquivalentTo(["travel", "plans"]);
        (await notes.AllTagsAsync("trav")).Select(t => t.Name).Should().Equal("travel");
        (await notes.ArticlesByTagAsync("travel")).Select(x => x.Id).Should().BeEquivalentTo([a, b]);

        await notes.SetTagsAsync(a, ["plans"]);
        (await notes.TagsOfAsync(a)).Should().Equal(["plans"]);

        await notes.RenameTagAsync("plans", "todo");
        (await notes.TagsOfAsync(a)).Should().Equal(["todo"]);

        await notes.MergeTagAsync("todo", "travel");
        (await notes.TagsOfAsync(a)).Should().Equal(["travel"]);
        (await notes.AllTagsAsync(null)).Select(t => t.Name).Should().Equal(["travel"]);

        await notes.DeleteTagAsync("travel");
        (await notes.TagsOfAsync(a)).Should().BeEmpty();
        (await notes.TagsOfAsync(b)).Should().BeEmpty();
    }

    [Fact]
    public async Task Comments_AddReadDelete_ThroughCommentService()
    {
        var (test, _) = await TestVault.CreateAsync("comments");
        await using var _2 = test;
        var notes = test.Notes;
        var id = await notes.SaveAsync(null, "Note", "/", "x");

        await notes.AddCommentAsync(id, "first thought", null);
        await notes.AddCommentAsync(id, "second thought", null);

        var comments = await notes.CommentsAsync(id, null);
        comments.Select(c => c.Text).Should().Equal(["first thought", "second thought"]);
        comments.Should().OnlyContain(c => !c.Locked);

        await notes.DeleteCommentAsync(comments[0].Id);
        (await notes.CommentsAsync(id, null)).Select(c => c.Text).Should().Equal(["second thought"]);
    }
}
