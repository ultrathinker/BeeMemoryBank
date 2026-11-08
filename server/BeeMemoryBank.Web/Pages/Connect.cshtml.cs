namespace BeeMemoryBank.Web.Pages;

/// <summary><c>/Connect</c> of earlier versions: the join code is a dialog of <c>/Admin/Devices</c> now, which this address opens. (<c>/connect/ca.crt</c>, the certificate download, is a file and stays.)</summary>
public class ConnectRedirectModel() : LegacyPageRedirectModel(Admin.AdminRoutes.Devices, "connect-device");
