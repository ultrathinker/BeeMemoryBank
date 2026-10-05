using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>
/// Asks the running node to hand its open vault over to the process that starts after an app
/// update (<c>POST /node/update/unlock-handoff</c>, see the API's UpdateUnlockHandoff), so the user
/// is not asked for the password again. Best effort: any failure only means one extra login.
/// </summary>
public static class NodeSessionHandoff
{
    private static readonly NodeFrontClient Client = new();

    public static async Task<bool> RequestAsync(string? frontUrl)
    {
        // Only a node this app hosted has a key in our environment (NodeLifecycleService sets it).
        var key = NodeFrontClient.KeyFromEnvironment();
        if (string.IsNullOrEmpty(frontUrl) || key is null) return false;

        var reply = await Client.SendAsync(HttpMethod.Post, frontUrl, "/node/update/unlock-handoff", key, asSuperadmin: true).ConfigureAwait(false);
        if (reply.Failure == NodeFrontFailure.None) return reply.IsSuccessStatus;

        Console.WriteLine(reply.Failure == NodeFrontFailure.NotLoopback
            ? "Session handoff before update failed: the node is not on this computer, so nothing was sent."
            : $"Session handoff before update failed: {reply.ErrorMessage ?? reply.Failure.ToString()}");
        return false;
    }
}
