using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit.Sdk;

namespace BeeMemoryBank.Storage.Tests.Squash;

/// <summary>
/// The migration chain of the releases before the squash (BMB-314), for the tests that need a REAL database of an older version.
/// The 39 files are not in the tree any more. This reads them from the git history of the checkout by blob id
/// (<c>blobs.txt</c>; a blob id is a content address, so it survives rebases and cherry-picks), checks every file against its id, and
/// builds a database the way a node of a released version built it: <see cref="LegacyMigrationRunner"/>, the frozen runner of 2.2.0..2.6.1,
/// over the list of files that version shipped (<c>chains.txt</c>). In a clone of the public repository the history is not there:
/// <see cref="LegacyChainFactAttribute"/> then skips the tests that need it, and says why.
/// </summary>
internal static class LegacyChain
{
    /// <summary>The prefix of the resource names the old ledger rows carry.</summary>
    public const string ResourcePrefix = "BeeMemoryBank.Storage.Migrations.";

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The tests run from inside the repository.");
    }

    private static string DataFile(string name) => Path.Combine(AppContext.BaseDirectory, "LegacyChain", name);

    /// <summary>The files each released version shipped, in order: tag -> file names.</summary>
    public static IReadOnlyDictionary<string, string[]> Chains { get; } = ReadChains();

    /// <summary>The names of the 39 files of 2.6.1 in order (the longest chain).</summary>
    public static string[] FinalChain => Chains["v2.6.1"];

    private static Dictionary<string, string[]> ReadChains() =>
        File.ReadAllLines(DataFile("chains.txt"))
            .Where(l => l.Length > 0 && l[0] != '#')
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(p => p[0], p => p.Skip(1).ToArray());

    private static readonly Lazy<(IReadOnlyDictionary<string, string>? Files, string Reason)> Loaded = new(Load);

    /// <summary>True when the files could be read from the history and are the ones the ids name.</summary>
    public static bool Available(out string reason)
    {
        reason = Loaded.Value.Reason;
        return Loaded.Value.Files is not null;
    }

    private static (IReadOnlyDictionary<string, string>?, string) Load()
    {
        try
        {
            var wanted = File.ReadAllLines(DataFile("blobs.txt"))
                .Where(l => l.Length > 0 && l[0] != '#')
                .Select(l => l.Split(' ')).ToDictionary(p => p[0], p => p[1]);
            var psi = new ProcessStartInfo("git", ["-C", RepoRoot(), "cat-file", "--batch"])
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var git = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
            var errors = git.StandardError.ReadToEndAsync();
            foreach (var blob in wanted.Values) git.StandardInput.Write(blob + "\n");
            git.StandardInput.Close();

            var result = new Dictionary<string, string>();
            var stream = git.StandardOutput.BaseStream;
            foreach (var (name, blob) in wanted)
            {
                var header = ReadLine(stream);
                var parts = header.Split(' ');
                if (parts.Length != 3 || parts[1] != "blob")
                    return (null, "the git history of this checkout does not hold the migration files of the releases before the squash (a clone of the public repository has no such history)");
                var size = int.Parse(parts[2]);
                var bytes = new byte[size];
                for (var read = 0; read < size;)
                {
                    var n = stream.Read(bytes, read, size - read);
                    if (n == 0) throw new EndOfStreamException();
                    read += n;
                }
                stream.ReadByte(); // the newline after each object
                // A blob id is sha1("blob <size>\0" + content): check it, so a wrong file cannot pass as the right one.
                var check = Convert.ToHexString(SHA1.HashData([.. Encoding.ASCII.GetBytes($"blob {size}\0"), .. bytes])).ToLowerInvariant();
                if (check != blob) return (null, $"the object {name} read from git does not match its id");
                result[name] = new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
            }
            git.WaitForExit(10_000);
            return (result, "");
        }
        catch (Exception ex)
        {
            // Whatever goes wrong (no git, not a repository, an odd answer), the tests that need the old files skip with this reason; a test
            // attribute that throws would break the discovery of the whole project.
            return (null, "git could not be run to read the migration files of the releases before the squash: " + ex.GetType().Name);
        }
    }

    private static string ReadLine(Stream s)
    {
        var sb = new StringBuilder();
        int b;
        while ((b = s.ReadByte()) != -1 && b != '\n') sb.Append((char)b);
        return sb.ToString();
    }

    /// <summary>The files a released version shipped, as (resource name, sql).</summary>
    public static IReadOnlyList<(string ResourceName, string Sql)> FilesOf(string tag)
    {
        Available(out var reason);
        var files = Loaded.Value.Files ?? throw new InvalidOperationException(reason);
        return Chains[tag].Select(n => (ResourcePrefix + n, files[n])).ToList();
    }

    /// <summary>
    /// Brings a database file to the state a node of <paramref name="tag"/> leaves it in: the frozen runner over that release's files.
    /// Called again with a later tag on the same file it does what an update did (the files it lacks are applied). The factory is
    /// returned open so a test may seed it; dispose it to close the file.
    /// </summary>
    public static async Task<DbConnectionFactory> ApplyAsync(DbConnectionFactory factory, string tag)
    {
        await new LegacyMigrationRunner(factory, FilesOf(tag)).RunMigrationsAsync();
        return factory;
    }

    public static async Task<DbConnectionFactory> BuildAsync(string dbPath, params string[] tagsInOrder)
    {
        var factory = new DbConnectionFactory(dbPath);
        foreach (var tag in tagsInOrder) await ApplyAsync(factory, tag);
        return factory;
    }

    /// <summary>The ledger rows a legacy node of <paramref name="tag"/> has: version and resource name of each of its files.</summary>
    public static IEnumerable<(int Version, string Name)> LedgerOf(string tag) =>
        Chains[tag].Select(n => (int.Parse(n[..3]), ResourcePrefix + n));

    /// <summary>Closes every pooled connection so the file can be read, hashed and copied as it lies on disk.</summary>
    public static void ReleaseFile() => SqliteConnection.ClearAllPools();
}

/// <summary>A test that needs the files of the releases before the squash; skipped, with the reason, when the history is not there.</summary>
internal sealed class LegacyChainFactAttribute : FactAttribute
{
    public LegacyChainFactAttribute()
    {
        if (!LegacyChain.Available(out var reason)) Skip = reason;
    }
}

/// <summary>The theory twin of <see cref="LegacyChainFactAttribute"/>.</summary>
internal sealed class LegacyChainTheoryAttribute : TheoryAttribute
{
    public LegacyChainTheoryAttribute()
    {
        if (!LegacyChain.Available(out var reason)) Skip = reason;
    }
}
