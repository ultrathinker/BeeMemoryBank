using System.CommandLine;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Vault.Sealed;

namespace BeeMemoryBank.Cli.Commands;

/// <summary>
/// <c>bmb vault status | seal | verify | auto-unlock status|enable|disable</c> (docs/design/step-b-spec.md 5.9, 8.7.2, 10.1, 11.2). <c>status</c> and
/// <c>verify</c> work on the folder; <c>seal</c> and <c>auto-unlock</c> call the running node, which is the one that holds the keys they need.
/// </summary>
public static class VaultCommand
{
    public const string ServerWarning =
        "Strongly not recommended on a server. With this on, the key that opens this server's database is kept on the server itself, so anyone who gets " +
        "this server's disk, a copy of its data folder or a backup of it can read the whole vault: the database encryption no longer protects it.";

    public const string ServerConfirmation =
        "Last check: after a restart this server will open the vault by itself, with nobody signed in. A stolen disk then opens everything.";

    public const string DesktopWarning =
        "Your notes are encrypted on this computer's disk, but with auto-unlock on, this computer opens them at start without your password. " +
        "If someone gets this computer or its disk, their protection rests only on your account password (and on disk encryption, if you use it).";

    public const string Phrase = "START UNLOCKED";

    public static void AddTo(RootCommand root, Option<string> dataOption, Option<bool> passwordStdinOption)
    {
        var vault = new Command("vault", "The encrypted database of this node");

        var status = new Command("status", "Is the database encrypted, is it open, what waits for the unlock (never asks for the password)");
        status.SetHandler(async data => Environment.Exit(await StatusAsync(data)), dataOption);
        vault.AddCommand(status);

        var yes = new Option<bool>("--yes", "Confirm \"Encrypt this node\". Without it the command only shows what the preflight says.");
        var seal = new Command("seal", "Encrypt this node's database (calls the running node)");
        seal.AddOption(passwordStdinOption);
        seal.AddOption(yes);
        seal.SetHandler(async (data, confirmed) => Environment.Exit(await SealAsync(data, confirmed)), dataOption, yes);
        vault.AddCommand(seal);

        var verify = new Command("verify", "Check the sealed database, the chat database and the boot store with the master password; leaves the database closed");
        verify.AddOption(passwordStdinOption);
        verify.SetHandler(async data => Environment.Exit(await VerifyAsync(data)), dataOption);
        vault.AddCommand(verify);

        var auto = new Command("auto-unlock", "Open the vault by itself at start (calls the running node)");
        var autoStatus = new Command("status", "Is auto-unlock on");
        autoStatus.SetHandler(async data => Environment.Exit(await AutoUnlockStatusAsync(data)), dataOption);
        var autoEnable = new Command("enable", "Turn auto-unlock on (shows the warnings and asks for the master password and the phrase)");
        autoEnable.AddOption(passwordStdinOption);
        autoEnable.SetHandler(async data => Environment.Exit(await AutoUnlockEnableAsync(data)), dataOption);
        var autoDisable = new Command("disable", "Turn auto-unlock off");
        autoDisable.AddOption(passwordStdinOption);
        autoDisable.SetHandler(async data => Environment.Exit(await AutoUnlockDisableAsync(data)), dataOption);
        auto.AddCommand(autoStatus);
        auto.AddCommand(autoEnable);
        auto.AddCommand(autoDisable);
        vault.AddCommand(auto);

        root.AddCommand(vault);
    }

    // ---------------------------------------------------------------- status

    public static async Task<int> StatusAsync(string dataPath, TextWriter? output = null)
    {
        output ??= Console.Out;
        var diagnosis = VaultStoreFiles.Diagnose(dataPath);
        if (BrokenStart.From(diagnosis) is { } broken)
        {
            await output.WriteLineAsync("Database: cannot start - " + broken.Reason);
            await output.WriteLineAsync(broken.Message);
            return 1;
        }
        if (diagnosis.Mode != VaultStoreMode.Sealed)
        {
            await output.WriteLineAsync("Database: not encrypted");
            if (await NodeApi.TryVaultStatusAsync(dataPath) is { } plainNode)
                await output.WriteLineAsync($"Node:     running ({plainNode.GetProperty("role").GetString() ?? "node"})");
            return 0;
        }
        await SealedStatusLinesAsync(dataPath, output, includeIdentity: false);
        return 0;
    }

