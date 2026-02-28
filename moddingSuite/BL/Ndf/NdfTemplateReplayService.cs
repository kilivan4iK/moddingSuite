using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using moddingSuite.Model.Ndfbin;
using moddingSuite.Model.Ndfbin.Types;
using moddingSuite.Model.Ndfbin.Types.AllTypes;

namespace moddingSuite.BL.Ndf
{
    internal sealed class NdfTemplateReplayService
    {
        private static readonly Regex HeaderRegex =
            new Regex(@"^\s*(?:export\s+)?(?<name>\S+)\s+is\s+(?<class>T[A-Za-z0-9_]+)\b", RegexOptions.Compiled);

        private static readonly Regex GuidRegex =
            new Regex(@"DescriptorId\s*=\s*(?:GUID:\{|GUID\(\""?)(?<guid>[0-9A-Fa-f\-]{36})", RegexOptions.Compiled);

        private static readonly Regex GeneratedOutputFileRegex =
            new Regex(@"_(decompiled|decomp)(?:_\d+)?\.ndf$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public NdfTemplateReplayResult TryReplay(NdfBinary ndfBinary, string sourceNdfbinPath)
        {
            if (ndfBinary == null)
                return NdfTemplateReplayResult.Failed("NDF binary is null.");

            if (string.IsNullOrWhiteSpace(sourceNdfbinPath))
                return NdfTemplateReplayResult.Failed("Source path is empty.");

            BinarySignature target = BinarySignature.FromBinary(ndfBinary);
            if (target.TopClassCounts.Count == 0)
                return NdfTemplateReplayResult.Failed("No top-level objects were found.");

            List<string> candidates = EnumerateCandidates(sourceNdfbinPath);
            if (candidates.Count == 0)
                return NdfTemplateReplayResult.Failed("No candidate .ndf files were found for auto-recovery.");

            CandidateEvaluation best = null;
            CandidateEvaluation bestAny = null;
            foreach (string candidatePath in candidates)
            {
                CandidateSignature candidate = CandidateSignature.FromFile(candidatePath);
                if (candidate == null)
                    continue;

                CandidateEvaluation evaluation = Evaluate(target, candidate, sourceNdfbinPath);
                if (bestAny == null || evaluation.Score > bestAny.Score)
                    bestAny = evaluation;

                if (!evaluation.IsViable)
                    continue;

                if (best == null || evaluation.Score > best.Score)
                    best = evaluation;
            }

            if (best == null && bestAny != null && IsLikelyCandidate(bestAny))
                best = bestAny;

            if (best == null)
            {
                if (bestAny != null)
                {
                    return NdfTemplateReplayResult.Failed(string.Format(
                        "No high-confidence template match was found. Best candidate: {0} (score={1:0.00}, guidOverlap={2}/{3}, classOverlap={4:P1}).",
                        bestAny.Candidate.Path,
                        bestAny.Score,
                        bestAny.GuidOverlapCount,
                        bestAny.TargetGuidCount,
                        bestAny.ClassOverlapRatio));
                }

                return NdfTemplateReplayResult.Failed("No high-confidence template match was found.");
            }

            try
            {
                string script = File.ReadAllText(best.Candidate.Path);
                return NdfTemplateReplayResult.Succeeded(
                    best.Candidate.Path,
                    script,
                    best.Score,
                    best.GuidOverlapCount,
                    best.TargetGuidCount,
                    best.CandidateGuidCount,
                    best.ClassOverlapRatio);
            }
            catch (Exception ex)
            {
                return NdfTemplateReplayResult.Failed(string.Format(
                    "Template candidate read failed ({0}): {1}",
                    best.Candidate.Path,
                    ex.Message));
            }
        }

        private static bool IsLikelyCandidate(CandidateEvaluation evaluation)
        {
            if (evaluation == null)
                return false;

            if (evaluation.TargetGuidCount > 0)
            {
                double guidCoverage = evaluation.TargetGuidCount > 0
                    ? (double)evaluation.GuidOverlapCount / evaluation.TargetGuidCount
                    : 0.0d;

                if (guidCoverage >= 0.35d && evaluation.ClassOverlapRatio >= 0.20d)
                    return true;
            }

            return evaluation.ClassOverlapRatio >= 0.80d;
        }

        private static CandidateEvaluation Evaluate(BinarySignature target, CandidateSignature candidate, string sourceNdfbinPath)
        {
            double classOverlap = ComputeClassOverlap(target.TopClassCounts, candidate.TopClassCounts);
            int guidOverlap = 0;
            int targetGuidCount = target.DescriptorGuids.Count;
            int candidateGuidCount = candidate.DescriptorGuids.Count;

            if (targetGuidCount > 0 && candidateGuidCount > 0)
                guidOverlap = target.DescriptorGuids.Count(x => candidate.DescriptorGuids.Contains(x));

            bool sameDirectory = string.Equals(
                Path.GetDirectoryName(sourceNdfbinPath) ?? string.Empty,
                Path.GetDirectoryName(candidate.Path) ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);

            double guidF1 = 0.0d;
            if (targetGuidCount > 0 && candidateGuidCount > 0 && guidOverlap > 0)
            {
                double precision = (double)guidOverlap / candidateGuidCount;
                double recall = (double)guidOverlap / targetGuidCount;
                guidF1 = (2.0d * precision * recall) / (precision + recall);
            }

            bool exactGuidMatch = targetGuidCount > 0 &&
                                  candidateGuidCount > 0 &&
                                  guidOverlap == targetGuidCount &&
                                  candidateGuidCount == targetGuidCount;

            double score = 0.0d;
            if (sameDirectory)
                score += 150.0d;

            if (exactGuidMatch)
                score += 10000.0d;

            score += guidF1 * 1000.0d;
            score += classOverlap * 400.0d;

            bool viable;
            if (exactGuidMatch)
            {
                viable = true;
            }
            else if (targetGuidCount > 0)
            {
                viable = guidF1 >= 0.80d && classOverlap >= 0.35d;
            }
            else
            {
                viable = classOverlap >= 0.92d;
            }

            return new CandidateEvaluation
            {
                Candidate = candidate,
                Score = score,
                IsViable = viable,
                GuidOverlapCount = guidOverlap,
                TargetGuidCount = targetGuidCount,
                CandidateGuidCount = candidateGuidCount,
                ClassOverlapRatio = classOverlap
            };
        }

        private static double ComputeClassOverlap(
            IReadOnlyDictionary<string, int> target,
            IReadOnlyDictionary<string, int> candidate)
        {
            if (target == null || candidate == null || target.Count == 0 || candidate.Count == 0)
                return 0.0d;

            int shared = 0;
            var allKeys = new HashSet<string>(target.Keys, StringComparer.OrdinalIgnoreCase);
            allKeys.UnionWith(candidate.Keys);

            foreach (string key in allKeys)
            {
                int left = target.ContainsKey(key) ? target[key] : 0;
                int right = candidate.ContainsKey(key) ? candidate[key] : 0;
                shared += Math.Min(left, right);
            }

            int targetTotal = target.Values.Sum();
            int candidateTotal = candidate.Values.Sum();
            int maxTotal = Math.Max(targetTotal, candidateTotal);
            if (maxTotal <= 0)
                return 0.0d;

            return (double)shared / maxTotal;
        }

        private static List<string> EnumerateCandidates(string sourceNdfbinPath)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string sourceDir = Path.GetDirectoryName(sourceNdfbinPath) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(sourceNdfbinPath) ?? string.Empty;
            string fileName = baseName + ".ndf";
            List<string> roots = ResolveRoots(sourceNdfbinPath).ToList();

            string direct = Path.Combine(sourceDir, fileName);
            if (File.Exists(direct) && seen.Add(direct))
                result.Add(direct);

            foreach (string root in roots)
            {
                foreach (string file in EnumerateByNameSafe(root, fileName))
                {
                    if (GeneratedOutputFileRegex.IsMatch(file))
                        continue;

                    if (seen.Add(file))
                        result.Add(file);
                }
            }

            if (result.Count == 0)
            {
                const int broadScanLimit = 5000;
                foreach (string root in roots)
                {
                    foreach (string file in EnumerateAllNdfSafe(root))
                    {
                        if (GeneratedOutputFileRegex.IsMatch(file))
                            continue;

                        if (!seen.Add(file))
                            continue;

                        result.Add(file);
                        if (result.Count >= broadScanLimit)
                            return result;
                    }
                }
            }

            return result;
        }

