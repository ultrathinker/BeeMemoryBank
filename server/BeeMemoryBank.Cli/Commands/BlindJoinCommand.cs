using System.CommandLine;
using System.Text.Json;

namespace BeeMemoryBank.Cli.Commands;

/// <summary>
/// `bmb blind join [invitation]` (release 2.7): a server blind node joins a full node with the invitation its owner made, instead of the owner pasting this node's pair code. The
/// command only talks to the RUNNING node over its local console API (<c>/api/blind/join</c>, the internal key, exactly as `status` does): the node reads and checks the invitation,
/// probes the full node, redeems with its own pair code and keys, and asks for the owner's decision. This command never reads a key file and never prints the invitation, a secret
/// or a key - only the preview the node sends back and the match code.
///
/// <para>The invitation is a one-time bearer capability: given as an argument it is visible to the host's other accounts in the process list and in the shell history for as long
/// as it lives (at most an hour), so `-` reads it from standard input (<c>docker exec -i … bmb blind join - &lt; invitation.txt</c>).</para>
/// </summary>
public static class BlindJoinCommand
{
    /// <summary>How the command behaves; the defaults are the interactive ones.</summary>
    public sealed record JoinOptions(bool Yes = false, bool NoWait = false, bool StatusOnly = false, bool Cancel = false);

    public static void AddTo(Command blind, Option<string> dataOption)
    {
        var join = new Command("join", "Join a full node with its invitation (bmb-invite:...); this node becomes a blind copy of that memory bank");
        var invitation = new Argument<string?>("invitation", () => null, "The invitation line, or - to read it from standard input (not visible to other accounts)");
        var yes = new Option<bool>(["--yes", "-y"], "Join without asking (needed when the invitation comes from standard input)");
        var noWait = new Option<bool>("--no-wait", "Print the match code and return; look at the answer later with --status");
        var statusOnly = new Option<bool>("--status", "Show where the join stands now");
        var cancel = new Option<bool>("--cancel", "Give up a join that waits for the owner's answer");
        join.AddArgument(invitation);
        join.AddOption(yes);
        join.AddOption(noWait);
        join.AddOption(statusOnly);
        join.AddOption(cancel);
        join.SetHandler(async ctx =>
        {
            var options = new JoinOptions(
                ctx.ParseResult.GetValueForOption(yes), ctx.ParseResult.GetValueForOption(noWait),
                ctx.ParseResult.GetValueForOption(statusOnly), ctx.ParseResult.GetValueForOption(cancel));
            Environment.Exit(await HandleJoinAsync(
                ctx.ParseResult.GetValueForOption(dataOption)!, ctx.ParseResult.GetValueForArgument(invitation), options, ct: ctx.GetCancellationToken()));
        });
        blind.AddCommand(join);
    }

    /// <summary>First wait, longest wait: the node asks the full node no oftener than every few seconds whatever this command does; 5 s at first, backing off to 30 s.</summary>
    public static readonly TimeSpan FirstPoll = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan LongestPoll = TimeSpan.FromSeconds(30);

    /// <summary>The local node not answering this many times in a row ends the waiting (the join itself goes on in the node).</summary>
    private const int MaxLocalFailures = 6;

    public static async Task<int> HandleJoinAsync(
        string dataPath, string? invitationArgument, JoinOptions options,
        BlindApiOptions? api = null, TextWriter? output = null, TextReader? input = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, CancellationToken ct = default)
    {
        output ??= Console.Out;
        input ??= Console.In;
        delay ??= (span, token) => Task.Delay(span, token);
        var client = BlindCommand.Create(dataPath, api);

        if (options.Cancel) return await PrintViewAsync(output, await client.SendAsync("DELETE", "api/blind/join"));
        if (options.StatusOnly) return await PrintViewAsync(output, await client.SendAsync("GET", "api/blind/join"));

        var fromInput = invitationArgument == "-";
        var text = fromInput ? (await input.ReadToEndAsync(ct)).Trim() : invitationArgument?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            await output.WriteLineAsync("Give the invitation: bmb blind join <invitation>   (or  -  to read it from standard input).");
            return 1;
        }
        var body = JsonSerializer.Serialize(new { invitation = text });

