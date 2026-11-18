using System.CommandLine;
using System.Text.Json;

namespace BeeMemoryBank.Cli.Commands;

public static partial class BlindCommand
{
    static partial void AddFullNodeCommands(Command blind, Option<string> dataOption)
    {
        var invite = new Command("invite", "Issue an invitation for a blind copy on this full node");
        var inviteShared = new Option<bool>("--shared", "Mark the invited blind node as shared by the organisation (default when --owner is omitted)");
        var inviteOwner = new Option<string?>("--owner", "Username or display name of the person who owns this blind device");
        var inviteLabel = new Option<string?>("--label", "Optional note for who this invitation is for");
        var inviteSource = new Option<string?>("--source-name", "Name shown to the recipient");
        var inviteAddress = new Option<string?>("--address", "Public https address the invited node will call");
        var inviteLifetime = new Option<int>("--lifetime", () => 30, "Lifetime in minutes (15, 30, or 60)");
        invite.AddOption(inviteShared);
        invite.AddOption(inviteOwner);
        invite.AddOption(inviteLabel);
        invite.AddOption(inviteSource);
        invite.AddOption(inviteAddress);
        invite.AddOption(inviteLifetime);
        invite.SetHandler(async ctx =>
        {
            Environment.Exit(await HandleInviteAsync(
                ctx.ParseResult.GetValueForOption(dataOption)!,
                shared: ctx.ParseResult.GetValueForOption(inviteShared),
                owner: ctx.ParseResult.GetValueForOption(inviteOwner),
                label: ctx.ParseResult.GetValueForOption(inviteLabel),
                sourceName: ctx.ParseResult.GetValueForOption(inviteSource),
                address: ctx.ParseResult.GetValueForOption(inviteAddress),
                lifetimeMinutes: ctx.ParseResult.GetValueForOption(inviteLifetime)));
        });
        blind.AddCommand(invite);
    }

    public static async Task<int> HandleInviteAsync(
        string dataPath,
        bool shared = false,
        string? owner = null,
        string? label = null,
        string? sourceName = null,
        string? address = null,
        int lifetimeMinutes = 30,
        HttpClient? httpOverride = null,
        TextWriter? output = null)
    {
        output ??= Console.Out;
        var hasOwner = !string.IsNullOrWhiteSpace(owner);
        if (shared && hasOwner)
        {
            await output.WriteLineAsync("Error: choose either --shared or --owner, not both.");
            return 2;
        }

        using var ownedHttp = httpOverride is null ? NodeApi.CreateClient(dataPath) : null;
        var http = httpOverride ?? ownedHttp!;

        string ownership = "shared";
        Guid? ownerPersonId = null;
        string? resolvedOwnerLabel = null;
        if (hasOwner)
        {
            var usersResp = await http.GetAsync("/api/users");
            if (!usersResp.IsSuccessStatusCode)
                return await FailAsync(output, (int)usersResp.StatusCode, await usersResp.Content.ReadAsStringAsync());

            using var usersDoc = JsonDocument.Parse(await usersResp.Content.ReadAsStringAsync());
            var needle = owner!.Trim();
            var matches = new List<(Guid PersonId, string Display)>();
            foreach (var u in usersDoc.RootElement.EnumerateArray())
            {
                var username = Str(u, "username") ?? "";
                var display = Str(u, "displayName") ?? "";
                var pidText = Str(u, "personId");
                if (!Guid.TryParse(pidText, out var pid) || pid == Guid.Empty) continue;
                if (string.Equals(username, needle, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(display, needle, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add((pid, string.IsNullOrWhiteSpace(display) ? username : display));
                }
            }
            if (matches.Count == 0)
            {
                await output.WriteLineAsync($"Error: no active user matched '{needle}'.");
                return 1;
            }
            if (matches.Count > 1)
            {
                await output.WriteLineAsync($"Error: multiple active users matched '{needle}'; pass the exact username.");
                return 1;
            }
            ownership = "personal";
            ownerPersonId = matches[0].PersonId;
            resolvedOwnerLabel = matches[0].Display;
        }

        var payload = new
        {
            label,
            sourceName,
            lifetimeMinutes,
            address,
            trust = "auto",
            ownership,
            ownerPersonId
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/blind-nodes/invites/")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
        };
        var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            return await FailAsync(output, (int)resp.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        await output.WriteLineAsync($"Invite ID: {Str(root, "inviteId")}");
        await output.WriteLineAsync(resolvedOwnerLabel is null ? "Ownership: shared" : $"Ownership: personal ({resolvedOwnerLabel})");
        await output.WriteLineAsync($"Expires:   {Str(root, "expiresAt")}");
        await output.WriteLineAsync(Str(root, "text") ?? "");
        return 0;
    }
}
