using System.CommandLine;
using System.Text.Json;

namespace BeeMemoryBank.Cli.Commands;

/// <summary>
/// `bmb blind …` — the operator's handle on a blind node (plan §9): init, status, pair-code,
/// restore-code, backup now|list|verify, jobs, wipe. The CLI is the break-glass twin of the
/// console page: same Api endpoints, same internal key, no browser. `pair-code` and
/// `restore-code` call the endpoints owned by the pairing (BMB-52) and recovery (BMB-53) work;
/// until those merge the Api answers 404 and this command says so.
/// </summary>
public static class BlindCommand
{
    public static void AddTo(RootCommand root, Option<string> dataOption)
    {
        var blind = new Command("blind", "Blind node management (local node's Api)");

        // Secrets never come from the command line (see BlindSecrets): --secrets-file, stdin or
        // a prompt. The repository location and the S3 key id are not secret and stay options.
        var secretsFile = new Option<string?>("--secrets-file",
            "File of key=value lines (console_password, restic_password, s3_secret_key); must be chmod 600");

        var init = new Command("init", "Set the console password and backup settings");
        var initRepo = new Option<string?>("--repo-folder", "Folder restic repository path");
        var initS3 = new Option<string?>("--s3-endpoint", "S3 endpoint URL (with --s3-bucket/--s3-prefix/--s3-access-key)");
        var initS3Bucket = new Option<string?>("--s3-bucket", "S3 bucket");
        var initS3Prefix = new Option<string?>("--s3-prefix", "S3 key prefix for the repository");
        var initS3Ak = new Option<string?>("--s3-access-key", "S3 access key id");
        init.AddOption(secretsFile);
        init.AddOption(initRepo);
        init.AddOption(initS3); init.AddOption(initS3Bucket); init.AddOption(initS3Prefix);
        init.AddOption(initS3Ak);
        init.SetHandler(async ctx =>
        {
            var s3 = ctx.ParseResult.GetValueForOption(initS3);
            var prompts = new Dictionary<string, string>
            {
                [BlindSecrets.ConsolePassword] = "Console page password (min 8 characters)",
                [BlindSecrets.ResticPassword] = "restic repository password (Enter to set it later)",
            };
            if (s3 is not null) prompts[BlindSecrets.S3SecretKey] = "S3 secret key";
            Environment.Exit(await HandleInitAsync(
                ctx.ParseResult.GetValueForOption(dataOption)!,
                BlindSecrets.Resolve(ctx.ParseResult.GetValueForOption(secretsFile), prompts),
                ctx.ParseResult.GetValueForOption(initRepo),
                s3,
                ctx.ParseResult.GetValueForOption(initS3Bucket),
                ctx.ParseResult.GetValueForOption(initS3Prefix),
                ctx.ParseResult.GetValueForOption(initS3Ak)));
        });
        blind.AddCommand(init);

        var status = new Command("status", "Blind node status (version, peers, storage, jobs)");
        status.SetHandler(async (data) => Environment.Exit(await HandleStatusAsync(data)), dataOption);
        blind.AddCommand(status);

        var pair = new Command("pair-code", "Show the pairing code. The device that uses it will manage this blind node (reseed, anchors, recovery)");
        pair.SetHandler(async (data) => Environment.Exit(await HandleSimplePostAsync(data, "api/blind/pair-code", viaGet: true)), dataOption);
        blind.AddCommand(pair);

        var restore = new Command("restore-code", "Issue a one-time recovery code (BMB-53 endpoint)");
        restore.SetHandler(async (data) => Environment.Exit(await HandleSimplePostAsync(data, "api/blind/restore-code")), dataOption);
        blind.AddCommand(restore);

        var backup = new Command("backup", "Backups");
        var now = new Command("now", "Start a backup now");
        now.SetHandler(async (data) => Environment.Exit(await HandleSimplePostAsync(data, "api/blind/backup/now")), dataOption);
        backup.AddCommand(now);
        var list = new Command("list", "List restic snapshots");
        list.SetHandler(async (data) => Environment.Exit(await HandleListAsync(data)), dataOption);
        backup.AddCommand(list);
        var verify = new Command("verify", "Check the repository (subset by default, --full for everything)");
        var full = new Option<bool>("--full", "Full check — reads the whole repository");
        verify.AddOption(full);
        verify.SetHandler(async (data, f) => Environment.Exit(await HandleVerifyAsync(data, f)), dataOption, full);
        backup.AddCommand(verify);
        var copy = new Command("copy", "Save a one-off copy of the repository into another folder (restic copy)");
        var copyTo = new Option<string>("--to", "Destination folder, outside the node's data folder (e.g. /backups/usb/bmb)") { IsRequired = true };
        copy.AddOption(copyTo);
        copy.SetHandler(async (data, to) => Environment.Exit(await HandleCopyAsync(data, to)), dataOption, copyTo);
        backup.AddCommand(copy);
        blind.AddCommand(backup);

        var jobs = new Command("jobs", "Running and recent jobs");
        jobs.SetHandler(async (data) => Environment.Exit(await HandleJobsAsync(data)), dataOption);
        blind.AddCommand(jobs);

        var wipe = new Command("wipe", "Disconnect and erase THIS node's data (DANGEROUS; the restic repository is not touched)");
        var wipeName = new Option<string>("--name", "The node's own name, typed as confirmation") { IsRequired = true };
        wipe.AddOption(secretsFile); wipe.AddOption(wipeName);
        wipe.SetHandler(async (data, file, name) =>
        {
            var secrets = BlindSecrets.Resolve(file, new Dictionary<string, string>
            {
                [BlindSecrets.ConsolePassword] = "Console page password (confirms the wipe)",
            });
            Environment.Exit(await HandleWipeAsync(data, secrets.GetValueOrDefault(BlindSecrets.ConsolePassword) ?? "", name));
        }, dataOption, secretsFile, wipeName);
        blind.AddCommand(wipe);

        root.AddCommand(blind);
    }

