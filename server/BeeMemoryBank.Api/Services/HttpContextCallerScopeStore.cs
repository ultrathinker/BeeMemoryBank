using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// HTTP-aware <see cref="ICallerScopeStore"/>: routes scope state through
/// <c>HttpContext.Items</c> so it survives child DI scopes created inside a single HTTP
/// request. This is critical for the MCP server, whose SDK (ModelContextProtocol)
/// spins up a fresh <c>IServiceScope</c> to instantiate each tool invocation — a naive
/// scoped holder would be a brand-new instance there with the default
/// <see cref="SystemCallerScope"/>, silently bypassing every folder ACL check.
///
/// Fail-closed: if an HTTP request reaches us before <c>CallerScopeMiddleware</c> has
/// written to <c>HttpContext.Items</c> (shouldn't happen in normal flow), we return
/// <see cref="DenyAllScope"/> rather than the default open scope.
///
/// Fallback field: when there is no active HttpContext (background work, startup code,
/// integration tests that don't simulate HTTP), behavior matches
/// <see cref="InstanceCallerScopeStore"/>.
/// </summary>
public sealed class HttpContextCallerScopeStore : ICallerScopeStore
{
    private const string ItemKey = "__BMB_CallerScope";

    private readonly IHttpContextAccessor _accessor;
    private ICallerScope _fallback = SystemCallerScope.Instance;

    public HttpContextCallerScopeStore(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    public ICallerScope Scope
    {
        get
        {
            var ctx = _accessor.HttpContext;
            if (ctx == null) return _fallback;
            if (ctx.Items.TryGetValue(ItemKey, out var value) && value is ICallerScope s)
                return s;
            return DenyAllScope.Instance;
        }
        set
        {
            var ctx = _accessor.HttpContext;
            if (ctx != null)
                ctx.Items[ItemKey] = value;
            else
                _fallback = value;
        }
    }
}
