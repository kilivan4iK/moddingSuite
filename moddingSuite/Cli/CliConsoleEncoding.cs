using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace moddingSuite.Cli;

// Makes every CLI subcommand write stdout/stderr as UTF-8 without BOM. Callers (the
// Blender plugin) decode our output as UTF-8, while the console default is the OEM code
// page, so non-ASCII paths (Cyrillic user names, accented install folders) came out
// mangled. Kept outside EntryPoint so it has no WPF dependency.
internal static class CliConsoleEncoding
{
    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleOutputCP(uint codePageId);

    private const uint Utf8CodePage = 65001;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Switches Console.Out and Console.Error to UTF-8 (no BOM). Call after AttachConsole
    /// and before anything is written. Never throws. Returns the console code page to hand
    /// back to <see cref="RestoreCodePage"/>, or 0 when there is nothing to restore.
    /// </summary>
    public static uint UseUtf8Output()
    {
        uint previousCodePage = TryGetConsoleOutputCodePage();
        try
        {
            // Covers both a real console and redirected handles: .NET builds the
            // redirected writers from OutputEncoding too.
            Console.OutputEncoding = Utf8NoBom;
            return previousCodePage != 0 && previousCodePage != Utf8CodePage ? previousCodePage : 0;
        }
        catch (Exception)
        {
            // On Windows the setter calls SetConsoleOutputCP, which fails when no console
            // is attached (parent without a console, e.g. a GUI app spawning us with
            // pipes) - exactly the plugin case. Fall through and wrap the raw handles.
        }

        try
        {
            UseUtf8Writers();
        }
        catch (Exception)
        {
            // Keep the default writers; wrong glyphs beat a CLI that cannot start.
        }

        return 0;
    }

    /// <summary>
    /// Puts back the code page <see cref="UseUtf8Output"/> replaced. The console belongs to
    /// the parent (cmd.exe), so without this the user's window would stay on UTF-8 after
    /// we exit.
    /// </summary>
    public static void RestoreCodePage(uint codePage)
    {
        if (codePage == 0)
            return;

        try
        {
            Console.Out.Flush();
            Console.Error.Flush();
            SetConsoleOutputCP(codePage);
        }
        catch (Exception)
        {
            // best effort
        }
    }

    private static void UseUtf8Writers()
    {
        // OpenStandard* returns Stream.Null for a missing handle, so this is safe even
        // when stdout/stderr are not connected at all.
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Utf8NoBom) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), Utf8NoBom) { AutoFlush = true });
    }

    private static uint TryGetConsoleOutputCodePage()
    {
        try
        {
            return GetConsoleOutputCP(); // 0 when no console is attached
        }
        catch (Exception)
        {
            return 0; // kernel32 unavailable (non-Windows test harness)
        }
    }
}
