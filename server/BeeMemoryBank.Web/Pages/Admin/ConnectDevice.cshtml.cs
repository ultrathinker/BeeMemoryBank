namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary><c>/Admin/ConnectDevice</c> of 2.7.5 and earlier: the join code is a dialog of Admin &gt; Devices, which this address opens. The old address stays for bookmarks and links (a GET answers a redirect, with its query).</summary>
public class ConnectDeviceRedirectModel() : LegacyPageRedirectModel(AdminRoutes.Devices, "connect-device");
