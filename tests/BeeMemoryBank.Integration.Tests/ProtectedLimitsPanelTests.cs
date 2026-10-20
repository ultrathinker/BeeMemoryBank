using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-320: under the editor of a password-protected article the page lists what such an article cannot do, instead of simply not drawing the attachments
/// block. The list is one partial; the editor of an existing protected article shows it, the editor of a new article shows it as soon as a password is typed,
/// and an article that is not protected never shows it. Each line states a rule the node keeps (the refusals themselves are tested where they live:
/// MediaService, CommentService, SearchService, the MCP read tools, ZipExportService, VersionEndpoints); this is the guard that the page keeps saying so.
/// </summary>
public class ProtectedLimitsPanelTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string Pages = Path.Combine(RepoRoot, "server", "BeeMemoryBank.Web", "Pages");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx")) || File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(parts.Prepend(Pages).ToArray()));

    [Theory]
    [InlineData("No attachments or pictures")]
    [InlineData("Comments are allowed, but sealed with the password")]
    [InlineData("Search does not look into the text")]
    [InlineData("AI assistants and MCP tools cannot read or change it")]
    [InlineData("Export leaves the text out")]
    [InlineData("Earlier versions are not shown")]
    [InlineData("No draft autosave")]
    [InlineData("The password cannot be recovered")]
    public void TheListSaysEveryLimit(string limit)
    {
        Read("Shared", "_ProtectedLimits.cshtml").Should().Contain(limit);
    }

    [Fact]
    public void TheEditorShowsTheListForAProtectedArticle_InPlaceOfTheAttachments_AndForANewOneOnlyWhenAPasswordIsTyped()
    {
        var edit = Read("Article", "Edit.cshtml");
        var protectedBranch = edit.IndexOf("@* BMB-320: no attachments block here", StringComparison.Ordinal);
        protectedBranch.Should().BeGreaterThan(0, "the protected branch under the editor has the list");
        edit.Should().Contain("<partial name=\"_ProtectedLimits\" />");
        edit.Split("<partial name=\"_ProtectedLimits\" />").Length.Should().Be(3, "once for a protected article, once (hidden) for a new one");
        edit.Should().Contain("id=\"create-limits\"");
        var attachments = edit.IndexOf("id=\"attachments-block\"", StringComparison.Ordinal);
        attachments.Should().BeGreaterThan(protectedBranch, "the attachments block belongs to the branch of an article that is not protected");

        var js = File.ReadAllText(Path.Combine(RepoRoot, "server", "BeeMemoryBank.Web", "wwwroot", "js", "pages", "article-edit.js"));
        js.Should().Contain("getElementById('create-limits')");
    }
}
