using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>
/// Storage protection (BMB-305): an encrypted node has nothing left to switch on here, the page is the node's storage protection - its status and, on a
/// server, whether it starts unlocked after a restart. A node whose database is still a plain file is offered "Database encryption", with its way to
/// "Encrypt this node" (the <c>/VaultEncryption</c> page).
/// </summary>
public class StorageProtectionModel(ApiClient api) : AdminPageModel(api)
{
    /// <summary>What the node says about its database (docs/design/step-b-spec.md 6.7); null when it did not answer.</summary>
    public VaultStatusDto? VaultStatus { get; private set; }

    /// <summary>The role of this node (spec 11.0): <c>server</c> sets whether it opens by itself on the encryption page.</summary>
    public string VaultRole => VaultStatus?.AutoUnlock?.Role ?? "desktop";

    public async Task OnGetAsync() => VaultStatus = await Api.GetVaultStatusAsync();
}
