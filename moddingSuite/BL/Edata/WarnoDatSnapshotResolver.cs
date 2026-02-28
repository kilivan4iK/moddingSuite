using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using moddingSuite.BL.Edata.Model;

namespace moddingSuite.BL.Edata
{
    public class WarnoDatSnapshotResolver
    {
        public const int DefaultMinDatFileCount = 20;

        public WarnoDatSnapshotResolution ResolveLatestFullSnapshot(string wargamePath, int minDatFileCount = DefaultMinDatFileCount)
        {
            if (string.IsNullOrWhiteSpace(wargamePath) || !Directory.Exists(wargamePath))
            {
                return new WarnoDatSnapshotResolution(
                    false,
                    null,
                    null,
                    Array.Empty<ZzSourceArchiveInfo>(),
                    "WARNO path is not configured or does not exist.");
            }

            string dataRoot = Path.Combine(wargamePath, "Data", "PC");
            if (!Directory.Exists(dataRoot))
            {
                return new WarnoDatSnapshotResolution(
                    false,
                    dataRoot,
                    null,
                    Array.Empty<ZzSourceArchiveInfo>(),
                    string.Format("WARNO data root was not found: {0}", dataRoot));
            }

            WarnoDatSnapshotResolution chainResolution = ResolveLatestPatchChain(dataRoot);
            if (chainResolution != null && chainResolution.Success)
                return chainResolution;

            return ResolveSingleSnapshotFallback(dataRoot, minDatFileCount);
        }

        private static WarnoDatSnapshotResolution ResolveLatestPatchChain(string dataRoot)
        {
            Dictionary<int, string> topLevelVersions = SafeEnumerateDirectories(dataRoot)
                .Select(path => new { Path = path, Version = ParseDirectoryVersion(path) })
                .Where(x => x.Version >= 0)
                .OrderBy(x => x.Version)
                .ToDictionary(x => x.Version, x => x.Path);

            if (topLevelVersions.Count == 0)
                return null;

            var nextByVersion = new Dictionary<int, int>();
            foreach (KeyValuePair<int, string> topVersion in topLevelVersions)
            {
                int nextVersion = SafeEnumerateDirectories(topVersion.Value)
                    .Select(ParseDirectoryVersion)
                    .Where(childVersion => childVersion >= 0 && topLevelVersions.ContainsKey(childVersion))
                    .DefaultIfEmpty(-1)
                    .Max();

                if (nextVersion >= 0)
                    nextByVersion[topVersion.Key] = nextVersion;
            }

            int latestVersion = topLevelVersions.Keys.Max();
            List<int> versionChain = BuildBestChainToLatestVersion(latestVersion, nextByVersion);
            if (versionChain.Count == 0)
                return null;

            List<string> archivePaths = BuildEffectiveArchiveList(versionChain, topLevelVersions);
            if (archivePaths.Count == 0)
            {
                return new WarnoDatSnapshotResolution(
                    false,
                    dataRoot,
                    topLevelVersions[latestVersion],
                    Array.Empty<ZzSourceArchiveInfo>(),
                    "No .dat archives were discovered in the resolved WARNO patch chain.");
            }

            List<ZzSourceArchiveInfo> archives = archivePaths
                .Select((path, index) => new ZzSourceArchiveInfo(path, index, Path.GetFileName(path)))
                .ToList();

            string chainDescriptor = string.Join(" -> ", versionChain);

            return new WarnoDatSnapshotResolution(
                true,
                dataRoot,
                topLevelVersions[latestVersion],
                archives,
                string.Format("Resolved patch chain: {0}", chainDescriptor));
        }

        private static List<int> BuildBestChainToLatestVersion(int latestVersion, IReadOnlyDictionary<int, int> nextByVersion)
        {
            var reverseEdges = new Dictionary<int, List<int>>();
            foreach (KeyValuePair<int, int> edge in nextByVersion)
            {
                List<int> predecessors;
                if (!reverseEdges.TryGetValue(edge.Value, out predecessors))
                {
                    predecessors = new List<int>();
                    reverseEdges[edge.Value] = predecessors;
                }

                predecessors.Add(edge.Key);
            }

            var memo = new Dictionary<int, List<int>>();
            return BuildBestPath(latestVersion, reverseEdges, memo);
        }

