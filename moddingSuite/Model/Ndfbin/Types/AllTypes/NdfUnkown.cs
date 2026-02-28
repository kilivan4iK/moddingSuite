using System;
using moddingSuite.BL.Ndf;
using moddingSuite.Util;

namespace moddingSuite.Model.Ndfbin.Types.AllTypes
{
    public class NdfUnkown : NdfFlatValueWrapper
    {
        public NdfUnkown(byte[] value)
            : base(NdfType.Unknown, value)
        {
        }

        public override byte[] GetBytes()
        {
            throw new InvalidOperationException("Cant serialize ndfunknown.");
        }

        public override byte[] GetNdfText()
        {
            return NdfTextWriter.NdfTextEncoding.GetBytes(string.Format("UNKNOWN(0x{0})", ToString()));
        }

        public override string ToString()
        {
            return string.Format("{0}", Utils.ByteArrayToBigEndianHexByteString((byte[])Value));
        }
    }
}
