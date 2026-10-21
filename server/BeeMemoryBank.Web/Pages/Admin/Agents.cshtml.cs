using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

public class AgentsModel(ApiClient api) : AdminPageModel(api)
{
    /// <summary>The agents of every user (the Profile page lists a person's own).</summary>
    public List<AgentDto>? Agents { get; private set; }

    public async Task OnGetAsync() => Agents = await Api.GetAgentsAsync(all: true);

    public async Task<IActionResult> OnPostDeleteAgentAsync(int agentId)
    {
        var ok = await Api.DeleteAgentAsync(agentId);
        return ok
            ? Back(msg: "Agent revoked")
            : Back(err: "Failed to revoke agent");
    }
}
