namespace moddingSuite.BL.Edata.Model
{
    public class ExternalNdfbinToolDiagnosticsResult
    {
        public ExternalNdfbinToolDiagnosticsResult(
            bool tableExporterFound,
            bool wgPatcherFound,
            bool? tableExporterCompatible,
            string summary)
        {
            TableExporterFound = tableExporterFound;
            WgPatcherFound = wgPatcherFound;
            TableExporterCompatible = tableExporterCompatible;
            Summary = summary;
        }

        public bool TableExporterFound { get; private set; }

        public bool WgPatcherFound { get; private set; }

        public bool? TableExporterCompatible { get; private set; }

        public string Summary { get; private set; }
    }
}
