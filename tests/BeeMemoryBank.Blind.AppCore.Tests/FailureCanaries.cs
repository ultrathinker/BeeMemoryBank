using System.Collections;
using System.Data.Common;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// The adversarial exceptions of the failure-text rule (fix round 2): each carries a canary and the kind of text a real one carries - a file path, a key alias, a request address with a token, a
/// library's words. Whatever a head can show - every string of a view record, the log, a job's outcome, the state a run leaves on disk - must hold none of <see cref="Needles"/>.
/// </summary>
internal static class FailureCanaries
{
    public const string Canary = "canary-123";
    public const string SecretPath = @"C:\secret\bmb_blind_seed_v1";

    /// <summary>Words that exist only in the adversarial messages (the sentences of the code under test never contain them).</summary>
    public static readonly string[] Needles =
    [
        Canary, "bmb_blind_", "canary-host", "token=", "being used by another process", "unable to open database", "Access to the path", @"C:\",
    ];

    /// <summary>The names of the adversarial exceptions, for a theory; <see cref="Make"/> builds each one (a fresh instance, so that a throw never re-uses a stack trace).</summary>
    public static TheoryData<string> Names => new(
        "io-path", "io-inner-path", "http-uri-token", "aggregate", "db", "crypto", "unauthorized", "invalid-operation-with-path", "argument-with-path", "unknown", "wrapper-with-inner",
        "type-initialization", "http-wrapped-in-io", "invalid-data",
        "invalid-operation-url-and-path", "argument-url-and-path", "unauthorized-url-and-path", "invalid-operation-plain", "wrapped-invalid-operation", "aggregate-of-invalid-operations",
        "target-invocation-of-argument");

