namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary><c>/Admin/DevicesOnMyNetwork</c> of 2.7.5 and earlier: it is a card of Admin &gt; Network settings. The old address stays for bookmarks and links (a GET answers a redirect, with its query).</summary>
public class DevicesOnMyNetworkRedirectModel() : LegacyPageRedirectModel(AdminRoutes.NetworkSettings, "devices-on-my-network");
