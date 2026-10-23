namespace BeeMemoryBank.Web.Pages;

/// <summary><c>/Connect</c> of earlier versions: the page is <c>/Admin/ConnectDevice</c> now. (<c>/connect/ca.crt</c>, the certificate download, is a file and stays.)</summary>
public class ConnectRedirectModel() : LegacyPageRedirectModel(Admin.AdminRoutes.ConnectDevice);
