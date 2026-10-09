using System.Text.Json;
using BeeMemoryBank.BlindDesktop.Windows;

namespace BeeMemoryBank.BlindDesktop.Tests.Windows;

public sealed class AtomicJsonStateStoreTests
{
    private static string NewPath() => Path.Combine(TestFolders.New("state"), "state.json");

    [Fact]
    public void ValuesPersistAcrossInstances_AndNullRemovesAKey()
    {
        var path = NewPath();
        var store = new AtomicJsonStateStore(path);
        store.Set("bmb.blind.node_id", "n-1");
        store.Set("bmb.blind.name", "Office PC");
        store.Set("bmb.blind.name", null);

        var again = new AtomicJsonStateStore(path);
        again.Get("bmb.blind.node_id").Should().Be("n-1");
        again.Get("bmb.blind.name").Should().BeNull();
        again.Get("never.set").Should().BeNull();
    }

    [Theory]
    [InlineData("a=b")]
    [InlineData("line1\nline2\r\nline3")]
    [InlineData("quote \" and backslash \\ and tab \t")]
    [InlineData("unicode: \u041F\u0440\u0438\u0432\u0435\u0442 é \U0001F41D")]
    [InlineData("{\"json\":[1,2,3]}")]
    [InlineData("")]
    public void AnyValue_RoundTripsExactly(string value)
    {
        var path = NewPath();
        new AtomicJsonStateStore(path).Set("k=with=equals", value);

        new AtomicJsonStateStore(path).Get("k=with=equals").Should().Be(value, "the state is JSON, so no value or key needs escaping by hand");
    }

