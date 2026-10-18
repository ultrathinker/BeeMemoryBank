using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Squash;

/// <summary>
/// The table in code that says which ledger a 2.6.x database has (<c>LegacyMigrationChain</c>): it is data, never executed, and it is the
/// whole of what the runner knows about the old migrations. These tests keep it equal to the lists of files the released versions shipped
/// (chains.txt, generated from the git tags; the files themselves are checked in <see cref="LegacyChain"/> where the history is there).
/// </summary>
public class RecognisedChainTests
{
    [Fact]
    public void The_recognised_list_is_the_file_list_of_2_6_1()
    {
        LegacyMigrationChain.Files.Select(f => f.File).Should().Equal(LegacyChain.FinalChain);
        LegacyMigrationChain.Files.Select(f => f.Version).Should().Equal(LegacyChain.FinalChain.Select(n => int.Parse(n[..3])),
            "the version is the number the file name starts with, as the runner reads it");
    }

    [Fact]
    public void The_recognised_list_is_the_39_rows_of_a_2_6_x_ledger()
    {
        LegacyMigrationChain.Files.Should().HaveCount(39);
        LegacyMigrationChain.Files.Select(f => f.Version).Should().Equal(Enumerable.Range(1, 40).Where(v => v != 32), "there is no migration 32");
        LegacyMigrationChain.Files[^1].Should().Be((40, "040_sealed_unlock_material.sql"));
        LegacyMigrationChain.LedgerRows.First().Should().Be((1, "BeeMemoryBank.Storage.Migrations.001_initial_schema.sql"));
    }

    [Theory]
    [InlineData("v2.1.0")]
    [InlineData("v2.2.0")]
    [InlineData("v2.3.0")]
    [InlineData("v2.4.0")]
    [InlineData("v2.5.0")]
    [InlineData("v2.5.2")]
    [InlineData("v2.6.0")]
    public void Every_released_chain_is_a_prefix_of_the_recognised_list(string tag)
    {
        // A node of an older release has a part of the recognised ledger and nothing else: that is what makes it "older", not "unknown".
        var chain = LegacyChain.Chains[tag];
        LegacyMigrationChain.Files.Select(f => f.File).Take(chain.Length).Should().Equal(chain);
    }

    [Fact]
    public void The_baseline_has_the_number_of_the_last_old_migration_and_its_own_name()
    {
        MigrationRunner.BaselineVersion.Should().Be(LegacyMigrationChain.Files[^1].Version);
        MigrationRunner.BaselineResourceName.Should().Be("BeeMemoryBank.Storage.Migrations.040_baseline.sql");
        LegacyMigrationChain.LedgerRows.Should().NotContain(r => r.ResourceName == MigrationRunner.BaselineResourceName,
            "a database below the final level must never find its own old row equal to the baseline's");
    }

    [LegacyChainFact]
    public void The_recognised_list_is_the_chain_whose_files_the_history_holds()
    {
        // Version 40 of the list is the file the history calls 040_...: read every file, they all exist under the recognised names.
        foreach (var (_, file) in LegacyMigrationChain.Files)
            LegacyChain.FilesOf("v2.6.1").Select(f => f.ResourceName).Should().Contain(LegacyMigrationChain.ResourcePrefix + file);
    }
}
