using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using moddingSuite.BL.Edata.Model;

namespace moddingSuite.BL.Edata
{
    public class QuickBmsEdatExtractorService
    {
        private const int QuickBmsTimeoutMs = 15 * 60 * 1000;
        private static readonly Regex FilesFoundRegex = new Regex(@"-\s*(\d+)\s+files\s+found", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public bool TryResolveExecutable(string gameRootPath, string configuredExecutablePath, out string executablePath, out string reason)
        {
            executablePath = null;
            reason = null;

            foreach (string candidate in BuildExecutableCandidates(gameRootPath, configuredExecutablePath))
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;

                string normalized = NormalizeQuickBmsExecutableCandidate(candidate);
                if (string.IsNullOrWhiteSpace(normalized))
                    continue;

                try
                {
                    if (File.Exists(normalized))
                    {
                        executablePath = Path.GetFullPath(normalized);
                        return true;
                    }
                }
                catch
                {
                }
            }

            string fromPath;
            if (TryResolveFromPath(out fromPath))
            {
                executablePath = fromPath;
                return true;
            }

            reason = "quickbms executable not found";
            return false;
        }

        public bool TryResolveScriptPath(
            string gameRootPath,
            string quickBmsExecutable,
            string configuredScriptPath,
            out string scriptPath,
            out string reason)
        {
            scriptPath = null;
            reason = null;

            foreach (string candidate in BuildScriptCandidates(gameRootPath, quickBmsExecutable, configuredScriptPath))
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;

                try
                {
                    if (File.Exists(candidate))
                    {
                        scriptPath = Path.GetFullPath(candidate);
                        return true;
                    }
                }
                catch
                {
                }
            }

            if (!string.IsNullOrWhiteSpace(configuredScriptPath))
                reason = string.Format("Configured script path was not found: {0}", configuredScriptPath);
            else
                reason = "wargame_edat.bms not found";

            return false;
        }

