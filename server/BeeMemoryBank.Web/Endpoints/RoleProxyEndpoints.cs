namespace BeeMemoryBank.Web.Endpoints;

/// <summary>
/// No explicit routes: role administration is a pure passthrough served from ProxyRouteTable
/// ("roles", plus role folder rules under "restrictions") by the catch-all forwarder in
/// MiscProxyEndpoints — superadmin-gated in the table, API error bodies passed through verbatim.
/// </summary>
public static class RoleProxyEndpoints
{
    public static void MapRoleProxyEndpoints(this WebApplication app) { /* all routes in ProxyRouteTable */ }
}
