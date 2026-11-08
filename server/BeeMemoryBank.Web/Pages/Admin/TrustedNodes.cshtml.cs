namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary><c>/Admin/TrustedNodes</c> of 2.7.5 and earlier: the list of the devices, and the page of each device, replaced it. The old address stays for bookmarks and links (a GET answers a redirect, with its query).</summary>
public class TrustedNodesRedirectModel() : LegacyPageRedirectModel(AdminRoutes.Devices);
