namespace moddingSuite.BL.Edata.Model
{
    public class UnifiedZzExportProgress
    {
        public UnifiedZzExportProgress(int processed, int total, string currentPath)
        {
            Processed = processed;
            Total = total;
            CurrentPath = currentPath;
        }

        public int Processed { get; private set; }

        public int Total { get; private set; }

        public string CurrentPath { get; private set; }
    }
}
