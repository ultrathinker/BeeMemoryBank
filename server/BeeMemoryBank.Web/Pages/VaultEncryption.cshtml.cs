namespace BeeMemoryBank.Web.Pages;

/// <summary>
/// <c>/VaultEncryption</c> of earlier versions: its status, "Encrypt this node" and the start-unlocked switch of a server are <c>/Admin/StorageProtection</c> now, and
/// the OS auto-unlock of a desktop is <c>/Admin/OsAutoUnlock</c>. The old address goes to the first, which is where most of it went.
/// </summary>
public class VaultEncryptionRedirectModel() : LegacyPageRedirectModel(Admin.AdminRoutes.StorageProtection);
