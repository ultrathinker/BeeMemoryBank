using BeeMemoryBank.AppPaths;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// Review release-b R1-2: every bmb command that opens a data directory passes the vault gate. It refuses while a
/// re-key runs, finishes an interrupted swap before it opens anything (instead of creating a fresh database in a D that
/// is between two renames), and holds the vault lease so the re-key refuses while the command runs.
/// </summary>
public sealed class CliVaultGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb-cli-gate-" + Guid.NewGuid().ToString("N"));
    private readonly string _d;

    public CliVaultGateTests() => _d = Path.Combine(_root, "vault");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task ACommand_RefusesWhileAReKeyRuns_AndCreatesNothing()
    {
        Directory.CreateDirectory(_d);
        using var running = RekeyLock.TryAcquire(_d)!;

        var open = () => CliServiceProvider.CreateAsync(_d);

        await open.Should().ThrowAsync<VaultInUseException>();
        File.Exists(Path.Combine(_d, "beememorybank.db")).Should().BeFalse("no database is created while the re-key runs");
    }

    /// <summary>
    /// A swap that stopped between its renames: D is gone. Before this fix a command created a fresh database in D, and
    /// the next start could no longer finish the swap. Now the command finishes it first and opens the new vault.
    /// </summary>
    [Fact]
    public async Task ACommand_BetweenTheRenames_FinishesTheSwap_InsteadOfCreatingAnEmptyVault()
    {
        var newDir = RekeySwapJournal.NewDirFor(_d);
        var oldDir = RekeySwapJournal.OldDirFor(_d, DateTimeOffset.UtcNow);
        Directory.CreateDirectory(newDir);
        File.WriteAllText(Path.Combine(newDir, "marker"), "new vault");
        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "marker"), "old vault");
        RekeySwapJournal.Write(_d, new RekeySwapJournal(newDir, oldDir, RekeySwapJournal.OldMoved));

        await using (await CliServiceProvider.CreateAsync(_d)) { }

        File.ReadAllText(Path.Combine(_d, "marker")).Should().Be("new vault");
        Directory.Exists(newDir).Should().BeFalse();
        File.ReadAllText(Path.Combine(oldDir, "marker")).Should().Be("old vault", "the old vault is untouched");
    }

    [Fact]
    public async Task WhileACommandRuns_TheVerbCannotTakeTheVault()
    {
        await using (await CliServiceProvider.CreateAsync(_d))
            VaultStartup.TryAcquireExclusive(_d).Should().BeNull("the command holds vault.lease");

        using var afterwards = VaultStartup.TryAcquireExclusive(_d);
        afterwards.Should().NotBeNull("disposing the command's services releases the lease");
    }
}
