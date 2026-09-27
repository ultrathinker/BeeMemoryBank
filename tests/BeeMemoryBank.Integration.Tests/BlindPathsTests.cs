using BeeMemoryBank.Api.Services.BlindBackup;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// A repository (or a one-off copy) must lie outside the node's data folder: the staging cleanup
/// and the wipe delete there, and a backup placed inside would die with them.
/// </summary>
public class BlindPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb_blind_paths_" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "data");

    public BlindPathsTests() => Directory.CreateDirectory(Path.Combine(Data, "blind", "stage"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private BlindBackupSettings Folder(string repo) => new() { RepoFolder = repo, ResticPassword = "pw" };

    [Theory]
    [InlineData("")]                           // the data folder itself
    [InlineData("blind/stage")]                // wiped at startup and after every backup
    [InlineData("blind/restic-cache")]         // wiped by "Disconnect and wipe"
    [InlineData("blind/stage/repo")]
    [InlineData("media")]
    public void RepositoryInsideTheDataFolder_IsRejected(string sub)
    {
        var repo = Path.Combine(Data, sub);
        var (ok, problem) = Folder(repo).Validate(Data);
        ok.Should().BeFalse($"{repo} would be deleted by the node's own cleanup");
        problem.Should().Contain("overlaps");
    }

    [Fact]
    public void RepositoryAroundTheDataFolder_IsRejected()
    {
        Folder(_root).Validate(Data).Ok.Should().BeFalse("a repository that contains the data folder overlaps it too");
    }

    [Fact]
    public void RepositoryBesideTheDataFolder_IsAccepted()
    {
        Folder(Path.Combine(_root, "backups", "restic")).Validate(Data).Ok.Should().BeTrue();
        Folder(Data + "-backups").Validate(Data).Ok.Should().BeTrue("a shared name prefix is not an overlap");
    }

    [Fact]
    public void RelativeRepository_IsRejected()
    {
        Folder("backups/restic").Validate(Data).Problem.Should().Contain("absolute");
    }

    [Fact]
    public void SymlinkIntoTheDataFolder_IsRejected()
    {
        var link = Path.Combine(_root, "backups-link");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(Data, "blind", "stage"));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return; // Windows without the symlink privilege; the container (Linux) always can
        }

        var (ok, problem) = Folder(Path.Combine(link, "restic")).Validate(Data);
        ok.Should().BeFalse("the link resolves into the staging folder, which the node empties");
        problem.Should().Contain("overlaps");
    }

    private bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false; // Windows without the symlink privilege; the container (Linux) always can
        }
    }

    /// <summary>
    /// A GUARD, not a regression proof: the implementation before 7f82c6a9 already rejected this
    /// on Linux/.NET 10 (FileInfo.Exists is true for a dangling link there and ResolveLinkTarget
    /// returns the missing final path), so reverting that commit leaves this green. It pins the
    /// behaviour for the current resolver, which reads the link itself instead of relying on it.
    /// </summary>
    [Fact]
    public void DanglingSymlinkIntoTheDataFolder_IsRejected()
    {
        var link = Path.Combine(_root, "nas-mount");
        // Points into the data folder at a path that does not exist yet — created later by a
        // mount or by restic init, and then the repository lives in the node volume.
        if (!TryLink(link, Path.Combine(Data, "blind", "not-created-yet"))) return;

        var (ok, problem) = Folder(Path.Combine(link, "restic")).Validate(Data);
        ok.Should().BeFalse("a dangling link is judged by where it points, not by its own name");
        problem.Should().Contain("overlaps");
    }

    [Fact]
    public void SymlinkLoop_IsRejected()
    {
        var a = Path.Combine(_root, "loop-a");
        var b = Path.Combine(_root, "loop-b");
        if (!TryLink(a, b) || !TryLink(b, a)) return;

        Folder(Path.Combine(a, "restic")).Validate(Data).Problem.Should().Contain("cannot be resolved");
    }
}
