namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary><c>/Admin/InvisibleMode</c> of 2.7.5 and earlier: it is a card of Admin &gt; Network settings. The old address stays for bookmarks and links (a GET answers a redirect, with its query).</summary>
public class InvisibleModeRedirectModel() : LegacyPageRedirectModel(AdminRoutes.NetworkSettings, "invisible-mode");
