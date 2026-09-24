namespace BeeMemoryBank.Web.Endpoints;

/// <summary>
/// No explicit routes: folder operations are pure passthroughs served from ProxyRouteTable by the
/// catch-all forwarder in MiscProxyEndpoints — "folders" (POST / PATCH / DELETE, the folder named
/// by the ?path= query string) and "folders/search". Folder zip export is the "downloads" pair
/// (POST prepare, GET single-use token). There is deliberately NO "folders/download" entry: no
/// browser caller needs one, so add it only if a caller ever appears.
/// </summary>
public static class FolderProxyEndpoints
{
    public static void MapFolderProxyEndpoints(this WebApplication app) { /* all routes in ProxyRouteTable */ }
}
