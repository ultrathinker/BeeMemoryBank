using System.Diagnostics;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.FullIos.Services;

/// <summary>
/// Measures, on the device it runs on, what the full node needs from the platform: SQLite with FTS5 and WAL, the Argon2id key derivation
/// at the vault's own parameters, creating and opening a vault, writing notes, the two searches, and node signatures. It works on a vault
/// of its own in <c>scratchDirectory</c> (a new folder each run), never on the app's vault, and reports <c>name=value</c> lines.
/// The feasibility evidence of docs/full-node/IOS.md came from here; the end-to-end check runs it again on every fresh simulator.
/// </summary>
public static class FullSelfCheck
{
    private const string Password = "self-check password, not a secret";

    /// <param name="memory">The platform's (footprint, peak) in bytes: what iOS counts against the app's limit.</param>
    public static async Task<IReadOnlyList<string>> RunAsync(string scratchDirectory, Action<string>? progress = null, Func<(long Footprint, long Peak)>? memory = null)
    {
        var lines = new List<string>();
        void Report(string name, object value)
        {
            var line = $"{name}={value}";
            lines.Add(line);
            progress?.Invoke(line);
        }

        var total = Stopwatch.StartNew();
        Report("os", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        Report("runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        Report("cpus", Environment.ProcessorCount);

        var paths = new FullNodePaths(Path.Combine(scratchDirectory, "selfcheck-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")));
        var services = new ServiceCollection().AddFullNode(paths).BuildServiceProvider();
        await using (services)
        {
            var sw = Stopwatch.StartNew();
            await FullNodeServices.PrepareAsync(services);
            Report("migrations_ms", sw.ElapsedMilliseconds);

            using (var conn = services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            {
                Report("sqlite_version", await conn.ExecuteScalarAsync<string>("select sqlite_version()") ?? "?");
                var options = (await conn.QueryAsync<string>("PRAGMA compile_options")).ToList();
                Report("sqlite_fts5", options.Contains("ENABLE_FTS5"));
                Report("sqlite_journal", await conn.ExecuteScalarAsync<string>("PRAGMA journal_mode") ?? "?");
            }

            sw.Restart();
            var kek = KeyDerivation.DeriveKek(Password, KeyDerivation.GenerateSalt());
            Array.Clear(kek);
            Report("argon2id_ms", sw.ElapsedMilliseconds);
            sw.Restart();
            kek = KeyDerivation.DeriveKek(Password, KeyDerivation.GenerateSalt());
            Array.Clear(kek);
            Report("argon2id_again_ms", sw.ElapsedMilliseconds);
            Report("argon2id_params", $"{CryptoConstants.DefaultArgonMemory}KiB/t{CryptoConstants.DefaultArgonIterations}/p{CryptoConstants.DefaultArgonParallelism}");

            using (var scope = services.CreateScope())
            {
                sw.Restart();
                await scope.ServiceProvider.GetRequiredService<InitializationService>().InitializeAsync("owner", "self-check", Password, canGenerateEmbeddings: false);
                Report("create_vault_ms", sw.ElapsedMilliseconds);
            }

            var session = services.GetRequiredService<SessionService>();
            sw.Restart();
            Report("unlock_ok", await session.UnlockAsync(Password));
            Report("unlock_ms", sw.ElapsedMilliseconds);

            using (var scope = services.CreateScope())
            {
                sw.Restart();
                var recovery = await scope.ServiceProvider.GetRequiredService<KeyManagementService>().AddRecoveryKeyAsync();
                Report("recovery_key_ms", sw.ElapsedMilliseconds);
                Report("recovery_key_chars", recovery.Length);
            }

            const int notes = 40;
            using (var scope = services.CreateScope())
            {
                var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
                sw.Restart();
                for (var i = 0; i < notes; i++)
                    await articles.CreateAsync($"Self-check note {i}", "/self-check", [], $"Body {i}: " + (i == 17 ? "the needle is here. " : "") + new string('x', 2000));
                Report("write_note_ms_avg", sw.ElapsedMilliseconds / notes);
            }

            using (var scope = services.CreateScope())
            {
                var search = scope.ServiceProvider.GetRequiredService<SearchService>();
                sw.Restart();
                var byTitle = await search.SearchAsync("note 17");
                Report("search_title_ms", sw.ElapsedMilliseconds);
                Report("search_title_hits", byTitle.Articles.Count);
                sw.Restart();
                var byBody = await search.SearchWithContentAsync("needle");
                Report("search_body_ms", sw.ElapsedMilliseconds);
                Report("search_body_hits", byBody.Articles.Count);
            }

            var (_, privateKey) = Ed25519Signer.GenerateKeyPair();
            var payload = new byte[256];
            sw.Restart();
            for (var i = 0; i < 200; i++) Ed25519Signer.Sign(privateKey, payload);
            Report("ed25519_sign_us_avg", (int)(sw.Elapsed.TotalMicroseconds / 200));
            Array.Clear(privateKey);

            session.Lock();
            Report("locked", !session.IsUnlocked);
        }

        Report("managed_heap_mb", GC.GetTotalMemory(false) / (1024 * 1024));
        if (memory is not null)
        {
            var (footprint, peak) = memory();
            Report("footprint_mb", footprint / (1024 * 1024));
            Report("footprint_peak_mb", peak / (1024 * 1024));
        }
        Report("total_ms", total.ElapsedMilliseconds);
        Report("result", "ok");
        return lines;
    }
}
