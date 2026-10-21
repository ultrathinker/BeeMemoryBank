using System.Text;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services.BlindLimits;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindNode.Tests;

/// <summary>
/// The edges of the one way the three small-body routes read their JSON (srvfix9, the coverage the review listed): a body of exactly the limit is accepted and one byte more is not,
/// a Content-Length that lies low does not widen the limit, a body that is not valid UTF-8 or not JSON is the documented 400, and a body that is not declared JSON is 415 and is not read.
/// </summary>
public sealed class BlindBodyLimitsTests
{
    private const long Limit = BlindBodyLimits.HashList;

    private static byte[] HashListOfExactly(long length)
    {
        var core = Encoding.UTF8.GetBytes("{\"hashes\":[\"" + new string('a', 64) + "\"]}");
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)' ');
        core.CopyTo(bytes, 0);   // the rest is whitespace JSON allows after the value
        return bytes;
    }

    private static DefaultHttpContext Post(Stream body, long? contentLength, string? contentType = "application/json")
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        context.Request.Method = "POST";
        context.Request.Body = body;
        context.Request.ContentLength = contentLength;
        if (contentType is not null) context.Request.ContentType = contentType;
        return context;
    }

    private static int StatusOf(IResult? result) => result is IStatusCodeHttpResult { StatusCode: { } status } ? status : -1;

    [Fact]
    public async Task A_body_of_exactly_the_limit_is_accepted()
    {
        var bytes = HashListOfExactly(Limit);
        var context = Post(new MemoryStream(bytes), bytes.Length);

        var (value, refusal) = await BlindBodyLimits.ReadJsonAsync<SyncBlobHashList>(context, Limit);

        refusal.Should().BeNull();
        value!.Hashes.Should().ContainSingle().Which.Should().HaveLength(64);
    }

    [Fact]
    public async Task One_byte_more_than_the_limit_is_413_whether_it_is_announced_or_not()
    {
        var bytes = HashListOfExactly(Limit + 1);

        var (_, announced) = await BlindBodyLimits.ReadJsonAsync<SyncBlobHashList>(Post(new MemoryStream(bytes), bytes.Length), Limit);
        var unannounced = new MemoryStream(bytes);
        var (_, chunked) = await BlindBodyLimits.ReadJsonAsync<SyncBlobHashList>(Post(unannounced, null), Limit);

        StatusOf(announced).Should().Be(StatusCodes.Status413PayloadTooLarge);
        StatusOf(chunked).Should().Be(StatusCodes.Status413PayloadTooLarge);
        unannounced.Position.Should().BeLessThanOrEqualTo(Limit + 1, "the read stopped one byte past the limit");
    }

    [Fact]
    public async Task A_content_length_that_lies_low_does_not_widen_the_limit()
    {
        var bytes = HashListOfExactly(Limit * 4);
        var stream = new MemoryStream(bytes);

        var (value, refusal) = await BlindBodyLimits.ReadJsonAsync<SyncBlobHashList>(Post(stream, 10), Limit);

        value.Should().BeNull();
        StatusOf(refusal).Should().Be(StatusCodes.Status413PayloadTooLarge);
        stream.Position.Should().BeLessThanOrEqualTo(Limit + 1, "what the connection really carries is what is bounded, not what the header says");
    }

    [Fact]
    public async Task A_body_that_is_not_valid_utf8_or_not_json_is_a_400()
    {
        byte[] invalidUtf8 = [.. "{\"hashes\":[\""u8.ToArray(), 0xFF, 0xFE, .. "\"]}"u8.ToArray()];

        var (_, utf8) = await BlindBodyLimits.ReadJsonAsync<SyncBlobHashList>(Post(new MemoryStream(invalidUtf8), invalidUtf8.Length), Limit);
        var (_, notJson) = await BlindBodyLimits.ReadJsonAsync<SyncBlobHashList>(Post(new MemoryStream("{ not json"u8.ToArray()), null), Limit);
        var (_, nothing) = await BlindBodyLimits.ReadJsonAsync<SyncBlobHashList>(Post(new MemoryStream("null"u8.ToArray()), null), Limit);

        StatusOf(utf8).Should().Be(StatusCodes.Status400BadRequest);
        StatusOf(notJson).Should().Be(StatusCodes.Status400BadRequest);
        StatusOf(nothing).Should().Be(StatusCodes.Status400BadRequest, "a JSON null is no list");
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/octet-stream")]
    [InlineData(null)]
    public async Task A_body_that_is_not_declared_json_is_415_and_is_not_read(string? contentType)
    {
        var stream = new MemoryStream(HashListOfExactly(1024));

        var (value, refusal) = await BlindBodyLimits.ReadJsonAsync<SyncBlobHashList>(Post(stream, 1024, contentType), Limit);

        value.Should().BeNull();
        StatusOf(refusal).Should().Be(StatusCodes.Status415UnsupportedMediaType);
        stream.Position.Should().Be(0, "nothing of it was read");
    }

    [Fact]
    public async Task The_bounded_stream_lets_exactly_the_limit_through_and_asks_the_connection_for_one_byte_more_at_most()
    {
        var inner = new MemoryStream(new byte[100]);
        var bounded = new BoundedReadStream(inner, 60);
        var buffer = new byte[80];

        var first = await bounded.ReadAsync(buffer.AsMemory(0, 60));
        first.Should().Be(60);
        var second = () => bounded.ReadAsync(buffer.AsMemory(0, 20)).AsTask();

        (await second.Should().ThrowAsync<BadHttpRequestException>()).Which.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        inner.Position.Should().Be(61, "one byte past the limit was taken to find out, no more");
    }
}
