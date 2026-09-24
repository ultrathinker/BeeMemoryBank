namespace BeeMemoryBank.Web.Endpoints;

/// <summary>
/// Every route here was a pure passthrough and moved into ProxyRouteTable
/// ("roles" and role folder rules under the "restrictions" prefix) — the catch-all forwarder in
/// MiscProxyEndpoints serves them now, superadmin-gated in the table the same way these explicit
/// routes were, with the API's own error bodies passed through verbatim.
/// </summary>
public static class RoleProxyEndpoints
{
    public static void MapRoleProxyEndpoints(this WebApplication app) { /* all routes in ProxyRouteTable */ }
}
