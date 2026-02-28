using System.Collections.Generic;
using System.Linq;

namespace moddingSuite.BL.Edata.Model
{
    public class UnifiedZzEntry
    {
        public UnifiedZzEntry(
            string virtualPath,
            MergeKind mergeKind,
            ZzFileOccurrence effectiveOccurrence,
            IEnumerable<ZzFileOccurrence> allOccurrences)
        {
            VirtualPath = virtualPath;
            MergeKind = mergeKind;
            EffectiveOccurrence = effectiveOccurrence;
            AllOccurrences = (allOccurrences ?? Enumerable.Empty<ZzFileOccurrence>()).ToList().AsReadOnly();
        }

        public string VirtualPath { get; private set; }

        public MergeKind MergeKind { get; private set; }

        public ZzFileOccurrence EffectiveOccurrence { get; private set; }

        public IReadOnlyList<ZzFileOccurrence> AllOccurrences { get; private set; }
    }
}
