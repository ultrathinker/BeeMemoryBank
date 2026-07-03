namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// W3 (Option A): a logged-in user whose security stamp is invalidated (superadmin demotes /
/// changes their password / deletes them) must be rejected on their NEXT authenticated request
/// once the 5-minute stamp-cache TTL elapses — i.e. redirected to /Login, not served normally.
///
/// This test is SKIPPED on purpose. The existing harness (BmbWebApplicationFactory) drives the
/// API host only: it is a WebApplicationFactory targeting the API's Program, and CreateClient()
/// injects X-Internal-Key + X-User-Role headers directly. It does NOT exercise the WEB cookie
/// pipeline (BeeMemoryBank.Web: AddAuthentication("BeeWebCookie"), the OnValidatePrincipal
/// security-stamp revalidation, the SecurityStamp claim embedded at login, the IMemoryCache
/// stamp TTL). This Integration.Tests project does not even reference the Web project, so driving
/// the cookie flow is out of reach without a new harness.
///
/// To un-skip, build a WebApplicationFactory&lt;Web.Program&gt; wired to an in-process (or fake)
/// API, then:
/// </summary>
public class SecurityStampCookieRevocationTests
{
    [Fact(Skip = "TODO: needs a Web cookie-flow harness (WebApplicationFactory<Web.Program> + cookie login); the existing Api-only BmbWebApplicationFactory cannot exercise this path")]
    public void DeletedUser_Cookie_IsRejected_AfterStampCacheTtl()
    {
        // 1. Initialize the node; create a regular (non-superadmin) user.
        // 2. Log in as that user via the Web /Login page; capture the bee_session cookie +
        //    assert a SecurityStamp claim was embedded.
        // 3. As superadmin, delete the user (or change their password / role) so the stored
        //    security stamp is bumped.
        // 4. Evict / wait out the security_stamp_{userId} IMemoryCache entry (5-min TTL).
        // 5. Re-issue an authenticated request carrying the stale cookie.
        //
        // ASSERT: the response is a redirect to /Login (302) or otherwise NOT authenticated —
        // proving OnValidatePrincipal called RejectPrincipal on the 404 / stamp-mismatch, rather
        // than failing open. Also assert a transport error (API down) still FAILS OPEN (keeps the
        // session) so this test does not regress the fail-open guarantee.
    }
}
