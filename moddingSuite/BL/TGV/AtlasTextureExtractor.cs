using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using moddingSuite.BL.Edata.Model;
using moddingSuite.BL.Ndf;
using moddingSuite.BL.TGV;
using moddingSuite.Model.Ndfbin;
using moddingSuite.Model.Ndfbin.Types.AllTypes;
using SkiaSharp;

namespace moddingSuite.BL.Tgv
{
    // First-version atlas → PNGs extractor.
    //
    // Pipeline:
    //   1. Parse the atlas NDF binary (TextureSmallContainer + TexturePart objects).
    //   2. For every TexturePart pick its container's source TGV reference, look
    //      that TGV up in the unified ZZ index, decode the full-res mip via
    //      TgvDecoder.
    //   3. Crop the decoded bitmap to the part's pixel rect and save as PNG
    //      using the TexturePartFileName basename. Cached so each container TGV
    //      is decoded only once per call.
    //
    // Intentionally simpler than the full AtlasCli pipeline (no channel
    // detection, no sibling-asset filtering, no ORM split). Tracking issues:
    // ORM/normal/alpha derivation lives in tgv_to_png.py — defer to a v2.
    public static class AtlasTextureExtractor
    {
        public sealed class Result
        {
            public int EntryCount   { get; set; }
            public int WrittenCount { get; set; }
            public int SkippedCount { get; set; }
            public List<string> Warnings { get; } = new();
        }

        private sealed class ContainerInfo
        {
            public string SourceTgvRel = "";
            public int    Width;
            public int    Height;
        }

        public static Result Extract(
            byte[] atlasBytes,
            string atlasVirtualPath,
            string outputDirectory,
            IReadOnlyList<UnifiedZzEntry> zzEntries,
            Action<string> progress = null)
        {
            var zzIndex = BuildZzIndex(zzEntries);
            return ExtractCore(atlasBytes, atlasVirtualPath, outputDirectory,
                rel => ResolveTgvBytes(rel, zzIndex, atlasVirtualPath), progress);
        }

        // Overload for filesystem-based testing (TgvCli --atlas mode): caller
        // provides a function that maps a normalized "PC/Atlas/Assets/.../foo.tgv"
        // ref to raw bytes (e.g. from disk).
        public static Result Extract(
            byte[] atlasBytes,
            string atlasVirtualPath,
            string outputDirectory,
            Func<string, byte[]> resolveTgv,
            Action<string> progress = null)
        {
            return ExtractCore(atlasBytes, atlasVirtualPath, outputDirectory, resolveTgv, progress);
        }

