using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using moddingSuite.BL.Edata.Model;

namespace moddingSuite.BL.Edata
{
    public class UnifiedZzMergeService
    {
        private static readonly HashSet<string> ConcatenateExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".spk",
            ".mpk",
            ".cpk",
            ".apk"
        };

        public IReadOnlyList<UnifiedZzEntry> Merge(IReadOnlyDictionary<string, List<ZzFileOccurrence>> occurrencesByPath)
        {
            if (occurrencesByPath == null || occurrencesByPath.Count == 0)
                return Array.Empty<UnifiedZzEntry>();

            var entries = new List<UnifiedZzEntry>(occurrencesByPath.Count);

            foreach (var kv in occurrencesByPath)
            {
                List<ZzFileOccurrence> sortedOccurrences = kv.Value
                    .OrderBy(x => x.ArchiveOrder)
                    .ThenBy(x => x.ArchivePath, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                bool shouldConcatenate = sortedOccurrences.Count > 1 &&
                                         ConcatenateExtensions.Contains(Path.GetExtension(kv.Key));

                MergeKind mergeKind = shouldConcatenate ? MergeKind.Concatenate : MergeKind.LatestWins;
                ZzFileOccurrence effective = sortedOccurrences.LastOrDefault();

                entries.Add(new UnifiedZzEntry(kv.Key, mergeKind, effective, sortedOccurrences));
            }

            return entries
                .OrderBy(x => x.VirtualPath, StringComparer.OrdinalIgnoreCase)
                .ToList()
                .AsReadOnly();
        }
    }
}
