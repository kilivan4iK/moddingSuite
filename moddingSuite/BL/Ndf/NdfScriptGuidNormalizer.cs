using System;
using System.Text;

namespace moddingSuite.BL.Ndf
{
    public static class NdfScriptGuidNormalizer
    {
        public static string NormalizeGuidForScript(Guid runtimeGuid)
        {
            return NormalizeGuidForScript(runtimeGuid.ToString("D"));
        }

        public static string NormalizeGuidForScript(string runtimeGuidText)
        {
            if (string.IsNullOrWhiteSpace(runtimeGuidText))
                throw new ArgumentException("GUID text must not be empty.", nameof(runtimeGuidText));

            string[] parts = runtimeGuidText.Trim().ToLowerInvariant().Split('-');
            if (parts.Length != 5)
                throw new FormatException(string.Format("Invalid GUID format: '{0}'.", runtimeGuidText));

            return string.Format(
                "{0}-{1}-{2}-{3}-{4}",
                ReverseBytePairs(parts[0]),
                ReverseBytePairs(parts[1]),
                ReverseBytePairs(parts[2]),
                parts[3],
                parts[4]);
        }

        private static string ReverseBytePairs(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length % 2 != 0)
                throw new FormatException(string.Format("Invalid GUID component '{0}'.", text));

            var sb = new StringBuilder(text.Length);
            for (int index = text.Length - 2; index >= 0; index -= 2)
                sb.Append(text, index, 2);

            return sb.ToString();
        }
    }
}
