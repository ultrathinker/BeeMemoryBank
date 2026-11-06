using System.Text;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Storage.PreUnlock;

namespace BeeMemoryBank.Storage.Tests.PreUnlock;

/// <summary>
/// The pre-unlock hint copy (BMB-336): a closed encrypted node answers the sign-in page from this file. It must not list the
/// node's user names, must be readable for a name in any case, must be replaced as a whole, and must never take the node down:
/// a missing, corrupt or half-written file simply means "no hint".
/// </summary>
public sealed class PasswordHintFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_hint_file_" + Guid.NewGuid().ToString("N"));

    public PasswordHintFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private PasswordHintFile NewFile() => new(Path.Combine(_dir, PasswordHintFile.FileName));

    [Fact]
    public async Task TheFileDoesNotHoldTheUserNames_ButDoesHoldTheHintText()
    {
        var file = NewFile();
        await file.ReplaceAsync([new("alice-unique-name", "the blue folder by the window"), new("bob-unique-name", "the red door")]);

        var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file.FilePath));
        text.Should().NotContain("alice-unique-name").And.NotContain("bob-unique-name");
        text.Should().Contain("the blue folder by the window", "the hint is plaintext by design: the page must show it before anything is unlocked");
        text.Should().NotContain("A hint is available");
    }

    [Theory]
    [InlineData("alice-unique-name", "the blue folder by the window")]
    [InlineData("ALICE-UNIQUE-NAME", "the blue folder by the window")]
    [InlineData("  Alice-Unique-Name  ", "the blue folder by the window")]
    public async Task AStoredNameIsFoundInAnyCaseAndWithSurroundingSpaces(string asked, string expected)
    {
        var file = NewFile();
        await file.ReplaceAsync([new("alice-unique-name", "the blue folder by the window")]);

        (await file.FindAsync(asked)).Should().Be(expected);
    }

    [Fact]
    public async Task AnUnknownName_AndAUserWithoutAHint_AnswerNull()
    {
        var file = NewFile();
        await file.ReplaceAsync([new("alice", "the blue folder"), new("carol", "")]);

        (await file.FindAsync("nobody")).Should().BeNull();
        (await file.FindAsync("carol")).Should().BeNull("an empty hint is stored as no entry at all");
    }

    [Fact]
    public async Task AReplacementDropsEveryNameThatIsNoLongerThere()
    {
        var file = NewFile();
        await file.ReplaceAsync([new("alice", "the first hint"), new("bob", "the red door")]);
        await file.ReplaceAsync([new("alice", "the second hint")]);

        (await file.FindAsync("alice")).Should().Be("the second hint");
        (await file.FindAsync("bob")).Should().BeNull("the file is a mirror, not an archive");
        Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file.FilePath)).Should().NotContain("the red door");
    }

    [Fact]
    public async Task AMissingCorruptOrForeignFile_AnswersNull_AndNeverThrows()
    {
        var missing = NewFile();
        (await missing.FindAsync("alice")).Should().BeNull();

        await File.WriteAllTextAsync(missing.FilePath, "not json at all");
        (await missing.FindAsync("alice")).Should().BeNull();

        await File.WriteAllTextAsync(missing.FilePath, """{"version":1,"salt":"!!!","hints":[]}""");
        (await missing.FindAsync("alice")).Should().BeNull("a salt that is not base64 is a damaged file");

        await File.WriteAllTextAsync(missing.FilePath, """
            {"version":99,"salt":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=","hints":[{"user":"x","hint":"y"}]}
            """);
        (await missing.FindAsync("alice")).Should().BeNull("a format this program does not know is not guessed at");
    }

    [Fact]
    public async Task TheFileIsOwnerOnly_WhereThePlatformSupportsIt()
    {
        var file = NewFile();
        await file.ReplaceAsync([new("alice", "the blue folder")]);

        OwnerOnlyFile.IsOwnerOnly(file.FilePath).Should().BeTrue();
    }

    [Fact]
    public async Task AFailedWriteLeavesTheOldFileWhole_AndNoTempFileBehind()
    {
        var file = NewFile();
        await file.ReplaceAsync([new("alice", "the first hint")]);
        var before = await File.ReadAllBytesAsync(file.FilePath);

        file.WrapWriteForTests = _ => new FailingStream();
        var failure = async () => await file.ReplaceAsync([new("alice", "the lost hint")]);

        await failure.Should().ThrowAsync<IOException>();
        (await File.ReadAllBytesAsync(file.FilePath)).Should().Equal(before, "the final name never holds a half write");
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty("the staged file is removed when the write fails");

        file.WrapWriteForTests = null;
        await file.ReplaceAsync([new("alice", "the second hint")]);
        (await file.FindAsync("alice")).Should().Be("the second hint");
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("the volume is full");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromException(new IOException("the volume is full"));
    }
}