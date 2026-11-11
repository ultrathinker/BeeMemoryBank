using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>AI / Chat: the model providers (each with its keys and models, approved by a superadmin), the default models and their fallback chain, and the
/// chat switch. All of it is read and saved by <c>ai-providers.js</c> through <c>/api-proxy/chat/*</c>, so the page has no data and no handler.</summary>
public class AiChatModel(ApiClient api) : AdminPageModel(api)
{
    public override IReadOnlyList<string> PageScripts => ["/js/pages/ai-providers.js"];

    public void OnGet() { }
}
