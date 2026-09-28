using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

/// <summary>
/// "Add an Android blind copy" (plan section 10), reached from Blind nodes: paste the phone's code, pick
/// the node the phone will call, show the phone its "where to call" code. Superadmin only, like Blind
/// nodes: it adds a peer and seals a key under the DEK.
/// </summary>
[Authorize(Roles = "superadmin")]
public class AndroidBlindCopyModel(ApiClient api) : PageModel
{
    public List<BlindPhoneListenerDto>? Listeners { get; set; }
    public BlindPhonePairedDto? Paired { get; set; }
    public string? CallCodeQr { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync() => Listeners = await api.ListBlindPhoneListenersAsync();

    public async Task OnPostAsync(string? code, Guid listenerId)
    {
        if (string.IsNullOrWhiteSpace(code))
            ErrorMessage = "Paste the code shown by the phone.";
        else
        {
            var (paired, error) = await api.PairBlindPhoneAsync(code.Trim(), listenerId);
            Paired = paired;
            ErrorMessage = error;
            if (paired != null) CallCodeQr = ConnectModel.GenerateQrPngDataUri(paired.CallCode);
        }
        if (Paired == null) Listeners = await api.ListBlindPhoneListenersAsync();
    }
}