        private static IEnumerable<string> ResolveRoots(string sourceNdfbinPath)
        {
            var roots = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string sourceDir = Path.GetDirectoryName(sourceNdfbinPath);
            if (!string.IsNullOrWhiteSpace(sourceDir))
            {
                string full = Path.GetFullPath(sourceDir);
                if (Directory.Exists(full) && seen.Add(full))
                    roots.Add(full);
            }

            string configuredWarnoPath = null;
            try
            {
                configuredWarnoPath = SettingsManager.Load().WargamePath;
            }
            catch
            {
                configuredWarnoPath = null;
            }

            IReadOnlyList<string> warnoRoots = WarnoPathResolver.EnumerateRoots(configuredWarnoPath);
            foreach (string warnoPath in warnoRoots)
            {
                string mods = Path.Combine(warnoPath, "Mods");

                if (Directory.Exists(mods) && seen.Add(mods))
                    roots.Add(mods);

                if (Directory.Exists(warnoPath) && seen.Add(warnoPath))
                    roots.Add(warnoPath);
            }

            return roots;
        }

        private static IEnumerable<string> EnumerateByNameSafe(string root, string fileName)
        {
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                string current = pending.Pop();

                IEnumerable<string> files = Enumerable.Empty<string>();
                try
                {
                    files = Directory.EnumerateFiles(current, fileName, SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    files = Enumerable.Empty<string>();
                }

                foreach (string file in files)
                    yield return file;

                IEnumerable<string> dirs = Enumerable.Empty<string>();
                try
                {
                    dirs = Directory.EnumerateDirectories(current);
                }
                catch
                {
                    dirs = Enumerable.Empty<string>();
                }

                foreach (string dir in dirs)
                    pending.Push(dir);
            }
        }

