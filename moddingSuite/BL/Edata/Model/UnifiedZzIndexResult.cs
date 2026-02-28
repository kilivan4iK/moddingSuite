using System.Collections.Generic;
using System.Linq;

namespace moddingSuite.BL.Edata.Model
{
    public class UnifiedZzIndexResult
    {
        public UnifiedZzIndexResult(
            string rootPath,
            IEnumerable<ZzSourceArchiveInfo> archives,
            IEnumerable<UnifiedZzEntry> entries,
            int indexedArchiveCount,
            IEnumerable<string> failedArchives,
            bool fromCache,
            string archiveSignature)
        {
            RootPath = rootPath;
            Archives = (archives ?? Enumerable.Empty<ZzSourceArchiveInfo>()).ToList().AsReadOnly();
            Entries = (entries ?? Enumerable.Empty<UnifiedZzEntry>()).ToList().AsReadOnly();
            IndexedArchiveCount = indexedArchiveCount;
            FailedArchives = (failedArchives ?? Enumerable.Empty<string>()).ToList().AsReadOnly();
            FromCache = fromCache;
            ArchiveSignature = archiveSignature;
        }

        public string RootPath { get; private set; }

        public IReadOnlyList<ZzSourceArchiveInfo> Archives { get; private set; }

        public IReadOnlyList<UnifiedZzEntry> Entries { get; private set; }

        public int IndexedArchiveCount { get; private set; }

        public IReadOnlyList<string> FailedArchives { get; private set; }

        public int FailedArchiveCount
        {
            get { return FailedArchives.Count; }
        }

        public bool FromCache { get; private set; }

        public string ArchiveSignature { get; private set; }

        public UnifiedZzIndexResult AsCacheHit()
        {
            return new UnifiedZzIndexResult(
                RootPath,
                Archives,
                Entries,
                IndexedArchiveCount,
                FailedArchives,
                true,
                ArchiveSignature);
        }
    }
}
