using BeeMemoryBank.Api.Services.BlindLimits;
using BeeMemoryBank.Hosting.AspNetCore;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Api.Middleware;

/// <summary>
/// The server blind node's limits, at the door (release 2.7, the server host's phase 2): the
/// routes that carry the mesh's data — the writes a full node pushes and the reads that serve
/// phones — are admitted by <see cref="BlindUsageGuard"/> and counted; when a limit holds, the
/// answer is 503 with <c>X-BMB-Paused: disk|traffic</c>, a <c>Retry-After</c> and a short body,
/// and the handler below never runs, so nothing is written.
///
/// <para>Everything else (identity, challenge, authenticate, report-position, the console, status,
/// health) passes untouched: a paused node must still say who it is, let peers authenticate and
/// report where they stand, and let its owner lift the pause. Sits after RateLimit and before
/// Maintenance in both blind pipelines (a node that is paused and in maintenance says "paused":
/// that is the answer its callers can act on). Mapped in the blind role only; a host composed
/// without the guard (a full node) passes everything through. Only the listed METHOD and path are
/// metered: a HEAD request to a data route has no body in either direction (and no handler: the
/// routes answer 405), so it is neither counted nor held by a pause.</para>
///
/// <para><b>The door looks, the handler reserves, the end settles.</b> The door only LOOKS
/// (<see cref="BlindUsageGuard.Admit"/>): it runs before anybody is known, and a reservation taken
/// there would be a budget lock for whoever can reach the node. The handler reserves what the request
/// is planned to move right after it has authenticated the caller
/// (<see cref="BlindAdmissionExtensions.ReserveForPeer"/>); a response that announces its length is
/// judged when it announces it, while it has not started; the wrappers below report what really
/// moves; and a <c>finally</c> settles — whatever the handler did: answered, threw, was cancelled, or
/// its connection was aborted. A request that was admitted finishes (its response is not cut off
/// mid-body); what it moves beyond its reservation is reserved as it moves, so a request that arrives
/// while it is running is judged against it. See <see cref="BlindUsageGuard"/> for what can still be
/// overshot and by how much.</para>
/// </summary>
public class BlindUsageMiddleware(RequestDelegate next)
{
    /// <summary>
    /// The data surface: every route that moves the mesh's ciphertext in bulk, as "METHOD path"
    /// keys. A poll of a few hundred bytes (a seed's progress, a position report) is not on it:
    /// pausing those would hide the node behind its own limits. The path is normalized the way the
    /// rate limiter normalizes, so a trailing slash cannot route around a pause. The value says
    /// whether the HANDLER judges the storage limit itself: the seed stages in parts and must remove
    /// its own staging when it refuses, so it asks <see cref="BlindUsageGuard.StagingRoom"/> with exact
    /// numbers, and the door judges only its traffic.
    /// </summary>
    private static readonly Dictionary<string, DataRoute> DataRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Writes a full node pushes (a reseed ships blobs first, then the seed; the events last).
        ["POST /api/sync/events"] = new(WritesStorage: true),
        ["POST /api/sync/blobs"] = new(WritesStorage: true),
        // A list of hashes to look up: a POST that stores nothing.
        ["POST /api/sync/blobs/check"] = new(WritesStorage: false, MaxBody: BlindBodyLimits.HashList),
        ["POST /api/blind/seed"] = new(WritesStorage: false, StorageIsTheHandlers: true),
        // Reads that serve phones and restoring devices (a pull page, the blobs it needs, the
        // first load's package, a restore's package and its events).
        ["GET /api/sync/events"] = new(WritesStorage: false),
        ["POST /api/sync/blobs/get"] = new(WritesStorage: false, MaxBody: BlindBodyLimits.HashList),
        ["GET /api/blind/replica"] = new(WritesStorage: false),
        ["GET /api/blind/restore/package"] = new(WritesStorage: false),
        ["GET /api/blind/restore/events"] = new(WritesStorage: false),
        ["POST /api/blind/claim"] = new(WritesStorage: false, MaxBody: BlindBodyLimits.Claim),
    };

    /// <summary>The "METHOD path" keys of the data routes: every one of them must reach <see cref="BlindAdmissionExtensions.ReserveForPeer"/> in its handler once the caller is known (a test drives each).</summary>
    public static IReadOnlyCollection<string> DataRouteKeys => DataRoutes.Keys;

    /// <param name="WritesStorage">The request's body is stored: its announced length is judged against, and reserved from, the storage limit.</param>
    /// <param name="StorageIsTheHandlers">The handler judges the storage limit itself (the seed).</param>
    /// <param name="MaxBody">The route takes a small JSON body and nothing else: a request that announces more is refused 413 at the door without a byte of it read, and the body is bounded under the counting wrapper whatever the handler does.</param>
    private sealed record DataRoute(bool WritesStorage, bool StorageIsTheHandlers = false, long? MaxBody = null);

    public async Task InvokeAsync(HttpContext ctx)
    {
        var guard = ctx.RequestServices.GetService<BlindUsageGuard>();
        var path = RateLimitPath.Normalize(ctx.Request.Path.Value);
        if (guard is null || !DataRoutes.TryGetValue(ctx.Request.Method + " " + path, out var route))
        {
            await next(ctx);
            return;
        }

        if (route.MaxBody is { } maxBody && ctx.Request.ContentLength > maxBody)
        {
            // Not a limit of the node's: a body this route never takes, said before anything is read or counted.
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        using var admission = guard.Admit(ctx.Request.ContentLength, route.WritesStorage, route.StorageIsTheHandlers);
        if (admission.Pause is { } pause)
        {
            await BlindPauseResult.WriteAsync(ctx.Response, pause);
            return;
        }

        // Count what really moves: the request's body as the handler reads it, the response's as it
        // is written (streams, ranges and all — the wrappers only count). The first write of the
        // response is the moment its length, if it announced one, is known.
        admission.OnExpired = ctx.Abort;
        var originalBody = ctx.Request.Body;
        var request = new CountingStream(route.MaxBody is { } limit ? new BoundedReadStream(originalBody, limit) : originalBody, admission.Read);
        var response = new CountingStream(ctx.Response.Body, admission.Written, () =>
        {
            // The response announced its length and is about to start: judge it against the day, while it can still be refused.
            if (ctx.Response.ContentLength is { } announced && admission.Announce(announced, mayRefuse: !ctx.Response.HasStarted) is { } refused)
                throw new BlindResponseRefusedException(refused);
        });
        ctx.Request.Body = request;
        ctx.Response.Body = response;
        ctx.Items[BlindAdmissionExtensions.ItemKey] = admission;
        try
        {
            await next(ctx);
        }
        catch (BlindResponseRefusedException refused) when (!ctx.Response.HasStarted)
        {
            // Nothing of the response has gone out: it is replaced by the pause (the entity headers of the file it was going to be go with it).
            ctx.Response.Body = response.Underlying;
            foreach (var name in ctx.Response.Headers.Keys.Where(k => k.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
                         || k.StartsWith("X-BMB-Snapshot-", StringComparison.OrdinalIgnoreCase)
                         || k is "Accept-Ranges" or "ETag" or "Last-Modified").ToList())
                ctx.Response.Headers.Remove(name);
            await BlindPauseResult.WriteAsync(ctx.Response, refused.Pause);
        }
        finally
        {
            ctx.Items.Remove(BlindAdmissionExtensions.ItemKey);
            ctx.Request.Body = originalBody;
            ctx.Response.Body = response.Underlying;
            admission.Dispose();
            guard.FlushTraffic();
        }
    }

    /// <summary>A stream that only counts: everything it is asked to pass, it passes.</summary>
    private sealed class CountingStream(Stream underlying, Action<long> moved, Action? onFirstWrite = null) : Stream
    {
        private bool _written;

        public Stream Underlying => underlying;

        public override bool CanRead => underlying.CanRead;
        public override bool CanSeek => underlying.CanSeek;
        public override bool CanWrite => underlying.CanWrite;
        public override long Length => underlying.Length;
        public override long Position { get => underlying.Position; set => underlying.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = underlying.Read(buffer, offset, count);
            moved(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = underlying.Read(buffer);
            moved(read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var read = await underlying.ReadAsync(buffer, offset, count, ct);
            moved(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await underlying.ReadAsync(buffer, ct);
            moved(read);
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            FirstWrite();
            underlying.Write(buffer, offset, count);
            moved(count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            FirstWrite();
            underlying.Write(buffer);
            moved(buffer.Length);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            FirstWrite();
            await underlying.WriteAsync(buffer, offset, count, ct);
            moved(count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            FirstWrite();
            await underlying.WriteAsync(buffer, ct);
            moved(buffer.Length);
        }

        private void FirstWrite()
        {
            if (_written) return;
            _written = true;
            onFirstWrite?.Invoke();
        }

        public override void Flush() => underlying.Flush();
        public override Task FlushAsync(CancellationToken ct) => underlying.FlushAsync(ct);
        public override long Seek(long offset, SeekOrigin origin) => underlying.Seek(offset, origin);
        public override void SetLength(long value) => underlying.SetLength(value);
        protected override void Dispose(bool disposing)
        {
            if (disposing) underlying.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await underlying.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}

/// <summary>
/// The hook a handler calls right after it has authenticated its caller (the peer's token, the seeder's proof, the restore code): the request reserves what it is planned to move
/// (see <see cref="BlindUsageGuard"/>). It returns the pause to answer with - <c>return new BlindPauseResult(pause)</c> - or null; null also where there is no admission (a full node,
/// a route that is not metered).
/// </summary>
public static class BlindAdmissionExtensions
{
    public const string ItemKey = "BMB.BlindAdmission";

    public static BlindPause? ReserveForPeer(this HttpContext ctx) =>
        ctx.Items.TryGetValue(ItemKey, out var held) && held is BlindAdmission admission ? admission.Authenticated() : null;
}

/// <summary>
/// Endpoint metadata: this endpoint is one of the node's metered data routes (the keys of <see cref="BlindUsageMiddleware.DataRouteKeys"/>), and its handler calls
/// <see cref="BlindAdmissionExtensions.ReserveForPeer"/>. The declaration says so next to the handler; a test compares the endpoints that carry it with the door's table, so a bulk
/// endpoint that is marked but not in the table (or the other way round) fails the build of the tests instead of being served unmetered.
/// </summary>
public sealed class BlindMeteredEndpoint
{
    public static readonly BlindMeteredEndpoint Instance = new();

    private BlindMeteredEndpoint() { }
}

/// <summary>The body of the paused answer: the reason twice (a code for the caller, a sentence for a person), and no number of the owner.</summary>
public sealed record PausedResponse(string Message, string Paused);

/// <summary>
/// The paused answer, written by the door and by a handler that must refuse for the same reason (the seed, which judges the storage limit as its parts arrive): 503,
/// <c>X-BMB-Paused: disk|traffic</c>, <c>Retry-After</c> and the short body, so the full node reads both the same way.
/// </summary>
public sealed class BlindPauseResult(BlindPause pause) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext) => WriteAsync(httpContext.Response, pause);

    public static Task WriteAsync(HttpResponse response, BlindPause pause)
    {
        response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        response.Headers[PeerPausedException.HeaderName] = pause.Reason;
        response.Headers.RetryAfter = ((int)Math.Max(1, pause.RetryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return response.WriteAsJsonAsync(new PausedResponse(pause.Message, pause.Reason));
    }
}