    /// <summary>The lines of a sealed folder that need no password (the boot store, and the running node when one answers).</summary>
    public static async Task SealedStatusLinesAsync(string dataPath, TextWriter output, bool includeIdentity)
    {
        var boot = new BootStore(Path.Combine(dataPath, VaultStoreFiles.Boot));
        if (includeIdentity)
        {
            var identity = await new BootMirrorStore(boot.Connections).GetIdentityAsync();
            await output.WriteLineAsync($"Node:      {identity?.DisplayName ?? "(unknown)"}");
            await output.WriteLineAsync($"ID:        {await boot.GetMetaAsync(BootMetaKeys.NodeId) ?? "(unknown)"}");
            await output.WriteLineAsync($"Path:      {dataPath}");
        }
        var sealedAt = await boot.GetMetaAsync(BootMetaKeys.SealedAt);
        var generation = await boot.GetMetaAsync(BootMetaKeys.DbkGeneration);
        await output.WriteLineAsync($"Database:  encrypted{(string.IsNullOrEmpty(sealedAt) ? "" : $" since {FormatTime(sealedAt)} UTC")}" +
                                    $"{(string.IsNullOrEmpty(generation) ? "" : $", key generation {generation}")}");

        var live = await NodeApi.TryVaultStatusAsync(dataPath);
        if (live is { } node)
        {
            await output.WriteLineAsync($"State:     {node.GetProperty("state").GetString()?.ToLowerInvariant()} (the node is running)");
            if (node.TryGetProperty("last_refusal", out var refusal) && refusal.ValueKind == JsonValueKind.String)
                await output.WriteLineAsync($"Last refusal: {refusal.GetString()}");
            if (node.TryGetProperty("migration", out var migration) && migration.ValueKind == JsonValueKind.Object
                && migration.TryGetProperty("wipe_pending", out var pending) && pending.GetArrayLength() > 0)
                await output.WriteLineAsync($"Pending:   {pending.GetArrayLength()} replaced unencrypted file(s) still to be wiped; a restart finishes it");
        }
        else
        {
            await output.WriteLineAsync("State:     closed (no node answers on this computer)");
        }

        if (boot.Exists)
        {
            var inbox = await new RelayInbox(boot.Connections).GetSummaryAsync();
            if (inbox.Events > 0 || inbox.Blobs > 0)
                await output.WriteLineAsync($"Inbox:     {inbox.Events} change(s) and {inbox.Blobs} file(s) received while closed, waiting for the unlock");
        }
    }

    private static string FormatTime(string iso) =>
        DateTime.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
            ? t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm") : iso;

    // ---------------------------------------------------------------- verify

    public static async Task<int> VerifyAsync(string dataPath, TextWriter? output = null)
    {
        output ??= Console.Out;
        var diagnosis = VaultStoreFiles.Diagnose(dataPath);
        if (BrokenStart.From(diagnosis) is { } broken)
        {
            await output.WriteLineAsync("failed: " + broken.Reason);
            await output.WriteLineAsync(broken.Message);
            return 1;
        }
        if (diagnosis.Mode != VaultStoreMode.Sealed)
        {
            await output.WriteLineAsync("failed: not_sealed - this node's database is not encrypted.");
            return 1;
        }
        var password = CliSealed.ResolvePassword();
        if (password is null)
        {
            await output.WriteLineAsync(CliSealed.NeedsPassword);
            return 1;
        }
        var result = await VaultVerifier.RunAsync(dataPath, password);
        await output.WriteLineAsync(result.Ok ? "ok" : $"failed: {result.Check} - {result.Message}");
        return result.Ok ? 0 : 1;
    }

    // ---------------------------------------------------------------- seal