        private static List<int> BuildBestPath(int version, IReadOnlyDictionary<int, List<int>> reverseEdges, IDictionary<int, List<int>> memo)
        {
            List<int> cached;
            if (memo.TryGetValue(version, out cached))
                return cached;

            List<int> predecessors;
            if (!reverseEdges.TryGetValue(version, out predecessors) || predecessors.Count == 0)
            {
                var single = new List<int> { version };
                memo[version] = single;
                return single;
            }

            List<int> best = null;
            foreach (int predecessor in predecessors.Distinct().OrderBy(x => x))
            {
                List<int> candidate = BuildBestPath(predecessor, reverseEdges, memo);
                if (best == null)
                {
                    best = candidate;
                    continue;
                }

                if (candidate.Count > best.Count)
                {
                    best = candidate;
                    continue;
                }

                if (candidate.Count == best.Count && candidate.FirstOrDefault() < best.FirstOrDefault())
                    best = candidate;
            }

            var result = new List<int>(best) { version };
            memo[version] = result;
            return result;
        }

        private static List<string> BuildEffectiveArchiveList(IReadOnlyList<int> versionChain, IReadOnlyDictionary<int, string> topLevelVersions)
        {
            var archivePaths = new List<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < versionChain.Count; i++)
            {
                int version = versionChain[i];
                string topLevelPath = topLevelVersions[version];

                AppendArchivesForDirectory(topLevelPath, archivePaths, visited);

                if (i + 1 < versionChain.Count)
                {
                    int nextVersion = versionChain[i + 1];
                    string edgePath = Path.Combine(topLevelPath, nextVersion.ToString());
                    AppendArchivesForDirectory(edgePath, archivePaths, visited);
                }
            }

            return archivePaths;
        }

        private static void AppendArchivesForDirectory(string directoryPath, ICollection<string> target, ISet<string> visited)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
                return;

            IEnumerable<string> rootArchives = SafeEnumerateDatFiles(directoryPath);
            IEnumerable<string> branchArchives = SafeEnumerateDirectories(directoryPath)
                .Where(path => !IsNumericDirectoryName(Path.GetFileName(path)))
                .SelectMany(path => SafeEnumerateDatFiles(path, SearchOption.AllDirectories));