        private static IEnumerable<string> EnumerateAllNdfSafe(string root)
        {
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                string current = pending.Pop();

                IEnumerable<string> files = Enumerable.Empty<string>();
                try
                {
                    files = Directory.EnumerateFiles(current, "*.ndf", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    files = Enumerable.Empty<string>();
                }

                foreach (string file in files)
                    yield return file;

                IEnumerable<string> dirs = Enumerable.Empty<string>();
                try
                {
                    dirs = Directory.EnumerateDirectories(current);
                }
                catch
                {
                    dirs = Enumerable.Empty<string>();
                }

                foreach (string dir in dirs)
                    pending.Push(dir);
            }
        }

        private sealed class BinarySignature
        {
            private BinarySignature(
                Dictionary<string, int> topClassCounts,
                HashSet<string> descriptorGuids)
            {
                TopClassCounts = topClassCounts;
                DescriptorGuids = descriptorGuids;
            }

            public Dictionary<string, int> TopClassCounts { get; private set; }
            public HashSet<string> DescriptorGuids { get; private set; }

            public static BinarySignature FromBinary(NdfBinary ndfBinary)
            {
                var classCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var guidSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (NdfObject instance in EnumerateTopInstances(ndfBinary))
                {
                    string className = instance.Class == null ? null : instance.Class.Name;
                    if (!string.IsNullOrWhiteSpace(className))
                    {
                        int current;
                        classCounts.TryGetValue(className, out current);
                        classCounts[className] = current + 1;
                    }

                    string guid;
                    if (TryGetDescriptorGuid(instance, out guid))
                        guidSet.Add(guid);
                }

                return new BinarySignature(classCounts, guidSet);
            }

            private static IEnumerable<NdfObject> EnumerateTopInstances(NdfBinary ndfBinary)
            {
                if (ndfBinary.Export != null && ndfBinary.Instances != null && ndfBinary.Export.Count > 0)
                {
                    foreach (uint id in ndfBinary.Export)
                    {
                        if (id < ndfBinary.Instances.Count && ndfBinary.Instances[(int)id] != null)
                            yield return ndfBinary.Instances[(int)id];
                    }

                    yield break;
                }

                if (ndfBinary.Instances == null)
                    yield break;

                foreach (NdfObject instance in ndfBinary.Instances.Where(x => x != null && x.IsTopObject))
                    yield return instance;
            }

