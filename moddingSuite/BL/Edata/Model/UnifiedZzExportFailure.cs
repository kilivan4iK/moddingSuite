namespace moddingSuite.BL.Edata.Model
{
    public class UnifiedZzExportFailure
    {
        public UnifiedZzExportFailure(string virtualPath, string reason)
        {
            VirtualPath = virtualPath;
            Reason = reason;
        }

        public string VirtualPath { get; private set; }

        public string Reason { get; private set; }
    }
}