        private static Result ExtractCore(
            byte[] atlasBytes,
            string atlasVirtualPath,
            string outputDirectory,
            Func<string, byte[]> resolveTgv,
            Action<string> progress)
        {
            if (atlasBytes == null) throw new ArgumentNullException(nameof(atlasBytes));
            if (resolveTgv == null) throw new ArgumentNullException(nameof(resolveTgv));
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("Output directory is required", nameof(outputDirectory));

            Directory.CreateDirectory(outputDirectory);
            var result = new Result();

            progress?.Invoke("Parsing atlas...");
            NdfBinary ndf = new NdfbinReader().Read(atlasBytes);

            // 1. Map container id → container info
            var containers = new Dictionary<uint, ContainerInfo>();
            foreach (NdfObject obj in ndf.Instances)
            {
                string className = obj.Class?.Name ?? "";
                if (!className.Contains("texturesmallcontainer", StringComparison.OrdinalIgnoreCase))
                    continue;

                string textureFile = ReadString(obj, "TextureFileName");
                if (string.IsNullOrWhiteSpace(textureFile)) continue;

                containers[obj.Id] = new ContainerInfo
                {
                    SourceTgvRel = NormalizeSourceTgvRef(textureFile),
                    Width  = (int)ReadDouble(obj, "Width", 0),
                    Height = (int)ReadDouble(obj, "Height", 0),
                };
            }

            if (containers.Count == 0)
            {
                result.Warnings.Add("Atlas has no TextureSmallContainer objects.");
                return result;
            }

            // Cache: each container's TGV decoded only once
            var bitmapCache = new Dictionary<string, SKBitmap>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // 2. Iterate TextureParts (each = one crop)
                int partIndex = 0;
                foreach (NdfObject obj in ndf.Instances)
                {
                    string partRaw = ReadString(obj, "TexturePartFileName");
                    if (string.IsNullOrWhiteSpace(partRaw)) continue;

                    result.EntryCount++;
                    partIndex++;

                    uint? containerId = ReadObjectRefId(obj, "Container");
                    if (!containerId.HasValue || !containers.TryGetValue(containerId.Value, out var ci))
                    {
                        result.SkippedCount++;
                        result.Warnings.Add($"Part #{partIndex} has no matching container.");
                        continue;
                    }

                    int x = ReadCropCoord(obj, "AbsX", "MinU", ci.Width);
                    int y = ReadCropCoord(obj, "AbsY", "MinV", ci.Height);
                    int w = ReadCropSize(obj, "WidthPixel",  "MinU", "MaxU", ci.Width);
                    int h = ReadCropSize(obj, "HeightPixel", "MinV", "MaxV", ci.Height);
                    if (w <= 0 || h <= 0)
                    {
                        result.SkippedCount++;
                        result.Warnings.Add($"Part #{partIndex} has zero size, skipped.");
                        continue;
                    }

                    // Decode (or cache) the container TGV
                    if (!bitmapCache.TryGetValue(ci.SourceTgvRel, out SKBitmap srcBitmap))
                    {
                        byte[] tgvBytes = resolveTgv(ci.SourceTgvRel);
                        if (tgvBytes == null)
                        {
                            result.SkippedCount++;
                            result.Warnings.Add($"TGV not found: {ci.SourceTgvRel}");
                            continue;
                        }
                        progress?.Invoke($"Decoding {Path.GetFileName(ci.SourceTgvRel)}...");
                        try
                        {
                            srcBitmap = TgvDecoder.DecodeFullRes(tgvBytes);
                        }
                        catch (Exception ex)
                        {
                            result.SkippedCount++;
                            result.Warnings.Add($"Decode failed for {ci.SourceTgvRel}: {ex.Message}");
                            continue;
                        }
                        bitmapCache[ci.SourceTgvRel] = srcBitmap;
                    }

                    // Clamp crop to image bounds (last row may be 1px short)
                    if (x < 0) x = 0;
                    if (y < 0) y = 0;
                    if (x + w > srcBitmap.Width)  w = srcBitmap.Width  - x;
                    if (y + h > srcBitmap.Height) h = srcBitmap.Height - y;
                    if (w <= 0 || h <= 0)
                    {
                        result.SkippedCount++;
                        result.Warnings.Add($"Part #{partIndex} crop out of bounds, skipped.");
                        continue;
                    }

                    // Pick basename from the first chunk of TexturePartFileName
                    string basename = PickBasename(partRaw);
                    string outPath = Path.Combine(outputDirectory, basename + ".png");
                    outPath = MakeUniquePath(outPath);

                    using var cropped = CropBitmap(srcBitmap, x, y, w, h);
                    TgvDecoder.SavePng(cropped, outPath);
                    result.WrittenCount++;
                    if (result.WrittenCount % 16 == 0)
                        progress?.Invoke($"Written {result.WrittenCount} / scanned {result.EntryCount}...");
                }
            }
            finally
            {
                foreach (var b in bitmapCache.Values) b?.Dispose();
            }

            return result;
        }

        private static SKBitmap CropBitmap(SKBitmap src, int x, int y, int w, int h)
        {
            var cropped = new SKBitmap(new SKImageInfo(w, h, src.ColorType, src.AlphaType));
            using (var canvas = new SKCanvas(cropped))
            {
                canvas.DrawBitmap(src,
                    source: SKRect.Create(x, y, w, h),
                    dest:   SKRect.Create(0, 0, w, h));
            }
            return cropped;
        }

