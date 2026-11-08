namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary><c>/Admin/NodeIdentity</c> of 2.7.5 and earlier: the identity of this node is on the card "This device" of Admin &gt; Devices. The old address stays for bookmarks and links (a GET answers a redirect, with its query).</summary>
public class NodeIdentityRedirectModel() : LegacyPageRedirectModel(AdminRoutes.Devices, "this-device");
