using System;
using System.Text;

namespace moddingSuite.BL.Ndf
{
    internal static class NdfScriptPrettyFormatter
    {
        private const int IndentSize = 4;

        public static string Format(string rawScript)
        {
            if (string.IsNullOrEmpty(rawScript))
                return string.Empty;

            string normalized = rawScript.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');

            var sb = new StringBuilder(rawScript.Length + 1024);
            int depth = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();

                if (trimmed.Length == 0)
                {
                    sb.Append("\r\n");
                    continue;
                }

                int leadingClosers = CountLeadingClosers(trimmed);
                int lineIndent = Math.Max(0, depth - leadingClosers);

                sb.Append(' ', lineIndent * IndentSize);
                sb.Append(trimmed);
                sb.Append("\r\n");

                depth = Math.Max(0, depth + CountBracketDelta(trimmed));
            }

            return sb.ToString();
        }

        private static int CountLeadingClosers(string line)
        {
            int count = 0;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == ')' || c == ']')
                    count++;
                else
                    break;
            }

            return count;
        }

        private static int CountBracketDelta(string line)
        {
            int opens = 0;
            int closes = 0;
            bool inSingleQuote = false;
            bool inDoubleQuote = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '\'' && !inDoubleQuote)
                {
                    bool escaped = i > 0 && line[i - 1] == '\\';
                    if (!escaped)
                        inSingleQuote = !inSingleQuote;
                    continue;
                }

                if (c == '"' && !inSingleQuote)
                {
                    bool escaped = i > 0 && line[i - 1] == '\\';
                    if (!escaped)
                        inDoubleQuote = !inDoubleQuote;
                    continue;
                }

                if (inSingleQuote || inDoubleQuote)
                    continue;

                if (c == '(' || c == '[')
                    opens++;
                else if (c == ')' || c == ']')
                    closes++;
            }

            return opens - closes;
        }
    }
}
