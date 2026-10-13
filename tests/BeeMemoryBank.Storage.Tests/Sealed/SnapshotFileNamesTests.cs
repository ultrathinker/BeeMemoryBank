using BeeMemoryBank.Storage.Sealed;

namespace BeeMemoryBank.Storage.Tests.Sealed;

/// <summary>
/// The names of snapshot archives: one UTC second, and free in both folders a restore by name looks in (<c>snapshots/</c>, then
/// <c>pre-encryption/</c>), so that a name never means two archives.
/// </summary>
public sealed class SnapshotFileNamesTests : IDisposable
{
    private static readonly DateTime Second = new(2026, 10, 13, 9, 30, 15, DateTimeKind.Utc);
    private const string Base = "bmb-snapshot-20261013-093015.tar.gz";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "bmb_snapnames_" + Guid.NewGuid().ToString("N"));

    public SnapshotFileNamesTests()
    {
        Directory.CreateDirectory(Path.Combine(_data, "snapshots"));
        Directory.CreateDirectory(Path.Combine(_data, "pre-encryption"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_data, true); } catch { }
    }

    private string Snapshots => Path.Combine(_data, "snapshots");

    private void Plant(string folder, string name) => File.WriteAllText(Path.Combine(_data, folder, name), "x");

    [Fact]
    public void A_free_second_gives_the_plain_name()
    {
        SnapshotFileNames.Unique(_data, Snapshots, Second).Should().Be(Base);
    }

    [Fact]
    public void The_name_is_the_utc_second_whatever_the_culture()
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            SnapshotFileNames.Unique(_data, Snapshots, Second).Should().Be(Base);
        }
        finally { Thread.CurrentThread.CurrentCulture = culture; }
    }

    [Theory]
    [InlineData("snapshots")]
    [InlineData("pre-encryption")]
    public void A_name_taken_in_either_snapshot_folder_gets_a_counter(string takenIn)
    {
        Plant(takenIn, Base);

        SnapshotFileNames.Unique(_data, Snapshots, Second).Should().Be("bmb-snapshot-20261013-093015-2.tar.gz");
    }

    [Fact]
    public void The_counter_skips_every_name_taken_in_any_of_the_folders()
    {
        Plant("snapshots", Base);
        Plant("pre-encryption", "bmb-snapshot-20261013-093015-2.tar.gz");
        Plant("snapshots", "bmb-snapshot-20261013-093015-3.tar.gz");

        SnapshotFileNames.Unique(_data, Snapshots, Second).Should().Be("bmb-snapshot-20261013-093015-4.tar.gz");
    }

    [Fact]
    public void A_name_taken_in_another_output_folder_counts_as_well_as_the_two_snapshot_folders()
    {
        var elsewhere = Path.Combine(_data, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, Base), "x");
        Plant("pre-encryption", "bmb-snapshot-20261013-093015-2.tar.gz");

        SnapshotFileNames.Unique(_data, elsewhere, Second).Should().Be("bmb-snapshot-20261013-093015-3.tar.gz");
    }

    [Fact]
    public void A_data_folder_without_the_snapshot_folders_gives_the_plain_name()
    {
        var empty = Path.Combine(_data, "empty");
        Directory.CreateDirectory(empty);

        SnapshotFileNames.Unique(empty, Path.Combine(empty, "snapshots"), Second).Should().Be(Base);
    }
}
