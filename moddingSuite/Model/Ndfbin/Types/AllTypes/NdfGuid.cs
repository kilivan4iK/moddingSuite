using System;
using moddingSuite.BL.Ndf;

namespace moddingSuite.Model.Ndfbin.Types.AllTypes
{
    public class NdfGuid : NdfFlatValueWrapper
    {
        public NdfGuid(Guid value)
            : base(NdfType.Guid, value)
        {
        }

        public override byte[] GetBytes()
        {
            return Guid.Parse(Value.ToString()).ToByteArray();
        }

        public override byte[] GetNdfText()
        {
            Guid guidValue;
            if (Value is Guid)
            {
                guidValue = (Guid)Value;
            }
            else if (!Guid.TryParse(Value == null ? null : Value.ToString(), out guidValue))
            {
                return NdfTextWriter.NdfTextEncoding.GetBytes("GUID:{00000000-0000-0000-0000-000000000000}");
            }

            string normalized = NdfScriptGuidNormalizer.NormalizeGuidForScript(guidValue).ToUpperInvariant();
            return NdfTextWriter.NdfTextEncoding.GetBytes(string.Format("GUID:{{{0}}}", normalized));
        }
    }
}
