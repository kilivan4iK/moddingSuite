using System.Collections.Generic;
using System.Linq;

namespace moddingSuite.BL.Edata.Model
{
    public class UnifiedZzExportResult
    {
        public UnifiedZzExportResult(
            int processed,
            int succeeded,
            IEnumerable<UnifiedZzExportFailure> failures)
        {
            Processed = processed;
            Succeeded = succeeded;
            Failures = (failures ?? Enumerable.Empty<UnifiedZzExportFailure>()).ToList().AsReadOnly();
        }

        public int Processed { get; private set; }

        public int Succeeded { get; private set; }

        public int Failed
        {
            get { return Failures.Count; }
        }

        public IReadOnlyList<UnifiedZzExportFailure> Failures { get; private set; }
    }
}
