namespace BeeMemoryBank.Web.Endpoints;

/// <summary>
/// Every route here was a pure passthrough and moved into ProxyRouteTable
/// ("users" incl. "users/me/change-password", "restrictions", "hard-delete", "keys", "agents") —
/// the catch-all forwarder in MiscProxyEndpoints serves them now, with the same superadmin gates
/// the explicit routes carried (the API enforces them as well; error bodies now come through
/// verbatim instead of being re-wrapped).
/// </summary>
public static class UserProxyEndpoints
{
    public static void MapUserProxyEndpoints(this WebApplication app) { /* all routes in ProxyRouteTable */ }
}