        // Index ZZ entries by every lookup-shape we might receive from the atlas
        // NDF, so the resolver tolerates "CombinedTexture:WARNO\Data\PC\..."
        // refs against entries stored as "PC/Atlas/Assets/...".
        private static Dictionary<string, UnifiedZzEntry> BuildZzIndex(IReadOnlyList<UnifiedZzEntry> entries)
        {
            var idx = new Dictionary<string, UnifiedZzEntry>(StringComparer.OrdinalIgnoreCase);
            if (entries == null) return idx;
            foreach (var e in entries)
            {
                if (e?.VirtualPath == null) continue;
                string vp = NormalizeForLookup(e.VirtualPath);
                Add(idx, vp, e);
                Add(idx, StripAtlasPrefix(vp), e);
                Add(idx, Path.GetFileName(vp), e);
            }
            return idx;
        }

        private static void Add(Dictionary<string, UnifiedZzEntry> idx, string key, UnifiedZzEntry e)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (!idx.ContainsKey(key)) idx[key] = e;
        }

        private static byte[] ResolveTgvBytes(string sourceTgvRel, Dictionary<string, UnifiedZzEntry> zzIndex, string atlasVirtualPath)
        {
            if (string.IsNullOrWhiteSpace(sourceTgvRel)) return null;
            string norm = NormalizeForLookup(sourceTgvRel);

            UnifiedZzEntry entry = null;
            if (!(zzIndex.TryGetValue(norm, out entry)
               || zzIndex.TryGetValue(StripAtlasPrefix(norm), out entry)
               || zzIndex.TryGetValue("pc/atlas/" + norm, out entry)
               || zzIndex.TryGetValue(Path.GetFileName(norm), out entry)))
            {
                return null;
            }

            var src = entry?.EffectiveOccurrence;
            if (src?.EntryRef != null && src.Manager != null)
                return src.Manager.GetRawData(src.EntryRef);
            return null;
        }

        // Lower-cases and strips every prefix we have ever seen in WARNO atlas
        // refs (CombinedTexture:/, GameData:/, ZZ:/, WARNO/Data/, leading /).
        private static string NormalizeForLookup(string raw)
        {
            string s = (raw ?? "").Trim().Trim('"').Trim('\'').Replace('\\', '/').ToLowerInvariant();
            string[] prefixes = { "combinedtexture:/", "combinedtexture:", "gamedata:/", "gamedata:", "zz:/", "zz:" };
            bool changed;
            do
            {
                changed = false;
                foreach (var p in prefixes)
                {
                    if (s.StartsWith(p, StringComparison.Ordinal))
                    {
                        s = s.Substring(p.Length);
                        changed = true;
                    }
                }
                if (s.StartsWith("/", StringComparison.Ordinal)) { s = s.Substring(1); changed = true; }
                if (s.StartsWith("warno/data/", StringComparison.Ordinal))
                {
                    s = s.Substring("warno/data/".Length);
                    changed = true;
                }
            } while (changed);
            return s;
        }

        private static string StripAtlasPrefix(string normLowered) =>
            normLowered.StartsWith("pc/atlas/", StringComparison.Ordinal)
                ? normLowered.Substring("pc/atlas/".Length)
                : normLowered;

        private static string PickBasename(string partRaw)
        {
            string raw = (partRaw ?? "").Trim();
            if (raw.StartsWith("CombinedTexture:/", StringComparison.OrdinalIgnoreCase))
                raw = raw["CombinedTexture:/".Length..];
            else if (raw.StartsWith("CombinedTexture:", StringComparison.OrdinalIgnoreCase))
                raw = raw["CombinedTexture:".Length..];

            int markerPos = raw.IndexOf("`_`", StringComparison.Ordinal);
            if (markerPos >= 0) raw = raw[..markerPos];

            string first = raw.Split("$_$", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? raw;
            string name = Path.GetFileNameWithoutExtension(first.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(name)) name = "atlas_part";
            return Sanitize(name);
        }

        private static string Sanitize(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        private static string MakeUniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir  = Path.GetDirectoryName(path) ?? "";
            string stem = Path.GetFileNameWithoutExtension(path);
            string ext  = Path.GetExtension(path);
            for (int i = 2; i < 9999; i++)
            {
                string candidate = Path.Combine(dir, $"{stem}_{i}{ext}");
                if (!File.Exists(candidate)) return candidate;
            }
            return path;
        }

        private static string NormalizePath(string p) =>
            (p ?? "").Replace('\\', '/').Trim().TrimStart('/');

        // TextureFileName in the NDF is the *target* PNG name (e.g.
        // "ZZ:/PC/Atlas/.../foo.png"). The real source TGV lives at the same
        // path but with a .tgv extension. Strip the URI-style prefix and swap.
        private static string NormalizeSourceTgvRef(string raw)
        {
            string s = (raw ?? "").Trim().Trim('"').Trim('\'').Replace('\\', '/');
            string[] prefixes = { "CombinedTexture:/", "CombinedTexture:", "GameData:/", "GameData:", "ZZ:/", "ZZ:" };
            bool changed;
            do
            {
                changed = false;
                foreach (var p in prefixes)
                {
                    if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    {
                        s = s.Substring(p.Length);
                        changed = true;
                    }
                }
            } while (changed);
            s = s.TrimStart('/');
            string ext = Path.GetExtension(s);
            if (!string.IsNullOrEmpty(ext) && !ext.Equals(".tgv", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - ext.Length) + ".tgv";
            else if (string.IsNullOrEmpty(ext))
                s += ".tgv";
            return s;
        }

        // --- NDF property accessors (mirror AtlasCli) ---

        private static string ReadString(NdfObject obj, string propName)
        {
            var p = obj.PropertyValues.FirstOrDefault(x =>
                string.Equals(x.Property?.Name, propName, StringComparison.OrdinalIgnoreCase));
            if (p?.Value == null) return "";
            return p.Value.ToString() ?? "";
        }

        private static double ReadDouble(NdfObject obj, string propName, double fallback)
        {
            var p = obj.PropertyValues.FirstOrDefault(x =>
                string.Equals(x.Property?.Name, propName, StringComparison.OrdinalIgnoreCase));
            if (p?.Value == null) return fallback;
            string s = p.Value.ToString() ?? "";
            if (double.TryParse(s, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out double v))
                return v;
            return fallback;
        }

        private static uint? ReadObjectRefId(NdfObject obj, string propName)
        {
            var p = obj.PropertyValues.FirstOrDefault(x =>
                string.Equals(x.Property?.Name, propName, StringComparison.OrdinalIgnoreCase));
            if (p?.Value is NdfObjectReference r) return r.InstanceId;
            return null;
        }

        private static int ReadCropCoord(NdfObject obj, string absName, string uvName, int fullSize)
        {
            double abs = ReadDouble(obj, absName, double.NaN);
            if (!double.IsNaN(abs)) return (int)Math.Round(abs);
            double uv = ReadDouble(obj, uvName, 0);
            return fullSize > 0 ? (int)Math.Round(uv * fullSize) : 0;
        }

        private static int ReadCropSize(NdfObject obj, string pxName, string minUv, string maxUv, int fullSize)
        {
            double px = ReadDouble(obj, pxName, double.NaN);
            if (!double.IsNaN(px) && px > 0) return (int)Math.Round(px);
            if (fullSize > 0)
            {
                double min = ReadDouble(obj, minUv, 0);
                double max = ReadDouble(obj, maxUv, 0);
                int fromUv = (int)Math.Round(Math.Max(0, max - min) * fullSize);
                if (fromUv > 0) return fromUv;
            }
            return 0;
        }
    }
}