    [Fact]
    public void TheFile_IsUtf8WithoutABom_AndPlainJson()
    {
        var path = NewPath();
        new AtomicJsonStateStore(path).Set("a", "b");

        var bytes = File.ReadAllBytes(path);
        bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "JSON configs are written without a byte-order mark");
        bytes[0].Should().Be((byte)'{');
        JsonSerializer.Deserialize<Dictionary<string, string>>(bytes).Should().Equal(new Dictionary<string, string> { ["a"] = "b" });
    }

    [Fact]
    public void EveryWrite_LeavesAWholeFile_AndNoTempFiles()
    {
        var path = NewPath();
        var store = new AtomicJsonStateStore(path);
        for (var i = 0; i < 50; i++)
        {
            store.Set("counter", i.ToString());
            JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(path))!["counter"].Should().Be(i.ToString());
        }
        Directory.GetFiles(Path.GetDirectoryName(path)!).Should().Equal([path], "only the state file itself remains");
    }

    [Fact]
    public async Task ParallelWriters_NeverCorruptTheFile_AndKeepEveryKey()
    {
        var path = NewPath();
        var store = new AtomicJsonStateStore(path);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++) store.Set($"w{w}.k{i}", $"{w}-{i}");
        })));

        var reread = new AtomicJsonStateStore(path);
        reread.WasUnreadable.Should().BeFalse();
        for (var w = 0; w < 8; w++)
            for (var i = 0; i < 25; i++)
                reread.Get($"w{w}.k{i}").Should().Be($"{w}-{i}");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"a\":")]
    [InlineData("null")]
    public void AnUnreadableFile_CountsAsEmpty_IsReported_AndIsReplacedByTheNextWrite(string content)
    {
        var path = NewPath();
        File.WriteAllText(path, content);

        var store = new AtomicJsonStateStore(path);

        store.WasUnreadable.Should().BeTrue();
        store.Get("anything").Should().BeNull();
        store.Set("fresh", "1");
        new AtomicJsonStateStore(path).Get("fresh").Should().Be("1");
        new AtomicJsonStateStore(path).WasUnreadable.Should().BeFalse();
    }

    [Fact]
    public void SettingTheSameValueAgain_DoesNotRewriteTheFile()
    {
        var path = NewPath();
        var store = new AtomicJsonStateStore(path);
        store.Set("a", "1");
        var written = File.GetLastWriteTimeUtc(path);
        Thread.Sleep(30);

        store.Set("a", "1");
        store.Set("never.there", null);

        File.GetLastWriteTimeUtc(path).Should().Be(written);
    }

    [Fact]
    public void Erase_RemovesTheStateFileAndTheTempLeftoversOfItsOwn_AndNothingElse()
    {
        var path = NewPath();
        var folder = Path.GetDirectoryName(path)!;
        var store = new AtomicJsonStateStore(path);
        store.Set("a", "1");
        var leftover = path + ".1a2b3c4d.tmp";
        File.WriteAllText(leftover, "{");
        var other = Path.Combine(folder, "other.json.1a2b3c4d.tmp");
        var neighbour = Path.Combine(folder, "neighbour.txt");
        File.WriteAllText(other, "x");
        File.WriteAllText(neighbour, "x");

        store.Erase();

        File.Exists(path).Should().BeFalse("the state file goes with the wipe, as on the other host");
        File.Exists(leftover).Should().BeFalse("an interrupted write's leftover goes too");
        File.Exists(other).Should().BeTrue("only the leftovers of the state file itself");
        File.Exists(neighbour).Should().BeTrue();
        store.Get("a").Should().BeNull();
        new AtomicJsonStateStore(path).Get("a").Should().BeNull();
    }

    [Fact]
    public void Erase_OnNothing_IsFine_AndALaterSetStartsANewFile()
    {
        var path = NewPath();
        var store = new AtomicJsonStateStore(path);

        store.Erase();
        store.Erase();
        File.Exists(path).Should().BeFalse();

        store.Set("fresh", "2");
        new AtomicJsonStateStore(path).Get("fresh").Should().Be("2");
    }

    [Fact]
    public void AMissingFolder_IsCreatedOnTheFirstWrite()
    {
        var path = Path.Combine(TestFolders.New("state"), "deep", "er", "state.json");
        new AtomicJsonStateStore(path).Set("a", "b");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void AFileHeldForAMoment_IsNotAnEmptyState_AndTheNextWriteDoesNotDestroyIt()
    {
        var path = NewPath();
        new AtomicJsonStateStore(path).Set("bmb.blind.node_id", "n-1");
        new AtomicJsonStateStore(path).Set("bmb.blind.name", "Office PC");

        var store = new AtomicJsonStateStore(path, readRetryDelay: TimeSpan.Zero);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) // an antivirus or a sync client has it open
        {
            var read = () => store.Get("bmb.blind.node_id");
            read.Should().Throw<IOException>("a file that cannot be read for now is not an empty state");
            var write = () => store.Set("last sync", "x");
            write.Should().Throw<IOException>("nothing may be written over a state that was never read");
        }

        store.Get("bmb.blind.node_id").Should().Be("n-1", "once the file is free the store reads it, the failure was not remembered");
        store.Set("last sync", "x");
        var again = new AtomicJsonStateStore(path);
        again.Get("bmb.blind.node_id").Should().Be("n-1");
        again.Get("bmb.blind.name").Should().Be("Office PC");
        again.Get("last sync").Should().Be("x");
    }

    [Fact]
    public void AFileHeldOnlyBrieflyAtStart_IsReadAfterAShortWait()
    {
        var path = NewPath();
        new AtomicJsonStateStore(path).Set("bmb.blind.node_id", "n-1");
        var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        // The release runs on a thread of its own that is known to be running before the read starts: a thread-pool continuation can wait
        // for CPU longer than the whole retry window of the read, which blocks the very thread the pool would need.
        using var running = new ManualResetEventSlim();
        var releaser = new Thread(() =>
        {
            running.Set();
            Thread.Sleep(150);
            holder.Dispose();
        }) { IsBackground = true };
        releaser.Start();
        running.Wait();

        var store = new AtomicJsonStateStore(path, readRetryDelay: TimeSpan.FromMilliseconds(100));

        try
        {
            store.Get("bmb.blind.node_id").Should().Be("n-1");
            store.WasUnreadable.Should().BeFalse();
        }
        finally
        {
            releaser.Join();
        }
    }

    [Fact]
    public void ADamagedFile_IsKeptAsACopy_BeforeTheNextWriteReplacesIt()
    {
        var path = NewPath();
        File.WriteAllText(path, "{\"a\":");

        var store = new AtomicJsonStateStore(path);
        store.WasUnreadable.Should().BeTrue();
        store.Set("fresh", "1");

        var copy = Directory.GetFiles(Path.GetDirectoryName(path)!, "state.json.damaged-*").Should().ContainSingle().Subject;
        File.ReadAllText(copy).Should().Be("{\"a\":", "what was there is not lost to a person who wants to look at it");
        new AtomicJsonStateStore(path).Get("fresh").Should().Be("1");
    }

    [Fact]
    public void Erase_RemovesTheDamagedCopiesToo()
    {
        var path = NewPath();
        File.WriteAllText(path, "not json");
        var store = new AtomicJsonStateStore(path);
        store.Set("fresh", "1");
        Directory.GetFiles(Path.GetDirectoryName(path)!, "state.json.damaged-*").Should().ContainSingle();

        store.Erase();

        Directory.GetFiles(Path.GetDirectoryName(path)!).Should().BeEmpty("nothing of the blind copy remains after the wipe");
    }

    [Fact]
    public void AWriteThatFails_LeavesTheStateAsItWas()
    {
        var path = NewPath();
        var store = new AtomicJsonStateStore(path);
        store.Set("k", "old");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) // the replace of the file fails
        {
            var set = () => store.Set("k", "new");
            set.Should().Throw<Exception>();
            var remove = () => store.Set("k", null);
            remove.Should().Throw<Exception>();
        }

        store.Get("k").Should().Be("old", "memory must not run ahead of the disk: a restart would lose it");
        new AtomicJsonStateStore(path).Get("k").Should().Be("old");
        store.Set("k", "new");
        new AtomicJsonStateStore(path).Get("k").Should().Be("new");
    }
}