    public static Exception Make(string name) => name switch
    {
        "io-path" => new IOException($"The process cannot access the file '{SecretPath}' because it is being used by another process. {Canary}"),
        "io-inner-path" => new IOException("The file could not be written.", new IOException($"{SecretPath} {Canary}")),
        "http-uri-token" => new HttpRequestException($"An error occurred while sending the request to https://canary-host.invalid/api?token={Canary}", new IOException(SecretPath)),
        "aggregate" => new AggregateException($"One or more errors occurred. {Canary}", new IOException($"{SecretPath} {Canary}"), new FormatException($"bad {Canary}")),
        "db" => new SqliteException($"SQLite Error 14: 'unable to open database file'. {SecretPath} {Canary}", 14),
        "crypto" => new CryptographicException($"The key {SecretPath} could not be loaded. {Canary}"),
        "unauthorized" => new UnauthorizedAccessException($"Access to the path '{SecretPath}' is denied. {Canary}"),
        "invalid-operation-with-path" => new InvalidOperationException($"Could not open {SecretPath} {Canary}"),
        "argument-with-path" => new ArgumentException($"The path '{SecretPath}' is not valid. {Canary} (Parameter 'path')"),
        "unknown" => new NotSupportedException($"{Canary} {SecretPath}"),
        "wrapper-with-inner" => new InvalidOperationException("The step failed.", new IOException($"{SecretPath} {Canary}")),
        "type-initialization" => new TypeInitializationException("Some.Type", new IOException($"{SecretPath} {Canary}")),
        "http-wrapped-in-io" => new IOException("The step failed.", new HttpRequestException($"https://canary-host.invalid/x?token={Canary}")),
        "invalid-data" => new InvalidDataException($"The package {SecretPath} is damaged. {Canary}"),
        // The TYPE of these says nothing about where the message came from: a framework, a library or a peer-influenced path can throw them with a URL, a path, an id or a token.
        "invalid-operation-url-and-path" => new InvalidOperationException($"https://x.example/?token={Canary} {SecretPath}"),
        "argument-url-and-path" => new ArgumentException($"https://x.example/?token={Canary} {SecretPath} (Parameter 'x')"),
        "unauthorized-url-and-path" => new UnauthorizedAccessException($"https://x.example/?token={Canary} {SecretPath}"),
        "invalid-operation-plain" => new InvalidOperationException($"account 8f3a-{Canary} is locked"),
        "wrapped-invalid-operation" => new InvalidOperationException("The step failed.", new InvalidOperationException($"https://x.example/?token={Canary} {SecretPath}")),
        "aggregate-of-invalid-operations" => new AggregateException("many",
            new InvalidOperationException($"https://x.example/?token={Canary} {SecretPath}"), new ArgumentException($"account 8f3a-{Canary}")),
        "target-invocation-of-argument" => new System.Reflection.TargetInvocationException(new ArgumentException($"https://x.example/?token={Canary} {SecretPath}")),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>The names whose exception is an I/O, HTTP or invalid-data failure: the ones a long job catches itself and records as its failure.</summary>
    public static bool IsRecordedByTheJob(string name) => name is "io-path" or "io-inner-path" or "http-uri-token" or "http-wrapped-in-io" or "invalid-data";

    /// <summary>True when the text holds any word that only an adversarial message has.</summary>
    public static bool Leaks(string? text) =>
        text is not null && Needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every string reachable from <paramref name="value"/>: the string fields and properties of records and classes of this product (the view records, the status, the log entries) and the
    /// items of lists. Dates, numbers and enums are not strings and are skipped. A bounded walk (depth 6), so that a cycle cannot run away.
    /// </summary>
    public static IEnumerable<string> Strings(object? value, int depth = 0)
    {
        switch (value)
        {
            case null: yield break;
            case string text: yield return text; yield break;
            case Exception ex:
                // A thrown exception is read the way a head reads it: its message and everything inside it.
                foreach (var part in ExceptionStrings(ex)) yield return part;
                yield break;
            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    foreach (var s in Strings(entry.Key, depth + 1)) yield return s;
                    foreach (var s in Strings(entry.Value, depth + 1)) yield return s;
                }
                yield break;
            case IEnumerable items when depth < 6:
                foreach (var item in items)
                    foreach (var s in Strings(item, depth + 1)) yield return s;
                yield break;
        }

        var type = value.GetType();
        if (depth >= 6 || type.IsPrimitive || type.IsEnum || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan)
            || type == typeof(decimal) || type.Namespace is null || !type.Namespace.StartsWith("BeeMemoryBank", StringComparison.Ordinal))
            yield break;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0) continue;
            object? inner;
            try { inner = property.GetValue(value); }
            catch (TargetInvocationException) { continue; }
            foreach (var s in Strings(inner, depth + 1)) yield return s;
        }
    }

    /// <summary>The message of an exception and of everything inside it (the inner exceptions and the members of an aggregate): what a head that tells "the whole story" would read.</summary>
    public static IEnumerable<string> ExceptionStrings(Exception ex, int depth = 0)
    {
        if (depth > 8) yield break;
        yield return ex.Message;
        if (ex is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions)
                foreach (var s in ExceptionStrings(inner, depth + 1)) yield return s;
        else if (ex.InnerException is { } next)
            foreach (var s in ExceptionStrings(next, depth + 1)) yield return s;
    }

    /// <summary>Fails with the place and the offending text when any string of <paramref name="value"/> carries an adversarial word.</summary>
    public static void AssertClean(object? value, string where)
    {
        var strings = Strings(value).ToList();
        strings.Where(Leaks).Should().BeEmpty($"{where} must show no word of the exception (a path, a key alias, an address, a library's words)");
    }

    /// <summary>The text of every file under <paramref name="folder"/> that is not a database (a log, a state file): the state a run leaves behind, read as text.</summary>
    public static IEnumerable<string> StoredTexts(string folder)
    {
        if (!Directory.Exists(folder)) yield break;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || file.EndsWith("-wal", StringComparison.Ordinal) || file.EndsWith("-shm", StringComparison.Ordinal)) continue;
            string text;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
            }
            // A file that is gone between the listing and the open — including one whose deletion is still under way, which Windows no longer opens at all —
            // has no text left to scan: the same as listing it a moment later.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            yield return text;
        }
    }
}
