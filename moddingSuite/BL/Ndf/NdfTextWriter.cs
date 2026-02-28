using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using moddingSuite.Model.Ndfbin;

namespace moddingSuite.BL.Ndf
{
    public class NdfTextWriter : INdfWriter
    {
        public const string InstanceNamePrefix = "public";
        public static readonly Encoding NdfTextEncoding = new UTF8Encoding(false);
        [ThreadStatic] private static Dictionary<uint, string> _resolvedObjectNames;

        public void Write(Stream outStrea, NdfBinary ndf, bool compressed)
        {
            throw new NotImplementedException();
        }

        public byte[] CreateNdfScript(NdfBinary ndf)
        {
            return CreateNdfScript(ndf, null);
        }

        public byte[] CreateNdfScript(NdfBinary ndf, string sourceNdfbinPath)
        {
            Dictionary<uint, string> previousNames = _resolvedObjectNames;
            _resolvedObjectNames = NdfScriptNameResolver.Resolve(ndf, sourceNdfbinPath);

            try
            {
                var rawTextBuilder = new StringBuilder();

                foreach (NdfObject instance in ndf.Instances.Where(x => x.IsTopObject))
                    rawTextBuilder.Append(NdfTextEncoding.GetString(instance.GetNdfText()));

                string formattedScript = NdfScriptPrettyFormatter.Format(rawTextBuilder.ToString());

                using (var ms = new MemoryStream())
                {
                    byte[] preamble = NdfTextEncoding.GetPreamble();
                    if (preamble.Length > 0)
                        ms.Write(preamble, 0, preamble.Length);

                    byte[] content = NdfTextEncoding.GetBytes(formattedScript);
                    ms.Write(content, 0, content.Length);

                    return ms.ToArray();
                }
            }
            finally
            {
                _resolvedObjectNames = previousNames;
            }
        }

        public static string GetObjectName(uint instanceId)
        {
            if (_resolvedObjectNames != null)
            {
                string resolvedName;
                if (_resolvedObjectNames.TryGetValue(instanceId, out resolvedName) && !string.IsNullOrWhiteSpace(resolvedName))
                    return resolvedName;
            }

            return string.Format("{0}_{1}", InstanceNamePrefix, instanceId);
        }
    }
}