    public static async Task<int> SealAsync(string dataPath, bool yes, TextWriter? output = null, TimeSpan? pollEvery = null)
    {
        output ??= Console.Out;
        try
        {
            using var http = NodeApi.CreateClient(dataPath, TimeSpan.FromMinutes(5));
            var preflight = await http.GetFromJsonAsync<JsonElement>("/api/vault/seal/preflight");
            foreach (var w in preflight.GetProperty("warnings").EnumerateArray())
                await output.WriteLineAsync("Note: " + w.GetProperty("message").GetString());
            if (!preflight.GetProperty("can_start").GetBoolean())
            {
                await output.WriteLineAsync("Not encrypted. These must be put right first:");
                foreach (var b in preflight.GetProperty("blockers").EnumerateArray())
                    await output.WriteLineAsync($"  - [{b.GetProperty("code").GetString()}] {b.GetProperty("message").GetString()}");
                foreach (var c in preflight.GetProperty("plain_copies").EnumerateArray())
                    await output.WriteLineAsync($"      {c.GetProperty("path").GetString()} ({c.GetProperty("kind").GetString()})");
                return 1;
            }
            if (!yes)
            {
                await output.WriteLineAsync(
                    "This encrypts the database of this node. It keeps the node out of service while it runs, and it cannot be undone from the node itself: " +
                    "no copy of the unencrypted database is kept. Keep your own backup if you may want to go back. Run again with --yes to start.");
                return 1;
            }

            var password = CliSealed.ResolvePassword();
            if (password is null) return 1;
            using var started = await http.PostAsJsonAsync("/api/vault/seal", new { password });
            if (started.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await output.WriteLineAsync("Error: wrong master password.");
                return 1;
            }
            if (!started.IsSuccessStatusCode)
            {
                await output.WriteLineAsync($"Error: {(int)started.StatusCode} {await started.Content.ReadAsStringAsync()}");
                return 1;
            }

            string? lastStep = null;
            while (true)
            {
                await Task.Delay(pollEvery ?? TimeSpan.FromSeconds(1));
                var progress = await http.GetFromJsonAsync<JsonElement>("/api/vault/seal/progress");
                var phase = progress.GetProperty("phase").GetString();
                var step = progress.GetProperty("step").GetString();
                if (step != lastStep)
                {
                    await output.WriteLineAsync($"[{progress.GetProperty("percent").GetInt32(),3}%] {step}");
                    lastStep = step;
                }
                if (phase == "failed")
                {
                    var locked = step == SealMigrationService.LockedStep;   // sealed after the swap, but kept locked: not "not encrypted"
                    await output.WriteLineAsync($"{(locked ? "Encrypted, but kept locked" : "Not encrypted")} ({progress.GetProperty("error_code").GetString()}): {progress.GetProperty("error").GetString()}");
                    return 1;
                }
                if (phase == "done")
                {
                    await output.WriteLineAsync("This node's database is encrypted.");
                    return 0;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            await output.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }
    }

    // ---------------------------------------------------------------- auto-unlock

    private static async Task<(string Role, string Prefix)?> RouteAsync(string dataPath, TextWriter output)
    {
        var status = await NodeApi.TryVaultStatusAsync(dataPath);
        if (status is null)
        {
            await output.WriteLineAsync("No node answers on this computer: auto-unlock is set on the running node.");
            return null;
        }
        var role = status.Value.GetProperty("auto_unlock").GetProperty("role").GetString() ?? "server";
        return (role, role == "desktop" ? "/api/keys/auto-unlock" : "/api/keys/server-auto-unlock");
    }

    public static async Task<int> AutoUnlockStatusAsync(string dataPath, TextWriter? output = null)
    {
        output ??= Console.Out;
        try
        {
            if (await RouteAsync(dataPath, output) is not { } route) return 1;
            using var http = NodeApi.CreateClient(dataPath);
            var body = await http.GetFromJsonAsync<JsonElement>(route.Prefix + "/status");
            var on = route.Role == "desktop" ? body.GetProperty("enabled").GetBoolean() : body.GetProperty("enabled").GetBoolean();
            await output.WriteLineAsync($"Role:      {route.Role}");
            await output.WriteLineAsync($"Auto-unlock: {(on ? "on" : "off")}");
            if (body.TryGetProperty("notice", out var notice) && notice.ValueKind == JsonValueKind.String)
                await output.WriteLineAsync(notice.GetString());
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            await output.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }
    }

    public static async Task<int> AutoUnlockEnableAsync(string dataPath, TextWriter? output = null)
    {
        output ??= Console.Out;
        try
        {
            if (await RouteAsync(dataPath, output) is not { } route) return 1;
            using var http = NodeApi.CreateClient(dataPath);
            await output.WriteLineAsync(route.Role == "desktop" ? DesktopWarning : ServerWarning);
            var password = CliSealed.ResolvePassword();
            if (password is null) return 1;

            object body;
            if (route.Role == "desktop")
            {
                body = new { };
            }
            else
            {
                await output.WriteLineAsync(ServerConfirmation);
                await output.WriteAsync($"Type {Phrase} to confirm: ");
                var phrase = CliSealed.Stdin.ReadLine()?.TrimEnd('\r');
                if (!string.Equals(phrase, Phrase, StringComparison.Ordinal))
                {
                    await output.WriteLineAsync("Not turned on: the phrase was not typed exactly.");
                    return 1;
                }
                body = new { password, confirmation = phrase };
            }
            using var response = await http.PostAsJsonAsync(route.Prefix + "/enable", body);
            if (response.IsSuccessStatusCode)
            {
                await output.WriteLineAsync("Auto-unlock is on.");
                return 0;
            }
            await output.WriteLineAsync($"Not turned on: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            await output.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }
    }

    public static async Task<int> AutoUnlockDisableAsync(string dataPath, TextWriter? output = null)
    {
        output ??= Console.Out;
        try
        {
            if (await RouteAsync(dataPath, output) is not { } route) return 1;
            using var http = NodeApi.CreateClient(dataPath);
            var password = CliSealed.ResolvePassword();
            if (password is null) return 1;
            using var response = await http.PostAsJsonAsync(route.Prefix + "/disable", route.Role == "desktop" ? new { } : (object)new { password });
            if (response.IsSuccessStatusCode)
            {
                await output.WriteLineAsync("Auto-unlock is off.");
                return 0;
            }
            await output.WriteLineAsync($"Not turned off: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            await output.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }
    }
}
