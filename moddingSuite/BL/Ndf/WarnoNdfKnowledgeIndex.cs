using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using moddingSuite.Util;

namespace moddingSuite.BL.Ndf
{
    public sealed class WarnoNdfKnowledgeIndex
    {
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, CacheEntry> CacheByKey = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        private static readonly Regex ExportDescriptorRegex =
            new Regex(@"^\s*export\s+(?<name>\S+)\s+is\s+TDeckDivisionDescriptor\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex AssignmentRegex =
            new Regex(@"^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>.+?)\s*$", RegexOptions.Compiled);

        private static readonly Regex GuidRegex =
            new Regex(@"(?<guid>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})", RegexOptions.Compiled);

        private static readonly Regex SingleQuotedTokenRegex =
            new Regex(@"'(?<token>[A-Za-z0-9_]+)'", RegexOptions.Compiled);

        private readonly Dictionary<string, HashSet<string>> _tokensByHash;
        private readonly List<DivisionKnowledgeFile> _files;

        private WarnoNdfKnowledgeIndex(List<DivisionKnowledgeFile> files, Dictionary<string, HashSet<string>> tokensByHash)
        {
            _files = files;
            _tokensByHash = tokensByHash;
        }

        public IReadOnlyList<DivisionKnowledgeFile> Files
        {
            get { return _files; }
        }

        public IReadOnlyDictionary<string, HashSet<string>> TokensByHash
        {
            get { return _tokensByHash; }
        }

        public static WarnoNdfKnowledgeIndex Build(string sourceDirectory, string warnoRootPath)
        {
            List<KnowledgeRoot> roots = ResolveRoots(sourceDirectory, warnoRootPath);
            if (roots.Count == 0)
                throw new InvalidOperationException("No valid knowledge roots found for strict Division decompile.");

            string cacheKey = string.Join("|", roots.Select(x => x.RootPath.ToLowerInvariant()));
            List<RootStamp> currentStamps = roots.Select(x => ComputeRootStamp(x.RootPath)).ToList();

            lock (CacheLock)
            {
                CacheEntry cached;
                if (CacheByKey.TryGetValue(cacheKey, out cached) && cached.IsSame(currentStamps))
                    return cached.Index;
            }

            var tokensByHash = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var files = new List<DivisionKnowledgeFile>();

            foreach (KnowledgeRoot root in roots)
            {
                foreach (string ndfFilePath in SafeEnumerateNdfFiles(root.RootPath))
                {
                    DivisionKnowledgeFile parsed = TryParseDivisionKnowledgeFile(ndfFilePath, root.Priority, tokensByHash);
                    if (parsed != null)
                        files.Add(parsed);
                }
            }

            if (files.Count == 0)
                throw new InvalidOperationException("Knowledge index did not find any TDeckDivisionDescriptor exports in WARNO/Mods sources.");

            files = files
                .OrderBy(x => x.RootPriority)
                .ThenBy(x => x.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var index = new WarnoNdfKnowledgeIndex(files, tokensByHash);

            lock (CacheLock)
            {
                CacheByKey[cacheKey] = new CacheEntry(index, currentStamps);
            }

            return index;
        }

        private static List<KnowledgeRoot> ResolveRoots(string sourceDirectory, string warnoRootPath)
        {
            var roots = new List<KnowledgeRoot>();
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(sourceDirectory))
            {
                string fullSource = Path.GetFullPath(sourceDirectory);
                if (Directory.Exists(fullSource) && added.Add(fullSource))
                    roots.Add(new KnowledgeRoot(fullSource, 0));
            }

            if (!string.IsNullOrWhiteSpace(warnoRootPath))
            {
                string fullWarno = Path.GetFullPath(warnoRootPath);
                string modsPath = Path.Combine(fullWarno, "Mods");

                if (Directory.Exists(modsPath) && added.Add(modsPath))
                    roots.Add(new KnowledgeRoot(modsPath, 1));

                if (Directory.Exists(fullWarno) && added.Add(fullWarno))
                    roots.Add(new KnowledgeRoot(fullWarno, 2));
            }

            return roots;
        }

        private static RootStamp ComputeRootStamp(string rootPath)
        {
            long fileCount = 0;
            long maxTicks = 0;
            long totalLength = 0;

            foreach (string filePath in SafeEnumerateNdfFiles(rootPath))
            {
                try
                {
                    var fileInfo = new FileInfo(filePath);
                    fileCount++;
                    maxTicks = Math.Max(maxTicks, fileInfo.LastWriteTimeUtc.Ticks);
                    totalLength += fileInfo.Length;
                }
                catch
                {
                    // Keep stamping resilient even if a file is inaccessible.
                }
            }

            return new RootStamp(rootPath, fileCount, maxTicks, totalLength);
        }

        private static IEnumerable<string> SafeEnumerateNdfFiles(string rootPath)
        {
            var pending = new Stack<string>();
            pending.Push(rootPath);

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
                    yield return file;

                IEnumerable<string> dirs = Enumerable.Empty<string>();
                try
                {
                    dirs = Directory.EnumerateDirectories(current);
                }
                catch
                {
                    dirs = Enumerable.Empty<string>();
                }

                foreach (string directory in dirs)
                    pending.Push(directory);
            }
        }

