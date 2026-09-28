using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.ViewComponents;

/// <summary>
/// Recovery-box warnings for the admin (plan 5.6, 6.3, 6.5): a device whose master password differs
/// from this PC's keeps a weaker box on the blind nodes, and no box holding the current key means a
/// restore would only reach data up to the last key that has one. A view component so the page that
/// shows it needs one line, not a new property and fetch in its page model.
/// </summary>
public class RecoveryWarningsViewComponent(ApiClient api) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync() =>
        View(await api.GetRecoveryStatusAsync());
}
