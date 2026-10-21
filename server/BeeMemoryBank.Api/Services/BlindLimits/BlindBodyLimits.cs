using System.Buffers;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace BeeMemoryBank.Api.Services.BlindLimits;

/// <summary>
/// The size of the small JSON bodies of the three metered routes that take one - a list of blob hashes (<c>blobs/check</c>, <c>blobs/get</c>) and a restored device's claim - and the one
/// way these routes read them. Their handlers do NOT bind the body as a parameter: binding runs before the handler can look at who is asking, and a body that is read before the
/// credential is accepted is a body that anyone can make the node read (and count against the day) up to the listener's own limit. Instead the handler checks the credential first and
/// reads through <see cref="ReadJsonAsync{T}"/>, which stops at the limit of its route: nothing past the limit is read, so nothing past it is counted.
///
/// <para>The limits are computed from what a legitimate caller can send, with room: a hash list is at most 2000 hashes of 64 characters (<c>SyncEndpoints.ValidateHashList</c>), about 134 KB
/// as JSON; a claim is a node id, a device name, a 32-byte key in base64 and an address of at most 2048 characters - a few hundred bytes, a device name that fills 14 KiB would be a
/// strange device. The claim is the one route that reads its body BEFORE its credential is judged: the restore code is judged together with the node id the body names (a device that
/// spent the code may repeat its claim), so the most an unauthenticated caller can make it read is <see cref="Claim"/>, and only when it presents a restore code at all.</para>
/// </summary>
public static class BlindBodyLimits
{
    /// <summary>A list of blob hashes (<c>POST /api/sync/blobs/check</c>, <c>POST /api/sync/blobs/get</c>): 256 KiB, about twice the largest legitimate list.</summary>
    public const long HashList = 256 * 1024;

    /// <summary>A restored device's claim (<c>POST /api/blind/claim</c>): 16 KiB.</summary>
    public const long Claim = 16 * 1024;

    /// <summary>
    /// Reads the request's JSON body, at most <paramref name="limit"/> bytes of it. Returns the value, or the answer to give: 415 for a body that is not JSON, 413 for one that is longer than the
    /// limit (announced, or found out while reading - the read stops one byte past the limit), 400 for one that is not valid JSON or is <c>null</c>. A read that fails because the caller went away
    /// is the caller's: the exception goes on.
    /// </summary>
    public static async Task<(T? Value, IResult? Refusal)> ReadJsonAsync<T>(HttpContext ctx, long limit) where T : class
    {
        var request = ctx.Request;
        if (!request.HasJsonContentType()) return (null, Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
        if (request.ContentLength is { } announced && announced > limit) return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = limit;

        using var body = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var room = limit - body.Length + 1;   // one byte past the limit tells it is too long; never more is asked of the connection
                var read = await request.Body.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, room)), ctx.RequestAborted);
                if (read == 0) break;
                body.Write(chunk, 0, read);
                if (body.Length > limit) return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        try
        {
            var options = ctx.RequestServices.GetService<IOptions<JsonOptions>>()?.Value.SerializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
            return JsonSerializer.Deserialize<T>(body.GetBuffer().AsSpan(0, (int)body.Length), options) is { } value
                ? (value, null)
                : (null, Results.BadRequest(new ErrorResponse("Invalid JSON")));
        }
        catch (JsonException)
        {
            return (null, Results.BadRequest(new ErrorResponse("Invalid JSON")));
        }
    }
}

/// <summary>
/// A request body that never lets more than <paramref name="limit"/> bytes through: the read that would pass it asks the connection for one byte past the limit at most and then throws a 413
/// <see cref="BadHttpRequestException"/>. The door puts it under the counting wrapper of a route that has a small limit (<see cref="BlindBodyLimits"/>), so a handler that read its body some
/// other way would still read - and count - no more than the limit.
/// </summary>
public sealed class BoundedReadStream(Stream underlying, long limit) : Stream
{
    private long _read;

    public override bool CanRead => underlying.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    private int Allowed(int count)
    {
        if (_read > limit) throw TooLarge();
        return (int)Math.Min(count, limit - _read + 1);
    }

    private int Took(int read)
    {
        _read += read;
        if (_read > limit) throw TooLarge();
        return read;
    }

    private static BadHttpRequestException TooLarge() => new("Request body too large.", StatusCodes.Status413PayloadTooLarge);

    public override int Read(byte[] buffer, int offset, int count) => Took(underlying.Read(buffer, offset, Allowed(count)));

    public override int Read(Span<byte> buffer) => Took(underlying.Read(buffer[..Allowed(buffer.Length)]));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Took(await underlying.ReadAsync(buffer.AsMemory(offset, Allowed(count)), cancellationToken));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Took(await underlying.ReadAsync(buffer[..Allowed(buffer.Length)], cancellationToken));

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
