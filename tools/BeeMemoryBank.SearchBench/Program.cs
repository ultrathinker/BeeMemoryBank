using System.Diagnostics;
using System.Security.Cryptography;

namespace BeeMemoryBank.SearchBench;

/// <summary>
/// bmb-searchbench entry point. Builds/seeds a scratch vault (or reuses one), launches a real
/// BeeMemoryBank.Api against it, runs the four search benchmark scenarios, and writes one JSON
/// baseline file per scenario. See README.md for the safety rules and output format.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var (opt, parseError) = OptionsParser.Parse(args);
        if (parseError == "__help__") return 0;
        if (parseError != null)
        {
            Console.Error.WriteLine($"error: {parseError}");
            Console.Error.WriteLine("Run 'bmb-searchbench --help' for usage.");
            return 2;
        }

        var repoRoot = ResolveRepoRoot();
        var progress = Console.Out;

        // Ctrl-C / unusual exits: ensure the Api child is torn down. The CTS lets scenarios unwind.
        using var runCts = new CancellationTokenSource();
        using var stopSig = ConsoleUtil.RegisterCancel(() => runCts.Cancel());

        // ── 1. Resolve data path & run the safety gate ───────────────────────────
        var createdScratch = false;
        if (string.IsNullOrWhiteSpace(opt.DataPath))
        {
            if (opt.SeedArticles is null or <= 0)
            {
                Console.Error.WriteLine("error: either --data-path <dir> or --seed-articles <N> --seed-folders <M> is required.");
                return 2;
            }
            opt.DataPath = PathSafety.DefaultScratchDir($"corpus-{opt.SeedArticles}");
            createdScratch = true;
            await progress.WriteLineAsync($"No --data-path given; using scratch dir {opt.DataPath}");
        }

        var hardRefusal = PathSafety.HardRefusalReason(opt.DataPath!);
        if (hardRefusal != null)
        {
            Console.Error.WriteLine($"REFUSING TO RUN: {hardRefusal}");
            Console.Error.WriteLine("This check is non-negotiable and cannot be overridden — it protects real user vaults.");
            return 3;
        }
        if (!PathSafety.IsScratchLike(opt.DataPath!) && !opt.AllowDataPath)
        {
            Console.Error.WriteLine($"REFUSING TO RUN: '{opt.DataPath}' doesn't look like a scratch/temp location.");
            Console.Error.WriteLine("Pass --allow-data-path to override this check (it does NOT override the");
            Console.Error.WriteLine("real-install-path hard refusal). See README.md §Safety for the heuristic.");
            return 3;
        }

        // ── 2. Seed if requested ─────────────────────────────────────────────────
        var overallSw = Stopwatch.StartNew();
        TimeSpan? seedDuration = null;
        bool shouldSeed = opt.SeedArticles is > 0;
        if (shouldSeed)
        {
            var dirExists = Directory.Exists(opt.DataPath!) && Directory.EnumerateFileSystemEntries(opt.DataPath!).Any();
            if (dirExists && !opt.ForceSeed)
            {
                Console.Error.WriteLine($"error: --seed-articles given but '{opt.DataPath}' is not empty. Clear it or pass --force-seed.");
                return 2;
            }
            Directory.CreateDirectory(opt.DataPath!);
            await progress.WriteLineAsync($"Seeding {opt.SeedArticles} articles / {opt.SeedFolders} folders into {opt.DataPath}...");
            var seedSw = Stopwatch.StartNew();
            await RunSeedGenAsync(repoRoot, opt, progress, runCts.Token);
            seedSw.Stop();
            seedDuration = seedSw.Elapsed;
            await progress.WriteLineAsync($"Seeding done in {seedDuration}.");
        }

        // ── 3. Build binaries (Api + SeedGen) if needed ───────────────────────────
        if (!opt.NoBuild)
        {
            await progress.WriteLineAsync("Ensuring Api and SeedGen binaries are built...");
            // SeedGen was already built as part of seeding; ensure Api is present too.
            await ApiProcess.BuildProjectAsync(
                Path.Combine(repoRoot, "server", "BeeMemoryBank.Api", "BeeMemoryBank.Api.csproj"),
                repoRoot, progress, runCts.Token);
        }

        // ── 4. Launch the Api against the data dir ───────────────────────────────
        var internalKey = GenerateInternalKey();
        var runStamp = opt.Label ?? DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var logDir = Path.Combine(opt.DataPath!, "..", "searchbench-logs", runStamp);
        var logDirFull = Path.GetFullPath(logDir);
        Directory.CreateDirectory(logDirFull);

        await using var api = await ApiProcess.StartAsync(repoRoot, opt.DataPath!, internalKey, logDirFull, progress, runCts.Token);
        var corpusLabel = ResolveCorpusLabel(opt);
        var scenarioSet = opt.ScenarioSet;

        // ── 5. Unlock + run scenarios ─────────────────────────────────────────────
        using var client = new BenchClient(api.BaseUrl, internalKey, maxConnectionsPerServer: Math.Max(50, opt.MixedClients * 4));
        await progress.WriteLineAsync($"Unlocking session with password (len {opt.Password.Length})...");
        var unlocked = await client.UnlockAsync(opt.Password, runCts.Token);
        if (!unlocked)
        {
            await progress.WriteLineAsync("ERROR: /api/session/unlock failed. Aborting.");
            return 4;
        }
        await progress.WriteLineAsync("Session unlocked.");

        var outputDir = ResolveOutputDir(repoRoot, opt);
        Directory.CreateDirectory(outputDir);
        var results = new List<ScenarioResult>();

        async Task RunAndStore(string name, Func<Task<ScenarioResult>> run)
        {
            await progress.WriteLineAsync($"\n=== Scenario: {name} ===");
            try
            {
                var res = await run();
                results.Add(res);
                var file = Path.Combine(outputDir, $"{name}-{corpusLabel}-{runStamp}.json");
                await Stats.WriteJsonAsync(file, res);
                await progress.WriteLineAsync($"  wrote {file}");
            }
            catch (Exception ex)
            {
                await progress.WriteLineAsync($"  FAILED scenario {name}: {ex.Message}");
                var skipped = new ScenarioResult
                {
                    Scenario = name,
                    CorpusSizeLabel = corpusLabel,
                    StartedAtUtc = DateTime.UtcNow,
                    EndedAtUtc = DateTime.UtcNow,
                    Note = $"skipped: {ex.Message}"
                };
                results.Add(skipped);
                var file = Path.Combine(outputDir, $"{name}-{corpusLabel}-{runStamp}.json");
                await Stats.WriteJsonAsync(file, skipped);
            }
        }

        if (scenarioSet.Contains("title"))
            await RunAndStore("title", () => Scenarios.TitleAsync(client, opt, corpusLabel, runCts.Token));
        if (scenarioSet.Contains("content"))
            await RunAndStore("content", () => Scenarios.ContentAsync(client, opt, corpusLabel, runCts.Token));
        if (scenarioSet.Contains("semantic"))
            await RunAndStore("semantic", () => Scenarios.SemanticAsync(client, opt, corpusLabel,
                (p, c) => AwaitEmbeddingsAsync(client, opt.SemanticWaitSeconds, p, c), progress, runCts.Token));
        if (scenarioSet.Contains("mixed"))
            await RunAndStore("mixed", () => Scenarios.MixedAsync(client, opt, corpusLabel, progress, runCts.Token));

        // ── 6. Tear down the Api cleanly ──────────────────────────────────────────
        await progress.WriteLineAsync("\nStopping Api...");
        await api.StopAsync(TimeSpan.FromSeconds(15), progress);

        overallSw.Stop();
        await progress.WriteLineAsync($"\nTotal run time: {overallSw.Elapsed}" +
            (seedDuration.HasValue ? $" (seed: {seedDuration})" : ""));

        // ── 7. Stdout summary table ──────────────────────────────────────────────
        PrintSummary(results, progress);

        // ── 8. Scratch cleanup ───────────────────────────────────────────────────
        if (createdScratch && !opt.KeepData)
        {
            await progress.WriteLineAsync($"\nCleaning up scratch data dir {opt.DataPath}");
            TryDelete(opt.DataPath!, progress);
            TryDelete(logDirFull, progress);
        }
        else if (createdScratch && opt.KeepData)
        {
            await progress.WriteLineAsync($"--keep-data: leaving scratch dir at {opt.DataPath}");
        }

        return 0;
    }

    private static async Task RunSeedGenAsync(string repoRoot, Options opt, TextWriter progress, CancellationToken ct)
    {
        var binary = await ApiProcess.EnsureSeedGenBuiltAsync(repoRoot, progress, ct);
        var forceArg = opt.ForceSeed ? "--force" : "";
        var args = $"\"{binary}\" --data-path \"{opt.DataPath}\" --articles {opt.SeedArticles} --folders {opt.SeedFolders} " +
                   $"--seed {opt.Seed} --locale {opt.Locale} --password {opt.Password} {forceArg}".TrimEnd();

        // Decide exe-vs-dotnet based on the resolved binary path (matches ApiProcess launch).
        string fileName;
        string argString;
        if (binary.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            fileName = "dotnet";
            argString = args;
        }
        else
        {
            fileName = binary;
            // strip the leading quoted binary token from args since it's now the filename
            argString = args.Substring(args.IndexOf(' ') + 1);
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = argString,
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        await progress.WriteLineAsync($"  seedgen: {fileName} {argString}");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start bmb-seedgen.");
        var forwardOut = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync(ct)) != null)
                await progress.WriteLineAsync($"  [seedgen] {line}");
        }, ct);
        var forwardErr = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardError.ReadLineAsync(ct)) != null)
                await progress.WriteLineAsync($"  [seedgen!] {line}");
        }, ct);
        await p.WaitForExitAsync(ct);
        await Task.WhenAll(forwardOut, forwardErr);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"bmb-seedgen exited with code {p.ExitCode}.");
    }

    /// <summary>
    /// Polls a fixed frequent semantic query until its result count stabilizes (3 consecutive polls
    /// with no growth) or <paramref name="maxWaitSeconds"/> elapses. Reports the wait and final count
    /// so the report can honestly state whether embeddings were ready.
    /// </summary>
    private static async Task AwaitEmbeddingsAsync(BenchClient client, int maxWaitSeconds, TextWriter progress, CancellationToken ct)
    {
        var probe = "incident response runbook";
        var deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
        long prev = -1;
        int stableHits = 0;
        var sw = Stopwatch.StartNew();
        long finalCount = 0;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var (ok, status, _, count, err) = await client.SendSemanticAsync(probe, topK: 50, ct);
                if (!ok)
                {
                    // 503 = model unavailable; 5xx = projection not ready. Keep waiting.
                    await progress.WriteLineAsync($"  semantic probe: HTTP {status} ({err}) — embeddings not ready yet, retrying...");
                }
                else
                {
                    finalCount = count ?? 0;
                    await progress.WriteLineAsync($"  semantic probe: {finalCount} results in {sw.Elapsed.TotalSeconds:0.0}s");
                    if (finalCount == prev)
                    {
                        stableHits++;
                        if (stableHits >= 2 && finalCount > 0)
                        {
                            await progress.WriteLineAsync($"  semantic probe: result count stabilized at {finalCount} (>=2 stable polls). Proceeding.");
                            return;
                        }
                    }
                    else
                    {
                        stableHits = 0;
                        prev = finalCount;
                    }
                }
            }
            catch (Exception ex)
            {
                await progress.WriteLineAsync($"  semantic probe threw: {ex.Message}");
            }
            try { await Task.Delay(5000, ct); } catch { break; }
        }
        await progress.WriteLineAsync($"  semantic wait finished after {sw.Elapsed.TotalSeconds:0.0}s (final probe count: {finalCount}). " +
            (finalCount == 0 ? "WARNING: no embeddings found — semantic results will be empty/unreliable." : "Proceeding with benchmark."));
    }

    private static void PrintSummary(List<ScenarioResult> results, TextWriter progress)
    {
        progress.WriteLine("");
        progress.WriteLine("┌──────────────┬──────────┬──────────┬──────────┬──────────┬──────────────┬───────────┬─────────────┐");
        progress.WriteLine("│ scenario     │   total  │  errors  │  p50 ms  │  p95 ms  │ throughput   │   p99 ms  │  mean ms    │");
        progress.WriteLine("├──────────────┼──────────┼──────────┼──────────┼──────────┼──────────────┼───────────┼─────────────┤");
        foreach (var r in results)
        {
            if (r.TotalRequests == 0 && r.Note?.StartsWith("skipped") == true)
            {
                progress.WriteLine($"│ {Pad(r.Scenario, 12)} │ {Pad(r.Note, 8)} │          │          │          │              │           │             │");
                continue;
            }
            progress.WriteLine($"│ {Pad(r.Scenario, 12)} │ {Pad(r.TotalRequests.ToString(), 8)} │ {Pad(r.ErrorCount.ToString(), 8)} │ " +
                $"{Pad(r.LatencyP50Ms.ToString("F1"), 8)} │ {Pad(r.LatencyP95Ms.ToString("F1"), 8)} │ {Pad(r.ThroughputReqPerSec.ToString("F1") + "/s", 12)} │ " +
                $"{Pad(r.LatencyP99Ms.ToString("F1"), 9)} │ {Pad(r.LatencyMeanMs.ToString("F1"), 11)} │");
        }
        progress.WriteLine("└──────────────┴──────────┴──────────┴──────────┴──────────┴──────────────┴───────────┴─────────────┘");
        foreach (var r in results.Where(x => x.PerQuery != null))
        {
            progress.WriteLine($"\n  Per-query detail — {r.Scenario}:");
            progress.WriteLine("    query                                 expect   samples     p50      p95      p99     results");
            foreach (var q in r.PerQuery!)
            {
                progress.WriteLine($"    {Pad(q.Query, 36)} {Pad(q.Expectation, 8)} {Pad(q.Samples.ToString(), 8)} " +
                    $"{Pad(q.P50Ms.ToString("F1"), 8)} {Pad(q.P95Ms.ToString("F1"), 8)} {Pad(q.P99Ms.ToString("F1"), 8)} {Pad(q.ResultCount.ToString(), 8)}");
            }
        }
    }

    private static string Pad(string s, int width) => s.Length >= width ? s[..width] : s + new string(' ', width - s.Length);

    private static string ResolveRepoRoot()
    {
        // The harness ships inside tools/BeeMemoryBank.SearchBench under the repo root.
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "BeeMemoryBank.slnx")) || File.Exists(Path.Combine(dir, "AGENTS.md")))
                return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir) break;
            dir = parent;
        }
        return Directory.GetCurrentDirectory();
    }

    private static string ResolveCorpusLabel(Options opt)
    {
        if (!string.IsNullOrWhiteSpace(opt.CorpusSizeLabel)) return opt.CorpusSizeLabel!;
        if (opt.SeedArticles is > 0) return opt.SeedArticles.Value.ToString();
        return "existing";
    }

    private static string ResolveOutputDir(string repoRoot, Options opt)
    {
        if (!string.IsNullOrWhiteSpace(opt.OutputDir)) return Path.GetFullPath(opt.OutputDir!);
        return Path.Combine(repoRoot, "_docs", "search-100k", "baseline");
    }

    private static string GenerateInternalKey()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    private static void TryDelete(string path, TextWriter progress)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            progress.WriteLine($"  (cleanup warning: could not delete {path}: {ex.Message})");
        }
    }
}

/// <summary>Console Ctrl-C registration that returns a disposable to detach the handler.</summary>
internal static class ConsoleUtil
{
    public static IDisposable RegisterCancel(Action onCancel)
    {
        void Handler(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            onCancel();
        }
        Console.CancelKeyPress += Handler;
        return new Detach(() => Console.CancelKeyPress -= Handler);
    }

    private sealed class Detach(Action detach) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) detach(); }
    }
}
