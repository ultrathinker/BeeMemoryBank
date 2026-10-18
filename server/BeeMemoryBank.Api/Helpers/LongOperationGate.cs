using System.Collections.Concurrent;
using BeeMemoryBank.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Api.Helpers;

/// <summary>
/// One running long operation per name (BMB-311): a second identical request while the first is still working is refused at once with
/// 409 "already running", instead of starting a second copy of something that takes minutes (a baseline transfer, a snapshot, an import ...).
/// A lease lasts as long as the handler runs, so a client that gave up and pressed the button again gets the refusal, not a second job.
/// <para>One gate per node, not per process: it is a singleton of the host (<c>AddBeeApiServices</c>), so several in-process nodes (a test that hosts a
/// joiner and its producer) never refuse each other.</para>
/// <para>
/// <b>How a lease ends.</b> When the work ends: the handler returns or throws, or - for an operation the handler only starts (a restore that answers 202 and
/// goes on in the background) - when the background work reaches a terminal state, because the handler handed the lease over
/// (<see cref="SingleFlightExtensions.DetachSingleFlightLease"/>). There is deliberately no time limit: a lease that expired while the work still ran would let
/// a second restore start next to the first. A work that hangs is ended by cancelling it - a client that goes away, or the host stopping, cancels what the
/// work passes its token to - and a restart of the node clears the gate; meanwhile <see cref="Running"/> (the Admin page's status) says what has been running
/// since when, so that a stuck operation can be seen instead of guessed at.
/// </para>
/// <para>
/// This is the in-process counterpart of what the operations that already have a guard of their own do (the backup jobs, a restore, an
/// update, a compaction, the encryption of the node, the key change). It is not a replacement for <c>HeavyOperationLock</c> or
/// <c>EventWriteGate</c>, which keep two different operations off the same data; this keeps one operation from being started twice.
/// </para>
/// </summary>
public sealed class LongOperationGate
{
    private readonly ConcurrentDictionary<string, RunningOperation> _running = new(StringComparer.Ordinal);

    /// <summary>The lease for <paramref name="key"/>, or null when one is already running. Dispose it when the work ends.</summary>
    /// <param name="what">What the operation is, in words, for <see cref="Running"/>.</param>
    public IDisposable? TryEnter(string key, string? what = null)
        => _running.TryAdd(key, new RunningOperation(key, what ?? key, DateTime.UtcNow)) ? new Lease(this, key) : null;

    public bool IsRunning(string key) => _running.ContainsKey(key);

    /// <summary>The operations holding a lease now, the oldest first: what the status of the node shows, and what an operator looks at when something seems stuck.</summary>
    public IReadOnlyList<RunningOperation> Running() => _running.Values.OrderBy(o => o.Since).ToList();

    private sealed class Lease(LongOperationGate gate, string key) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate._running.TryRemove(key, out _);
        }
    }
}

/// <summary>One lease held in the gate: its key, what it is, and since when.</summary>
public sealed record RunningOperation(string Key, string What, DateTime Since);

/// <summary>Endpoint metadata: this route is a long operation that runs once at a time (read by the guard test).</summary>
public sealed record SingleFlightMetadata(string Key, string What);

/// <summary>
/// Answers 409 "<c>{what}</c> is already running" while another request for the same key is in flight. The key may name route values
/// in braces (<c>"blind-reseed:{nodeId}"</c>): the same operation on another node runs next to it.
/// </summary>
public sealed class SingleFlightFilter(string key, string what, Func<EndpointFilterInvocationContext, string?>? select = null) : IEndpointFilter
{
    public const string Code = "already_running";

    /// <summary>Where the filter leaves the lease for a handler that hands it over to work that outlives the request.</summary>
    internal const string LeaseItem = "bmb.single-flight.lease";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // A route whose key depends on the request itself (what is in its body, who asks) says so with <paramref name="select"/>; null means this request is
        // not a long one (an article export is one article) and runs without a lease.
        var resolved = select is null ? Resolve(key, context.HttpContext) : select(context);
        if (resolved is null) return await next(context);

        var lease = context.HttpContext.RequestServices.GetRequiredService<LongOperationGate>().TryEnter(resolved, what);
        if (lease == null)
            return Results.Json(new ErrorResponse($"{what} is already running. Wait until it has finished.", Code), statusCode: StatusCodes.Status409Conflict);

        var cell = new LeaseCell(lease);
        context.HttpContext.Items[LeaseItem] = cell;
        try { return await next(context); }
        finally { cell.Release(); }   // unless the handler handed it over
    }

    /// <summary>The lease while the request holds it; the handler may take it over (<see cref="SingleFlightExtensions.DetachSingleFlightLease"/>).</summary>
    internal sealed class LeaseCell(IDisposable lease)
    {
        private IDisposable? _lease = lease;

        public IDisposable? Detach() => Interlocked.Exchange(ref _lease, null);

        public void Release() => Interlocked.Exchange(ref _lease, null)?.Dispose();
    }

    internal static string Resolve(string template, HttpContext ctx)
    {
        if (!template.Contains('{')) return template;
        var result = template;
        foreach (var (name, value) in ctx.Request.RouteValues)
            result = result.Replace("{" + name + "}", Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "", StringComparison.Ordinal);
        return result;
    }
}

public static class SingleFlightExtensions
{
    /// <summary>Marks the route as a long operation: only one runs at a time per <paramref name="key"/> (route values in braces make it per value).</summary>
    public static RouteHandlerBuilder SingleFlight(this RouteHandlerBuilder builder, string key, string what)
        => builder.WithMetadata(new SingleFlightMetadata(key, what)).AddEndpointFilter(new SingleFlightFilter(key, what));

    /// <summary>
    /// The same for a route whose key is not in its route values: <paramref name="select"/> reads it from the request (its body, the caller) and returns null for
    /// a request that is not a long one. <paramref name="keyTemplate"/> only describes the key (it is what the route inventory lists).
    /// </summary>
    public static RouteHandlerBuilder SingleFlight(this RouteHandlerBuilder builder, string keyTemplate, string what, Func<EndpointFilterInvocationContext, string?> select)
        => builder.WithMetadata(new SingleFlightMetadata(keyTemplate, what)).AddEndpointFilter(new SingleFlightFilter(keyTemplate, what, select));

    /// <summary>
    /// For an operation the handler only STARTS: takes the lease of this request over, so that it is not given back when the handler returns. The caller owns it
    /// from here and disposes it when its work reaches a terminal state (success, failure, cancellation). Call it at the point where the background work is
    /// started, and only then: whatever throws before it is the handler's failure, and the filter gives the lease back. A route without a lease gets a lease that
    /// does nothing.
    /// </summary>
    public static IDisposable DetachSingleFlightLease(this HttpContext context)
        => context.Items.TryGetValue(SingleFlightFilter.LeaseItem, out var cell) && cell is SingleFlightFilter.LeaseCell held && held.Detach() is { } lease
            ? lease
            : NoLease.Instance;

    private sealed class NoLease : IDisposable
    {
        public static readonly NoLease Instance = new();
        public void Dispose() { }
    }
}