            List<string> ordered = rootArchives
                .Concat(branchArchives)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => GetArchiveBranchSortKey(directoryPath, path), StringComparer.OrdinalIgnoreCase)
                .ThenBy(GetDatSortBucket)
                .ThenBy(GetDatSortName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string archivePath in ordered)
            {
                if (visited.Add(archivePath))
                    target.Add(archivePath);
            }
        }

        private static string GetArchiveBranchSortKey(string chainRoot, string archivePath)
        {
            if (string.IsNullOrWhiteSpace(chainRoot) || string.IsNullOrWhiteSpace(archivePath))
                return string.Empty;

            string parentDir = Path.GetDirectoryName(archivePath) ?? string.Empty;
            if (string.Equals(parentDir, chainRoot, StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            try
            {
                return Path.GetRelativePath(chainRoot, parentDir) ?? string.Empty;
            }
            catch
            {
                return parentDir;
            }
        }

        private static WarnoDatSnapshotResolution ResolveSingleSnapshotFallback(string dataRoot, int minDatFileCount)
        {
            IReadOnlyList<string> zzDirectories = FindDirectoriesContainingZzArchives(dataRoot);
            if (zzDirectories.Count == 0)
            {
                return new WarnoDatSnapshotResolution(
                    false,
                    dataRoot,
                    null,
                    Array.Empty<ZzSourceArchiveInfo>(),
                    string.Format("No directory with ZZ*.dat archives found under {0}.", dataRoot));
            }

            var fullSnapshotCandidates = zzDirectories
                .Select(path => new
                {
                    Path = path,
                    DatFiles = SafeEnumerateDatFiles(path).ToList(),
                    VersionVector = GetVersionVector(dataRoot, path)
                })
                .Where(x => x.DatFiles.Count >= minDatFileCount)
                .ToList();

            if (fullSnapshotCandidates.Count == 0)
            {
                return new WarnoDatSnapshotResolution(
                    false,
                    dataRoot,
                    null,
                    Array.Empty<ZzSourceArchiveInfo>(),
                    string.Format(
                        "No full snapshot found under {0}. Found ZZ directories but each has less than {1} .dat files.",
                        dataRoot,
                        minDatFileCount));
            }

            var selected = fullSnapshotCandidates
                .OrderByDescending(x => x.VersionVector, new VersionVectorComparer())
                .ThenByDescending(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .First();

            List<ZzSourceArchiveInfo> archives = selected.DatFiles
                .OrderBy(GetDatSortBucket)
                .ThenBy(GetDatSortName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select((path, index) => new ZzSourceArchiveInfo(path, index, Path.GetFileName(path)))
                .ToList();

            return new WarnoDatSnapshotResolution(
                true,
                dataRoot,
                selected.Path,
                archives,
                "Fallback to single snapshot mode.");
        }

        private static IReadOnlyList<string> FindDirectoriesContainingZzArchives(string rootPath)
        {
            var matchingDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>();
            pending.Push(rootPath);

            while (pending.Count > 0)
            {
                string current = pending.Pop();

                IEnumerable<string> subDirectories = SafeEnumerateDirectories(current);
                foreach (string subDirectory in subDirectories)
                    pending.Push(subDirectory);

                bool hasZz = SafeEnumerateDatFiles(current)
                    .Any(path => IsZzDatFileName(Path.GetFileName(path)));

                if (hasZz)
                    matchingDirectories.Add(current);
            }

            return matchingDirectories.ToList().AsReadOnly();
        }

        private static IEnumerable<string> SafeEnumerateDirectories(string path)
        {
            try
            {
                return Directory.EnumerateDirectories(path);
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }

        private static IEnumerable<string> SafeEnumerateDatFiles(string path, SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            try
            {
                return Directory.EnumerateFiles(path, "*.dat", searchOption);
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }

        private static bool IsNumericDirectoryName(string directoryName)
        {
            if (string.IsNullOrWhiteSpace(directoryName))
                return false;

            return directoryName.All(char.IsDigit);
        }

        private static int ParseDirectoryVersion(string path)
        {
            string name = Path.GetFileName(path) ?? string.Empty;
            int version;
            return int.TryParse(name, out version) ? version : -1;
        }

        private static List<int> GetVersionVector(string rootPath, string directoryPath)
        {
            string relativePath;
            try
            {
                relativePath = Path.GetRelativePath(rootPath, directoryPath);
            }
            catch
            {
                relativePath = directoryPath;
            }

            string[] parts = (relativePath ?? string.Empty)
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            var numbers = new List<int>();
            foreach (string part in parts)
            {
                int parsed;
                if (int.TryParse(part, out parsed))
                    numbers.Add(parsed);
            }

            return numbers;
        }

        private static bool IsZzDatFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            if (!fileName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                return false;

            string withoutExtension = Path.GetFileNameWithoutExtension(fileName);
            if (string.Equals(withoutExtension, "ZZ", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!withoutExtension.StartsWith("ZZ_", StringComparison.OrdinalIgnoreCase))
                return false;

            string suffix = withoutExtension.Substring(3);
            return suffix.Length > 0 && suffix.All(char.IsDigit);
        }

        private static int GetDatSortBucket(string path)
        {
            string fileName = Path.GetFileName(path);
            return IsZzDatFileName(fileName) ? 1 : 0;
        }

        private static string GetDatSortName(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);

            if (string.Equals(name, "ZZ", StringComparison.OrdinalIgnoreCase))
                return "ZZ_000000";

            if (name.StartsWith("ZZ_", StringComparison.OrdinalIgnoreCase))
            {
                string suffix = name.Substring(3);
                int parsed;
                if (int.TryParse(suffix, out parsed))
                    return string.Format("ZZ_{0:D6}", parsed);
            }

            return name ?? string.Empty;
        }

        private sealed class VersionVectorComparer : IComparer<IReadOnlyList<int>>
        {
            public int Compare(IReadOnlyList<int> x, IReadOnlyList<int> y)
            {
                if (ReferenceEquals(x, y))
                    return 0;

                if (x == null)
                    return -1;
                if (y == null)
                    return 1;

                int max = Math.Max(x.Count, y.Count);
                for (int i = 0; i < max; i++)
                {
                    int xv = i < x.Count ? x[i] : -1;
                    int yv = i < y.Count ? y[i] : -1;

                    int cmp = xv.CompareTo(yv);
                    if (cmp != 0)
                        return cmp;
                }

                return 0;
            }
        }
    }
}