    public static async Task<int> HandleInitAsync(
        string dataPath, IReadOnlyDictionary<string, string> secrets,
        string? repoFolder, string? s3, string? bucket, string? prefix, string? ak,
        BlindApiOptions? options = null, TextWriter? output = null)
    {
        output ??= Console.Out;
        var api = Create(dataPath, options);

        if (s3 is not null && (bucket is null || prefix is null))
        {
            await output.WriteLineAsync("--s3-bucket and --s3-prefix are required with --s3-endpoint");
            return 1;
        }
        if (secrets.GetValueOrDefault(BlindSecrets.ConsolePassword) is not { Length: > 0 } consolePassword)
        {
            await output.WriteLineAsync($"{BlindSecrets.ConsolePassword} is required (secrets file, stdin or the prompt)");
            return 1;
        }
        var resticPw = secrets.GetValueOrDefault(BlindSecrets.ResticPassword);
        var sk = secrets.GetValueOrDefault(BlindSecrets.S3SecretKey);

        var (status, body) = await api.SendAsync("POST", "api/blind/console/password",
            $$"""{"newPassword":{{Json(consolePassword)}}}""");
        if (status == 204) await output.WriteLineAsync("Console password set.");
        else return await FailAsync(output, status, body);

        if (repoFolder is not null || s3 is not null || resticPw is not null)
        {
            var settings = new Dictionary<string, object?>();
            if (repoFolder is not null) settings["repoType"] = "folder";
            if (repoFolder is not null) settings["repoFolder"] = repoFolder;
            if (s3 is not null)
            {
                settings["repoType"] = "s3";
                settings["s3Endpoint"] = s3;
                settings["s3Bucket"] = bucket;
                settings["s3Prefix"] = prefix;
                settings["s3AccessKey"] = ak;
                settings["s3SecretKey"] = sk;
            }
            if (resticPw is not null) settings["resticPassword"] = resticPw;
            (status, body) = await api.SendAsync("PUT", "api/blind/backup/settings", JsonSerializer.Serialize(settings));
            if (status == 200) await output.WriteLineAsync("Backup settings saved.");
            else return await FailAsync(output, status, body);
        }
        return 0;
    }

    public static async Task<int> HandleStatusAsync(string dataPath, BlindApiOptions? options = null, TextWriter? output = null)
    {
        output ??= Console.Out;
        var (status, body) = await Create(dataPath, options).SendAsync("GET", "api/blind/status");
        if (status != 200) return await FailAsync(output, status, body);

        using var doc = JsonDocument.Parse(body);
        var j = doc.RootElement;
        await output.WriteLineAsync($"Node:      {Str(j, "node_name")} ({Str(j, "node_id")})");
        await output.WriteLineAsync($"Role:      {Str(j, "role")}");
        await output.WriteLineAsync($"Version:   {Str(j, "version")}  protocol {Str(j, "protocol")}");
        await output.WriteLineAsync($"Data:      {Str(j, "data_path")}");
        await output.WriteLineAsync($"Backups:   {Str(j, "backups_path") ?? "(not configured)"}");
        if (j.TryGetProperty("stored", out var st))
            await output.WriteLineAsync($"Stored:    {Str(st, "articles")} articles, {Str(st, "blobs")} blobs, {Bytes(st, "bytes")}");
        await output.WriteLineAsync($"Free:      {Bytes(j, "free_bytes")}");
        await output.WriteLineAsync($"CPU mode:  {Str(j, "cpu_mode")}");
        if (j.TryGetProperty("peers", out var peers) && peers.GetArrayLength() > 0)
        {
            await output.WriteLineAsync("Peers:");
            foreach (var p in peers.EnumerateArray())
                await output.WriteLineAsync(
                    $"  {Str(p, "name"),-20} lag {Str(p, "lag") ?? "?"} events, last contact {Str(p, "last_contact")}");
        }
        return 0;
    }

