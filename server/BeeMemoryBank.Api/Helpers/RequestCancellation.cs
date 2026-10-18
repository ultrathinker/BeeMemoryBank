using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BeeMemoryBank.Api.Helpers;

/// <summary>
/// The token a long write of a route can stop at (BMB-311 fix round 3, G6): it fires when the client goes away (<see cref="HttpContext.RequestAborted"/>) or when the application
/// is stopping. A service that takes it looks at it ONLY where stopping is safe - before its first write, or between units that can be undone (a copy rolls its partial result
/// back) - and never in the middle of a key-slot rewrite or a multi-row write that has to reach its consistent end. The route's single-flight lease is held until the handler
/// returns, so it is held to the terminal state (done, or stopped and undone), never released while the work is still running.
/// </summary>
public static class RequestCancellation
{
    /// <summary>A source linked to the request and to the application's stopping; dispose it when the handler is done.</summary>
    public static CancellationTokenSource Linked(HttpContext ctx)
    {
        var stopping = ctx.RequestServices.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;
        return CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, stopping);
    }
}
