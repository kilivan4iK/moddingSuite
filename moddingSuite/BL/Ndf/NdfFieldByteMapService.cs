using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using moddingSuite.Model.Ndfbin;
using moddingSuite.Model.Ndfbin.Types;
using moddingSuite.Model.Ndfbin.Types.AllTypes;

namespace moddingSuite.BL.Ndf
{
    public sealed class NdfFieldByteMapService
    {
        private readonly NdfbinReader _reader = new NdfbinReader();
        private readonly NdfbinWriter _writer = new NdfbinWriter();

        public NdfFieldByteMapResult MapField(string sourceNdfbinPath, string selector, string outputDirectory, string preferredGuidHint = null)
        {
            EnsureSourceExists(sourceNdfbinPath);
            if (string.IsNullOrWhiteSpace(selector))
                throw new ArgumentException("Selector must not be empty.", nameof(selector));

            string outputDir = ResolveOutputDirectory(sourceNdfbinPath, outputDirectory);
            Directory.CreateDirectory(outputDir);

            byte[] baselineRaw = File.ReadAllBytes(sourceNdfbinPath);
            NdfBinary baselineBinary = _reader.Read((byte[])baselineRaw.Clone());
            bool compressed = baselineBinary.Header != null && baselineBinary.Header.IsCompressedBody;
            byte[] baselineNormalized = _writer.Write(baselineBinary, compressed);

            string baseName = Path.GetFileNameWithoutExtension(sourceNdfbinPath);
            string baselinePath = BuildUniquePath(outputDir, baseName + "_baseline", ".ndfbin");
            File.WriteAllBytes(baselinePath, baselineNormalized);

            MutationResult mutation = MutateBySelector(baselineRaw, baselineNormalized, selector, preferredGuidHint);
            if (!mutation.Success)
            {
                return new NdfFieldByteMapResult
                {
                    Success = false,
                    ErrorMessage = mutation.ErrorMessage,
                    BaselinePath = baselinePath
                };
            }

            string safeSelector = SanitizeFileToken(mutation.EffectiveSelector);
            string variantPath = BuildUniquePath(outputDir, baseName + "_field_" + safeSelector, ".ndfbin");
            File.WriteAllBytes(variantPath, mutation.Bytes);

            string reportPath = BuildUniquePath(outputDir, baseName + "_field_" + safeSelector + "_map", ".txt");
            File.WriteAllText(reportPath, BuildTextReport(mutation.EffectiveSelector, mutation, variantPath), new UTF8Encoding(false));

            string csvPath = Path.Combine(outputDir, baseName + "_field_map.csv");
            EnsureCsvHeader(csvPath);
            File.AppendAllText(csvPath, BuildCsvRow(mutation.EffectiveSelector, mutation, variantPath), new UTF8Encoding(false));

            return new NdfFieldByteMapResult
            {
                Success = true,
                Selector = mutation.EffectiveSelector,
                ResolvedTarget = mutation.TargetPath,
                PropertyType = mutation.PropertyType,
                BaselinePath = baselinePath,
                VariantPath = variantPath,
                ReportPath = reportPath,
                CsvPath = csvPath,
                ChangedByteCount = mutation.Offsets.Count,
                FirstOffset = mutation.Offsets.Count > 0 ? (int?)mutation.Offsets[0] : null
            };
        }

        public NdfFieldByteMapBulkResult MapAll(string sourceNdfbinPath, string outputDirectory, int? maxCount)
        {
            EnsureSourceExists(sourceNdfbinPath);
            if (maxCount.HasValue && maxCount.Value <= 0)
                throw new ArgumentException("maxCount must be positive.", nameof(maxCount));

            string outputDir = ResolveOutputDirectory(sourceNdfbinPath, outputDirectory);
            Directory.CreateDirectory(outputDir);

            byte[] baselineRaw = File.ReadAllBytes(sourceNdfbinPath);
            NdfBinary binary = _reader.Read((byte[])baselineRaw.Clone());
            bool compressed = binary.Header != null && binary.Header.IsCompressedBody;
            byte[] baselineNormalized = _writer.Write(binary, compressed);

            string baseName = Path.GetFileNameWithoutExtension(sourceNdfbinPath);
            string baselinePath = BuildUniquePath(outputDir, baseName + "_baseline", ".ndfbin");
            File.WriteAllBytes(baselinePath, baselineNormalized);

            List<string> selectors = BuildSelectors(binary);
            if (maxCount.HasValue)
                selectors = selectors.Take(maxCount.Value).ToList();

            string csvPath = Path.Combine(outputDir, baseName + "_full_byte_map.csv");
            EnsureCsvHeader(csvPath);

            int ok = 0;
            int fail = 0;
            string lastError = null;

            foreach (string selector in selectors)
            {
                MutationResult mutation = MutateBySelector(baselineRaw, baselineNormalized, selector, null);
                if (!mutation.Success)
                {
                    fail++;
                    lastError = mutation.ErrorMessage;
                    continue;
                }

                ok++;
                File.AppendAllText(csvPath, BuildCsvRow(selector, mutation, string.Empty), new UTF8Encoding(false));
            }

            return new NdfFieldByteMapBulkResult
            {
                Success = true,
                BaselinePath = baselinePath,
                CsvPath = csvPath,
                ProcessedCount = selectors.Count,
                SucceededCount = ok,
                FailedCount = fail,
                LastError = lastError
            };
        }

