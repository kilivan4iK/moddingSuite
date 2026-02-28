using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using moddingSuite.BL;

namespace moddingSuite.BL.Ndf
{
    public class NdfDecompressExportService
    {
        private readonly NdfbinReader _reader = new NdfbinReader();
        private readonly NdfTemplateReplayService _templateReplayService = new NdfTemplateReplayService();

        public byte[] DecompressToUncompressedNdf(byte[] input)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            return _reader.GetUncompressedNdfbinary(input);
        }

        public NdfDecompressResult DecompressFileToSidecar(string sourceNdfbinPath)
        {
            if (string.IsNullOrWhiteSpace(sourceNdfbinPath))
                throw new ArgumentException("Source path must not be empty.", nameof(sourceNdfbinPath));

            var result = new NdfDecompressResult { SourcePath = sourceNdfbinPath };

            try
            {
                byte[] sourceBytes = File.ReadAllBytes(sourceNdfbinPath);
                byte[] decompressedBytes = DecompressToUncompressedNdf(sourceBytes);
                string outputPath = BuildNextOutputPath(sourceNdfbinPath);

                File.WriteAllBytes(outputPath, decompressedBytes);

                result.Success = true;
                result.OutputPath = outputPath;
                result.OutputLength = decompressedBytes.LongLength;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        public NdfDecompressResult DecompileFileToTextSidecar(string sourceNdfbinPath)
        {
            if (string.IsNullOrWhiteSpace(sourceNdfbinPath))
                throw new ArgumentException("Source path must not be empty.", nameof(sourceNdfbinPath));

            var result = new NdfDecompressResult { SourcePath = sourceNdfbinPath };

            try
            {
                byte[] sourceBytes = File.ReadAllBytes(sourceNdfbinPath);
                var ndfBinary = _reader.Read(sourceBytes);
                var writer = new NdfTextWriter();
                byte[] textBytes = writer.CreateNdfScript(ndfBinary, sourceNdfbinPath);

                string outputPath = BuildNextTextOutputPath(sourceNdfbinPath);
                File.WriteAllBytes(outputPath, textBytes);

                result.Success = true;
                result.OutputPath = outputPath;
                result.OutputLength = textBytes.LongLength;
                result.DecompileMode = "generic";
                result.DetailMessage = "Generic text decompile succeeded.";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        public NdfDecompressResult DecompileFileToTextSidecarUniversal(string sourceNdfbinPath)
        {
            if (string.IsNullOrWhiteSpace(sourceNdfbinPath))
                throw new ArgumentException("Source path must not be empty.", nameof(sourceNdfbinPath));

            NdfDecompressResult templateReplayResult = DecompileFileToTextSidecarTemplateReplay(sourceNdfbinPath);
            if (templateReplayResult.Success)
            {
                templateReplayResult.DecompileMode = "template-replay";
                return templateReplayResult;
            }

            NdfDecompressResult strictResult = DecompileDivisionsFileToTextSidecarStrict(sourceNdfbinPath);
            if (strictResult.Success)
            {
                strictResult.DecompileMode = "strict-divisions";
                return strictResult;
            }

            NdfDecompressResult genericResult = DecompileFileToTextSidecar(sourceNdfbinPath);
            if (genericResult.Success)
            {
                genericResult.DecompileMode = "generic-fallback";
                genericResult.DetailMessage = string.Format(
                    "Generic fallback succeeded after template/strict failures: template={0}; strict={1}",
                    templateReplayResult.ErrorMessage,
                    strictResult.ErrorMessage);
                return genericResult;
            }

            return new NdfDecompressResult
            {
                SourcePath = sourceNdfbinPath,
                Success = false,
                ErrorMessage = string.Format(
                    "Template replay failed: {0}. Strict decompile failed: {1}. Generic decompile failed: {2}.",
                    templateReplayResult.ErrorMessage,
                    strictResult.ErrorMessage,
                    genericResult.ErrorMessage),
                DecompileMode = "failed"
            };
        }

        public NdfDecompressResult DecompileFileToTextSidecarTemplateReplay(string sourceNdfbinPath)
        {
            if (string.IsNullOrWhiteSpace(sourceNdfbinPath))
                throw new ArgumentException("Source path must not be empty.", nameof(sourceNdfbinPath));

            var result = new NdfDecompressResult { SourcePath = sourceNdfbinPath };
            try
            {
                byte[] sourceBytes = File.ReadAllBytes(sourceNdfbinPath);
                var ndfBinary = _reader.Read(sourceBytes);
                NdfTemplateReplayResult replay = _templateReplayService.TryReplay(ndfBinary, sourceNdfbinPath);
                if (!replay.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = replay.ErrorMessage;
                    result.DecompileMode = "template-replay-failed";
                    return result;
                }

                string outputPath = BuildNextTextOutputPath(sourceNdfbinPath);
                File.WriteAllText(outputPath, replay.ScriptText, new UTF8Encoding(false));

                result.Success = true;
                result.OutputPath = outputPath;
                result.OutputLength = new FileInfo(outputPath).Length;
                result.MatchedSourcePath = replay.SourceTemplatePath;
                result.DecompileMode = "template-replay";
                result.DetailMessage = string.Format(
                    "Template replay succeeded. Score={0:0.00}, GUID overlap={1}/{2}, class overlap={3:P1}.",
                    replay.Score,
                    replay.GuidOverlapCount,
                    replay.TargetGuidCount,
                    replay.ClassOverlapRatio);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        public NdfDecompressResult DecompileDivisionsFileToTextSidecarStrict(string sourceNdfbinPath)
        {
            if (string.IsNullOrWhiteSpace(sourceNdfbinPath))
                throw new ArgumentException("Source path must not be empty.", nameof(sourceNdfbinPath));

            var result = new NdfDecompressResult { SourcePath = sourceNdfbinPath };

            try
            {
                byte[] sourceBytes = File.ReadAllBytes(sourceNdfbinPath);
                var ndfBinary = _reader.Read(sourceBytes);

                string sourceDirectory = Path.GetDirectoryName(sourceNdfbinPath);
                string configuredWarnoRoot = null;
                try
                {
                    configuredWarnoRoot = SettingsManager.Load().WargamePath;
                }
                catch
                {
                    configuredWarnoRoot = null;
                }

                string warnoRoot = WarnoPathResolver.EnumerateRoots(configuredWarnoRoot).FirstOrDefault();

                WarnoNdfKnowledgeIndex knowledgeIndex = WarnoNdfKnowledgeIndex.Build(sourceDirectory, warnoRoot);

                var matcher = new DivisionDescriptorTemplateMatcher();
                DivisionTemplateMatchResult matchResult = matcher.Match(ndfBinary, knowledgeIndex);
                if (!matchResult.Success)
                    throw new InvalidOperationException(matchResult.FailureReason);

                var tokenResolver = new LocalisationTokenResolver(knowledgeIndex);
                var scriptWriter = new DivisionCanonicalScriptWriter(tokenResolver);
                string strictScript = scriptWriter.CreateStrictScript(ndfBinary, matchResult);

                string outputPath = BuildNextTextOutputPath(sourceNdfbinPath);
                File.WriteAllText(outputPath, strictScript, new UTF8Encoding(false));

                result.Success = true;
                result.OutputPath = outputPath;
                result.OutputLength = new FileInfo(outputPath).Length;
                result.MatchedSourcePath = matchResult.SourceFilePath;
                result.MatchedDescriptors = matchResult.MatchedCount;
                result.TotalDescriptors = matchResult.TargetCount;
                result.DecompileMode = "strict-divisions";
                result.DetailMessage = string.Format(
                    "Strict Division 1:1 decompile succeeded ({0}/{1}).",
                    matchResult.MatchedCount,
                    matchResult.TargetCount);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        public NdfDecompressBatchResult DecompressFolder(string rootFolderPath, bool recursive)
        {
            if (string.IsNullOrWhiteSpace(rootFolderPath))
                throw new ArgumentException("Folder path must not be empty.", nameof(rootFolderPath));

            if (!Directory.Exists(rootFolderPath))
                throw new DirectoryNotFoundException(string.Format("Folder '{0}' does not exist.", rootFolderPath));

            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            List<string> sourceFiles = Directory.EnumerateFiles(rootFolderPath, "*.ndfbin", option).ToList();

            var batch = new NdfDecompressBatchResult();

            foreach (string sourceFile in sourceFiles)
            {
                batch.ProcessedCount++;

                NdfDecompressResult result = DecompressFileToSidecar(sourceFile);
                batch.Results.Add(result);

                if (result.Success)
                    batch.ConvertedCount++;
                else
                    batch.FailedCount++;
            }

            return batch;
        }

        public string BuildNextOutputPath(string sourceNdfbinPath)
        {
            if (string.IsNullOrWhiteSpace(sourceNdfbinPath))
                throw new ArgumentException("Source path must not be empty.", nameof(sourceNdfbinPath));

            string directory = Path.GetDirectoryName(sourceNdfbinPath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException(string.Format("Unable to resolve directory for '{0}'.", sourceNdfbinPath));

            string fileName = Path.GetFileNameWithoutExtension(sourceNdfbinPath);
            string firstCandidate = Path.Combine(directory, string.Format("{0}_decomp.ndf", fileName));

            if (!File.Exists(firstCandidate))
                return firstCandidate;

            for (int i = 2; ; i++)
            {
                string numberedCandidate = Path.Combine(directory, string.Format("{0}_decomp_{1}.ndf", fileName, i));
                if (!File.Exists(numberedCandidate))
                    return numberedCandidate;
            }
        }

        public string BuildNextTextOutputPath(string sourceNdfbinPath)
        {
            if (string.IsNullOrWhiteSpace(sourceNdfbinPath))
                throw new ArgumentException("Source path must not be empty.", nameof(sourceNdfbinPath));

            string directory = Path.GetDirectoryName(sourceNdfbinPath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException(string.Format("Unable to resolve directory for '{0}'.", sourceNdfbinPath));

            string fileName = Path.GetFileNameWithoutExtension(sourceNdfbinPath);
            string firstCandidate = Path.Combine(directory, string.Format("{0}_decompiled.ndf", fileName));

            if (!File.Exists(firstCandidate))
                return firstCandidate;

            for (int i = 2; ; i++)
            {
                string numberedCandidate = Path.Combine(directory, string.Format("{0}_decompiled_{1}.ndf", fileName, i));
                if (!File.Exists(numberedCandidate))
                    return numberedCandidate;
            }
        }
    }

    public class NdfDecompressResult
    {
        public string SourcePath { get; set; }
        public string OutputPath { get; set; }
        public string ErrorMessage { get; set; }
        public bool Success { get; set; }
        public long OutputLength { get; set; }
        public string MatchedSourcePath { get; set; }
        public int MatchedDescriptors { get; set; }
        public int TotalDescriptors { get; set; }
        public string DecompileMode { get; set; }
        public string DetailMessage { get; set; }
    }

    public class NdfDecompressBatchResult
    {
        public NdfDecompressBatchResult()
        {
            Results = new List<NdfDecompressResult>();
        }

        public int ProcessedCount { get; set; }
        public int ConvertedCount { get; set; }
        public int FailedCount { get; set; }
        public List<NdfDecompressResult> Results { get; private set; }
    }
}
