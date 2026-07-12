using BeeMemoryBank.Hosting;

namespace BeeMemoryBank.Node.Tests.StubProcess;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? readyFilePath = null;
        var urls = new List<string> { "http://127.0.0.1:8080" };
        int exitCode = 0;
        bool crashImmediately = false;
        bool writeReadyFile = true;
        int exitDelayMs = -1;
        string appName = "StubApp";

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--ready-file" && i + 1 < args.Length)
            {
                readyFilePath = args[++i];
            }
            else if (args[i] == "--urls" && i + 1 < args.Length)
            {
                urls = args[++i].Split(',').ToList();
            }
            else if (args[i] == "--exit-code" && i + 1 < args.Length)
            {
                exitCode = int.Parse(args[++i]);
            }
            else if (args[i] == "--crash-immediately")
            {
                crashImmediately = true;
            }
            else if (args[i] == "--no-ready-file")
            {
                writeReadyFile = false;
            }
            else if (args[i] == "--exit-delay-ms" && i + 1 < args.Length)
            {
                exitDelayMs = int.Parse(args[++i]);
            }
            else if (args[i] == "--app-name" && i + 1 < args.Length)
            {
                appName = args[++i];
            }
        }

        // If BMB_READY_FILE environment variable is specified, use it as fallback
        readyFilePath ??= Environment.GetEnvironmentVariable("BMB_READY_FILE");

        if (crashImmediately)
        {
            Console.WriteLine("[StubProcess] Crashing immediately with exit code 99");
            return 99;
        }

        if (writeReadyFile && !string.IsNullOrEmpty(readyFilePath))
        {
            Console.WriteLine($"[StubProcess] Writing ready file to '{readyFilePath}'...");
            var info = new ReadyFileInfo(
                Pid: Environment.ProcessId,
                Urls: urls,
                ApplicationName: appName,
                Version: "1.0.0-stub",
                StartupTimeUtc: DateTime.UtcNow
            );
            ReadyFileManager.Write(readyFilePath, info);
        }

        if (exitDelayMs >= 0)
        {
            Console.WriteLine($"[StubProcess] Sleeping for {exitDelayMs}ms before exit...");
            await Task.Delay(exitDelayMs);
            Console.WriteLine($"[StubProcess] Exiting with code {exitCode}");
            return exitCode;
        }

        // Wait on stdin until standard input is closed (signals clean shutdown)
        Console.WriteLine("[StubProcess] Waiting on stdin until closed...");
        try
        {
            while (Console.ReadLine() != null)
            {
                // Continue reading
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StubProcess] Exception reading stdin: {ex.Message}");
        }

        Console.WriteLine($"[StubProcess] Stdin closed, exiting with code {exitCode}");
        return exitCode;
    }
}
