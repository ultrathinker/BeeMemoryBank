namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary><c>/Admin/RestoredDevices</c> of 2.7.5 and earlier: the devices a restore left waiting are a block of Admin &gt; Devices. The old address stays for bookmarks and links (a GET answers a redirect, with its query).</summary>
public class RestoredDevicesRedirectModel() : LegacyPageRedirectModel(AdminRoutes.Devices, "restored-peers");
