namespace BeeMemoryBank.Web.Endpoints;

/// <summary>
/// No explicit routes: user administration is a pure passthrough served from ProxyRouteTable
/// ("users" incl. "users/me/change-password", "restrictions", "hard-delete", "keys/add-recovery",
/// "agents") by the catch-all forwarder in MiscProxyEndpoints, with superadmin gates in the table
/// (the API enforces them as well) and API error bodies passed through verbatim.
/// </summary>
public static class UserProxyEndpoints
{
    public static void MapUserProxyEndpoints(this WebApplication app) { /* all routes in ProxyRouteTable */ }
}