        public UnifiedZzExportResult ExtractArchives(
            IEnumerable<ZzSourceArchiveInfo> archives,
            string destinationRoot,
            string quickBmsExecutable,
            string quickBmsScriptPath,
            Action<UnifiedZzExportProgress> progressCallback = null)
        {
            if (string.IsNullOrWhiteSpace(quickBmsExecutable) || !File.Exists(quickBmsExecutable))
                throw new FileNotFoundException("QuickBMS executable was not found.", quickBmsExecutable);

            if (string.IsNullOrWhiteSpace(quickBmsScriptPath) || !File.Exists(quickBmsScriptPath))
                throw new FileNotFoundException("QuickBMS script was not found.", quickBmsScriptPath);

            if (string.IsNullOrWhiteSpace(destinationRoot))
                throw new ArgumentException("Destination root path is required.", nameof(destinationRoot));

            Directory.CreateDirectory(destinationRoot);

            List<ZzSourceArchiveInfo> orderedArchives = (archives ?? Enumerable.Empty<ZzSourceArchiveInfo>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.ArchivePath))
                .OrderBy(x => x.ArchiveOrder)
                .ThenBy(x => x.ArchivePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int total = orderedArchives.Count;
            int processed = 0;
            int succeeded = 0;
            int archivesWithFiles = 0;
            var failures = new List<UnifiedZzExportFailure>();

            foreach (ZzSourceArchiveInfo archive in orderedArchives)
            {
                string archivePath = archive.ArchivePath;
                string archiveName = Path.GetFileName(archivePath) ?? archivePath;

                try
                {
                    if (!File.Exists(archivePath))
                        throw new FileNotFoundException("Archive file was not found.", archivePath);

                    QuickBmsRunResult run = RunQuickBms(quickBmsExecutable, quickBmsScriptPath, archivePath, destinationRoot);

                    if (run.ExitCode == 0)
                    {
                        succeeded++;
                        if (run.FilesFound > 0)
                            archivesWithFiles++;
                        else if (HasFatalError(run.Output))
                            failures.Add(new UnifiedZzExportFailure(archiveName, BuildFailureReason(run)));
                    }
                    else
                    {
                        failures.Add(new UnifiedZzExportFailure(archiveName, BuildFailureReason(run)));
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(new UnifiedZzExportFailure(archiveName, ex.Message));
                }
                finally
                {
                    processed++;
                    progressCallback?.Invoke(new UnifiedZzExportProgress(processed, total, archiveName));
                }
            }

            if (succeeded > 0 && archivesWithFiles == 0 && total > 0)
            {
                failures.Add(new UnifiedZzExportFailure(
                    Path.GetFileName(orderedArchives[0].ArchivePath),
                    "quickbms completed but no files were extracted from any archive."));
            }

            return new UnifiedZzExportResult(processed, succeeded, failures);
        }

        private static IEnumerable<string> BuildExecutableCandidates(string gameRootPath, string configuredExecutablePath)
        {
            string appBase = AppDomain.CurrentDomain.BaseDirectory ?? string.Empty;
            string projectRoot = NormalizeProjectRoot(appBase);
            string bundledAppBase = Path.Combine(appBase, "tools", "quickbms_win");
            string bundledProjectRoot = Path.Combine(projectRoot ?? string.Empty, "tools", "quickbms_win");

            return new[]
                {
                    configuredExecutablePath,
                    Path.Combine(bundledAppBase, "quickbms_4gb_files.exe"),
                    Path.Combine(bundledAppBase, "quickbms.exe"),
                    Path.Combine(bundledProjectRoot, "quickbms_4gb_files.exe"),
                    Path.Combine(bundledProjectRoot, "quickbms.exe"),
                    Path.Combine(gameRootPath ?? string.Empty, "Tools", "quickbms_win", "quickbms.exe"),
                    Path.Combine(gameRootPath ?? string.Empty, "Tools", "quickbms.exe"),
                    Path.Combine(projectRoot ?? string.Empty, "quickbms_win", "quickbms.exe")
                }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> BuildScriptCandidates(string gameRootPath, string quickBmsExecutable, string configuredScriptPath)
        {
            string appBase = AppDomain.CurrentDomain.BaseDirectory ?? string.Empty;
            string projectRoot = NormalizeProjectRoot(appBase);
            string quickBmsDir = Path.GetDirectoryName(quickBmsExecutable ?? string.Empty) ?? string.Empty;
            string bundledAppBase = Path.Combine(appBase, "tools", "quickbms_win");
            string bundledProjectRoot = Path.Combine(projectRoot ?? string.Empty, "tools", "quickbms_win");

            var candidates = new List<string>
            {
                NormalizeBmsScriptCandidate(configuredScriptPath),
                Path.Combine(quickBmsDir, "wargame_edat.bms"),
                Path.Combine(bundledAppBase, "wargame_edat.bms"),
                Path.Combine(bundledProjectRoot, "wargame_edat.bms"),
                Path.Combine(gameRootPath ?? string.Empty, "Tools", "quickbms_win", "wargame_edat.bms"),
                Path.Combine(projectRoot ?? string.Empty, "tools", "quickbms_win", "wargame_edat.bms")
            };

            return candidates
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string NormalizeQuickBmsExecutableCandidate(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return null;

            if (Directory.Exists(candidate))
            {
                string fourGbPath = Path.Combine(candidate, "quickbms_4gb_files.exe");
                if (File.Exists(fourGbPath))
                    return fourGbPath;

                return Path.Combine(candidate, "quickbms.exe");
            }

            return candidate;
        }

        private static string NormalizeBmsScriptCandidate(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return null;

            if (Directory.Exists(candidate))
                return Path.Combine(candidate, "wargame_edat.bms");

            return candidate;
        }

        private static bool TryResolveFromPath(out string executablePath)
        {
            executablePath = null;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = "quickbms.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (Process process = Process.Start(psi))
                {
                    if (process == null)
                        return false;

                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                        return false;

                    string firstLine = output
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault();

                    if (string.IsNullOrWhiteSpace(firstLine) || !File.Exists(firstLine))
                        return false;

                    executablePath = firstLine.Trim();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static QuickBmsRunResult RunQuickBms(string executablePath, string scriptPath, string archivePath, string destinationRoot)
        {
            var psi = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = string.Format("-q -o -Y -. \"{0}\" \"{1}\" \"{2}\"", scriptPath, archivePath, destinationRoot),
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (Process process = Process.Start(psi))
            {
                if (process == null)
                    return new QuickBmsRunResult(-1, 0, "Failed to start quickbms process.");

                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(QuickBmsTimeoutMs))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                    }

                    return new QuickBmsRunResult(-1, 0, "quickbms timed out.");
                }

                Task.WaitAll(stdoutTask, stderrTask);

                string output = string.Concat(stdoutTask.Result, Environment.NewLine, stderrTask.Result);
                int filesFound = ParseFilesFound(output);
                return new QuickBmsRunResult(process.ExitCode, filesFound, output);
            }
        }

        private static int ParseFilesFound(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return 0;

            MatchCollection matches = FilesFoundRegex.Matches(output);
            if (matches.Count == 0)
                return 0;

            Match last = matches[matches.Count - 1];
            if (!last.Success || last.Groups.Count < 2)
                return 0;

            int parsed;
            return int.TryParse(last.Groups[1].Value, out parsed) ? parsed : 0;
        }

        private static string BuildFailureReason(QuickBmsRunResult run)
        {
            if (run == null)
                return "quickbms run did not return a result.";

            if (run.ExitCode != 0)
                return string.Format("quickbms exit code {0}. {1}", run.ExitCode, ExtractErrorPreview(run.Output));

            if (run.FilesFound <= 0)
                return "quickbms produced no files.";

            return string.Format("quickbms failed with unknown reason. {0}", ExtractErrorPreview(run.Output));
        }

        private static bool HasFatalError(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return false;

            string[] lines = output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();

            return lines.Any(line =>
                line.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) &&
                line.IndexOf("myfseek", StringComparison.OrdinalIgnoreCase) < 0);
        }

        private static string ExtractErrorPreview(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return string.Empty;

            string[] lines = output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();

            string errorLine = lines.FirstOrDefault(x => x.StartsWith("Error:", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(errorLine))
                return errorLine;

            return lines.Length == 0 ? string.Empty : lines[Math.Max(0, lines.Length - 1)];
        }

        private static string NormalizeProjectRoot(string appBase)
        {
            if (string.IsNullOrWhiteSpace(appBase))
                return string.Empty;

            string full = Path.GetFullPath(appBase);
            var current = new DirectoryInfo(full);
            for (int i = 0; i < 8 && current != null; i++)
            {
                if (File.Exists(Path.Combine(current.FullName, "moddingSuite.sln")))
                    return current.FullName;

                current = current.Parent;
            }

            return full;
        }

        private sealed class QuickBmsRunResult
        {
            public QuickBmsRunResult(int exitCode, int filesFound, string output)
            {
                ExitCode = exitCode;
                FilesFound = filesFound;
                Output = output ?? string.Empty;
            }

            public int ExitCode { get; }

            public int FilesFound { get; }

            public string Output { get; }
        }
    }
}
