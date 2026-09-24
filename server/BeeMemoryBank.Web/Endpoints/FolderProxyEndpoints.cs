namespace BeeMemoryBank.Web.Endpoints;

/// <summary>
/// Every route this file served was a pure passthrough and moved into ProxyRouteTable:
/// "folders" (POST / PATCH / DELETE — the folder is named by the ?path= query string) and
/// "folders/search". Folder zip export is the "downloads" pair (POST prepare, GET single-use
/// token), as before. The old GET /api-proxy/folders/download died with the migration: it had
/// no browser caller and its ApiClient method (DownloadFolderZipAsync) was removed as
/// zero-reference, so there is deliberately NO "folders/download" entry — add one only if a
/// caller ever appears. The catch-all forwarder in MiscProxyEndpoints serves everything above.
/// </summary>
public static class FolderProxyEndpoints
{
    public static void MapFolderProxyEndpoints(this WebApplication app) { /* all routes in ProxyRouteTable */ }
}