    public static async Task<int> HandleListAsync(string dataPath, BlindApiOptions? options = null, TextWriter? output = null)
    {
        output ??= Console.Out;
        var (status, body) = await Create(dataPath, options).SendAsync("GET", "api/blind/backup/list");
        if (status != 200) return await FailAsync(output, status, body);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.GetArrayLength() == 0)
        {
            await output.WriteLineAsync("No snapshots yet.");
            return 0;
        }
        foreach (var s in doc.RootElement.EnumerateArray())
        {
            var shortId = Str(s, "short_id") ?? Str(s, "id")?[..Math.Min(12, (Str(s, "id") ?? "").Length)];
            await output.WriteLineAsync($"{shortId}  {Str(s, "time")}  {Str(s, "hostname")}");
        }
        return 0;
    }

    public static async Task<int> HandleVerifyAsync(string dataPath, bool full, BlindApiOptions? options = null, TextWriter? output = null)
    {
        output ??= Console.Out;
        var (status, body) = await Create(dataPath, options).SendAsync("POST", "api/blind/backup/verify",
            $$"""{"full":{{(full ? "true" : "false")}}}""");
        if (status == 202)
        {
            await output.WriteLineAsync(full ? "Full check started. See: bmb blind jobs" : "Subset check started. See: bmb blind jobs");
            return 0;
        }
        return await FailAsync(output, status, body);
    }

    public static async Task<int> HandleCopyAsync(string dataPath, string destination,
        BlindApiOptions? options = null, TextWriter? output = null)
    {
        output ??= Console.Out;
        var (status, body) = await Create(dataPath, options).SendAsync("POST", "api/blind/backup/copy",
            JsonSerializer.Serialize(new { destination }));
        if (status == 202)
        {
            await output.WriteLineAsync($"Copy to {destination} started. See: bmb blind jobs");
            return 0;
        }
        return await FailAsync(output, status, body);
    }

    public static async Task<int> HandleJobsAsync(string dataPath, BlindApiOptions? options = null, TextWriter? output = null)
    {
        output ??= Console.Out;
        var (status, body) = await Create(dataPath, options).SendAsync("GET", "api/blind/status");
        if (status != 200) return await FailAsync(output, status, body);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("jobs", out var jobs) || jobs.GetArrayLength() == 0)
        {
            await output.WriteLineAsync("No jobs.");
            return 0;
        }
        foreach (var job in jobs.EnumerateArray())
        {
            var progress = job.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Number
                ? $"{Math.Round(p.GetDouble() * 100)}%" : "";
            var speed = job.TryGetProperty("speed_bytes_per_sec", out var sp) && sp.ValueKind == JsonValueKind.Number
                ? $"{sp.GetInt64() / 1024} KiB/s" : "";
            await output.WriteLineAsync(
                $"{Str(job, "id"),-22} {Str(job, "kind"),-7} {Str(job, "state"),-9} {progress,-5} {speed,-12} {Str(job, "detail") ?? ""}");
        }
        return 0;
    }

    public static async Task<int> HandleWipeAsync(string dataPath, string consolePassword, string name,
        BlindApiOptions? options = null, TextWriter? output = null)
    {
        output ??= Console.Out;
        var (status, body) = await Create(dataPath, options).SendAsync("POST", "api/blind/wipe/cli",
            $$"""{"consolePassword":{{Json(consolePassword)}},"confirmNodeName":{{Json(name)}}}""");
        if (status == 200)
        {
            await output.WriteLineAsync("Node wiped. It needs a fresh pairing (or init) before it serves again.");
            return 0;
        }
        return await FailAsync(output, status, body);
    }

    private static async Task<int> HandleSimplePostAsync(string dataPath, string path, bool viaGet = false)
    {
        var (status, body) = await Create(dataPath, null).SendAsync(viaGet ? "GET" : "POST", path, viaGet ? null : "{}");
        if (status == 200 || status == 202)
        {
            Console.WriteLine(Pretty(body));
            return 0;
        }
        Console.WriteLine($"HTTP {status}: {Trim(body)}");
        return status == 404 ? 2 : 1;
    }

    private static BlindApi Create(string dataPath, BlindApiOptions? options) =>
        new(new BlindApiOptions
        {
            BaseUrl = options?.BaseUrl,
            // The CLI runs on the node itself: the key comes from the environment or the shared
            // key file, exactly as the Web layer finds it.
            InternalKey = options?.InternalKey ?? BlindApi.ResolveInternalKey(dataPath),
            Handler = options?.Handler,
        });

    private static async Task<int> FailAsync(TextWriter output, int status, string body)
    {
        await output.WriteLineAsync($"HTTP {status}: {Trim(body)}");
        return 1;
    }

    private static string Json(string s) => JsonSerializer.Serialize(s);

    private static string Pretty(string body)
    {
        try { return JsonSerializer.Serialize(JsonDocument.Parse(body), new JsonSerializerOptions { WriteIndented = true }); }
        catch (JsonException) { return body; }
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";

    // Numbers print as their JSON text: protocol, counts and lag are numbers in the status JSON.
    private static string? Str(JsonElement e, string name) =>
        !e.TryGetProperty(name, out var v) ? null : v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };

    private static string Bytes(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? $"{v.GetInt64() / 1_000_000} MB" : "?";
}
