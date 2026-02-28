using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using moddingSuite.BL.Edata.Model;

namespace moddingSuite.BL.Edata
{
    public class ZzDatDiscoveryService
    {
        public IReadOnlyList<ZzSourceArchiveInfo> DiscoverRecursively(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                return Array.Empty<ZzSourceArchiveInfo>();

            IReadOnlyList<string> archiveDirectories = FindArchiveDirectories(rootPath);
            if (archiveDirectories.Count > 0)
                return GetDatFilesInDirectories(archiveDirectories);

            // Fallback for non-standard layouts: keep old recursive ZZ behavior.
            var files = new List<string>();
            var pending = new Stack<string>();
            pending.Push(rootPath);

            while (pending.Count > 0)
            {
                string current = pending.Pop();

                foreach (string file in SafeEnumerateFiles(current))
                {
                    if (IsZzDatFileName(Path.GetFileName(file)))
                        files.Add(file);
                }

                foreach (string subDirectory in SafeEnumerateDirectories(current))
                    pending.Push(subDirectory);
            }

            return files
                .OrderBy(path => GetArchiveOrder(path))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select((path, index) => new ZzSourceArchiveInfo(path, index, Path.GetFileName(path)))
                .OrderBy(info => info.ArchiveOrder)
                .ThenBy(info => info.ArchivePath, StringComparer.OrdinalIgnoreCase)
                .ToList()
                .AsReadOnly();
        }

        public static int GetArchiveOrder(string archivePath)
        {
            string withoutExtension = Path.GetFileNameWithoutExtension(archivePath ?? string.Empty);

            if (string.Equals(withoutExtension, "ZZ", StringComparison.OrdinalIgnoreCase))
                return 0;

            if (!withoutExtension.StartsWith("ZZ_", StringComparison.OrdinalIgnoreCase))
                return int.MaxValue;

            string suffix = withoutExtension.Substring(3);
            int parsedIndex;
            if (int.TryParse(suffix, out parsedIndex))
                return parsedIndex;

            return int.MaxValue;
        }

        public static bool IsZzDatFileName(string fileName)
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

        private static IEnumerable<string> SafeEnumerateDirectories(string directory)
        {
            try
            {
                return Directory.EnumerateDirectories(directory);
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }

        private static IEnumerable<string> SafeEnumerateFiles(string directory)
        {
            try
            {
                return Directory.EnumerateFiles(directory, "ZZ*.dat", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }

        private static IEnumerable<string> SafeEnumerateFiles(string directory, string pattern)
        {
            try
            {
                return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }

        private static IReadOnlyList<string> FindArchiveDirectories(string rootPath)
        {
            var candidates = new List<(string Path, string VersionVectorKey, int DatCount, DateTime LastWriteUtc)>();
            var pending = new Stack<string>();
            pending.Push(rootPath);

            while (pending.Count > 0)
            {
                string current = pending.Pop();

                foreach (string subDirectory in SafeEnumerateDirectories(current))
                    pending.Push(subDirectory);

                List<string> datFiles = SafeEnumerateFiles(current, "*.dat")
                    .Where(file => Path.GetExtension(file).Equals(".dat", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (datFiles.Count == 0)
                    continue;

                bool hasZzLayer = datFiles.Any(file => IsZzDatFileName(Path.GetFileName(file)));
                if (!hasZzLayer)
                    continue;

                candidates.Add((current, BuildVersionVectorKey(current), datFiles.Count, GetDirectoryLastWriteUtcSafe(current)));
            }

            return candidates
                .OrderBy(c => c.VersionVectorKey, StringComparer.Ordinal)
                .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                .Select(c => c.Path)
                .ToList()
                .AsReadOnly();
        }

        private static IReadOnlyList<ZzSourceArchiveInfo> GetDatFilesInDirectories(IEnumerable<string> directories)
        {
            var files = new List<string>();
            foreach (string directory in directories ?? Enumerable.Empty<string>())
            {
                files.AddRange(
                    SafeEnumerateFiles(directory, "*.dat")
                        .Where(file => Path.GetExtension(file).Equals(".dat", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(GetDatSortBucket)
                        .ThenBy(GetDatSortName, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(path => path, StringComparer.OrdinalIgnoreCase));
            }

            return files
                .Select((path, index) => new ZzSourceArchiveInfo(path, index, Path.GetFileName(path)))
                .ToList()
                .AsReadOnly();
        }

        private static int GetDatSortBucket(string path)
        {
            // Keep non-ZZ packages first, then let ZZ layers override by being processed later.
            string name = Path.GetFileName(path);
            return IsZzDatFileName(name) ? 1 : 0;
        }

        private static string GetDatSortName(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);

            if (string.Equals(name, "ZZ", StringComparison.OrdinalIgnoreCase))
                return "ZZ_000000";

            if (name.StartsWith("ZZ_", StringComparison.OrdinalIgnoreCase))
            {
                string suffix = name.Substring(3);
                if (int.TryParse(suffix, out int index))
                    return string.Format("ZZ_{0:D6}", index);
            }

            return name ?? string.Empty;
        }

        private static string BuildVersionVectorKey(string directory)
        {
            string[] parts = (directory ?? string.Empty)
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            var numericParts = new List<int>();
            foreach (string part in parts)
            {
                if (int.TryParse(part, out int value))
                    numericParts.Add(value);
            }

            if (numericParts.Count == 0)
                return "0000000000";

            return string.Join("/", numericParts.Select(v => v.ToString("D10")));
        }

        private static DateTime GetDirectoryLastWriteUtcSafe(string directory)
        {
            try
            {
                return Directory.GetLastWriteTimeUtc(directory);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }
    }
}
