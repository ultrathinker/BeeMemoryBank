using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.FullIos.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>The note pages' operations on a real vault: folders and notes, reading, saving, the trash, both searches, protected notes.</summary>
public class NotesServiceTests
{
    [Fact]
    public async Task AFolder_ListsItsSubFoldersFirst_ThenItsOwnNotes_Sorted()
    {
        var (test, _) = await TestVault.CreateAsync("list");
        await using var _2 = test;
        var notes = test.Notes;
        await notes.CreateFolderAsync("/", "Work");
        await notes.CreateFolderAsync("/", "_Inbox");
        await notes.SaveAsync(null, "zebra", "/", "z");
        await notes.SaveAsync(null, "Apple", "/", "a");
        await notes.SaveAsync(null, "Deep note", "/Work", "d");

        var top = await notes.ListFolderAsync("/");

        top.Where(i => i.IsFolder).Select(i => i.Name).Should().Equal("_Inbox", "Work");
        top.Where(i => !i.IsFolder).Select(i => i.Name).Should().Equal("Apple", "zebra");
        top.Select(i => i.IsFolder).Should().BeInDescendingOrder("folders come first");
        (await notes.ListFolderAsync("/Work")).Select(i => i.Name).Should().Equal("Deep note");
    }

    [Fact]
    public async Task ANote_IsSaved_ReadBack_AndEdited()
    {
        var (test, _) = await TestVault.CreateAsync("edit");
        await using var _2 = test;
        var notes = test.Notes;

        var id = await notes.SaveAsync(null, "  Plan ", "/Projects/", "# Plan\nfirst");
        var note = await notes.GetAsync(id);
        note!.Title.Should().Be("Plan");
        note.Path.Should().Be("/Projects");
        note.Body.Should().Be("# Plan\nfirst");
        note.IsProtected.Should().BeFalse();

        await notes.SaveAsync(id, "Plan B", "/Projects", "second");
        (await notes.GetAsync(id))!.Should().Match<NoteView>(n => n.Title == "Plan B" && n.Body == "second");
    }

    [Fact]
    public async Task EveryWrite_IsASignedEventForTheOtherNodes()
    {
        var (test, _) = await TestVault.CreateAsync("events");
        await using var _2 = test;
        using var scope = test.Services.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
        var before = await events.GetMaxLamportTimestampAsync();

        var id = await test.Notes.SaveAsync(null, "Synced", "/", "body");
        await test.Notes.SaveAsync(id, "Synced", "/", "body 2");
        await test.Notes.DeleteAsync(id);

        (await events.GetMaxLamportTimestampAsync()).Should().BeGreaterThan(before + 2, "create, update and delete are each an event");
    }

    [Fact]
    public async Task Delete_MovesTheNoteToTheTrash_AndRestore_BringsItBackAsACopy()
    {
        var (test, _) = await TestVault.CreateAsync("trash");
        await using var _2 = test;
        var notes = test.Notes;
        var id = await notes.SaveAsync(null, "Old idea", "/Ideas", "keep me");

        await notes.DeleteAsync(id);

        (await notes.GetAsync(id)).Should().BeNull();
        (await notes.ListFolderAsync("/Ideas")).Should().BeEmpty();
        var trash = await notes.TrashAsync();
        trash.Should().ContainSingle(t => t.Id == id && t.Title == "Old idea" && t.Path == "/Ideas");

        var restored = await notes.RestoreAsync(id);
        var copy = await notes.GetAsync(restored);
        copy!.Title.Should().Be("[RESTORED] Old idea");
        copy.Body.Should().Be("keep me");
    }

    [Fact]
    public async Task Search_FindsTitles_AndWithText_TheTextToo()
    {
        var (test, _) = await TestVault.CreateAsync("search");
        await using var _2 = test;
        var notes = test.Notes;
        await notes.SaveAsync(null, "Holiday packing list", "/Lists", "passport, charger");
        await notes.SaveAsync(null, "Recipes", "/", "the secret is cardamom");

        (await notes.SearchAsync("packing", inText: false)).Select(i => i.Name).Should().Contain("Holiday packing list");
        (await notes.SearchAsync("cardamom", inText: false)).Should().BeEmpty("titles only: the text is encrypted");
        (await notes.SearchAsync("cardamom", inText: true)).Select(i => i.Name).Should().Equal("Recipes");
        (await notes.SearchAsync("   ", inText: true)).Should().BeEmpty();
    }

    [Fact]
    public async Task AProtectedNote_ShowsSealed_OpensWithItsPassphrase_AndIsSavedSealedAgain()
    {
        var (test, _) = await TestVault.CreateAsync("protected");
        await using var _2 = test;
        var notes = test.Notes;
        var id = await notes.SaveAsync(null, "Bank", "/", ProtectedContentCodec.Wrap("PIN 1234", "own-pass"));

        var note = await notes.GetAsync(id);
        note!.IsProtected.Should().BeTrue();
        note.Body.Should().NotContain("1234");
        NotesService.OpenProtected(note.Body, "wrong").Should().BeNull();
        NotesService.OpenProtected(note.Body, "own-pass").Should().Be("PIN 1234");

        await notes.SaveAsync(id, "Bank", "/", "PIN 5678", passphrase: "own-pass");
        var saved = await notes.GetAsync(id);
        saved!.Body.Should().NotContain("5678");
        NotesService.OpenProtected(saved.Body, "own-pass").Should().Be("PIN 5678");
    }

    [Theory]
    [InlineData("", "Notes")]
    [InlineData("/", "Notes")]
    [InlineData("/Work/Projects/", "Projects")]
    public void AFoldersTitle_IsItsLastSegment(string path, string title) => NotesService.FolderTitle(path).Should().Be(title);

    [Fact]
    public async Task AFolderName_WithASlash_IsRefused()
    {
        var (test, _) = await TestVault.CreateAsync("folder-slash");
        await using var _2 = test;
        var act = () => test.Notes.CreateFolderAsync("/", "a/b");
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
