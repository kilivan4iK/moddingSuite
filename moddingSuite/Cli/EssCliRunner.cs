using System;
using System.IO;
using moddingSuite.BL.Ess;

namespace moddingSuite.Cli;

public static class EssCliRunner
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: moddingSuite.exe ess <file.ess> [more.ess ...]");
            Console.Error.WriteLine("       Each .ess is decoded to .wav next to it.");
            return 1;
        }

        int failed = 0;
        foreach (var path in args)
        {
            try
            {
                var ess = File.ReadAllBytes(path);
                var wav = WarnoEssDecoder.DecodeToWav(ess);
                var outPath = Path.ChangeExtension(path, ".wav");
                File.WriteAllBytes(outPath, wav);
                Console.WriteLine($"{Path.GetFileName(path)} -> {Path.GetFileName(outPath)} ({wav.Length} bytes)");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {Path.GetFileName(path)}: {ex.Message}");
            }
        }
        return failed == 0 ? 0 : 2;
    }
}
