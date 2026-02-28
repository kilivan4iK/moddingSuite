using System;
using moddingSuite.BL.Ndf;

namespace moddingSuite.Model.Ndfbin.Types.AllTypes
{
    public class NdfNull : NdfValueWrapper
    {
        public NdfNull()
            : base(NdfType.Unset)
        {
        }

        public override string ToString()
        {
            return "nil";
        }

        public override byte[] GetBytes()
        {
            throw new InvalidOperationException("Null is not to be saved");
        }

        public override byte[] GetNdfText()
        {
            return NdfTextWriter.NdfTextEncoding.GetBytes("nil");
        }
    }
}