        private MutationResult MutateBySelector(byte[] baselineRaw, byte[] baselineNormalized, string selector, string preferredGuidHint)
        {
            string guid;
            string propertyName;
            string parseError;
            if (!TryParseSelector(selector, out guid, out propertyName, out parseError))
                return MutationResult.Fail(parseError);

            NdfBinary binary = _reader.Read((byte[])baselineRaw.Clone());
            NdfObject instance;
            string effectiveSelector;
            if (!TryResolveTargetInstance(binary, guid, propertyName, preferredGuidHint, out instance, out effectiveSelector, out parseError))
                return MutationResult.Fail(parseError);

            NdfPropertyValue property = instance.PropertyValues.FirstOrDefault(x =>
                x.Property != null &&
                x.Value != null &&
                x.Type != NdfType.Unset &&
                string.Equals(x.Property.Name, propertyName, StringComparison.OrdinalIgnoreCase));

            if (property == null)
                return MutationResult.Fail("Property not found: " + propertyName);

            string mutateError;
            if (!TryMutate(property, out mutateError))
                return MutationResult.Fail(mutateError);

            bool compressed = binary.Header != null && binary.Header.IsCompressedBody;
            byte[] variant = _writer.Write(binary, compressed);

            byte[] baselineUncompressed = _reader.GetUncompressedNdfbinary((byte[])baselineNormalized.Clone());
            byte[] variantUncompressed = _reader.GetUncompressedNdfbinary((byte[])variant.Clone());
            List<int> offsets = DiffOffsets(baselineUncompressed, variantUncompressed);

            return MutationResult.Ok(
                BuildObjectKey(instance) + "." + property.Property.Name,
                property.Type.ToString(),
                offsets,
                variant,
                effectiveSelector);
        }

        private static bool TryMutate(NdfPropertyValue property, out string error)
        {
            error = null;
            if (property.Value is NdfBoolean)
            {
                var b = (NdfBoolean)property.Value;
                b.Value = !Convert.ToBoolean(b.Value, CultureInfo.InvariantCulture);
                return true;
            }

            if (property.Value is NdfSingle)
            {
                var f = (NdfSingle)property.Value;
                f.Value = f.Value + 0.125f;
                return true;
            }

            if (property.Value is NdfInt32)
            {
                var i = (NdfInt32)property.Value;
                int v = Convert.ToInt32(i.Value, CultureInfo.InvariantCulture);
                i.Value = v == int.MaxValue ? v - 1 : v + 1;
                return true;
            }

            if (property.Value is NdfUInt32)
            {
                var u = (NdfUInt32)property.Value;
                uint v = Convert.ToUInt32(u.Value, CultureInfo.InvariantCulture);
                u.Value = v == uint.MaxValue ? v - 1 : v + 1;
                return true;
            }

            error = "Supported only: Boolean, Float32, Int32, UInt32.";
            return false;
        }

        private static bool TryParseSelector(string selector, out string guid, out string property, out string error)
        {
            guid = null;
            property = null;
            error = null;
            string cleaned = (selector ?? string.Empty).Trim().TrimEnd('>');
            int dot = cleaned.LastIndexOf('.');
            if (dot > 0 && dot < cleaned.Length - 1)
            {
                string left = cleaned.Substring(0, dot).Replace("GUID:{", "").Replace("GUID:", "").Replace("{", "").Replace("}", "").Trim();
                string right = cleaned.Substring(dot + 1).Trim();
                Guid parsedGuid;
                if (Guid.TryParse(left, out parsedGuid))
                {
                    guid = parsedGuid.ToString("D").ToUpperInvariant();
                    property = right;
                    return true;
                }
            }

            if (string.IsNullOrWhiteSpace(cleaned))
            {
                error = "Selector is empty.";
                return false;
            }

            property = cleaned;
            return true;
        }

