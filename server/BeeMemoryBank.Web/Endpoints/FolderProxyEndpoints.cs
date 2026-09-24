namespace BeeMemoryBank.Web.Endpoints;

/// <summary>
/// Every folder/download route here was a pure passthrough and moved into ProxyRouteTable
/// ("folders", "folders/search" and "folders/download" via the "folders" prefix; "downloads" for
/// prepare + token fetch) — the catch-all forwarder in MiscProxyEndpoints serves them now, with the
/// upstream Content-Disposition/Content-Type supplying the zip's download filename.
/// </summary>
public static class FolderProxyEndpoints
{
    public static void MapFolderProxyEndpoints(this WebApplication app) { /* all routes in ProxyRouteTable */ }
}
