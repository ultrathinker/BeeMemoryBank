using System;
using System.IO;
using System.Text;

namespace BeeMemoryBank.Hosting;

/// <summary>
/// Makes redirected stdout/stderr UTF-8. On Windows a process whose output goes to a pipe (bmbd and
/// its Api/Web children under the Desktop app) otherwise encodes with the OEM code page, which turns
/// any non-Latin text in log lines — display names, folder titles — into '?' before it reaches the
/// log file. The reading side (the Desktop app) decodes the pipe as UTF-8.
/// </summary>
public static class Utf8Console
{
    /// <summary>Call first thing in Main, before any logger or host writes to the console.</summary>
    public static void EnableForRedirectedOutput()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (Console.IsOutputRedirected)
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
        if (Console.IsErrorRedirected)
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
    }
}