        // What the invitation says, read by the node from the text alone: nothing is sent anywhere yet.
        var (previewStatus, previewBody) = await client.SendAsync("POST", "api/blind/join/preview", body);
        if (previewStatus != 200) return await FailAsync(output, previewStatus, previewBody);
        using (var preview = JsonDocument.Parse(previewBody))
        {
            var p = preview.RootElement;
            await output.WriteLineAsync($"Invitation from:  {Str(p, "sourceName")}");
            await output.WriteLineAsync($"Full node:        {Str(p, "address")}");
            await output.WriteLineAsync($"Certificate:      {(Str(p, "trust") == "pin" ? "a pinned certificate named in the invitation" : "the normal certificate of that address")}");
            await output.WriteLineAsync($"                  {Str(p, "validity")}");
            await output.WriteLineAsync($"Limits:           {Str(p, "limits")}");
            await output.WriteLineAsync("Once they confirm it, they manage this blind node (reseed, anchors, recovery) and it holds a blind copy of their memory bank.");
        }

        if (!options.Yes)
        {
            if (fromInput)
            {
                await output.WriteLineAsync("The invitation came from standard input, so there is nobody to ask. Run it again with --yes to join.");
                return 1;
            }
            await output.WriteAsync("Join this full node? [y/N] ");
            var answer = (await input.ReadLineAsync(ct))?.Trim();
            if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            {
                await output.WriteLineAsync("Nothing was done.");
                return 1;
            }
        }

        var (startStatus, startBody) = await client.SendAsync("POST", "api/blind/join", body);
        if (startStatus != 200) return await FailAsync(output, startStatus, startBody);
        string? shown = null;
        using (var started = JsonDocument.Parse(startBody))
        {
            var v = started.RootElement;
            await output.WriteLineAsync();
            await output.WriteLineAsync($"Match code:  {Str(v, "matchCode")}");
            await output.WriteLineAsync($"Read this code out to {Str(v, "sourceName")}. They compare it with the one on their Blind nodes page and confirm this node with their master password.");
            if (Str(v, "confirmBy") is { } until) await output.WriteLineAsync($"They have until {until} to answer.");
        }
        if (options.NoWait)
        {
            await output.WriteLineAsync("Look at the answer later with: bmb blind join --status");
            return 0;
        }

        // Waiting for the owner. The node asks the full node (signed) when this command asks it; a few seconds apart, backing off.
        await output.WriteLineAsync("Waiting for the owner's answer (Ctrl+C stops waiting; the join goes on in the node)...");
        var wait = FirstPoll;
        var failures = 0;
        while (true)
        {
            try { await delay(wait, ct); }
            catch (OperationCanceledException)
            {
                await output.WriteLineAsync("Stopped waiting. The join goes on in the node; look at it with: bmb blind join --status");
                return 1;
            }
            wait = TimeSpan.FromSeconds(Math.Min(LongestPoll.TotalSeconds, wait.TotalSeconds * 1.5));

            var (status, answer) = await client.SendAsync("GET", "api/blind/join");
            if (status != 200)
            {
                if (++failures >= MaxLocalFailures)
                {
                    await output.WriteLineAsync($"The node does not answer ({LineOf(status, answer)}). The join goes on in the node; look at it later with: bmb blind join --status");
                    return 1;
                }
                continue;
            }
            failures = 0;
            using var doc = JsonDocument.Parse(answer);
            var view = doc.RootElement;
            var message = Str(view, "message");
            switch (Str(view, "state"))
            {
                case "confirmed":
                    await output.WriteLineAsync(message);
                    return 0;
                case "refused" or "expired" or "none":
                    await output.WriteLineAsync(message);
                    return 1;
                default:
                    // Still waiting: say so again only when the words changed (a hub that could not be reached, a clock that is off).
                    if (message != shown && message is not null && !message.StartsWith("Waiting for ", StringComparison.Ordinal))
                        await output.WriteLineAsync(message);
                    shown = message;
                    break;
            }
        }
    }

    /// <summary>Prints the view the node answered (cancel, status) and returns the exit code.</summary>
    private static async Task<int> PrintViewAsync(TextWriter output, (int Status, string Body) answer)
    {
        if (answer.Status != 200) return await FailAsync(output, answer.Status, answer.Body);
        using var doc = JsonDocument.Parse(answer.Body);
        var view = doc.RootElement;
        await output.WriteLineAsync(Str(view, "message"));
        return Str(view, "state") is "refused" or "expired" ? 1 : 0;
    }

    /// <summary>The node's refusal in its own sentence (the <c>error</c> member), not the raw body.</summary>
    private static async Task<int> FailAsync(TextWriter output, int status, string body)
    {
        await output.WriteLineAsync(LineOf(status, body));
        return 1;
    }

    private static string LineOf(int status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && Str(doc.RootElement, "error") is { Length: > 0 } sentence) return sentence;
        }
        catch (JsonException)
        {
            // Not JSON: say the status only (a proxy's page is not shown).
        }
        return status == 404
            ? "This node does not know bmb blind join (HTTP 404): it is older than 2.7. Update the node first."
            : $"HTTP {status}";
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;
}
