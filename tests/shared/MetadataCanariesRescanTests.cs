namespace BeeMemoryBank.TestSupport;

/// <summary>
/// The raw scan of a folder that an open database or a running host is still changing (one source in tests/shared, compiled into every test
/// project that has a <see cref="MetadataCanaries"/>): a -wal or -shm file that is replaced while the folder is scanned is not a finding and not
/// a reason to skip a file.
/// </summary>
public sealed class MetadataCanariesRescanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb-canary-rescan-" + Guid.NewGuid().ToString("N"));

    public MetadataCanariesRescanTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task SidecarsThatComeAndGoDuringTheScan_AreNotAFinding_AndTheCanaryOfAStableFileIsStillFound()
    {
        var canary = MetadataCanaries.Ascii("rescan");
        var database = Path.Combine(_dir, "beememorybank.db");
        File.WriteAllText(database, "head " + canary + " tail");
        var sidecars = Enumerable.Range(0, 6).Select(i => Path.Combine(_dir, $"beememorybank.db-shm{i}")).ToArray();
        using var stop = new CancellationTokenSource();
        var churn = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var sidecar in sidecars) File.WriteAllBytes(sidecar, [1, 2, 3]);
                Thread.Sleep(1);
                foreach (var sidecar in sidecars) File.Delete(sidecar);
                Thread.Sleep(1);
            }
        });
        try
        {
            for (var scan = 0; scan < 300; scan++)
            {
                var hits = MetadataCanaries.ScanDirectory(_dir, [canary]);
                hits.Should().NotBeEmpty($"scan {scan}: a clear canary in a file that stays is found, whatever the sidecars do")
                    .And.OnlyContain(h => h.Where == database);
            }
        }
        finally
        {
            stop.Cancel();
            await churn;
        }
    }

    [Fact]
    public void AFolderThatHoldsNoFile_IsScannedClean_AsABackupInputWithoutMediaIs()
    {
        MetadataCanaries.ScanDirectory(_dir, [MetadataCanaries.Ascii("empty")]).Should().BeEmpty();
    }

    [Fact]
    public void AFolderThatIsNotThere_FailsAtOnce_InsteadOfWaitingForTheWindow()
    {
        var started = DateTime.UtcNow;
        var scan = () => MetadataCanaries.ScanDirectory(Path.Combine(_dir, "missing"), [MetadataCanaries.Ascii("missing")]);

        scan.Should().Throw<DirectoryNotFoundException>();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(5));
    }
}
