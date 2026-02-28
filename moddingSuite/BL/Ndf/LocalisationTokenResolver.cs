using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using moddingSuite.Util;

namespace moddingSuite.BL.Ndf
{
    public sealed class LocalisationTokenResolver
    {
        private static readonly Regex SingleQuotedLiteralRegex =
            new Regex(@"^'(?<token>[A-Za-z0-9_]+)'$", RegexOptions.Compiled);

        private readonly WarnoNdfKnowledgeIndex _knowledgeIndex;

        public LocalisationTokenResolver(WarnoNdfKnowledgeIndex knowledgeIndex)
        {
            _knowledgeIndex = knowledgeIndex ?? throw new ArgumentNullException(nameof(knowledgeIndex));
        }

        public string ResolveStrict(byte[] localisationHash, string templateFieldValue, string fieldName)
        {
            if (localisationHash == null || localisationHash.Length == 0)
                throw new InvalidOperationException(string.Format("Field '{0}' has an empty localisation hash.", fieldName));

            string hashHex = Utils.ByteArrayToBigEndianHexByteString(localisationHash).ToUpperInvariant();
            HashSet<string> tokensForHash;

            if (_knowledgeIndex.TokensByHash.TryGetValue(hashHex, out tokensForHash))
            {
                if (tokensForHash.Count == 1)
                    return string.Format("'{0}'", tokensForHash.First());

                string templateToken = ExtractSingleQuotedToken(templateFieldValue);
                if (!string.IsNullOrWhiteSpace(templateToken) && tokensForHash.Contains(templateToken))
                    return string.Format("'{0}'", templateToken);

                throw new InvalidOperationException(
                    string.Format(
                        "Field '{0}' has ambiguous localisation hash 0x{1} ({2} candidate tokens).",
                        fieldName,
                        hashHex,
                        tokensForHash.Count));
            }

            if (!string.IsNullOrWhiteSpace(templateFieldValue))
                return templateFieldValue.Trim();

            throw new InvalidOperationException(
                string.Format("Field '{0}' has unresolved localisation hash 0x{1}.", fieldName, hashHex));
        }

        private static string ExtractSingleQuotedToken(string literal)
        {
            if (string.IsNullOrWhiteSpace(literal))
                return null;

            Match match = SingleQuotedLiteralRegex.Match(literal.Trim());
            if (!match.Success)
                return null;

            return match.Groups["token"].Value;
        }
    }
}
