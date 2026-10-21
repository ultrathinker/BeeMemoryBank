using System.Net;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// What one account's HTTP pipeline counts into and asks: its <see cref="BlindUsageLedger"/> and its <see cref="BlindLimitGuard"/>. <see cref="BlindMeteringHandler"/> and the counting
/// streams use it; it holds no state of its own.
/// </summary>
public sealed class BlindMeteringTap(BlindUsageLedger ledger, BlindLimitGuard guard)
{
    /// <summary>
    /// What headers, the TLS framing and the handshake are taken to cost per request, in each direction: an estimate (the bodies are counted exactly), which is why a screen says "about".
    /// The system's own counters see somewhat more (retransmits, DNS, the handshake itself).
    /// </summary>
    public const int OverheadBytes = 1024;

    /// <summary>A response is checked against the limits after every this many bytes read.</summary>
    public const int CheckEveryBytes = 256 * 1024;

    public BlindUsageLedger Ledger { get; } = ledger;

    public BlindLimitGuard Guard { get; } = guard;
}

/// <summary>
/// The handler in every account's pipeline that counts and guards, between the https-and-origin check and the socket handler: the only place every request of the copy passes
/// (<see cref="BlindHttpClientProvider"/>). Per request, in one step (<see cref="BlindLimitGuard.AdmitRequest"/>): the guard decides against the account's limits and the device's, the request is
/// counted, and its cost is set aside; a pause is a <see cref="BlindPausedException"/> and no request goes out. The body of the request is counted by the writes that SUCCEED, whether or not
/// its length was announced (an announced length only sets the traffic aside); the answer's headers are 1 KB in; its body is counted as it is READ, in reads that never pass the next 256 KB
/// check, the day's traffic that is left or the storage that is covered or left; and an announced length is set aside before the first byte, so that a download that cannot fit the storage limit
/// is refused before it starts (an absurd length cannot fit, and a missing or understated one only goes as far as the room that is left).
///
/// <para>An <paramref name="exempt"/> pipeline counts but never blocks: the paused report to the source must go out whatever the limits say (about 3 KB, counted).</para>
/// </summary>
public sealed class BlindMeteringHandler(BlindMeteringTap tap, bool exempt = false) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var announced = request.Content?.Headers.ContentLength ?? 0;
        var admission = exempt ? tap.Guard.AdmitExempt() : tap.Guard.AdmitRequest(announced);

        HttpResponseMessage response;
        try
        {
            // Counted by what is actually written, however long the body said it was: a send that is cancelled, or a body that is shorter or longer than its header, is counted for what went out.
            if (request.Content is { } body) request.Content = new CountingRequestContent(body, admission);

            response = await base.SendAsync(request, ct).ConfigureAwait(false);
            admission.CompleteResponseHeaders();
        }
        catch
        {
            admission.Dispose();
            throw;
        }

        if (response.Content is not { } content)
        {
            admission.Dispose();
            return response;
        }

        try
        {
            // Refused before the first byte when this copy would not fit; nothing is set aside for an unannounced length (its reads are held to the room that is left).
            admission.ReserveResponseStorage(content.Headers.ContentLength);
        }
        catch
        {
            response.Dispose();
            admission.Dispose();
            throw;
        }
        response.Content = new CountingResponseContent(content, admission);
        return response;
    }
}

/// <summary>A request body that counts the bytes it writes to the wire, as they are written; everything else about it (headers, length) is the wrapped body's.</summary>
internal sealed class CountingRequestContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly BlindRequestAdmission _admission;

    public CountingRequestContent(HttpContent inner, BlindRequestAdmission admission)
    {
        _inner = inner;
        _admission = admission;
        foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await _inner.CopyToAsync(new CountingWriteStream(stream, _admission), context, cancellationToken).ConfigureAwait(false);
    }

    protected override bool TryComputeLength(out long length)
    {
        if (_inner.Headers.ContentLength is { } known)
        {
            length = known;
            return true;
        }
        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A response body that counts the bytes read from it, in reads the admission sizes (see <see cref="BlindRequestAdmission.BeginRead"/>).</summary>
internal sealed class CountingResponseContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly BlindRequestAdmission _admission;

    public CountingResponseContent(HttpContent inner, BlindRequestAdmission admission)
    {
        _inner = inner;
        _admission = admission;
        foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await using var source = new CountingReadStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _admission);
        await source.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
        new CountingReadStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _admission);

    protected override Stream CreateContentReadStream(CancellationToken cancellationToken) =>
        new CountingReadStream(_inner.ReadAsStream(cancellationToken), _admission);

    protected override bool TryComputeLength(out long length)
    {
        if (_inner.Headers.ContentLength is { } known)
        {
            length = known;
            return true;
        }
        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _admission.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Reads the answer in steps the admission has sized: before each read the caller's buffer is cut to what may be read now (never past the next 256 KB check, the traffic or the storage that is
/// left), and the read is spent from what was set aside; a read that fails gives it back. The stream OWNS the release of what the answer set aside: it gives it back at the end of the body
/// (a read of zero for a buffer that was not empty), when the stream is disposed (<c>Dispose</c> and <c>DisposeAsync</c> alike) and, whichever comes first, when the answer's content is, so a
/// caller that closes only the stream, or keeps the content after it, leaves nothing held. After the end a read answers zero at once and touches nothing; after disposal it is refused.
/// </summary>
internal sealed class CountingReadStream(Stream inner, BlindRequestAdmission admission) : Stream
{
    private int _disposed;
    private volatile bool _ended;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_ended) return 0;
        var allowed = admission.BeginRead(buffer.Length);
        int read;
        try
        {
            read = inner.Read(buffer[..allowed]);
        }
        catch
        {
            admission.AbortRead();
            throw;
        }
        return Spent(read, buffer.Length);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_ended) return 0;
        var allowed = admission.BeginRead(buffer.Length);
        int read;
        try
        {
            read = await inner.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            admission.AbortRead();
            throw;
        }
        return Spent(read, buffer.Length);
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    private int Spent(int read, int requested)
    {
        admission.EndRead(Math.Max(0, read));
        // The end of the body: nothing more will be read, so nothing more is set aside for it, and a further read does not go back to the inner stream. (A read of zero bytes that asked for
        // none, a "wait for data" some stacks make, is not the end.)
        if (read <= 0 && requested > 0)
        {
            _ended = true;
            admission.Dispose();
        }
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            admission.Dispose();
            inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            admission.Dispose();
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Writes through to the wire and counts what was written: a write that fails is not counted, and every write is covered by the traffic that was set aside before it (or refused).</summary>
internal sealed class CountingWriteStream(Stream inner, BlindRequestAdmission admission) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count)
    {
        admission.BeginWrite(count);
        inner.Write(buffer, offset, count);
        admission.CompleteWrite(count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        admission.BeginWrite(buffer.Length);
        inner.Write(buffer);
        admission.CompleteWrite(buffer.Length);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        admission.BeginWrite(buffer.Length);
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        admission.CompleteWrite(buffer.Length);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        admission.BeginWrite(count);
        await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        admission.CompleteWrite(count);
    }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
