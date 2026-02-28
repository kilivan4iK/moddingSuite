using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using moddingSuite.BL.Edata.Model;

namespace moddingSuite.BL.Edata
{
    public class UnifiedZzExportService
    {
        public UnifiedZzExportResult ExportEntries(
            IEnumerable<UnifiedZzEntry> entries,
            string destinationRoot,
            Action<UnifiedZzExportProgress> progressCallback = null)
        {
            if (string.IsNullOrWhiteSpace(destinationRoot))
                throw new ArgumentException("Destination root path is required.", nameof(destinationRoot));

            Directory.CreateDirectory(destinationRoot);

            List<UnifiedZzEntry> exportEntries = (entries ?? Enumerable.Empty<UnifiedZzEntry>())
                .Where(entry => entry != null)
                .ToList();

            int total = exportEntries.Count;
            int processed = 0;
            int succeeded = 0;
            var failures = new List<UnifiedZzExportFailure>();

            foreach (UnifiedZzEntry entry in exportEntries)
            {
                try
                {
                    ExportEntry(entry, destinationRoot);
                    succeeded++;
                }
                catch (Exception ex)
                {
                    failures.Add(new UnifiedZzExportFailure(entry.VirtualPath, ex.Message));
                }
                finally
                {
                    processed++;
                    progressCallback?.Invoke(new UnifiedZzExportProgress(processed, total, entry.VirtualPath));
                }
            }

            return new UnifiedZzExportResult(processed, succeeded, failures);
        }

        public void ExportEntry(UnifiedZzEntry entry, string destinationRoot)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));

            if (string.IsNullOrWhiteSpace(destinationRoot))
                throw new ArgumentException("Destination root path is required.", nameof(destinationRoot));

            string destinationPath = BuildDestinationPath(destinationRoot, entry.VirtualPath);
            string destinationDirectory = Path.GetDirectoryName(destinationPath);

            if (!string.IsNullOrWhiteSpace(destinationDirectory))
                Directory.CreateDirectory(destinationDirectory);

            switch (entry.MergeKind)
            {
                case MergeKind.Concatenate:
                    WriteConcatenated(entry, destinationPath);
                    break;
                case MergeKind.LatestWins:
                    WriteLatest(entry, destinationPath);
                    break;
                default:
                    throw new NotSupportedException(string.Format("Unsupported merge kind: {0}", entry.MergeKind));
            }
        }

        public UnifiedZzExportResult ExportOccurrences(
            IEnumerable<ZzFileOccurrence> occurrences,
            string sourceRoot,
            string destinationRoot,
            Action<UnifiedZzExportProgress> progressCallback = null)
        {
            if (string.IsNullOrWhiteSpace(destinationRoot))
                throw new ArgumentException("Destination root path is required.", nameof(destinationRoot));

            Directory.CreateDirectory(destinationRoot);

            List<ZzFileOccurrence> exportOccurrences = (occurrences ?? Enumerable.Empty<ZzFileOccurrence>())
                .Where(x => x != null && x.EntryRef != null && x.Manager != null)
                .ToList();

            int total = exportOccurrences.Count;
            int processed = 0;
            int succeeded = 0;
            var failures = new List<UnifiedZzExportFailure>();

            foreach (ZzFileOccurrence occurrence in exportOccurrences)
            {
                string virtualTargetPath = BuildOccurrenceTargetPath(occurrence, sourceRoot);

                try
                {
                    string destinationPath = BuildDestinationPath(destinationRoot, virtualTargetPath);
                    string destinationDirectory = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrWhiteSpace(destinationDirectory))
                        Directory.CreateDirectory(destinationDirectory);

                    byte[] bytes = occurrence.Manager.GetRawData(occurrence.EntryRef);
                    File.WriteAllBytes(destinationPath, bytes);
                    succeeded++;
                }
                catch (Exception ex)
                {
                    failures.Add(new UnifiedZzExportFailure(virtualTargetPath, ex.Message));
                }
                finally
                {
                    processed++;
                    progressCallback?.Invoke(new UnifiedZzExportProgress(processed, total, virtualTargetPath));
                }
            }

            return new UnifiedZzExportResult(processed, succeeded, failures);
        }

        private static string BuildDestinationPath(string destinationRoot, string virtualPath)
        {
            string normalizedVirtualPath = (virtualPath ?? string.Empty)
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);

            return Path.Combine(destinationRoot, normalizedVirtualPath);
        }

        private static string BuildOccurrenceTargetPath(ZzFileOccurrence occurrence, string sourceRoot)
        {
            string archivePath = occurrence.ArchivePath ?? string.Empty;
            string archiveDirectory = Path.GetDirectoryName(archivePath) ?? string.Empty;
            string archiveName = Path.GetFileNameWithoutExtension(archivePath);
            if (string.IsNullOrWhiteSpace(archiveName))
                archiveName = "unknown_dat";

            string archiveRelativeDirectory = string.Empty;
            if (!string.IsNullOrWhiteSpace(sourceRoot))
            {
                try
                {
                    archiveRelativeDirectory = Path.GetRelativePath(sourceRoot, archiveDirectory);
                    if (archiveRelativeDirectory.StartsWith("..", StringComparison.Ordinal))
                        archiveRelativeDirectory = string.Empty;
                }
                catch
                {
                    archiveRelativeDirectory = string.Empty;
                }
            }

            string normalizedEntryPath = (occurrence.VirtualPath ?? string.Empty)
                .Replace('\\', '/')
                .TrimStart('/');

            string prefix = string.IsNullOrWhiteSpace(archiveRelativeDirectory)
                ? archiveName
                : archiveRelativeDirectory.Replace('\\', '/') + "/" + archiveName;

            return string.IsNullOrWhiteSpace(normalizedEntryPath)
                ? prefix
                : prefix + "/" + normalizedEntryPath;
        }

        private static void WriteLatest(UnifiedZzEntry entry, string destinationPath)
        {
            ZzFileOccurrence source = entry.EffectiveOccurrence;
            if (source == null)
                throw new InvalidDataException(string.Format("No source occurrence for '{0}'.", entry.VirtualPath));

            byte[] bytes = source.Manager.GetRawData(source.EntryRef);
            File.WriteAllBytes(destinationPath, bytes);
        }

        private static void WriteConcatenated(UnifiedZzEntry entry, string destinationPath)
        {
            if (entry.AllOccurrences == null || entry.AllOccurrences.Count == 0)
                throw new InvalidDataException(string.Format("No source occurrences for '{0}'.", entry.VirtualPath));

            using (var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (ZzFileOccurrence occurrence in entry.AllOccurrences
                             .OrderBy(x => x.ArchiveOrder)
                             .ThenBy(x => x.ArchivePath, StringComparer.OrdinalIgnoreCase))
                {
                    byte[] bytes = occurrence.Manager.GetRawData(occurrence.EntryRef);
                    output.Write(bytes, 0, bytes.Length);
                }
            }
        }
    }
}