        private static DivisionKnowledgeFile TryParseDivisionKnowledgeFile(
            string sourcePath,
            int rootPriority,
            Dictionary<string, HashSet<string>> tokensByHash)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(sourcePath);
            }
            catch
            {
                return null;
            }

            if (lines.Length == 0)
                return null;

            string wholeFile = string.Join(Environment.NewLine, lines);
            if (wholeFile.IndexOf("TDeckDivisionDescriptor", StringComparison.OrdinalIgnoreCase) < 0)
                return null;

            foreach (Match tokenMatch in SingleQuotedTokenRegex.Matches(wholeFile))
                TryAddToken(tokensByHash, tokenMatch.Groups["token"].Value);

            var preludeLines = new List<string>();
            var descriptors = new List<DivisionDescriptorKnowledge>();
            bool firstDescriptorFound = false;
            int orderInFile = 0;

            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                Match exportMatch = ExportDescriptorRegex.Match(line);
                if (!exportMatch.Success)
                {
                    if (!firstDescriptorFound)
                        preludeLines.Add(line);

                    continue;
                }

                firstDescriptorFound = true;
                string exportName = exportMatch.Groups["name"].Value.Trim();

                var blockLines = new List<string> { line };
                for (index = index + 1; index < lines.Length; index++)
                {
                    blockLines.Add(lines[index]);
                    if (Regex.IsMatch(lines[index], @"^\s*\)\s*$"))
                        break;
                }

