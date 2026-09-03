using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using moddingSuite.BL.Ndf;
using moddingSuite.Model.Ndfbin;
using moddingSuite.Model.Ndfbin.Types.AllTypes;

namespace moddingSuite.Cli;

public static class AtlasCliRunner
{
    private const int ExitOk = 0;
    private const int ExitNoEntries = 2;
    private const int ExitHardError = 3;

    private sealed class CliOptions
    {
        public required string WarnoRoot { get; init; }
        public required string AssetPath { get; init; }
        public required string OutJson { get; init; }
        public required string CacheDir { get; init; }
        public bool Verbose { get; init; }
        public bool IncludeSiblingAssets { get; init; }
    }

    private sealed class ContainerInfo
    {
        public string SourceTgvRel { get; init; } = "";
        public string SourceRole { get; init; } = "generic";
        public int WidthPx { get; init; }
        public int HeightPx { get; init; }
    }

    private sealed class TargetItem
    {
        public string LogicalRel { get; init; } = "";
        public string Basename { get; init; } = "";
        public string Channel { get; init; } = "generic";
    }

    private sealed class TextureItem
    {
        public string SourceTgvRel { get; init; } = "";
        public string SourceRole { get; init; } = "generic";
        public string SourceAssetPath { get; init; } = "";
        public int X { get; init; }
        public int Y { get; init; }
        public int W { get; init; }
        public int H { get; init; }
        public List<TargetItem> Targets { get; init; } = new();
    }

