using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using moddingSuite.Model.Ndfbin;
using moddingSuite.Model.Ndfbin.Types;
using moddingSuite.Model.Ndfbin.Types.AllTypes;

namespace moddingSuite.BL.Ndf
{
    public static class NdfScriptNameResolver
    {
        private static readonly Regex HeaderRegex =
            new Regex(@"^\s*(?:export\s+)?(?<name>\S+)\s+is\s+(?<class>T[A-Za-z0-9_]+)\b", RegexOptions.Compiled);

        private static readonly Regex GuidRegex =
            new Regex(@"DescriptorId\s*=\s*(?:GUID:\{|GUID\(\""?)(?<guid>[0-9A-Fa-f\-]{36})", RegexOptions.Compiled);

        private static readonly Regex ShortDbRegex =
            new Regex(@"_ShortDatabaseName\s*=\s*""(?<value>[^""]+)""", RegexOptions.Compiled);

        private static readonly Regex ClassNameForDebugRegex =
            new Regex(@"ClassNameForDebug\s*=\s*'(?<value>[^']+)'", RegexOptions.Compiled);

        private static readonly Regex CfgNameRegex =
            new Regex(@"CfgName\s*=\s*'(?<value>[^']+)'", RegexOptions.Compiled);

        private static readonly Regex GeneratedOutputFileRegex =
            new Regex(@"_(decompiled|decomp)(?:_\d+)?\.ndf$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex GeneratedObjectNameRegex =
            new Regex(@"^public_\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, NameKnowledgeIndex> KnowledgeCache =
            new Dictionary<string, NameKnowledgeIndex>(StringComparer.OrdinalIgnoreCase);

        public static Dictionary<uint, string> Resolve(NdfBinary ndf, string sourceNdfbinPath)
        {
            var result = new Dictionary<uint, string>();
            if (ndf == null || ndf.Instances == null)
                return result;

            NameKnowledgeIndex knowledge = BuildKnowledgeIndex(sourceNdfbinPath);

            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (NdfObject instance in ndf.Instances.Where(x => x != null).OrderBy(x => x.Id))
            {
                string suggestedName = ResolveName(instance, knowledge);
                string uniqueName = MakeUniqueName(suggestedName, instance.Id, usedNames);
                result[instance.Id] = uniqueName;
            }

            return result;
        }

        private static string ResolveName(NdfObject instance, NameKnowledgeIndex knowledge)
        {
            string className = instance.Class != null ? instance.Class.Name : null;

            string guid;
            if (TryGetGuid(instance, "DescriptorId", out guid))
            {
                string byGuid;
                if (knowledge.TryGetByGuid(guid, out byGuid))
                    return byGuid;
            }

            string shortDbName;
            if (TryGetString(instance, "_ShortDatabaseName", out shortDbName))
            {
                string byShort;
                if (knowledge.TryGetByClassAndShort(className, shortDbName, out byShort))
                    return byShort;

                return shortDbName;
            }

            string classNameForDebug;
            if (TryGetString(instance, "ClassNameForDebug", out classNameForDebug))
            {
                string byDebug;
                if (knowledge.TryGetByClassAndDebug(className, classNameForDebug, out byDebug))
                    return byDebug;

                if (string.Equals(className, "TEntityDescriptor", StringComparison.OrdinalIgnoreCase)
                    && classNameForDebug.StartsWith("Unit_", StringComparison.OrdinalIgnoreCase))
                {
                    return "Descriptor_" + classNameForDebug;
                }

                return classNameForDebug;
            }

            string cfgName;
            if (TryGetString(instance, "CfgName", out cfgName))
            {
                string byCfg;
                if (knowledge.TryGetByClassAndCfg(className, cfgName, out byCfg))
                    return byCfg;

                return cfgName;
            }

            if (!string.IsNullOrWhiteSpace(className))
                return className;

            return null;
        }

        private static string MakeUniqueName(string suggestedName, uint instanceId, HashSet<string> usedNames)
        {
            string baseName = NormalizeIdentifier(suggestedName);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = string.Format("{0}_{1}", NdfTextWriter.InstanceNamePrefix, instanceId);

            if (usedNames.Add(baseName))
                return baseName;

            for (int i = 2; ; i++)
            {
                string candidate = string.Format("{0}_{1}", baseName, i);
                if (usedNames.Add(candidate))
                    return candidate;
            }
        }

        private static string NormalizeIdentifier(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            string trimmed = input.Trim();
            var sb = new StringBuilder(trimmed.Length);

            foreach (char c in trimmed)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '/' || c == '~' || c == '.')
                    sb.Append(c);
                else
                    sb.Append('_');
            }

            string normalized = sb.ToString().Trim('_');
            if (string.IsNullOrWhiteSpace(normalized))
                return null;

            if (char.IsDigit(normalized[0]))
                normalized = "_" + normalized;

            return normalized;
        }

