using System.Text.Json;
using BeeMemoryBank.Rekey;

namespace BeeMemoryBank.Cli.Commands;

/// <summary>
/// <c>bmb rekey --data D [--password-stdin] [--progress-json]</c> (rekey-offline.md §8.4): the offline content re-key,
/// with the node stopped. Exit codes: 0 done, 2 the pre-flight refused (nothing was created), 3 failed before the swap
/// (the old vault is in use), 4 the swap is pending (the next start finishes it). With <c>--progress-json</c> every
/// event is one JSON line on stdout, and the last one is <c>{"result":...,"report":...}</c>.
/// </summary>
public static class RekeyCommand
{
    public static async Task<int> HandleAsync(string dataPath, bool passwordStdin, bool progressJson, CancellationToken ct = default)
    {
        var password = passwordStdin ? Console.In.ReadLine() : PromptNoEcho("Owner's master password: ");
        if (string.IsNullOrEmpty(password))
        {
            await Console.Error.WriteLineAsync("No password given.");
            return (int)RekeyExit.PreflightRefused;
        }

        IRekeyProgress progress = progressJson ? new JsonLines(Console.Out) : new Human(Console.Error);
        var outcome = await RekeyRunner.RunAsync(new RekeyOptions
        {
            DataDir = dataPath,
            OwnerPassword = password,
            Progress = progress,
            Steps = RekeyPlan.Steps.Select(f => f()).ToList(),
            Preflight = RekeyPlan.Preflight?.Invoke(),
        }, ct);

        if (progressJson)
            Console.Out.WriteLine(JsonSerializer.Serialize(new { result = outcome.Result, report = outcome.ReportPath, message = outcome.Message }));
        else
            await (outcome.Exit == RekeyExit.Done ? Console.Out : Console.Error).WriteLineAsync(outcome.Message);
        return (int)outcome.Exit;
    }

    private static string? PromptNoEcho(string prompt)
    {
        Console.Error.Write(prompt);
        if (Console.IsInputRedirected) return Console.In.ReadLine();
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
            if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
        }
        Console.Error.WriteLine();
        return new string(chars.ToArray());
    }

    /// <summary>§8.4: <c>{"step":"...","done":n,"total":n,"note":"..."}</c>, one per line.</summary>
    public sealed class JsonLines(TextWriter output) : IRekeyProgress
    {
        private readonly object _gate = new();

        public void Report(string step, long done, long total, string? note = null)
        {
            var line = JsonSerializer.Serialize(new { step, done, total, note });
            lock (_gate) output.WriteLine(line);
        }
    }

    private sealed class Human(TextWriter output) : IRekeyProgress
    {
        private string? _last;

        public void Report(string step, long done, long total, string? note = null)
        {
            // One line per step and each tenth of it, not per row.
            var key = $"{step}:{note}:{(total > 0 ? done * 10 / total : 0)}";
            if (key == _last) return;
            _last = key;
            output.WriteLine(total > 0 ? $"[{step}] {done}/{total} {note}" : $"[{step}] {note}");
        }
    }
}