    public static int Run(string[] args)
    {
        try
        {
            var options = ParseArgs(args);
            return RunCore(options);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitHardError;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"atlas_cli_error: {ex.Message}");
            return ExitHardError;
        }
    }

    private static int RunCore(CliOptions options)
    {
        string assetPath = NormalizeAssetPath(options.AssetPath);
        string outJsonPath = ResolveOutputPath(options.OutJson);
        string cacheDir = Path.GetFullPath(options.CacheDir);
        string warnoRoot = Path.GetFullPath(options.WarnoRoot);

        Directory.CreateDirectory(Path.GetDirectoryName(outJsonPath) ?? ".");
        Directory.CreateDirectory(cacheDir);

        if (!Directory.Exists(warnoRoot))
        {
            Console.Error.WriteLine($"WARNO root not found: {warnoRoot}");
            return ExitHardError;
        }

        string? atlasPath = ResolveAtlasPath(
            warnoRoot: warnoRoot,
            cacheDir: cacheDir,
            assetPath: assetPath,
            verbose: options.Verbose
        );
        if (string.IsNullOrWhiteSpace(atlasPath))
        {
            Console.Error.WriteLine($"Atlas not found for asset: {assetPath}");
            return ExitHardError;
        }

        if (options.Verbose)
        {
            Console.Error.WriteLine($"atlas_source={atlasPath}");
        }

        var reader = new NdfbinReader();
        NdfBinary ndf = reader.Read(File.ReadAllBytes(atlasPath));
        var containers = BuildContainerMap(ndf, assetPath);
        List<TextureItem> textures = BuildTextureRows(
            ndf: ndf,
            containers: containers,
            assetPath: assetPath,
            includeSiblingAssets: options.IncludeSiblingAssets,
            verbose: options.Verbose
        );

        if (textures.Count == 0)
        {
            Console.Error.WriteLine($"atlas_cli_no_entries: atlas data found but no entries for asset {assetPath}");
            return ExitNoEntries;
        }

        string cliVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        List<string> includedAssetPaths = textures
            .Select(t => t.SourceAssetPath)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var jsonPayload = new
        {
            schema_version = 1,
            asset_path = assetPath,
            included_asset_paths = includedAssetPaths,
            atlas_source = atlasPath,
            generated_at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            cli_version = cliVersion,
            textures = textures
                .OrderBy(t => t.SourceTgvRel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.SourceAssetPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Y)
                .ThenBy(t => t.X)
                .ThenBy(t => t.W)
                .ThenBy(t => t.H)
                .Select(t => new
                {
                    source_tgv_rel = t.SourceTgvRel,
                    source_role = t.SourceRole,
                    source_asset_path = t.SourceAssetPath,
                    crop_rect_px = new { x = t.X, y = t.Y, w = t.W, h = t.H },
                    targets = t.Targets
                        .OrderBy(x => x.LogicalRel, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.Channel, StringComparer.OrdinalIgnoreCase)
                        .Select(x => new
                        {
                            logical_rel = x.LogicalRel,
                            basename = x.Basename,
                            channel = x.Channel,
                        }),
                }),
        };

        var serializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
        };
        File.WriteAllText(outJsonPath, JsonSerializer.Serialize(jsonPayload, serializerOptions));
        Console.WriteLine($"[OK] atlas json exported: {outJsonPath} entries={textures.Count}");
        return ExitOk;
    }

    private static CliOptions ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool verbose = false;
        bool includeSiblingAssets = false;

        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i].Trim();
            if (token.Equals("--verbose", StringComparison.OrdinalIgnoreCase))
            {
                verbose = true;
                continue;
            }
            if (token.Equals("--include-sibling-assets", StringComparison.OrdinalIgnoreCase))
            {
                includeSiblingAssets = true;
                continue;
            }

            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unknown argument: {token}");
            }

            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for argument: {token}");
            }

            string value = args[++i].Trim();
            map[token] = value;
        }

        string Require(string key)
        {
            if (map.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            throw new ArgumentException(
                "Required arguments: --warno-root --asset-path --out-json --cache-dir [--verbose] [--include-sibling-assets]"
            );
        }

        return new CliOptions
        {
            WarnoRoot = Require("--warno-root"),
            AssetPath = Require("--asset-path"),
            OutJson = Require("--out-json"),
            CacheDir = Require("--cache-dir"),
            Verbose = verbose,
            IncludeSiblingAssets = includeSiblingAssets,
        };
    }

    private static string ResolveOutputPath(string rawPath)
    {
        string p = Path.GetFullPath(rawPath);
        if (p.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return p;
        }

        return Path.Combine(p, "atlas_map.json");
    }

    private static string? ResolveAtlasPath(string warnoRoot, string cacheDir, string assetPath, bool verbose)
    {
        string assetDir = Path.GetDirectoryName(assetPath.Replace('/', Path.DirectorySeparatorChar)) ?? "Assets";
        string rel = assetDir.Replace(Path.DirectorySeparatorChar, '/');
        string[] relParts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string relFs = Path.Combine(relParts);

        var candidates = new[]
        {
            Path.Combine(warnoRoot, "Output", "PC", "Atlas", relFs, "TextureSmall.atlas"),
            Path.Combine(warnoRoot, "Mods", "ModData", "base", "PC", "Atlas", relFs, "TextureSmall.atlas"),
            Path.Combine(cacheDir, "PC", "Atlas", relFs, "TextureSmall.atlas"),
        };

        foreach (string candidate in candidates)
        {
            if (verbose)
            {
                Console.Error.WriteLine($"atlas_candidate={candidate}");
            }

            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static Dictionary<uint, ContainerInfo> BuildContainerMap(NdfBinary ndf, string assetPath)
    {
        var containers = new Dictionary<uint, ContainerInfo>();

        foreach (NdfObject obj in ndf.Instances)
        {
            string className = obj.Class?.Name ?? "";
            if (!className.Contains("texturesmallcontainer", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string textureFile = ReadStringProperty(obj, "TextureFileName");
            string sourceTgvRel = NormalizeSourceTgv(textureFile, assetPath);
            string textureCarac = ReadStringProperty(obj, "TextureCarac");
            string sourceRole = CanonicalSourceRole(textureCarac, sourceTgvRel);

            int width = ReadIntProperty(obj, "Width");
            int height = ReadIntProperty(obj, "Height");
            // WARNO v2 atlas (the post-188908 update that also switched NDF bodies to
            // zstd): the container stores its page size in a single EugInt2 "Size"
            // property instead of separate Width/Height ints.
            if ((width <= 0 || height <= 0) && TryReadFloat2(obj, "Size", out double sizeW, out double sizeH))
            {
                if (width <= 0) width = (int)Math.Round(sizeW, MidpointRounding.AwayFromZero);
                if (height <= 0) height = (int)Math.Round(sizeH, MidpointRounding.AwayFromZero);
            }

            containers[obj.Id] = new ContainerInfo
            {
                SourceTgvRel = sourceTgvRel,
                SourceRole = sourceRole,
                WidthPx = Math.Max(0, width),
                HeightPx = Math.Max(0, height),
            };
        }

        return containers;
    }

    private static List<TextureItem> BuildTextureRows(
        NdfBinary ndf,
        Dictionary<uint, ContainerInfo> containers,
        string assetPath,
        bool includeSiblingAssets,
        bool verbose
    )
    {
        var textures = new List<TextureItem>();
        string assetDir = NormalizeAssetPath(Path.GetDirectoryName(assetPath.Replace('\\', '/')) ?? "Assets");
        string assetStem = Path.GetFileNameWithoutExtension(assetPath).ToLowerInvariant();

        foreach (NdfObject obj in ndf.Instances)
        {
            string partRaw = ReadStringProperty(obj, "TexturePartFileName");
            if (string.IsNullOrWhiteSpace(partRaw))
            {
                continue;
            }

            uint? containerId = ReadObjectReferenceId(obj, "Container");
            ContainerInfo container = ResolveContainer(containers, containerId);

            // WARNO v2 atlas part schema: AbsPos (EugFloat2 pixel x,y), SizeInPixels
            // (EugFloat2 pixel w,h), MinUV/MaxUV (EugFloat2). The first part at the
            // origin omits AbsPos/MinUV (defaults to 0,0). v1 used AbsX/AbsY +
            // WidthPixel/HeightPixel or MinU/MinV/MaxU/MaxV. Support both, preferring v2.
            int cw = container.WidthPx;
            int ch = container.HeightPx;
            bool hasAbsPos = TryReadFloat2(obj, "AbsPos", out double absPosX, out double absPosY);
            bool hasSizePx = TryReadFloat2(obj, "SizeInPixels", out double sizePxW, out double sizePxH);
            bool hasMinUv = TryReadFloat2(obj, "MinUV", out double minUvU, out double minUvV);
            bool hasMaxUv = TryReadFloat2(obj, "MaxUV", out double maxUvU, out double maxUvV);

            int x;
            if (hasAbsPos) x = (int)Math.Round(absPosX, MidpointRounding.AwayFromZero);
            else if (hasMinUv && cw > 0) x = (int)Math.Round(minUvU * cw, MidpointRounding.AwayFromZero);
            else x = ReadCropCoord(obj, "AbsX", "MinU", cw);

            int y;
            if (hasAbsPos) y = (int)Math.Round(absPosY, MidpointRounding.AwayFromZero);
            else if (hasMinUv && ch > 0) y = (int)Math.Round(minUvV * ch, MidpointRounding.AwayFromZero);
            else y = ReadCropCoord(obj, "AbsY", "MinV", ch);

            int w;
            if (hasSizePx && sizePxW > 0) w = (int)Math.Round(sizePxW, MidpointRounding.AwayFromZero);
            else if (hasMaxUv && cw > 0) w = (int)Math.Round((maxUvU - (hasMinUv ? minUvU : 0.0)) * cw, MidpointRounding.AwayFromZero);
            else w = ReadCropSize(obj, "WidthPixel", "MinU", "MaxU", cw);

            int h;
            if (hasSizePx && sizePxH > 0) h = (int)Math.Round(sizePxH, MidpointRounding.AwayFromZero);
            else if (hasMaxUv && ch > 0) h = (int)Math.Round((maxUvV - (hasMinUv ? minUvV : 0.0)) * ch, MidpointRounding.AwayFromZero);
            else h = ReadCropSize(obj, "HeightPixel", "MinV", "MaxV", ch);

            if (w <= 0 || h <= 0)
            {
                continue;
            }

            List<TargetItem> targets = ParseTargets(
                partRaw: partRaw,
                assetDir: assetDir,
                sourceRole: container.SourceRole
            );
            targets = targets
                .Where(t => IsTargetForAsset(t.LogicalRel, assetDir, assetStem, includeSiblingAssets))
                .ToList();

            if (targets.Count == 0)
            {
                continue;
            }

            string sourceTgvRel = container.SourceTgvRel;
            if (string.IsNullOrWhiteSpace(sourceTgvRel))
            {
                sourceTgvRel = DeriveSourceFromTarget(assetDir, container.SourceRole);
            }

            if (verbose)
            {
                Console.Error.WriteLine(
                    $"part source={sourceTgvRel} rect=({x},{y},{w},{h}) targets={targets.Count} raw={partRaw}"
                );
            }

            string sourceAssetPath = DeriveSourceAssetPathFromTargets(targets, assetDir, assetPath);

            textures.Add(new TextureItem
            {
                SourceTgvRel = sourceTgvRel,
                SourceRole = container.SourceRole,
                SourceAssetPath = sourceAssetPath,
                X = Math.Max(0, x),
                Y = Math.Max(0, y),
                W = w,
                H = h,
                Targets = targets,
            });
        }

        return textures;
    }

    private static ContainerInfo ResolveContainer(Dictionary<uint, ContainerInfo> containers, uint? containerId)
    {
        if (containerId.HasValue && containers.TryGetValue(containerId.Value, out ContainerInfo? info))
        {
            return info;
        }

        if (containers.Count > 0)
        {
            return containers.Values.First();
        }

        return new ContainerInfo();
    }

    private static int ReadCropCoord(NdfObject obj, string absName, string uvName, int fullSize)
    {
        double abs = ReadDoubleProperty(obj, absName, double.NaN);
        if (!double.IsNaN(abs))
        {
            return (int)Math.Round(abs, MidpointRounding.AwayFromZero);
        }

        double uv = ReadDoubleProperty(obj, uvName, 0.0);
        if (fullSize > 0)
        {
            return (int)Math.Round(uv * fullSize, MidpointRounding.AwayFromZero);
        }

        return 0;
    }

    private static int ReadCropSize(
        NdfObject obj,
        string pxName,
        string minUvName,
        string maxUvName,
        int fullSize
    )
    {
        double px = ReadDoubleProperty(obj, pxName, double.NaN);
        if (!double.IsNaN(px) && px > 0.0)
        {
            return (int)Math.Round(px, MidpointRounding.AwayFromZero);
        }

        if (fullSize > 0)
        {
            double minUv = ReadDoubleProperty(obj, minUvName, 0.0);
            double maxUv = ReadDoubleProperty(obj, maxUvName, 0.0);
            double delta = Math.Max(0.0, maxUv - minUv);
            int fromUv = (int)Math.Round(delta * fullSize, MidpointRounding.AwayFromZero);
            if (fromUv > 0)
            {
                return fromUv;
            }
        }

        return 0;
    }

    private static List<TargetItem> ParseTargets(string partRaw, string assetDir, string sourceRole)
    {
        string raw = (partRaw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new List<TargetItem>();
        }

        string cleaned = RemovePrefix(raw, "CombinedTexture:/");
        cleaned = RemovePrefix(cleaned, "CombinedTexture:");

        string combineHint = "";
        int markerPos = cleaned.IndexOf("`_`", StringComparison.Ordinal);
        if (markerPos >= 0)
        {
            combineHint = cleaned[(markerPos + 3)..];
            cleaned = cleaned[..markerPos];
        }

        string[] chunks = cleaned
            .Split("$_$", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (chunks.Length == 0)
        {
            chunks = new[] { cleaned };
        }

        string baseDir = assetDir;
        string firstNorm = NormalizeLogicalPath(chunks[0], defaultDir: assetDir, defaultExt: ".png");
        if (!string.IsNullOrWhiteSpace(firstNorm))
        {
            string firstDir = NormalizeAssetPath(Path.GetDirectoryName(firstNorm.Replace('\\', '/')) ?? assetDir);
            if (!string.IsNullOrWhiteSpace(firstDir))
            {
                baseDir = firstDir;
            }
        }

        var targets = new List<TargetItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < chunks.Length; i++)
        {
            string logical = NormalizeLogicalPath(chunks[i], defaultDir: baseDir, defaultExt: ".png");
            if (string.IsNullOrWhiteSpace(logical))
            {
                continue;
            }

            string basename = Path.GetFileNameWithoutExtension(logical.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(basename))
            {
                continue;
            }

            string channel = ChannelFromSuffix(basename);
            if (channel == "generic")
            {
                channel = ChannelFromCombinedHint(combineHint, i, chunks.Length);
            }
            if (channel == "generic")
            {
                channel = CanonicalSourceRole(sourceRole, sourceRole);
            }
            if (channel == "generic")
            {
                channel = "generic";
            }

            string key = $"{logical}|{channel}";
            if (!seen.Add(key))
            {
                continue;
            }

            targets.Add(new TargetItem
            {
                LogicalRel = logical,
                Basename = basename,
                Channel = channel,
            });
        }

        return targets;
    }

    private static bool IsTargetForAsset(string logicalRel, string assetDir, string assetStem, bool includeSiblingAssets)
    {
        string logical = NormalizeAssetPath(logicalRel).ToLowerInvariant();
        string logicalDir = NormalizeAssetPath(Path.GetDirectoryName(logical.Replace('\\', '/')) ?? "");
        string assetDirNorm = NormalizeAssetPath(assetDir);

        if (!string.Equals(logicalDir, assetDirNorm, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (includeSiblingAssets)
        {
            return true;
        }

        string stem = (assetStem ?? "").Trim().ToLowerInvariant();
        string name = Path.GetFileNameWithoutExtension(logical).ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(stem))
        {
            if (name.Equals(stem, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (name.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string needle = "/" + stem + "_";
            if (logical.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string DeriveSourceAssetPathFromTargets(
        List<TargetItem> targets,
        string assetDir,
        string fallbackAssetPath
    )
    {
        if (targets is null || targets.Count == 0)
        {
            return NormalizeAssetPath(fallbackAssetPath);
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (TargetItem t in targets)
        {
            string basename = (t.Basename ?? "").Trim();
            if (string.IsNullOrWhiteSpace(basename))
            {
                basename = Path.GetFileNameWithoutExtension(t.LogicalRel ?? "");
            }
            string assetStem = StripTextureChannelSuffix(basename);
            if (string.IsNullOrWhiteSpace(assetStem))
            {
                continue;
            }
            if (!counts.ContainsKey(assetStem))
            {
                counts[assetStem] = 0;
            }
            counts[assetStem] += 1;
        }

        if (counts.Count == 0)
        {
            return NormalizeAssetPath(fallbackAssetPath);
        }

        string pickStem = counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key)
            .FirstOrDefault() ?? Path.GetFileNameWithoutExtension(fallbackAssetPath);
        return NormalizeAssetPath($"{assetDir}/{pickStem}.fbx");
    }

    private static string StripTextureChannelSuffix(string stemRaw)
    {
        string stem = (stemRaw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(stem))
        {
            return stem;
        }
        string[] suffixes = { "_NM", "_ORM", "_AO", "_A", "_R", "_M", "_O", "_OS", "_RS", "_DA", "_D" };
        foreach (string suffix in suffixes)
        {
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return stem[..^suffix.Length];
            }
        }
        return stem;
    }

    private static string DeriveSourceFromTarget(string assetDir, string sourceRole)
    {
        string roleSuffix = sourceRole switch
        {
            "normal" => "TSCNM_NormalTexture01",
            "orm" => "TSCORM_CombinedORMTexture01",
            "combined_da" => "TSCColorAlpha_CombinedDATexture01",
            _ => "TSCColor_DiffuseTextureNoAlpha01",
        };
        return NormalizeSourceTgv($"{assetDir}/{roleSuffix}.png", assetDir);
    }

    private static string NormalizeSourceTgv(string rawPath, string defaultDir)
    {
        string normalized = NormalizeLogicalPath(rawPath, defaultDir, ".tgv");
        if (!normalized.EndsWith(".tgv", StringComparison.OrdinalIgnoreCase))
        {
            normalized = Path.ChangeExtension(normalized.Replace('/', Path.DirectorySeparatorChar), ".tgv")
                .Replace(Path.DirectorySeparatorChar, '/');
        }
        return normalized;
    }

    private static string NormalizeLogicalPath(string rawPath, string defaultDir, string defaultExt)
    {
        string s = (rawPath ?? "").Trim().Trim('"').Trim('\'').Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(s))
        {
            return "";
        }

        s = RemovePrefix(s, "CombinedTexture:/");
        s = RemovePrefix(s, "CombinedTexture:");
        s = RemovePrefix(s, "GameData:/");
        s = RemovePrefix(s, "GameData:");
        s = RemovePrefix(s, "ZZ:/");
        s = RemovePrefix(s, "ZZ:");
        s = s.TrimStart('/');

        if (s.StartsWith("PC/Atlas/Assets/", StringComparison.OrdinalIgnoreCase))
        {
            s = "Assets/" + s["PC/Atlas/Assets/".Length..];
        }
        else if (!s.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        {
            int idxAssets = s.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
            if (idxAssets >= 0)
            {
                s = s[(idxAssets + 1)..];
            }
            else if (!s.Contains('/') && !s.Contains(':'))
            {
                s = $"{NormalizeAssetPath(defaultDir)}/{s}";
            }
            else
            {
                s = $"{NormalizeAssetPath(defaultDir)}/{s}";
            }
        }

        while (s.Contains("//", StringComparison.Ordinal))
        {
            s = s.Replace("//", "/", StringComparison.Ordinal);
        }

        if (!s.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        {
            s = "Assets/" + s.TrimStart('/');
        }
        else
        {
            s = "Assets/" + s["Assets/".Length..];
        }

        if (string.IsNullOrWhiteSpace(Path.GetExtension(s)))
        {
            s += defaultExt;
        }

        return s.Replace('\\', '/');
    }

    private static string NormalizeAssetPath(string raw)
    {
        string s = (raw ?? "").Trim().Replace('\\', '/');
        s = RemovePrefix(s, "GameData:/");
        s = RemovePrefix(s, "GameData:");
        s = RemovePrefix(s, "CombinedTexture:/");
        s = RemovePrefix(s, "CombinedTexture:");
        s = s.TrimStart('/');

        if (s.StartsWith("PC/Atlas/Assets/", StringComparison.OrdinalIgnoreCase))
        {
            s = "Assets/" + s["PC/Atlas/Assets/".Length..];
        }
        else if (!s.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        {
            int idxAssets = s.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
            if (idxAssets >= 0)
            {
                s = s[(idxAssets + 1)..];
            }
        }

        while (s.Contains("//", StringComparison.Ordinal))
        {
            s = s.Replace("//", "/", StringComparison.Ordinal);
        }

        if (s.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        {
            s = "Assets/" + s["Assets/".Length..];
        }

        return s;
    }

    private static string RemovePrefix(string value, string prefix)
    {
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return value[prefix.Length..];
        }

        return value;
    }

    private static string CanonicalSourceRole(string rawRole, string fallbackName)
    {
        string src = (rawRole + " " + fallbackName).ToLowerInvariant();
        if (src.Contains("tscnm", StringComparison.Ordinal) || src.Contains("_nm", StringComparison.Ordinal) || src.Contains("normal", StringComparison.Ordinal))
        {
            return "normal";
        }
        if (src.Contains("tscorm", StringComparison.Ordinal) || src.Contains("_orm", StringComparison.Ordinal) || src.Contains("combinedorm", StringComparison.Ordinal))
        {
            return "orm";
        }
        if (src.Contains("tsccoloralpha", StringComparison.Ordinal) || src.Contains("combinedda", StringComparison.Ordinal) || src.Contains("dacomb", StringComparison.Ordinal))
        {
            return "combined_da";
        }
        if (src.Contains("tsccolor", StringComparison.Ordinal) || src.Contains("diffuse", StringComparison.Ordinal))
        {
            return "diffuse";
        }
        return "generic";
    }

    private static string ChannelFromSuffix(string basename)
    {
        string up = (" " + basename).ToUpperInvariant();

        if (up.EndsWith("_NM", StringComparison.Ordinal))
            return "normal";
        if (up.EndsWith("_ORM", StringComparison.Ordinal))
            return "orm";
        if (up.EndsWith("_AO", StringComparison.Ordinal) || up.EndsWith("_OS", StringComparison.Ordinal))
            return "occlusion";
        if (up.EndsWith("_DA", StringComparison.Ordinal))
            return "combined_da";
        if (up.EndsWith("_A", StringComparison.Ordinal))
            return "alpha";
        if (up.EndsWith("_R", StringComparison.Ordinal) || up.EndsWith("_RS", StringComparison.Ordinal))
            return "roughness";
        if (up.EndsWith("_M", StringComparison.Ordinal))
            return "metallic";
        if (up.EndsWith("_O", StringComparison.Ordinal))
            return "occlusion";
        if (up.EndsWith("_D", StringComparison.Ordinal))
            return "diffuse";
        return "generic";
    }

    private static string ChannelFromCombinedHint(string hintRaw, int index, int total)
    {
        string hint = (hintRaw ?? "").ToLowerInvariant();
        if (hint.Contains("ormcomb", StringComparison.Ordinal) || hint.Contains("combinedorm", StringComparison.Ordinal))
        {
            return index switch
            {
                0 => "occlusion",
                1 when total >= 2 => "roughness",
                2 when total >= 3 => "metallic",
                _ => "orm",
            };
        }

        if (hint.Contains("dacomb", StringComparison.Ordinal) || hint.Contains("combinedda", StringComparison.Ordinal))
        {
            return index switch
            {
                0 => "diffuse",
                1 when total >= 2 => "alpha",
                _ => "combined_da",
            };
        }

        return "generic";
    }

    private static string ReadStringProperty(NdfObject obj, string propertyName)
    {
        NdfValueWrapper? value = FindValue(obj, propertyName);
        if (value is NdfString s)
        {
            return ((NdfStringReference?)s.Value)?.Value ?? "";
        }

        if (value is NdfFlatValueWrapper flat && flat.Value is not null)
        {
            return flat.Value.ToString() ?? "";
        }

        return value?.ToString() ?? "";
    }

    private static int ReadIntProperty(NdfObject obj, string propertyName)
    {
        double value = ReadDoubleProperty(obj, propertyName, 0.0);
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private static double ReadDoubleProperty(NdfObject obj, string propertyName, double fallback)
    {
        NdfValueWrapper? value = FindValue(obj, propertyName);
        if (value is null)
        {
            return fallback;
        }

        if (value is NdfSingle single)
        {
            return single.Value;
        }

        if (value is NdfFlatValueWrapper flat && flat.Value is not null)
        {
            object raw = flat.Value;
            try
            {
                return Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            }
            catch
            {
                string txt = raw.ToString() ?? "";
                if (double.TryParse(txt, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsedInvariant))
                {
                    return parsedInvariant;
                }
                if (double.TryParse(txt, NumberStyles.Any, CultureInfo.CurrentCulture, out double parsedCurrent))
                {
                    return parsedCurrent;
                }
            }
        }

        return fallback;
    }

    private static bool TryReadFloat2(NdfObject obj, string propertyName, out double v1, out double v2)
    {
        v1 = 0.0;
        v2 = 0.0;
        NdfValueWrapper? value = FindValue(obj, propertyName);
        if (value is NdfEugFloat2 f2)
        {
            v1 = Convert.ToDouble(f2.Value, CultureInfo.InvariantCulture);
            v2 = f2.Value2;
            return true;
        }
        if (value is NdfEugInt2 i2)
        {
            v1 = Convert.ToDouble(i2.Value, CultureInfo.InvariantCulture);
            v2 = i2.Value2;
            return true;
        }
        return false;
    }

    private static uint? ReadObjectReferenceId(NdfObject obj, string propertyName)
    {
        NdfValueWrapper? value = FindValue(obj, propertyName);
        if (value is NdfObjectReference reference)
        {
            return reference.InstanceId;
        }
        return null;
    }

    private static NdfValueWrapper? FindValue(NdfObject obj, string propertyName)
    {
        foreach (var pv in obj.PropertyValues)
        {
            if (pv?.Property?.Name is null)
            {
                continue;
            }
            if (!pv.Property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return pv.Value;
        }
        return null;
    }
}
