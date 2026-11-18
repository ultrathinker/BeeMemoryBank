using System.Net.Http.Json;
using System.Text.Json;

namespace BeeMemoryBank.Cli.Commands;

/// <summary>
/// The removal of a superadmin from the command line (release 2.12.0, plan WS-E): <c>bmb user remove-superadmin</c> starts the process,
/// <c>bmb offboarding status</c> shows where it stands and what it waits for, <c>bmb offboarding continue</c> runs it on (the review's
/// pre-selected choices, the password the master-key change needs). All through the running node's Api with the internal key, as
/// <c>bmb dek-rotate</c> does; the administrator who acts is named with <c>--as</c> and proves it with their own password, which is never an
/// argument (a terminal prompt with no echo, or <c>--password-stdin</c>). Every refusal is the node's own plain sentence.
/// </summary>
public static class OffboardingCommand
{
    /// <summary>The tests' node: an <see cref="HttpClient"/> over a fake handler instead of the running node.</summary>
    public static Func<HttpClient>? ClientOverride { get; set; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static HttpClient Client(string dataPath) => ClientOverride?.Invoke() ?? NodeApi.CreateClient(dataPath, TimeSpan.FromMinutes(30));

    /// <summary><c>bmb user remove-superadmin NAME (--demote-to ROLE | --delete) --as ADMIN [--open-role] [--folder ID[:ro]]... [--password-stdin]</c>.</summary>
    public static async Task<int> RemoveSuperadminAsync(
        string dataPath, string name, string? demoteTo, bool delete, string? actingUser, bool openRole, IReadOnlyList<string>? folders, bool passwordStdin,
        TextReader? input = null, bool? inputRedirected = null, TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        if (delete == !string.IsNullOrWhiteSpace(demoteTo))
        {
            await error.WriteLineAsync("Say how the removal ends: --delete (the account is deleted) or --demote-to <role> (they stay as an ordinary user).");
            return 1;
        }
        if (string.IsNullOrWhiteSpace(actingUser))
        {
            await error.WriteLineAsync("Say which superadmin acts: --as <your user name>. Your own password confirms it.");
            return 1;
        }
        if (delete && (openRole || folders is { Count: > 0 }))
        {
            await error.WriteLineAsync("--open-role and --folder say what a demoted person may see; a deleted account sees nothing.");
            return 1;
        }
        var rules = new List<object>();
        foreach (var folder in folders ?? [])
        {
            var parts = folder.Split(':', 2);
            if (!Guid.TryParse(parts[0], out var folderId) || (parts.Length == 2 && parts[1] != "ro"))
            {
                await error.WriteLineAsync($"--folder takes a folder id, optionally followed by ':ro' (read-only): '{folder}' is not one.");
                return 1;
            }
            rules.Add(new { folderId, effect = "allow", isReadOnly = parts.Length == 2 });
        }

        try
        {
            using var http = Client(dataPath);
            using var usersResponse = await http.GetAsync("/api/users");
            if (!usersResponse.IsSuccessStatusCode) return await RefusedAsync(usersResponse, error);
            var users = await usersResponse.Content.ReadFromJsonAsync<JsonElement>();
            var target = users.ValueKind == JsonValueKind.Array
                ? users.EnumerateArray().FirstOrDefault(u => string.Equals(u.GetProperty("username").GetString(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                : default;
            if (target.ValueKind != JsonValueKind.Object)
            {
                await error.WriteLineAsync($"There is no user named '{name.Trim()}' on this node.");
                return 1;
            }

            var password = PasswordInput.Resolve(null, passwordStdin, $"Password of {actingUser.Trim()}: ", input ?? Console.In,
                inputRedirected ?? Console.IsInputRedirected, error);
            if (string.IsNullOrEmpty(password)) return 1;

            using var started = await http.PostAsJsonAsync("/api/offboarding", new
            {
                userId = target.GetProperty("id").GetInt32(),
                target = delete ? "delete" : "demote",
                role = delete ? null : demoteTo!.Trim(),
                folderRules = rules.Count > 0 ? rules : null,
                allowOpenRole = openRole,
                password,
                actingUser = actingUser.Trim(),
            });
            if (!started.IsSuccessStatusCode) return await RefusedAsync(started, error);
            var view = await started.Content.ReadFromJsonAsync<JsonElement>();
            await output.WriteLineAsync("The removal has started. They are signed out everywhere and can no longer sign in.");
            await PrintAsync(view, output);
            await output.WriteLineAsync($"Follow it with: bmb offboarding status {view.GetProperty("processId").GetString()}");
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            await error.WriteLineAsync("The node did not answer. Is it running? (" + ex.GetType().Name + ")");
            return 1;
        }
    }

    /// <summary><c>bmb offboarding status [PROCESS-ID]</c>: one removal, or every removal this node knows of.</summary>
    public static async Task<int> StatusAsync(string dataPath, string? processId, TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        try
        {
            using var http = Client(dataPath);
            if (!string.IsNullOrWhiteSpace(processId))
            {
                if (!Guid.TryParse(processId, out var id))
                {
                    await error.WriteLineAsync($"'{processId}' is not a process id.");
                    return 1;
                }
                using var one = await http.GetAsync($"/api/offboarding/{id:D}");
                if (!one.IsSuccessStatusCode) return await RefusedAsync(one, error);
                await PrintAsync(await one.Content.ReadFromJsonAsync<JsonElement>(), output);
                return 0;
            }
            using var all = await http.GetAsync("/api/offboarding");
            if (!all.IsSuccessStatusCode) return await RefusedAsync(all, error);
            var list = await all.Content.ReadFromJsonAsync<JsonElement>();
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            {
                await output.WriteLineAsync("Nobody is being removed, and nobody was.");
                return 0;
            }
            foreach (var view in list.EnumerateArray())
            {
                await PrintAsync(view, output);
                await output.WriteLineAsync();
            }
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            await error.WriteLineAsync("The node did not answer. Is it running? (" + ex.GetType().Name + ")");
            return 1;
        }
    }

    /// <summary>
    /// <c>bmb offboarding continue PROCESS-ID [--as ADMIN] [--accept-review] [--password-stdin]</c>: takes the review's pre-selected choices when asked
    /// to, gives the password when the master-key change waits for it, and runs every step that can run now.
    /// </summary>
    public static async Task<int> ContinueAsync(
        string dataPath, string processId, string? actingUser, bool acceptReview, bool passwordStdin,
        TextReader? input = null, bool? inputRedirected = null, TextWriter? output = null, TextWriter? error = null, IReadOnlyList<string>? keep = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        if (!Guid.TryParse(processId, out var id))
        {
            await error.WriteLineAsync($"'{processId}' is not a process id.");
            return 1;
        }
        try
        {
            using var http = Client(dataPath);
            using var first = await http.GetAsync($"/api/offboarding/{id:D}");
            if (!first.IsSuccessStatusCode) return await RefusedAsync(first, error);
            var view = await first.Content.ReadFromJsonAsync<JsonElement>();
            if (!view.GetProperty("open").GetBoolean())
            {
                await PrintAsync(view, output);
                return 0;
            }

            string? password = null;
            string? Password() => password ??= PasswordInput.Resolve(null, passwordStdin, $"Password of {actingUser!.Trim()}: ", input ?? Console.In,
                inputRedirected ?? Console.IsInputRedirected, error);

            if (acceptReview || keep is { Count: > 0 })
            {
                if (string.IsNullOrWhiteSpace(actingUser))
                {
                    await error.WriteLineAsync("Say which superadmin decides the review: --as <your user name>.");
                    return 1;
                }
                // Every superadmin is kept only by name: --keep superadmin:<id>; --accept-review never decides one.
                var decisions = (keep ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => new { itemId = k.Trim(), decision = "keep" }).ToList();
                using var decided = await http.PostAsJsonAsync($"/api/offboarding/{id:D}/review",
                    new { acceptDefaults = acceptReview, actingUser = actingUser.Trim(), decisions });
                if (!decided.IsSuccessStatusCode) return await RefusedAsync(decided, error);
                view = await decided.Content.ReadFromJsonAsync<JsonElement>();
            }

            object body = new { };
            if (view.GetProperty("open").GetBoolean() && view.GetProperty("needsPassword").GetBoolean())
            {
                if (string.IsNullOrWhiteSpace(actingUser))
                {
                    await error.WriteLineAsync("Changing the master key needs a superadmin's own password: add --as <your user name>.");
                    return 1;
                }
                if (string.IsNullOrEmpty(Password())) return 1;
                body = new { password, actingUser = actingUser.Trim() };
            }
            using var continued = await http.PostAsJsonAsync($"/api/offboarding/{id:D}/continue", body);
            if (!continued.IsSuccessStatusCode) return await RefusedAsync(continued, error);
            await PrintAsync(await continued.Content.ReadFromJsonAsync<JsonElement>(), output);
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            await error.WriteLineAsync("The node did not answer. Is it running? (" + ex.GetType().Name + ")");
            return 1;
        }
    }

    /// <summary>The node's own sentence of a refusal (<c>{"error": "..."}</c>), else the status code.</summary>
    private static async Task<int> RefusedAsync(HttpResponseMessage response, TextWriter error)
    {
        string? sentence = null;
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var e)) sentence = e.GetString();
        }
        catch (JsonException) { /* no body or not JSON: the status says it */ }
        await error.WriteLineAsync(sentence ?? $"The node refused ({(int)response.StatusCode}).");
        return 1;
    }

    /// <summary>One removal: who, the status ("being removed, step k of n"), each step with what it waits for, the review, the bridges, the reminders.</summary>
    internal static async Task PrintAsync(JsonElement view, TextWriter output)
    {
        await output.WriteLineAsync($"{view.GetProperty("person").GetString()}: {view.GetProperty("status").GetString()}  (process {view.GetProperty("processId").GetString()})");
        foreach (var step in view.GetProperty("steps").EnumerateArray())
        {
            await output.WriteLineAsync($"  [{step.GetProperty("state").GetString()}] {step.GetProperty("number").GetInt32() + 1}. {step.GetProperty("title").GetString()}" +
                (step.TryGetProperty("sentence", out var s) && s.ValueKind == JsonValueKind.String ? $": {s.GetString()}" : ""));
            foreach (var wait in step.GetProperty("waits").EnumerateArray())
                await output.WriteLineAsync($"      {wait.GetProperty("nodeName").GetString()}: {wait.GetProperty("state").GetString()}" +
                    (wait.TryGetProperty("why", out var w) && w.ValueKind == JsonValueKind.String ? $" - {w.GetString()}" : ""));
        }
        var review = view.GetProperty("review");
        if (review.GetArrayLength() > 0)
        {
            var open = review.EnumerateArray().Count(i => i.TryGetProperty("decision", out var d) && d.ValueKind != JsonValueKind.String);
            await output.WriteLineAsync($"  Review: {review.GetArrayLength()} item(s), {open} undecided." +
                (open > 0 ? " Decide them on the People being removed page, or take the pre-selected choices with --accept-review." : ""));
            // What --accept-review leaves open: every superadmin (and an accounts node pre-selected for a look) needs its own decision.
            foreach (var item in review.EnumerateArray())
            {
                if (item.TryGetProperty("decision", out var d) && d.ValueKind == JsonValueKind.String) continue;
                var kind = item.TryGetProperty("kind", out var k) ? k.GetString() : null;
                var preselected = item.TryGetProperty("defaultRemove", out var dr) && dr.ValueKind == JsonValueKind.True;
                if (kind != "superadmin" && !(kind == "accounts-home" && preselected)) continue;
                var itemId = item.TryGetProperty("id", out var i) ? i.GetString() : null;
                var label = item.TryGetProperty("label", out var l) ? l.GetString() : itemId;
                await output.WriteLineAsync($"    {label}: decide it yourself - keep it with --keep {itemId}" +
                    (kind == "superadmin" ? ", or remove it on the People being removed page." : " (with a reason on the page), or make another node the accounts node."));
            }
        }
        foreach (var bridge in view.GetProperty("bridges").EnumerateArray())
            await output.WriteLineAsync($"  ! {bridge.GetString()}");
        foreach (var reminder in view.GetProperty("reminders").EnumerateArray())
            await output.WriteLineAsync($"  - {reminder.GetString()}");
        if (view.GetProperty("open").GetBoolean() && view.GetProperty("needsPassword").GetBoolean())
            await output.WriteLineAsync("  The master key is changed with a superadmin's own password: bmb offboarding continue <process id> --as <your user name>");
    }
}