        private static bool TryGetString(NdfObject instance, string propertyName, out string value)
        {
            value = null;
            NdfPropertyValue property = instance.PropertyValues
                .FirstOrDefault(x => x.Property != null && x.Property.Name == propertyName && x.Type != NdfType.Unset && x.Value != null);

            if (property == null)
                return false;

            var flat = property.Value as NdfFlatValueWrapper;
            if (flat == null || flat.Value == null)
                return false;

            var stringReference = flat.Value as NdfStringReference;
            if (stringReference != null)
            {
                value = stringReference.Value;
                return !string.IsNullOrWhiteSpace(value);
            }

            var tranReference = flat.Value as NdfTranReference;
            if (tranReference != null)
            {
                value = tranReference.Value;
                return !string.IsNullOrWhiteSpace(value);
            }

            string asString = flat.Value as string;
            if (asString != null)
            {
                value = asString;
                return !string.IsNullOrWhiteSpace(value);
            }

            return false;
        }

        private static bool TryGetGuid(NdfObject instance, string propertyName, out string guid)
        {
            guid = null;
            NdfPropertyValue property = instance.PropertyValues
                .FirstOrDefault(x => x.Property != null && x.Property.Name == propertyName && x.Type != NdfType.Unset && x.Value != null);

            if (property == null)
                return false;

            var guidWrapper = property.Value as NdfGuid;
            if (guidWrapper == null || guidWrapper.Value == null)
                return false;

            Guid parsedGuid;
            if (guidWrapper.Value is Guid)
            {
                parsedGuid = (Guid)guidWrapper.Value;
            }
            else if (!Guid.TryParse(guidWrapper.Value.ToString(), out parsedGuid))
            {
                return false;
            }

            try
            {
                guid = NdfScriptGuidNormalizer.NormalizeGuidForScript(parsedGuid).ToLowerInvariant();
            }
            catch
            {
                guid = parsedGuid.ToString("D").ToLowerInvariant();
            }
            return true;
        }

        private static NameKnowledgeIndex BuildKnowledgeIndex(string sourceNdfbinPath)
        {
            List<string> roots = ResolveKnowledgeRoots(sourceNdfbinPath);
            if (roots.Count == 0)
                return NameKnowledgeIndex.Empty;

            string cacheKey = string.Join("|", roots.Select(x => x.ToLowerInvariant()));
            lock (CacheLock)
            {
                NameKnowledgeIndex cached;
                if (KnowledgeCache.TryGetValue(cacheKey, out cached))
                    return cached;
            }

            // The in-memory cache above only helps WITHIN one process, and the GFX CLI is
            // launched as a fresh process per asset. Building this index means regex-scanning
            // every .ndf text file under the roots, which measured ~158 s on a real install --
            // paid again for every single asset imported. Persist it to disk under a signature
            // of the scanned files so it is built once per game install instead.
            List<string> scanned = NameKnowledgeIndex.EnumerateKnowledgeFiles(roots);
            string signature = ComputeKnowledgeSignature(scanned);
            string diskCachePath = ResolveKnowledgeDiskCachePath(signature);

            NameKnowledgeIndex built = LoadKnowledgeFromDisk(diskCachePath);
            if (built == null)
            {
                built = NameKnowledgeIndex.Build(roots);
                SaveKnowledgeToDisk(diskCachePath, built);
            }

            lock (CacheLock)
            {
                KnowledgeCache[cacheKey] = built;
            }

            return built;
        }

