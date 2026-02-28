using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using moddingSuite.BL.Edata.Model;
using moddingSuite.BL;

namespace moddingSuite.BL.Edata
{
    public class UnifiedZzIndexService
    {
        private readonly UnifiedZzMergeService _mergeService;

        private string _cachedRootPath;
        private string _cachedArchiveSignature;
        private UnifiedZzIndexResult _cachedResult;

        public UnifiedZzIndexService(UnifiedZzMergeService mergeService)
        {
            _mergeService = mergeService ?? throw new ArgumentNullException(nameof(mergeService));
        }

        public UnifiedZzIndexResult BuildOrGetCached(
            string rootPath,
            IReadOnlyList<ZzSourceArchiveInfo> archives,
            bool forceRebuild = false)
        {
            archives = archives ?? Array.Empty<ZzSourceArchiveInfo>();
            string signature = BuildArchiveSignature(archives);

            bool cacheHit =
                !forceRebuild &&
                _cachedResult != null &&
                string.Equals(_cachedRootPath, rootPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_cachedArchiveSignature, signature, StringComparison.Ordinal);

            if (cacheHit)
                return _cachedResult.AsCacheHit();

            var failedArchives = new List<string>();
            var byPath = new Dictionary<string, List<ZzFileOccurrence>>(StringComparer.OrdinalIgnoreCase);

            int indexedArchiveCount = 0;
            foreach (ZzSourceArchiveInfo archive in archives)
            {
                try
                {
                    var manager = new EdataManager(archive.ArchivePath);
                    manager.ParseEdataFile();

                    indexedArchiveCount++;
                    foreach (var file in manager.Files)
                    {
                        string normalizedPath = NormalizeVirtualPath(file == null ? null : file.Path);
                        if (string.IsNullOrWhiteSpace(normalizedPath))
                            continue;

                        if (!byPath.TryGetValue(normalizedPath, out List<ZzFileOccurrence> list))
                        {
                            list = new List<ZzFileOccurrence>();
                            byPath[normalizedPath] = list;
                        }

                        list.Add(new ZzFileOccurrence(
                            normalizedPath,
                            archive.ArchiveOrder,
                            archive.ArchivePath,
                            archive.DisplayName,
                            file,
                            manager));
                    }
                }
                catch (Exception ex)
                {
                    failedArchives.Add(string.Format("{0}: {1}", archive.ArchivePath, ex.Message));
                }
            }

            IReadOnlyList<UnifiedZzEntry> entries = _mergeService.Merge(byPath);

            var freshResult = new UnifiedZzIndexResult(
                rootPath,
                archives,
                entries,
                indexedArchiveCount,
                failedArchives,
                false,
                signature);

            _cachedRootPath = rootPath;
            _cachedArchiveSignature = signature;
            _cachedResult = freshResult;
            return freshResult;
        }

        private static string BuildArchiveSignature(IEnumerable<ZzSourceArchiveInfo> archives)
        {
            var builder = new StringBuilder();

            foreach (ZzSourceArchiveInfo archive in archives.OrderBy(x => x.ArchiveOrder).ThenBy(x => x.ArchivePath, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var info = new FileInfo(archive.ArchivePath);
                    builder
                        .Append(archive.ArchiveOrder).Append('|')
                        .Append(archive.ArchivePath).Append('|')
                        .Append(info.Exists ? info.Length.ToString() : "missing").Append('|')
                        .Append(info.Exists ? info.LastWriteTimeUtc.Ticks.ToString() : "0")
                        .Append('\n');
                }
                catch
                {
                    builder
                        .Append(archive.ArchiveOrder).Append('|')
                        .Append(archive.ArchivePath).Append("|error\n");
                }
            }

            return builder.ToString();
        }

        private static string NormalizeVirtualPath(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return string.Empty;

            string normalized = rawPath.Replace('\\', '/').Trim();
            while (normalized.StartsWith("/", StringComparison.Ordinal))
                normalized = normalized.Substring(1);

            return normalized;
        }
    }
}
