using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Regression tests for the DI-scope leak that silently bypassed folder ACL in MCP.
///
/// Background: the MCP SDK (ModelContextProtocol.AspNetCore) creates a child IServiceScope
/// per tool invocation. A naive <c>Scoped&lt;CallerScopeHolder&gt;</c> would be a brand-new
/// instance in that child scope, defaulting to <c>SystemCallerScope.Instance</c> —
/// silently granting superadmin rights to a non-superadmin agent.
///
/// The fix is <see cref="HttpContextCallerScopeStore"/>: route scope state through
/// <c>HttpContext.Items</c>, which is shared across any child scope spawned inside the
/// same HTTP request. These tests pin that contract so future refactors of DI wiring
/// or a swap of the MCP SDK can't quietly reintroduce the leak.
/// </summary>
public class CallerScopeChildScopeTests
{
    private static ServiceProvider BuildProvider(bool httpAware)
    {
        var services = new ServiceCollection();
        services.AddHttpContextAccessor();
        services.AddScoped<CallerScopeHolder>();
        if (httpAware)
            services.Replace(ServiceDescriptor.Scoped<ICallerScopeStore, HttpContextCallerScopeStore>());
        else
            services.AddScoped<ICallerScopeStore, InstanceCallerScopeStore>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void HttpContextStore_ChildScopeSeesParentScopeValue()
    {
        using var sp = BuildProvider(httpAware: true);
        var accessor = sp.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext();

        using var parent = sp.CreateScope();
        var parentHolder = parent.ServiceProvider.GetRequiredService<CallerScopeHolder>();
        var allow = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/Public" };
        parentHolder.Scope = new HttpCallerScope(
            isSuperadmin: false,
            denyPaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            allowPaths: allow);

        // Simulate what the MCP SDK does: create a child scope to instantiate tool services.
        using var child = parent.ServiceProvider.CreateScope();
        var childHolder = child.ServiceProvider.GetRequiredService<CallerScopeHolder>();

        childHolder.Scope.Should().BeOfType<HttpCallerScope>();
        childHolder.Scope.IsAccessDenied("/Public").Should().BeFalse();
        childHolder.Scope.IsAccessDenied("/Secret").Should().BeTrue(
            "child scope must see the same ACL as the HTTP request scope — otherwise ACL is silently bypassed");
    }

    [Fact]
    public void HttpContextStore_WithoutHttpContext_FallsBackToInstanceField()
    {
        using var sp = BuildProvider(httpAware: true);
        // No HttpContext set — simulates background work / startup code.

        using var scope = sp.CreateScope();
        var holder = scope.ServiceProvider.GetRequiredService<CallerScopeHolder>();
        holder.Scope.Should().BeSameAs(SystemCallerScope.Instance,
            "default for non-HTTP code paths must remain System — matches pre-refactor behavior");

        // And explicit assignment must persist.
        var customScope = new HttpCallerScope(true, [], []);
        holder.Scope = customScope;
        holder.Scope.Should().BeSameAs(customScope);
    }

    [Fact]
    public void HttpContextStore_FailsClosedWhenMiddlewareDidNotSetScope()
    {
        using var sp = BuildProvider(httpAware: true);
        var accessor = sp.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext();
        // Deliberately DO NOT assign holder.Scope — simulates a request that bypassed
        // CallerScopeMiddleware (e.g. middleware removed by mistake).

        using var scope = sp.CreateScope();
        var holder = scope.ServiceProvider.GetRequiredService<CallerScopeHolder>();

        holder.Scope.Should().BeSameAs(DenyAllScope.Instance,
            "fail-closed: an HTTP request with no scope must see nothing, not everything");
        holder.Scope.IsAccessDenied("/Anything").Should().BeTrue();
        holder.Scope.FilterArticles([]).Should().BeEmpty();
    }

    [Fact]
    public void HttpContextStore_TwoRequestsAreIsolated()
    {
        using var sp = BuildProvider(httpAware: true);
        var accessor = sp.GetRequiredService<IHttpContextAccessor>();

        accessor.HttpContext = new DefaultHttpContext();
        using (var s1 = sp.CreateScope())
        {
            var h1 = s1.ServiceProvider.GetRequiredService<CallerScopeHolder>();
            h1.Scope = new HttpCallerScope(true, [], []);
            h1.Scope.IsSuperadmin.Should().BeTrue();
        }

        // A new request: fresh HttpContext, scope must not leak from the previous one.
        accessor.HttpContext = new DefaultHttpContext();
        using (var s2 = sp.CreateScope())
        {
            var h2 = s2.ServiceProvider.GetRequiredService<CallerScopeHolder>();
            h2.Scope.Should().BeSameAs(DenyAllScope.Instance);
        }
    }

    [Fact]
    public void DefaultInstanceStore_LeaksAcrossChildScopes_DocumentsWhyWeNeedHttpContextStore()
    {
        // This test exists to pin the counter-example: if we ever revert to the plain
        // scoped InstanceCallerScopeStore, child scopes get a fresh holder and the ACL
        // set by middleware is invisible to the MCP tool. Its failure would mean the
        // framework no longer isolates child scopes the way we assume.
        using var sp = BuildProvider(httpAware: false);

        using var parent = sp.CreateScope();
        var parentHolder = parent.ServiceProvider.GetRequiredService<CallerScopeHolder>();
        parentHolder.Scope = new HttpCallerScope(
            isSuperadmin: false,
            denyPaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            allowPaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/Public" });

        using var child = parent.ServiceProvider.CreateScope();
        var childHolder = child.ServiceProvider.GetRequiredService<CallerScopeHolder>();

        childHolder.Scope.Should().BeSameAs(SystemCallerScope.Instance,
            "baseline: scoped holder in a child scope is a fresh instance — THIS is the bug " +
            "that HttpContextCallerScopeStore fixes");
    }
}