        private sealed class KnowledgePayload
        {
            public Dictionary<string, List<string>> ByGuid { get; set; } = new();
            public Dictionary<string, List<string>> ByClassAndShort { get; set; } = new();
            public Dictionary<string, List<string>> ByClassAndDebug { get; set; } = new();
            public Dictionary<string, List<string>> ByClassAndCfg { get; set; } = new();
        }

        private static string ComputeKnowledgeSignature(List<string> files)
        {
            var sb = new StringBuilder();
            foreach (string f in files)
            {
                sb.Append(f.ToLowerInvariant());
                try
                {
                    var fi = new FileInfo(f);
                    sb.Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks);
                }
                catch
                {
                    sb.Append("|?|?");
                }
                sb.AppendLine();
            }

            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(hash).Substring(0, 32).ToLowerInvariant();
        }

        private static string ResolveKnowledgeDiskCachePath(string signature)
        {
            try
            {
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(baseDir))
                    return string.Empty;
                string dir = Path.Combine(baseDir, "moddingSuite", "ndf_name_index");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "knowledge_" + signature + ".json");
            }
            catch
            {
                return string.Empty;
            }
        }

        private static NameKnowledgeIndex LoadKnowledgeFromDisk(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;
            try
            {
                KnowledgePayload payload = System.Text.Json.JsonSerializer.Deserialize<KnowledgePayload>(
                    File.ReadAllText(path, Encoding.UTF8));
                if (payload == null)
                    return null;
                return NameKnowledgeIndex.FromPayload(payload);
            }
            catch
            {
                return null;
            }
        }

        private static void SaveKnowledgeToDisk(string path, NameKnowledgeIndex index)
        {
            if (string.IsNullOrWhiteSpace(path) || index == null)
                return;
            try
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(index.ToPayload()), Encoding.UTF8);
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(tmp, path);
            }
            catch
            {
                // a cache we cannot write is not fatal; the index was still built in memory
            }
        }

        private static List<string> ResolveKnowledgeRoots(string sourceNdfbinPath)
        {
            var roots = new List<string>();
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(sourceNdfbinPath))
            {
                string sourceDirectory = Path.GetDirectoryName(sourceNdfbinPath);
                if (!string.IsNullOrWhiteSpace(sourceDirectory))
                {
                    string full = Path.GetFullPath(sourceDirectory);
                    if (Directory.Exists(full) && added.Add(full))
                        roots.Add(full);
                }
            }

            string configuredWarnoPath = null;
            try
            {
                configuredWarnoPath = SettingsManager.Load().WargamePath;
            }
            catch
            {
                configuredWarnoPath = null;
            }

            IReadOnlyList<string> warnoRoots = WarnoPathResolver.EnumerateRoots(configuredWarnoPath);
            foreach (string warnoPath in warnoRoots)
            {
                string mods = Path.Combine(warnoPath, "Mods");

                if (Directory.Exists(mods) && added.Add(mods))
                    roots.Add(mods);

                if (Directory.Exists(warnoPath) && added.Add(warnoPath))
                    roots.Add(warnoPath);
            }

            return roots;
        }

        private sealed class NameKnowledgeIndex
        {
            private readonly Dictionary<string, HashSet<string>> _byGuid;
            private readonly Dictionary<string, HashSet<string>> _byClassAndShort;
            private readonly Dictionary<string, HashSet<string>> _byClassAndDebug;
            private readonly Dictionary<string, HashSet<string>> _byClassAndCfg;

            public static readonly NameKnowledgeIndex Empty = new NameKnowledgeIndex(
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase));

            private NameKnowledgeIndex(
                Dictionary<string, HashSet<string>> byGuid,
                Dictionary<string, HashSet<string>> byClassAndShort,
                Dictionary<string, HashSet<string>> byClassAndDebug,
                Dictionary<string, HashSet<string>> byClassAndCfg)
            {
                _byGuid = byGuid;
                _byClassAndShort = byClassAndShort;
                _byClassAndDebug = byClassAndDebug;
                _byClassAndCfg = byClassAndCfg;
            }

            public static NameKnowledgeIndex Build(IEnumerable<string> roots)
            {
                var byGuid = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                var byClassAndShort = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                var byClassAndDebug = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                var byClassAndCfg = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                foreach (string root in roots)
                {
                    foreach (string file in EnumerateNdfFilesSafe(root))
                        ParseFile(file, byGuid, byClassAndShort, byClassAndDebug, byClassAndCfg);
                }

                return new NameKnowledgeIndex(byGuid, byClassAndShort, byClassAndDebug, byClassAndCfg);
            }

            /// Same traversal Build() uses, exposed so the caller can fingerprint the inputs
            /// without paying for the regex parse.
            public static List<string> EnumerateKnowledgeFiles(IEnumerable<string> roots)
            {
                var files = new List<string>();
                foreach (string root in roots)
                {
                    foreach (string file in EnumerateNdfFilesSafe(root))
                        files.Add(file);
                }
                files.Sort(StringComparer.OrdinalIgnoreCase);
                return files;
            }

            public KnowledgePayload ToPayload()
            {
                static Dictionary<string, List<string>> Flat(Dictionary<string, HashSet<string>> src)
                {
                    var outMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (KeyValuePair<string, HashSet<string>> kv in src)
                        outMap[kv.Key] = kv.Value.ToList();
                    return outMap;
                }

                return new KnowledgePayload
                {
                    ByGuid = Flat(_byGuid),
                    ByClassAndShort = Flat(_byClassAndShort),
                    ByClassAndDebug = Flat(_byClassAndDebug),
                    ByClassAndCfg = Flat(_byClassAndCfg),
                };
            }

            public static NameKnowledgeIndex FromPayload(KnowledgePayload payload)
            {
                static Dictionary<string, HashSet<string>> Rehydrate(Dictionary<string, List<string>> src)
                {
                    var outMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                    if (src == null)
                        return outMap;
                    foreach (KeyValuePair<string, List<string>> kv in src)
                        outMap[kv.Key] = new HashSet<string>(kv.Value ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                    return outMap;
                }

                return new NameKnowledgeIndex(
                    Rehydrate(payload.ByGuid),
                    Rehydrate(payload.ByClassAndShort),
                    Rehydrate(payload.ByClassAndDebug),
                    Rehydrate(payload.ByClassAndCfg));
            }

            public bool TryGetByGuid(string guid, out string name)
            {
                return TryGetSingle(_byGuid, guid, out name);
            }

            public bool TryGetByClassAndShort(string className, string shortName, out string name)
            {
                string key = BuildPairKey(className, shortName);
                return TryGetSingle(_byClassAndShort, key, out name);
            }

            public bool TryGetByClassAndDebug(string className, string classNameForDebug, out string name)
            {
                string key = BuildPairKey(className, classNameForDebug);
                return TryGetSingle(_byClassAndDebug, key, out name);
            }

            public bool TryGetByClassAndCfg(string className, string cfgName, out string name)
            {
                string key = BuildPairKey(className, cfgName);
                return TryGetSingle(_byClassAndCfg, key, out name);
            }

            private static bool TryGetSingle(Dictionary<string, HashSet<string>> source, string key, out string value)
            {
                value = null;
                if (string.IsNullOrWhiteSpace(key))
                    return false;

                HashSet<string> candidates;
                if (!source.TryGetValue(key, out candidates) || candidates.Count != 1)
                    return false;

                value = candidates.First();
                return !string.IsNullOrWhiteSpace(value);
            }

            private static string BuildPairKey(string className, string value)
            {
                if (string.IsNullOrWhiteSpace(className) || string.IsNullOrWhiteSpace(value))
                    return null;

                return string.Format("{0}|{1}", className.Trim(), value.Trim());
            }

            private static IEnumerable<string> EnumerateNdfFilesSafe(string root)
            {
                var pending = new Stack<string>();
                pending.Push(root);

                while (pending.Count > 0)
                {
                    string current = pending.Pop();

                    IEnumerable<string> files = Enumerable.Empty<string>();
                    try
                    {
                        files = Directory.EnumerateFiles(current, "*.ndf", SearchOption.TopDirectoryOnly);
                    }
                    catch
                    {
                        files = Enumerable.Empty<string>();
                    }

                    foreach (string file in files)
                    {
                        if (GeneratedOutputFileRegex.IsMatch(file))
                            continue;

                        yield return file;
                    }

                    IEnumerable<string> directories = Enumerable.Empty<string>();
                    try
                    {
                        directories = Directory.EnumerateDirectories(current);
                    }
                    catch
                    {
                        directories = Enumerable.Empty<string>();
                    }

                    foreach (string directory in directories)
                        pending.Push(directory);
                }
            }

            private static void ParseFile(
                string filePath,
                Dictionary<string, HashSet<string>> byGuid,
                Dictionary<string, HashSet<string>> byClassAndShort,
                Dictionary<string, HashSet<string>> byClassAndDebug,
                Dictionary<string, HashSet<string>> byClassAndCfg)
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(filePath);
                }
                catch
                {
                    return;
                }

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    string trimmed = line.TrimStart();
                    if (trimmed.StartsWith("template ", StringComparison.OrdinalIgnoreCase))
                        continue;

                    Match header = HeaderRegex.Match(line);
                    if (!header.Success)
                        continue;

                    string objectName = header.Groups["name"].Value.Trim();
                    string className = header.Groups["class"].Value.Trim();

                    if (GeneratedObjectNameRegex.IsMatch(objectName))
                        continue;

                    string descriptorGuid = null;
                    string shortName = null;
                    string classNameForDebug = null;
                    string cfgName = null;

                    int depth = 0;
                    bool hasBlock = false;
                    int cursor = i;

                    depth += Count(line, '(') - Count(line, ')');
                    if (depth > 0)
                        hasBlock = true;
                    else if (cursor + 1 < lines.Length && lines[cursor + 1].TrimStart().StartsWith("(", StringComparison.Ordinal))
                    {
                        cursor++;
                        hasBlock = true;
                    }

                    if (hasBlock)
                    {
                        for (; cursor < lines.Length; cursor++)
                        {
                            string blockLine = lines[cursor];

                            if (descriptorGuid == null)
                            {
                                Match guidMatch = GuidRegex.Match(blockLine);
                                if (guidMatch.Success)
                                    descriptorGuid = guidMatch.Groups["guid"].Value.ToLowerInvariant();
                            }

                            if (shortName == null)
                            {
                                Match shortMatch = ShortDbRegex.Match(blockLine);
                                if (shortMatch.Success)
                                    shortName = shortMatch.Groups["value"].Value;
                            }

                            if (classNameForDebug == null)
                            {
                                Match debugMatch = ClassNameForDebugRegex.Match(blockLine);
                                if (debugMatch.Success)
                                    classNameForDebug = debugMatch.Groups["value"].Value;
                            }

                            if (cfgName == null)
                            {
                                Match cfgMatch = CfgNameRegex.Match(blockLine);
                                if (cfgMatch.Success)
                                    cfgName = cfgMatch.Groups["value"].Value;
                            }

                            if (cursor > i)
                                depth += Count(blockLine, '(') - Count(blockLine, ')');

                            if (depth <= 0 && cursor > i)
                                break;
                        }

                        i = cursor;
                    }

                    if (!string.IsNullOrWhiteSpace(descriptorGuid))
                        Add(byGuid, descriptorGuid, objectName);

                    if (!string.IsNullOrWhiteSpace(shortName))
                        Add(byClassAndShort, BuildPairKey(className, shortName), objectName);

                    if (!string.IsNullOrWhiteSpace(classNameForDebug))
                        Add(byClassAndDebug, BuildPairKey(className, classNameForDebug), objectName);

                    if (!string.IsNullOrWhiteSpace(cfgName))
                        Add(byClassAndCfg, BuildPairKey(className, cfgName), objectName);
                }
            }

            private static void Add(Dictionary<string, HashSet<string>> map, string key, string value)
            {
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                    return;

                HashSet<string> values;
                if (!map.TryGetValue(key, out values))
                {
                    values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    map[key] = values;
                }

                values.Add(value.Trim());
            }

            private static int Count(string source, char target)
            {
                int count = 0;
                for (int i = 0; i < source.Length; i++)
                {
                    if (source[i] == target)
                        count++;
                }

                return count;
            }
        }
    }
}
