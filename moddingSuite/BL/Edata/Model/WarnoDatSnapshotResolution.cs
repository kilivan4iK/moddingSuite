using System.Collections.Generic;
using System.Linq;

namespace moddingSuite.BL.Edata.Model
{
    public class WarnoDatSnapshotResolution
    {
        public WarnoDatSnapshotResolution(
            bool success,
            string dataRootPath,
            string snapshotPath,
            IEnumerable<ZzSourceArchiveInfo> archives,
            string reason)
        {
            Success = success;
            DataRootPath = dataRootPath;
            SnapshotPath = snapshotPath;
            Archives = (archives ?? Enumerable.Empty<ZzSourceArchiveInfo>()).ToList().AsReadOnly();
            Reason = reason;
        }

        public bool Success { get; private set; }

        public string DataRootPath { get; private set; }

        public string SnapshotPath { get; private set; }

        public IReadOnlyList<ZzSourceArchiveInfo> Archives { get; private set; }

        public string Reason { get; private set; }
    }
}
