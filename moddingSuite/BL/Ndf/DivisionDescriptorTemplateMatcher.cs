using System;
using System.Collections.Generic;
using System.Linq;
using moddingSuite.Model.Ndfbin;
using moddingSuite.Model.Ndfbin.Types;
using moddingSuite.Model.Ndfbin.Types.AllTypes;

namespace moddingSuite.BL.Ndf
{
    public sealed class DivisionDescriptorTemplateMatcher
    {
        public DivisionTemplateMatchResult Match(NdfBinary ndfBinary, WarnoNdfKnowledgeIndex knowledgeIndex)
        {
            if (ndfBinary == null)
                throw new ArgumentNullException(nameof(ndfBinary));

            if (knowledgeIndex == null)
                throw new ArgumentNullException(nameof(knowledgeIndex));

            Dictionary<string, NdfObject> runtimeDescriptors = ExtractRuntimeDescriptors(ndfBinary);
            if (runtimeDescriptors.Count == 0)
            {
                return DivisionTemplateMatchResult.Fail(
                    "Strict mode supports Division files only: no exported TDeckDivisionDescriptor objects were found.");
            }

            int targetCount = runtimeDescriptors.Count;
            DivisionKnowledgeFile bestFile = null;
            int bestOverlap = -1;
            bool bestExact = false;

            foreach (DivisionKnowledgeFile candidate in knowledgeIndex.Files)
            {
                int overlap = runtimeDescriptors.Keys.Count(candidate.DescriptorGuids.Contains);
                if (overlap <= 0)
                    continue;

                bool exactSetMatch = overlap == targetCount && candidate.DescriptorGuids.Count == targetCount;
                bool isBetter = IsBetterCandidate(bestFile, bestOverlap, bestExact, candidate, overlap, exactSetMatch);
                if (!isBetter)
                    continue;

                bestFile = candidate;
                bestOverlap = overlap;
                bestExact = exactSetMatch;
            }

            if (bestFile == null)
            {
                return DivisionTemplateMatchResult.Fail(
                    string.Format("No knowledge source matched exported Division descriptors (count: {0}).", targetCount));
            }

            if (!bestExact)
            {
                return DivisionTemplateMatchResult.Fail(
                    string.Format(
                        "1:1 strict mode rejected partial knowledge match ({0}/{1}).",
                        bestOverlap,
                        targetCount));
            }

            return DivisionTemplateMatchResult.FromSuccess(bestFile, runtimeDescriptors, bestOverlap, targetCount);
        }

        private static bool IsBetterCandidate(
            DivisionKnowledgeFile currentBest,
            int currentOverlap,
            bool currentExact,
            DivisionKnowledgeFile candidate,
            int candidateOverlap,
            bool candidateExact)
        {
            if (currentBest == null)
                return true;

            if (candidateExact && !currentExact)
                return true;

            if (candidateExact == currentExact && candidateOverlap > currentOverlap)
                return true;

            if (candidateExact == currentExact && candidateOverlap == currentOverlap)
            {
                if (candidate.RootPriority < currentBest.RootPriority)
                    return true;

                if (candidate.RootPriority == currentBest.RootPriority)
                {
                    return string.Compare(candidate.SourcePath, currentBest.SourcePath, StringComparison.OrdinalIgnoreCase) < 0;
                }
            }

            return false;
        }

        private static Dictionary<string, NdfObject> ExtractRuntimeDescriptors(NdfBinary ndfBinary)
        {
            var result = new Dictionary<string, NdfObject>(StringComparer.OrdinalIgnoreCase);
            if (ndfBinary.Export == null || ndfBinary.Instances == null)
                return result;

            foreach (uint exportId in ndfBinary.Export)
            {
                if (exportId >= ndfBinary.Instances.Count)
                    continue;

                NdfObject instance = ndfBinary.Instances[(int)exportId];
                if (instance == null || instance.Class == null)
                    continue;

                if (!string.Equals(instance.Class.Name, "TDeckDivisionDescriptor", StringComparison.Ordinal))
                    continue;

                NdfPropertyValue descriptorIdProperty = instance.PropertyValues
                    .FirstOrDefault(x => x.Property != null && x.Property.Name == "DescriptorId" && x.Type != NdfType.Unset);

                if (descriptorIdProperty == null)
                    continue;

                var guidValue = descriptorIdProperty.Value as NdfGuid;
                if (guidValue == null)
                    continue;

                string runtimeGuid = guidValue.Value.ToString();
                string canonicalGuid = NdfScriptGuidNormalizer.NormalizeGuidForScript(runtimeGuid).ToLowerInvariant();

                if (!result.ContainsKey(canonicalGuid))
                    result[canonicalGuid] = instance;
            }

            return result;
        }
    }

    public sealed class DivisionTemplateMatchResult
    {
        private DivisionTemplateMatchResult()
        {
        }

        public bool Success { get; private set; }
        public string FailureReason { get; private set; }
        public string SourceFilePath { get; private set; }
        public DivisionKnowledgeFile MatchedKnowledgeFile { get; private set; }
        public IReadOnlyDictionary<string, NdfObject> RuntimeDescriptorsByGuid { get; private set; }
        public int MatchedCount { get; private set; }
        public int TargetCount { get; private set; }

        public static DivisionTemplateMatchResult Fail(string reason)
        {
            return new DivisionTemplateMatchResult
            {
                Success = false,
                FailureReason = reason
            };
        }

        public static DivisionTemplateMatchResult FromSuccess(
            DivisionKnowledgeFile matchedKnowledgeFile,
            Dictionary<string, NdfObject> runtimeDescriptors,
            int matchedCount,
            int targetCount)
        {
            return new DivisionTemplateMatchResult
            {
                Success = true,
                MatchedKnowledgeFile = matchedKnowledgeFile,
                SourceFilePath = matchedKnowledgeFile.SourcePath,
                RuntimeDescriptorsByGuid = runtimeDescriptors,
                MatchedCount = matchedCount,
                TargetCount = targetCount
            };
        }
    }
}
