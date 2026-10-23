namespace BeeMemoryBank.Web.Pages;

/// <summary><c>/PromotePasswordPeers</c> of earlier versions: it is a card of <c>/Admin/TrustedNodes</c> now, shown while there are devices to offer.</summary>
public class PromotePasswordPeersRedirectModel() : LegacyPageRedirectModel(Admin.AdminRoutes.TrustedNodes, "promote-password-peers");
