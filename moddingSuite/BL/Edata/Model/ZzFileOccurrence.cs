using moddingSuite.Model.Edata;
using moddingSuite.BL;

namespace moddingSuite.BL.Edata.Model
{
    public class ZzFileOccurrence
    {
        public ZzFileOccurrence(
            string virtualPath,
            int archiveOrder,
            string archivePath,
            string packageId,
            EdataContentFile entryRef,
            EdataManager manager)
        {
            VirtualPath = virtualPath;
            ArchiveOrder = archiveOrder;
            ArchivePath = archivePath;
            PackageId = packageId;
            EntryRef = entryRef;
            Manager = manager;
        }

        public string VirtualPath { get; private set; }

        public int ArchiveOrder { get; private set; }

        public string ArchivePath { get; private set; }

        public string PackageId { get; private set; }

        public EdataContentFile EntryRef { get; private set; }

        public EdataManager Manager { get; private set; }
    }
}
