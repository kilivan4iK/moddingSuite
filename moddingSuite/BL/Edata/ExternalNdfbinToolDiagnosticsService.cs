using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using moddingSuite.BL.Edata.Model;

namespace moddingSuite.BL.Edata
{
    public class ExternalNdfbinToolDiagnosticsService
    {
        private static readonly string[] KnownRoots =
        {
            @"D:\WARNO EXTRACTOR\RUSE Modding Utilities-20250412T162610Z-001\RUSE Modding Utilities"
        };

        public ExternalNdfbinToolDiagnosticsResult RunWarnoCompatibilityCheck(string sampleDatPath, string preferredRoot = null)
        {
            string toolRoot = ResolveToolRoot(preferredRoot);
            if (string.IsNullOrWhiteSpace(toolRoot))
            {
                return new ExternalNdfbinToolDiagnosticsResult(
                    false,
                    false,
                    null,
                    "RUSE ndfbin tools not found.");
            }

            string tableExporterPath = ResolveToolPath(toolRoot, "TableExporter.exe");
            string wgPatcherPath = ResolveToolPath(toolRoot, "WGPatcher.exe");

            bool hasTableExporter = !string.IsNullOrWhiteSpace(tableExporterPath) && File.Exists(tableExporterPath);
            bool hasWgPatcher = !string.IsNullOrWhiteSpace(wgPatcherPath) && File.Exists(wgPatcherPath);

            bool? compatible = null;
            string compatibilityMessage = "TableExporter compatibility not checked.";

            if (hasTableExporter && !string.IsNullOrWhiteSpace(sampleDatPath) && File.Exists(sampleDatPath))
            {
                ToolRunResult result = RunTool(tableExporterPath, string.Format("\"{0}\"", sampleDatPath), 15000);
                compatible = result.ExitCode == 0 && !result.Output.Contains("Unhandled Exception", StringComparison.OrdinalIgnoreCase);

                if (!compatible.Value && result.Output.IndexOf("System.IO.IOException", StringComparison.OrdinalIgnoreCase) >= 0)
                    compatibilityMessage = "Tool incompatible with current WARNO dat format.";
                else if (compatible.Value)
                    compatibilityMessage = "TableExporter test passed on current WARNO dat format.";
                else
                    compatibilityMessage = string.Format("TableExporter exited with code {0}.", result.ExitCode);
            }

            var summary = new StringBuilder();
            summary.AppendFormat("RUSE tools: TableExporter={0}, WGPatcher={1}.", hasTableExporter ? "found" : "missing", hasWgPatcher ? "found" : "missing");
            if (compatible.HasValue)
                summary.Append(' ').Append(compatibilityMessage);

            return new ExternalNdfbinToolDiagnosticsResult(
                hasTableExporter,
                hasWgPatcher,
                compatible,
                summary.ToString());
        }

        private static string ResolveToolRoot(string preferredRoot)
        {
            if (!string.IsNullOrWhiteSpace(preferredRoot) && Directory.Exists(preferredRoot))
                return preferredRoot;

            foreach (string root in KnownRoots)
            {
                if (Directory.Exists(root))
                    return root;
            }

            return null;
        }

        private static string ResolveToolPath(string rootPath, string fileName)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(fileName))
                return null;

            try
            {
                string directMatch = Directory
                    .EnumerateFiles(rootPath, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault();

                return directMatch;
            }
            catch
            {
                return null;
            }
        }

        private static ToolRunResult RunTool(string executablePath, string arguments, int timeoutMs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments ?? string.Empty,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (Process process = Process.Start(psi))
            {
                if (process == null)
                    return new ToolRunResult(-1, "Failed to start process.");

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(timeoutMs))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                    }

                    return new ToolRunResult(-1, "Process timed out.");
                }

                Task.WaitAll(stdoutTask, stderrTask);
                return new ToolRunResult(process.ExitCode, string.Concat(stdoutTask.Result, Environment.NewLine, stderrTask.Result));
            }
        }

        private sealed class ToolRunResult
        {
            public ToolRunResult(int exitCode, string output)
            {
                ExitCode = exitCode;
                Output = output ?? string.Empty;
            }

            public int ExitCode { get; }

            public string Output { get; }
        }
    }
}
