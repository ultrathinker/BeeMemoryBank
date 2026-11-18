using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>
/// The parity part 2 operations on a real vault, through the same core services the pages call:
/// protect / unprotect / change-passphrase (body and comments), related articles, picture upload
/// and resolution, the search's tag filter and recent list, and the editor's pure text rules.
/// </summary>
public class ParityP2Tests
{
    [Fact]
    public async Task Protect_Unprotect_AndChangePassphrase_RoundTrip()
    {
        var (test, _) = await TestVault.CreateAsync("protect");
        await using var _2 = test;
        var notes = test.Notes;
        var id = await notes.SaveAsync(null, "Secret", "/", "the body");
        await notes.AddCommentAsync(id, "a comment", null);

        // A plain note gains a password: the body seals and the comment seals under it.
        await notes.SaveAsync(id, "Secret", "/", "the body", null, null, new ProtectSpec(true, "pass-1234", "my hint"));
        var sealedNote = await notes.GetAsync(id);
        sealedNote!.IsProtected.Should().BeTrue();
        sealedNote.ProtectionHint.Should().Be("my hint");
        NotesService.OpenProtected(sealedNote.Body, "pass-1234").Should().Be("the body");
        (await notes.CommentsAsync(id, null)).Should().OnlyContain(c => c.Locked);
        (await notes.CommentsAsync(id, "pass-1234")).Select(c => c.Text).Should().Equal(["a comment"]);

        // A new passphrase re-wraps the body and re-seals the comment.
        await notes.SaveAsync(id, "Secret", "/", "the body v2", "pass-1234", null, new ProtectSpec(true, "pass-5678", null));
        var changed = await notes.GetAsync(id);
        NotesService.OpenProtected(changed!.Body, "pass-5678").Should().Be("the body v2");
        (await notes.CommentsAsync(id, "pass-5678")).Select(c => c.Text).Should().Equal(["a comment"]);

        // The switch off takes the protection off the body and the comment.
        await notes.SaveAsync(id, "Secret", "/", "the body v3", "pass-5678", null, new ProtectSpec(false, null, null));
        var open = await notes.GetAsync(id);
        open!.IsProtected.Should().BeFalse();
        open.Body.Should().Be("the body v3");
        (await notes.CommentsAsync(id, null)).Select(c => c.Text).Should().Equal(["a comment"]);
    }

    [Fact]
    public async Task NewNote_ProtectedAtCreate_CarriesOnlyCiphertext()
    {
        var (test, _) = await TestVault.CreateAsync("protect-create");
        await using var _2 = test;
        var notes = test.Notes;
        var id = await notes.SaveAsync(null, "Born sealed", "/", "hidden text", null, null, new ProtectSpec(true, "pass-1234", "hint"));
        var note = await notes.GetAsync(id);
        note!.IsProtected.Should().BeTrue();
        NotesService.OpenProtected(note.Body, "pass-1234").Should().Be("hidden text");
    }

    [Fact]
    public async Task UnprotectAsync_FromTheNoteMenu_OpensBodyAndComments()
    {
        var (test, _) = await TestVault.CreateAsync("unprotect-menu");
        await using var _2 = test;
        var notes = test.Notes;
        var id = await notes.SaveAsync(null, "N", "/", "text", null, null, new ProtectSpec(true, "pass-1234", null));
        await notes.AddCommentAsync(id, "c", "pass-1234");

        await notes.UnprotectAsync(id, "pass-1234");

        (await notes.GetAsync(id))!.IsProtected.Should().BeFalse();
        (await notes.CommentsAsync(id, null)).Select(c => c.Text).Should().Equal(["c"]);
    }

    [Fact]
    public async Task Related_ListsNotesSharingATag()
    {
        var (test, _) = await TestVault.CreateAsync("related");
        await using var _2 = test;
        var notes = test.Notes;
        var a = await notes.SaveAsync(null, "A", "/", "x", tags: ["travel"]);
        var b = await notes.SaveAsync(null, "B", "/", "y", tags: ["travel"]);
        await notes.SaveAsync(null, "C", "/", "z", tags: ["cooking"]);

        var related = await notes.RelatedAsync(a);
        related.Select(r => r.Id).Should().Contain(b);
        related.Select(r => r.Id).Should().NotContain(a);
    }

    [Fact]
    public async Task Images_UploadResolve_AndUnavailableFallback()
    {
        var (test, _) = await TestVault.CreateAsync("images");
        await using var _2 = test;
        var notes = test.Notes;
        var id = await notes.SaveAsync(null, "With picture", "/", "before");
        var bytes = new byte[] { 1, 2, 3, 4 };
        var mediaId = await notes.AddImageAsync(id, "p.png", "image/png", bytes);
        var body = EditorText.InsertImageMarkdown("before", "before".Length, mediaId);
        await notes.SaveAsync(id, "With picture", "/", body, null, null);

        var resolved = await notes.ResolveImagesAsync(body, "[unavailable {0}]");
        resolved.Should().Contain("data:image/png;base64,");
        resolved.Should().NotContain("/api/media/");

        // A reference to a picture that is not there becomes the unavailable line.
        var missing = await notes.ResolveImagesAsync("![alt](/api/media/00000000-0000-0000-0000-000000000000)", "[unavailable {0}]");
        missing.Should().Be("*[unavailable alt]*");
    }

    [Fact]
    public async Task Search_Recent_TagFilter_AndQueryIntersect()
    {
        var (test, _) = await TestVault.CreateAsync("search-filter");
        await using var _2 = test;
        var notes = test.Notes;
        var a = await notes.SaveAsync(null, "Alpha travel", "/", "x", tags: ["travel"]);
        var b = await notes.SaveAsync(null, "Beta travel", "/", "y", tags: ["travel"]);
        var c = await notes.SaveAsync(null, "Gamma cooking", "/", "z", tags: ["cooking"]);

        // Empty query, no tag: the recent list holds all three.
        (await notes.SearchFilteredAsync("", false, null)).Select(i => i.NoteId).Should().BeEquivalentTo([a, b, c]);
        // Tag only: just the travel notes.
        (await notes.SearchFilteredAsync("", false, "travel")).Select(i => i.NoteId).Should().BeEquivalentTo([a, b]);
        // Query + tag: the intersection.
        (await notes.SearchFilteredAsync("Alpha", false, "travel")).Select(i => i.NoteId).Should().Equal([a]);
        // Query that matches a cooking note, narrowed by travel: empty.
        (await notes.SearchFilteredAsync("Gamma", false, "travel")).Should().BeEmpty();
    }

    [Fact]
    public void EditorText_Snippets_AndImageAlt()
    {
        EditorText.InsertSnippet("ab", 1, "X").Should().Be("aXb");
        EditorText.InsertSnippet("ab", 99, "X").Should().Be("abX");
        EditorText.NextImageAlt("no images").Should().Be("image-000");
        EditorText.NextImageAlt("![image-000](x) ![image-002](y)").Should().Be("image-003");
        var id = Guid.NewGuid();
        var inserted = EditorText.InsertImageMarkdown("t", 1, id);
        inserted.Should().Contain($"![image-000](/api/media/{id})");
    }
}