            private static bool TryGetDescriptorGuid(NdfObject instance, out string guid)
            {
                guid = null;
                if (instance == null || instance.PropertyValues == null)
                    return false;

                NdfPropertyValue descriptorId = instance.PropertyValues.FirstOrDefault(x =>
                    x.Property != null &&
                    x.Type != NdfType.Unset &&
                    string.Equals(x.Property.Name, "DescriptorId", StringComparison.Ordinal) &&
                    x.Value is NdfGuid);

                if (descriptorId == null)
                    return false;

                var guidValue = descriptorId.Value as NdfGuid;
                if (guidValue == null || guidValue.Value == null)
                    return false;

                Guid parsed;
                if (guidValue.Value is Guid)
                    parsed = (Guid)guidValue.Value;
                else if (!Guid.TryParse(guidValue.Value.ToString(), out parsed))
                    return false;

                guid = NdfScriptGuidNormalizer.NormalizeGuidForScript(parsed).ToLowerInvariant();
                return true;
            }
        }

        private sealed class CandidateSignature
        {
            private CandidateSignature(
                string path,
                Dictionary<string, int> topClassCounts,
                HashSet<string> descriptorGuids)
            {
                Path = path;
                TopClassCounts = topClassCounts;
                DescriptorGuids = descriptorGuids;
            }

            public string Path { get; private set; }
            public Dictionary<string, int> TopClassCounts { get; private set; }
            public HashSet<string> DescriptorGuids { get; private set; }

            public static CandidateSignature FromFile(string filePath)
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(filePath);
                }
                catch
                {
                    return null;
                }

                var classCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string line in lines)
                {
                    Match header = HeaderRegex.Match(line);
                    if (header.Success)
                    {
                        string className = header.Groups["class"].Value.Trim();
                        if (!string.IsNullOrWhiteSpace(className))
                        {
                            int current;
                            classCounts.TryGetValue(className, out current);
                            classCounts[className] = current + 1;
                        }
                    }

                    Match guidMatch = GuidRegex.Match(line);
                    if (guidMatch.Success)
                    {
                        string raw = guidMatch.Groups["guid"].Value;
                        if (!string.IsNullOrWhiteSpace(raw))
                        {
                            string normalized = NdfScriptGuidNormalizer.NormalizeGuidForScript(raw).ToLowerInvariant();
                            guids.Add(normalized);
                        }
                    }
                }

                return new CandidateSignature(filePath, classCounts, guids);
            }
        }

        private sealed class CandidateEvaluation
        {
            public CandidateSignature Candidate { get; set; }
            public double Score { get; set; }
            public bool IsViable { get; set; }
            public int GuidOverlapCount { get; set; }
            public int TargetGuidCount { get; set; }
            public int CandidateGuidCount { get; set; }
            public double ClassOverlapRatio { get; set; }
        }
    }

    internal sealed class NdfTemplateReplayResult
    {
        private NdfTemplateReplayResult()
        {
        }

        public bool Success { get; private set; }
        public string ErrorMessage { get; private set; }
        public string SourceTemplatePath { get; private set; }
        public string ScriptText { get; private set; }
        public double Score { get; private set; }
        public int GuidOverlapCount { get; private set; }
        public int TargetGuidCount { get; private set; }
        public int CandidateGuidCount { get; private set; }
        public double ClassOverlapRatio { get; private set; }

        public static NdfTemplateReplayResult Succeeded(
            string sourceTemplatePath,
            string scriptText,
            double score,
            int guidOverlapCount,
            int targetGuidCount,
            int candidateGuidCount,
            double classOverlapRatio)
        {
            return new NdfTemplateReplayResult
            {
                Success = true,
                SourceTemplatePath = sourceTemplatePath,
                ScriptText = scriptText,
                Score = score,
                GuidOverlapCount = guidOverlapCount,
                TargetGuidCount = targetGuidCount,
                CandidateGuidCount = candidateGuidCount,
                ClassOverlapRatio = classOverlapRatio
            };
        }

        public static NdfTemplateReplayResult Failed(string errorMessage)
        {
            return new NdfTemplateReplayResult
            {
                Success = false,
                ErrorMessage = errorMessage
            };
        }
    }
}