                DivisionDescriptorKnowledge descriptor = ParseDescriptorBlock(sourcePath, exportName, blockLines, orderInFile, rootPriority);
                if (descriptor != null)
                {
                    descriptors.Add(descriptor);
                    orderInFile++;
                }
            }

            if (descriptors.Count == 0)
                return null;

            return new DivisionKnowledgeFile(sourcePath, rootPriority, preludeLines, descriptors);
        }

        private static DivisionDescriptorKnowledge ParseDescriptorBlock(
            string sourcePath,
            string exportName,
            List<string> blockLines,
            int orderInFile,
            int rootPriority)
        {
            string descriptorGuid = null;
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var fieldOrder = new List<string>();

            foreach (string line in blockLines)
            {
                Match assignment = AssignmentRegex.Match(line);
                if (!assignment.Success)
                    continue;

                string fieldName = assignment.Groups["name"].Value;
                string fieldValue = assignment.Groups["value"].Value.Trim();
                fields[fieldName] = fieldValue;
                if (!fieldOrder.Contains(fieldName, StringComparer.OrdinalIgnoreCase))
                    fieldOrder.Add(fieldName);

                if (fieldName.Equals("DescriptorId", StringComparison.OrdinalIgnoreCase))
                {
                    Match guidMatch = GuidRegex.Match(fieldValue);
                    if (guidMatch.Success)
                        descriptorGuid = guidMatch.Groups["guid"].Value.ToLowerInvariant();
                }
            }

            if (string.IsNullOrWhiteSpace(descriptorGuid))
            {
                foreach (string line in blockLines)
                {
                    Match guidMatch = GuidRegex.Match(line);
                    if (!guidMatch.Success)
                        continue;

                    descriptorGuid = guidMatch.Groups["guid"].Value.ToLowerInvariant();
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(descriptorGuid))
                return null;

            return new DivisionDescriptorKnowledge(sourcePath, rootPriority, exportName, descriptorGuid, orderInFile, fields, fieldOrder);
        }

        private static void TryAddToken(Dictionary<string, HashSet<string>> tokensByHash, string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return;

            try
            {
                byte[] hash = Utils.CreateLocalisationHash(token, token.Length);
                string hashHex = Utils.ByteArrayToBigEndianHexByteString(hash).ToUpperInvariant();

                HashSet<string> tokens;
                if (!tokensByHash.TryGetValue(hashHex, out tokens))
                {
                    tokens = new HashSet<string>(StringComparer.Ordinal);
                    tokensByHash[hashHex] = tokens;
                }

                tokens.Add(token);
            }
            catch
            {
                // Skip tokens that cannot be converted by CreateLocalisationHash.
            }
        }

        private sealed class CacheEntry
        {
            public CacheEntry(WarnoNdfKnowledgeIndex index, List<RootStamp> stamps)
            {
                Index = index;
                Stamps = stamps;
            }

            public WarnoNdfKnowledgeIndex Index { get; private set; }
            public List<RootStamp> Stamps { get; private set; }

            public bool IsSame(List<RootStamp> otherStamps)
            {
                if (otherStamps == null || Stamps.Count != otherStamps.Count)
                    return false;

                for (int i = 0; i < Stamps.Count; i++)
                {
                    RootStamp left = Stamps[i];
                    RootStamp right = otherStamps[i];
                    if (!left.Equals(right))
                        return false;
                }

                return true;
            }
        }

        private struct RootStamp : IEquatable<RootStamp>
        {
            public RootStamp(string rootPath, long fileCount, long maxTicks, long totalLength)
            {
                RootPath = rootPath;
                FileCount = fileCount;
                MaxTicks = maxTicks;
                TotalLength = totalLength;
            }

            public string RootPath { get; private set; }
            public long FileCount { get; private set; }
            public long MaxTicks { get; private set; }
            public long TotalLength { get; private set; }

            public bool Equals(RootStamp other)
            {
                return string.Equals(RootPath, other.RootPath, StringComparison.OrdinalIgnoreCase)
                       && FileCount == other.FileCount
                       && MaxTicks == other.MaxTicks
                       && TotalLength == other.TotalLength;
            }
        }

        private sealed class KnowledgeRoot
        {
            public KnowledgeRoot(string rootPath, int priority)
            {
                RootPath = rootPath;
                Priority = priority;
            }

            public string RootPath { get; private set; }
            public int Priority { get; private set; }
        }
    }

    public sealed class DivisionKnowledgeFile
    {
        private readonly Dictionary<string, DivisionDescriptorKnowledge> _descriptorByGuid;

        public DivisionKnowledgeFile(
            string sourcePath,
            int rootPriority,
            List<string> preludeLines,
            List<DivisionDescriptorKnowledge> descriptors)
        {
            SourcePath = sourcePath;
            RootPriority = rootPriority;
            PreludeLines = preludeLines;
            Descriptors = descriptors;
            DescriptorGuids = new HashSet<string>(descriptors.Select(x => x.DescriptorGuid), StringComparer.OrdinalIgnoreCase);
            _descriptorByGuid = descriptors.ToDictionary(x => x.DescriptorGuid, x => x, StringComparer.OrdinalIgnoreCase);
        }

        public string SourcePath { get; private set; }
        public int RootPriority { get; private set; }
        public IReadOnlyList<string> PreludeLines { get; private set; }
        public IReadOnlyList<DivisionDescriptorKnowledge> Descriptors { get; private set; }
        public HashSet<string> DescriptorGuids { get; private set; }

        public bool TryGetDescriptor(string descriptorGuid, out DivisionDescriptorKnowledge descriptor)
        {
            return _descriptorByGuid.TryGetValue(descriptorGuid, out descriptor);
        }
    }

    public sealed class DivisionDescriptorKnowledge
    {
        private readonly Dictionary<string, string> _fields;

        public DivisionDescriptorKnowledge(
            string sourcePath,
            int rootPriority,
            string exportName,
            string descriptorGuid,
            int orderInFile,
            Dictionary<string, string> fields,
            List<string> fieldOrder)
        {
            SourcePath = sourcePath;
            RootPriority = rootPriority;
            ExportName = exportName;
            DescriptorGuid = descriptorGuid;
            OrderInFile = orderInFile;
            _fields = fields;
            FieldOrder = fieldOrder ?? new List<string>();
        }

        public string SourcePath { get; private set; }
        public int RootPriority { get; private set; }
        public string ExportName { get; private set; }
        public string DescriptorGuid { get; private set; }
        public int OrderInFile { get; private set; }
        public IReadOnlyDictionary<string, string> Fields
        {
            get { return _fields; }
        }
        public IReadOnlyList<string> FieldOrder { get; private set; }

        public bool TryGetField(string fieldName, out string fieldValue)
        {
            return _fields.TryGetValue(fieldName, out fieldValue);
        }
    }
}