        private static bool TryResolveTargetInstance(
            NdfBinary binary,
            string guid,
            string propertyName,
            string preferredGuidHint,
            out NdfObject instance,
            out string effectiveSelector,
            out string error)
        {
            instance = null;
            effectiveSelector = null;
            error = null;

            if (!string.IsNullOrWhiteSpace(guid))
            {
                HashSet<string> guidCandidates = BuildGuidCandidates(guid);
                instance = binary.Instances
                    .Where(x => x != null)
                    .FirstOrDefault(x => guidCandidates.Contains((TryGetGuid(x, "DescriptorId") ?? string.Empty).ToUpperInvariant()));

                if (instance == null)
                {
                    error = "Descriptor GUID not found: " + guid;
                    return false;
                }

                effectiveSelector = (TryGetGuid(instance, "DescriptorId") ?? guid) + "." + propertyName;
                return true;
            }

            List<NdfObject> candidates = binary.Instances
                .Where(x => x != null)
                .Where(x => !string.IsNullOrWhiteSpace(TryGetGuid(x, "DescriptorId")))
                .Where(x => x.PropertyValues.Any(p =>
                    p.Property != null &&
                    p.Value != null &&
                    p.Type != NdfType.Unset &&
                    string.Equals(p.Property.Name, propertyName, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (candidates.Count == 0)
            {
                error = "Property not found on any GUID-bearing descriptor: " + propertyName;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(preferredGuidHint))
            {
                HashSet<string> preferred = BuildGuidCandidates(preferredGuidHint);
                NdfObject preferredMatch = candidates.FirstOrDefault(x => preferred.Contains((TryGetGuid(x, "DescriptorId") ?? string.Empty).ToUpperInvariant()));
                if (preferredMatch != null)
                {
                    instance = preferredMatch;
                    effectiveSelector = (TryGetGuid(instance, "DescriptorId") ?? preferredGuidHint) + "." + propertyName;
                    return true;
                }
            }

            if (candidates.Count == 1)
            {
                instance = candidates[0];
                effectiveSelector = (TryGetGuid(instance, "DescriptorId") ?? "UNKNOWN") + "." + propertyName;
                return true;
            }

            string sample = string.Join(", ", candidates
                .Take(5)
                .Select(x => string.Format("{0}.{1}", TryGetGuid(x, "DescriptorId"), propertyName)));
            error = string.Format(
                "Property '{0}' is ambiguous ({1} matches). Use full GUID.Property. Examples: {2}",
                propertyName,
                candidates.Count,
                sample);
            return false;
        }

        private static List<string> BuildSelectors(NdfBinary binary)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (NdfObject instance in binary.Instances.Where(x => x != null))
            {
                string guid = TryGetGuid(instance, "DescriptorId");
                if (string.IsNullOrWhiteSpace(guid))
                    continue;

                foreach (NdfPropertyValue prop in instance.PropertyValues.Where(x => x != null && x.Property != null && x.Value != null && x.Type != NdfType.Unset))
                {
                    if (!(prop.Value is NdfBoolean || prop.Value is NdfSingle || prop.Value is NdfInt32 || prop.Value is NdfUInt32))
                        continue;

                    string selector = guid + "." + prop.Property.Name;
                    if (seen.Add(selector))
                        result.Add(selector);
                }
            }

            return result;
        }

        private static HashSet<string> BuildGuidCandidates(string guidText)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Guid parsed;
            if (!Guid.TryParse(guidText, out parsed))
                return set;

            set.Add(parsed.ToString("D").ToUpperInvariant());
            set.Add(NdfScriptGuidNormalizer.NormalizeGuidForScript(parsed).ToUpperInvariant());
            return set;
        }

        private static string TryGetGuid(NdfObject instance, string propertyName)
        {
            NdfPropertyValue prop = instance.PropertyValues.FirstOrDefault(x =>
                x.Property != null && x.Value is NdfGuid && x.Type != NdfType.Unset && x.Property.Name == propertyName);
            if (prop == null)
                return null;

            var guidValue = (NdfGuid)prop.Value;
            Guid parsed;
            if (guidValue.Value is Guid)
                parsed = (Guid)guidValue.Value;
            else if (!Guid.TryParse(guidValue.Value == null ? null : guidValue.Value.ToString(), out parsed))
                return null;

            return NdfScriptGuidNormalizer.NormalizeGuidForScript(parsed).ToUpperInvariant();
        }

        private static List<int> DiffOffsets(byte[] left, byte[] right)
        {
            int max = Math.Max(left.Length, right.Length);
            var offsets = new List<int>();
            for (int i = 0; i < max; i++)
            {
                byte a = i < left.Length ? left[i] : (byte)0;
                byte b = i < right.Length ? right[i] : (byte)0;
                if (i < left.Length && i < right.Length && a == b)
                    continue;
                offsets.Add(i);
            }

            return offsets;
        }

        private static string BuildObjectKey(NdfObject instance)
        {
            return string.Format(
                "{0}[ID:{1}|GUID:{2}]",
                instance.Class != null ? instance.Class.Name : "UnknownClass",
                instance.Id.ToString(CultureInfo.InvariantCulture),
                TryGetGuid(instance, "DescriptorId") ?? "N/A");
        }

        private static string BuildTextReport(string selector, MutationResult mutation, string variantPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("NDF Targeted Byte Map");
            sb.AppendLine("Selector: " + selector);
            sb.AppendLine("Target  : " + mutation.TargetPath);
            sb.AppendLine("Type    : " + mutation.PropertyType);
            sb.AppendLine("Variant : " + variantPath);
            sb.AppendLine("Changed bytes: " + mutation.Offsets.Count.ToString(CultureInfo.InvariantCulture));
            foreach (int offset in mutation.Offsets.Take(4096))
                sb.AppendLine("  +0x" + offset.ToString("X8", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static void EnsureCsvHeader(string csvPath)
        {
            if (!File.Exists(csvPath))
                File.WriteAllText(csvPath, "Selector,ResolvedTarget,PropertyType,ChangedByteCount,FirstOffsetHex,OffsetsHex,VariantPath\r\n", new UTF8Encoding(false));
        }

        private static string BuildCsvRow(string selector, MutationResult mutation, string variantPath)
        {
            string first = mutation.Offsets.Count > 0 ? "0x" + mutation.Offsets[0].ToString("X8", CultureInfo.InvariantCulture) : string.Empty;
            string offsets = string.Join(";", mutation.Offsets.Take(256).Select(x => "0x" + x.ToString("X8", CultureInfo.InvariantCulture)));
            string[] fields = { selector, mutation.TargetPath, mutation.PropertyType, mutation.Offsets.Count.ToString(CultureInfo.InvariantCulture), first, offsets, variantPath ?? string.Empty };
            return string.Join(",", fields.Select(EscapeCsv)) + "\r\n";
        }

        private static string EscapeCsv(string value)
        {
            string safe = value ?? string.Empty;
            if (safe.Contains(",") || safe.Contains("\"") || safe.Contains("\r") || safe.Contains("\n"))
                return "\"" + safe.Replace("\"", "\"\"") + "\"";
            return safe;
        }

        private static string ResolveOutputDirectory(string sourcePath, string outputDirectory)
        {
            if (!string.IsNullOrWhiteSpace(outputDirectory))
                return Path.GetFullPath(outputDirectory);
            return Path.GetDirectoryName(sourcePath);
        }

        private static void EnsureSourceExists(string sourceNdfbinPath)
        {
            if (string.IsNullOrWhiteSpace(sourceNdfbinPath))
                throw new ArgumentException("Source path must not be empty.", nameof(sourceNdfbinPath));
            if (!File.Exists(sourceNdfbinPath))
                throw new FileNotFoundException("Source .ndfbin file not found.", sourceNdfbinPath);
        }

        private static string BuildUniquePath(string directory, string stem, string extension)
        {
            string first = Path.Combine(directory, stem + extension);
            if (!File.Exists(first))
                return first;

            for (int i = 2; ; i++)
            {
                string candidate = Path.Combine(directory, string.Format("{0}_{1}{2}", stem, i, extension));
                if (!File.Exists(candidate))
                    return candidate;
            }
        }

        private static string SanitizeFileToken(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "field";
            var sb = new StringBuilder(input.Length);
            foreach (char c in input)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return sb.ToString().Trim('_');
        }

        private sealed class MutationResult
        {
            public bool Success { get; private set; }
            public string ErrorMessage { get; private set; }
            public string TargetPath { get; private set; }
            public string PropertyType { get; private set; }
            public List<int> Offsets { get; private set; }
            public byte[] Bytes { get; private set; }
            public string EffectiveSelector { get; private set; }

            public static MutationResult Ok(string targetPath, string propertyType, List<int> offsets, byte[] bytes, string effectiveSelector)
            {
                return new MutationResult
                {
                    Success = true,
                    TargetPath = targetPath,
                    PropertyType = propertyType,
                    Offsets = offsets,
                    Bytes = bytes,
                    EffectiveSelector = effectiveSelector
                };
            }

            public static MutationResult Fail(string error)
            {
                return new MutationResult { Success = false, ErrorMessage = error, Offsets = new List<int>() };
            }
        }
    }

    public sealed class NdfFieldByteMapResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public string Selector { get; set; }
        public string ResolvedTarget { get; set; }
        public string PropertyType { get; set; }
        public string BaselinePath { get; set; }
        public string VariantPath { get; set; }
        public string ReportPath { get; set; }
        public string CsvPath { get; set; }
        public int ChangedByteCount { get; set; }
        public int? FirstOffset { get; set; }
    }

    public sealed class NdfFieldByteMapBulkResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public string BaselinePath { get; set; }
        public string CsvPath { get; set; }
        public int ProcessedCount { get; set; }
        public int SucceededCount { get; set; }
        public int FailedCount { get; set; }
        public string LastError { get; set; }
    }
}
