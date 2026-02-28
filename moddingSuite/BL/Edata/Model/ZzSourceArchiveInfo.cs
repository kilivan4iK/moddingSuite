namespace moddingSuite.BL.Edata.Model
{
    public class ZzSourceArchiveInfo
    {
        public ZzSourceArchiveInfo(string archivePath, int archiveOrder, string displayName)
        {
            ArchivePath = archivePath;
            ArchiveOrder = archiveOrder;
            DisplayName = displayName;
        }

        public string ArchivePath { get; private set; }

        public int ArchiveOrder { get; private set; }

        public string DisplayName { get; private set; }
    }
}
