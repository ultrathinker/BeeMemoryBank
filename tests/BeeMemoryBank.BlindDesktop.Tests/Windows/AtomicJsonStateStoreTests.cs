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
}
