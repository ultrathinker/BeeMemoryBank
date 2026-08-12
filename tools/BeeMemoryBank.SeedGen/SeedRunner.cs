using System.Diagnostics;
using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.SeedGen;

/// <summary>
/// Wires a minimal BeeMemoryBank service container (the same composition the Migrator uses) and
/// seeds a data directory with a synthetic corpus entirely through the normal service layer, so
/// bodies are encrypted exactly like real usage.
/// </summary>
internal sealed class SeedRunner
{
    private readonly SeedOptions _opts;
    private readonly TextWriter _out;

    public SeedRunner(SeedOptions opts, TextWriter? output = null)
    {
        _opts = opts;
        _out = output ?? Console.Out;
    }

    public async Task<int> RunAsync()
    {
        var corpus = new SyntheticCorpus(_opts.Seed, _opts.Articles, _opts.Folders, _opts.Locales);

        var services = new ServiceCollection()
            .AddStorage(_opts.DataPath)
            .AddCore()
            .AddOnnxEmbeddings(_opts.DataPath)
            .AddSync()
            .BuildServiceProvider();

        try
        {
            // 1. Run migrations (idempotent) and restore the Lamport clock from the max stored ts.
            using (var scope = services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<MigrationRunner>().RunMigrationsAsync();
            }
            {
                using var scope = services.CreateScope();
                var maxTs = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxLamportTimestampAsync();
                services.GetRequiredService<LamportClock>().Initialize(maxTs);
            }

            // 2. Idempotency guard: refuse to touch a directory that already has data unless --force.
            Guid? existingNodeId;
            int existingFolderCount;
            using (var scope = services.CreateScope())
            {
                existingNodeId = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())?.NodeId;
                existingFolderCount = await scope.ServiceProvider.GetRequiredService<IFolderRepository>().CountAsync();
            }

            if ((existingNodeId != null || existingFolderCount > 0) && !_opts.Force)
            {
                _out.WriteLine($"Refusing to seed: '{_opts.DataPath}' already contains a node or folders.");
                _out.WriteLine("Pass --force to seed additively onto the existing vault.");
                return 2;
            }

            // 3. Initialize the node on a fresh directory (skipped under --force on an existing node).
            if (existingNodeId == null)
            {
                using var scope = services.CreateScope();
                var init = scope.ServiceProvider.GetRequiredService<InitializationService>();
                const string nodeAdmin = "seedadmin";
                await init.InitializeAsync(nodeAdmin, "SeedGen Node", _opts.Password);
                _out.WriteLine($"Initialized node '{nodeAdmin}'.");
            }

            // 4. Unlock so article bodies can be encrypted with the session's master DEK.
            var session = services.GetRequiredService<SessionService>();
            if (!await session.UnlockAsync(_opts.Password))
            {
                _out.WriteLine("Unlock failed: wrong password for the existing node.");
                return 3;
            }

            // 5. Create folders through FolderService (depth-sorted so ancestors precede descendants —
            //    FolderService.CreateAsync throws on an existing path, and a shallow path can never be
            //    an implicit ancestor of an already-created shallower one).
            using (var workScope = services.CreateScope())
            {
                var folderService = workScope.ServiceProvider.GetRequiredService<FolderService>();
                foreach (var path in corpus.Folders.OrderBy(SegmentDepth))
                {
                    await folderService.CreateAsync(path);
                }
                _out.WriteLine($"Created {corpus.Folders.Count} leaf folders (plus their ancestor stubs).");
            }

            // 6. Stream articles through ArticleService. Protected specs are wrapped with the codec
            //    first; ArticleService derives the Protected flag from the wrapped body itself.
            int created = 0, protectedCount = 0, failed = 0;
            var sw = Stopwatch.StartNew();
            using (var workScope = services.CreateScope())
            {
                var articleService = workScope.ServiceProvider.GetRequiredService<ArticleService>();
                foreach (var spec in corpus.BuildArticles())
                {
                    string plaintext = spec.Protected
                        ? ProtectedContentCodec.Wrap(spec.Body, SyntheticCorpus.ProtectedPassphrase)
                        : spec.Body;

                    try
                    {
                        await articleService.CreateAsync(spec.Title, spec.TreePath, spec.Tags.ToList(), plaintext);
                        created++;
                        if (spec.Protected) protectedCount++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _out.WriteLine($"  FAIL [{created + failed}] \"{spec.Title}\" @ {spec.TreePath}: {ex.Message}");
                    }

                    if ((created + failed) % 1000 == 0)
                    {
                        double secs = sw.Elapsed.TotalSeconds;
                        double rate = secs > 0 ? (created + failed) / secs : 0;
                        _out.WriteLine($"  progress: {created + failed}/{corpus.ArticleCount} articles ({created} ok, {failed} failed) — {rate:F0} art/s, {sw.Elapsed}");
                    }
                }
            }

            sw.Stop();
            _out.WriteLine();
            _out.WriteLine($"Done in {sw.Elapsed}: {created} articles created ({protectedCount} protected), {failed} failed.");
            return failed > 0 ? 1 : 0;
        }
        finally
        {
            if (services is IAsyncDisposable ad)
                await ad.DisposeAsync();
        }
    }

    private static int SegmentDepth(string path) =>
        path.Count(c => c == '/');
}
