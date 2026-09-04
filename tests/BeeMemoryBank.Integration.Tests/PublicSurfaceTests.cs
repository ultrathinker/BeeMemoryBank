using System.Net;
using System.Net.Http.Json;
using BeeMemoryBank.Hosting.AspNetCore;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The list in <see cref="PublicSurface"/> is only worth having if it stays true, and it can rot in
/// two directions. An endpoint added without a thought becomes reachable by anyone the proxy lets
/// through (the old failure — the reverse proxy was the only thing that knew), or a listed path is
/// renamed and quietly stops being reachable by the peers that need it, which shows up months later
/// as "sync stopped working on one machine".
///
/// So: every endpoint the API maps must be either published on purpose or refused to a keyless
/// caller, and every published pattern must correspond to an endpoint that actually exists.
/// </summary>
public class PublicSurfaceTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _keyless = null!;

    public async Task InitializeAsync()
    {
        // CreateClient() stamps the internal key on every request; the whole point here is a caller
        // that does not have it, so this client is built without it.
        _keyless = _factory.CreateDefaultClient();
        await _factory.InitializeNodeAsync(password: "publicSurfacePassword");
    }

    public Task DisposeAsync()
    {
        _keyless.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TheVaultUnlockEndpoint_IsNotReachableWithoutTheInternalKey()
    {
        // The specific mistake this whole mechanism exists to prevent. /api/session/unlock takes a
        // password and, on success, unlocks the vault for every user and agent on the node at once;
        // a proxy misconfiguration that exposed it published a brute-forceable oracle with a
        // node-wide effect. It must be invisible, not merely rejected.
        var resp = await _keyless.PostAsJsonAsync("/api/session/unlock", new { password = "guess" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("/api/articles")]
    [InlineData("/api/users")]
    [InlineData("/api/keys")]
    [InlineData("/api/session/status")]
    [InlineData("/api/init/status")]
    [InlineData("/api/snapshots")]
    [InlineData("/api/whitelist")]
    public async Task NodeInternals_AnswerNotFoundToAKeylessCaller(string path)
    {
        (await _keyless.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Health_StaysReachable()
    {
        // The container healthcheck and any operator checking a node from outside depend on this.
        (await _keyless.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PeerEndpoints_AreReachedAndRejectedOnTheirOwnTerms()
    {
        // Not 404: a peer has no internal key by definition, so these must reach their handler and
        // fail on the sync handshake instead. A 404 here would mean sync is broken for every peer.
        var resp = await _keyless.GetAsync("/api/sync/identity");

        resp.StatusCode.Should().NotBe(HttpStatusCode.NotFound,
            "peers authenticate with the sync handshake, not with the internal key");
    }

    [Fact]
    public async Task TheInternalKey_StillOpensEverything()
    {
        // The web layer and the desktop tray must be completely unaffected by the gate.
        var withKey = _factory.CreateClient();

        (await withKey.GetAsync("/api/session/status")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void EveryPublishedPattern_MatchesAnEndpointThatExists()
    {
        var mapped = MappedRoutePatterns();

        // Catches the rename: a listed pattern that no longer resolves is a peer-facing endpoint
        // that has silently stopped being reachable.
        var orphaned = PublicSurface.Entries
            .Where(e => !mapped.Any(m => PatternsAgree(e.Pattern, m)))
            .Select(e => $"{e.Method ?? "*"} {e.Pattern}")
            .ToList();

        orphaned.Should().BeEmpty(
            "every entry in PublicSurface must correspond to a real endpoint — these do not, so " +
            "either the endpoint was renamed and the list needs updating, or the entry is dead. " +
            "Mapped routes are: " + string.Join(", ", mapped.OrderBy(m => m)));
    }

    [Fact]
    public void PublishedPaths_DoNotIncludeAnythingThatTouchesTheMasterPassword()
    {
        // A blunt guard against the class of mistake, not just the one instance: nothing that takes
        // or changes the master password belongs on a list of things anonymous callers may reach.
        // /api/join is the deliberate exception — joining IS authorised by the master password, and
        // a joining node has nothing else to present — so it is named here rather than excluded by
        // a pattern that would also let a future sibling through unnoticed.
        string[] forbidden = ["unlock", "reset", "password", "keys"];

        var offenders = PublicSurface.Entries
            .Where(e => !e.Pattern.Equals("/api/join", StringComparison.OrdinalIgnoreCase))
            .Where(e => forbidden.Any(f => e.Pattern.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Select(e => e.Pattern)
            .ToList();

        offenders.Should().BeEmpty();
    }

    private List<string> MappedRoutePatterns()
    {
        var sources = _factory.Services.GetRequiredService<EndpointDataSource>();
        return sources.Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/'))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// True when a published pattern and a mapped route describe the same path. Both sides use
    /// braces for parameters but not the same NAMES (<c>{eventId}</c> here, <c>{id:guid}</c>
    /// there), so segments are compared structurally: a parameter matches a parameter, and "**"
    /// matches any tail.
    /// </summary>
    private static bool PatternsAgree(string published, string mapped)
    {
        var publishedSegments = published.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var mappedSegments = mapped.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < publishedSegments.Length; i++)
        {
            // A published subtree is satisfied by anything at or below its root. "Below" covers
            // /api/sync/** (many mapped routes, no route at the root itself); "at" covers /mcp/**,
            // where the SDK maps one handler for the whole subtree and the sub-paths are its
            // business rather than ours.
            if (publishedSegments[i] == "**") return mappedSegments.Length >= i;
            if (i >= mappedSegments.Length) return false;

            var isPublishedParam = publishedSegments[i].StartsWith('{');
            var isMappedParam = mappedSegments[i].StartsWith('{');
            if (isPublishedParam || isMappedParam)
            {
                if (isPublishedParam != isMappedParam) return false;
                continue;
            }

            if (!string.Equals(publishedSegments[i], mappedSegments[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return mappedSegments.Length == publishedSegments.Length;
    }
}
