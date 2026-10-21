using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>AI / Chat (OpenRouter): the node-wide keys, the model catalogue and the defaults. All of it is read and saved by <c>admin.js</c> through <c>/api-proxy/chat/*</c>, so the page has no data and no handler.</summary>
public class AiChatModel(ApiClient api) : AdminPageModel(api)
{
    public void OnGet() { }
}
