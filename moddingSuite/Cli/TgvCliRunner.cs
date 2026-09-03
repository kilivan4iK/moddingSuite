using System;
using System.IO;
using System.Linq;
using moddingSuite.BL.TGV;
using moddingSuite.BL.Tgv;

namespace moddingSuite.Cli;

public static class TgvCliRunner
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        // Atlas mode: extract all sub-textures from a .atlas file.
        if (args[0] == "--atlas")
        {
            if (args.Length < 5)
            {
                PrintUsage();
                return 1;
            }
            return RunAtlasMode(args);
        }

        int failed = 0;
        foreach (string arg in args)
        {
            try
            {
                if (!File.Exists(arg))
                {
                    Console.Error.WriteLine($"[MISSING] {arg}");
                    failed++;
                    continue;
                }
                byte[] bytes = File.ReadAllBytes(arg);
                var file = new TgvReader().Read(bytes);
                Console.WriteLine($"  fmt={file.PixelFormatStr} declared={file.ImageWidth}x{file.ImageHeight} mips={file.MipMapCount}");
                using var bmp = TgvDecoder.DecodeFullRes(bytes);
                string outPath = Path.ChangeExtension(arg, ".png");
                TgvDecoder.SavePng(bmp, outPath);
                Console.WriteLine($"{Path.GetFileName(arg)} -> {Path.GetFileName(outPath)} ({bmp.Width}x{bmp.Height})");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ERROR] {arg}: {ex.Message}");
                failed++;
            }
        }
        return failed == 0 ? 0 : 2;
    }

    private static int RunAtlasMode(string[] args)
    {
        string atlasPath  = null;
        string tgvRoot    = null;
        string outDir     = null;
        for (int i = 1; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--file":     atlasPath = args[++i]; break;
                case "--tgv-root": tgvRoot   = args[++i]; break;
                case "--out":      outDir    = args[++i]; break;
            }
        }
        if (atlasPath == null || tgvRoot == null || outDir == null)
        {
            PrintUsage();
            return 1;
        }
        if (!File.Exists(atlasPath))    { Console.Error.WriteLine($"Atlas not found: {atlasPath}"); return 1; }
        if (!Directory.Exists(tgvRoot)) { Console.Error.WriteLine($"TGV root not found: {tgvRoot}"); return 1; }
        Directory.CreateDirectory(outDir);

        byte[] atlasBytes = File.ReadAllBytes(atlasPath);

        // Build a filesystem-based resolver. For every "Assets/.../foo.tgv" we
        // look up matching files under tgvRoot recursively, then return raw bytes.
        var tgvFiles = Directory.EnumerateFiles(tgvRoot, "*.tgv", SearchOption.AllDirectories).ToList();
        Console.WriteLine($"Atlas:    {atlasPath}");
        Console.WriteLine($"TGV root: {tgvRoot} ({tgvFiles.Count} .tgv files)");
        Console.WriteLine($"Output:   {outDir}");

        byte[] Resolve(string sourceTgvRel)
        {
            string norm = (sourceTgvRel ?? "").Replace('\\', '/').ToLowerInvariant();
            string fileName = Path.GetFileName(norm);
            // 1. Try exact tail match (last N path components)
            foreach (string p in tgvFiles)
            {
                string pn = p.Replace('\\', '/').ToLowerInvariant();
                if (pn.EndsWith(norm, StringComparison.Ordinal))     return File.ReadAllBytes(p);
                if (Path.GetFileName(pn) == fileName)                return File.ReadAllBytes(p);
            }
            return null;
        }

        var result = AtlasTextureExtractor.Extract(
            atlasBytes: atlasBytes,
            atlasVirtualPath: atlasPath,
            outputDirectory: outDir,
            resolveTgv: Resolve,
            progress: msg => Console.WriteLine("  " + msg));

        Console.WriteLine($"Done. Entries={result.EntryCount}  Written={result.WrittenCount}  Skipped={result.SkippedCount}");
        if (result.Warnings.Count > 0)
        {
            Console.WriteLine("Warnings:");
            foreach (var w in result.Warnings.Take(10)) Console.WriteLine("  " + w);
            if (result.Warnings.Count > 10) Console.WriteLine($"  ... and {result.Warnings.Count - 10} more");
        }
        return result.WrittenCount > 0 ? 0 : 2;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage:\n" +
            "  moddingSuite.TgvCli.exe <file.tgv> [more.tgv ...]\n" +
            "    Decode each TGV to a sibling .png (full-res mip).\n" +
            "  moddingSuite.TgvCli.exe --atlas --file <TextureSmall.atlas> --tgv-root <folder> --out <folder>\n" +
            "    Extract all sub-textures from an atlas; source TGVs are looked up by\n" +
            "    filename tail match under <folder> recursively.");
    }
}
