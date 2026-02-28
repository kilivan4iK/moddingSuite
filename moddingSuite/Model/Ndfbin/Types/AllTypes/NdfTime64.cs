using System;
using moddingSuite.BL.Ndf;

namespace moddingSuite.Model.Ndfbin.Types.AllTypes
{
    public class NdfTime64 : NdfFlatValueWrapper
    {
        public NdfTime64(DateTime value)
            : base(NdfType.Time64, value)
        {
        }

        public override byte[] GetBytes()
        {
            var unixdt = new DateTime(1970, 1, 1);
            var msdt = (DateTime)Value;

            ulong res = (ulong)msdt.Subtract(unixdt).TotalSeconds;

            return BitConverter.GetBytes(res);
        }

        public override byte[] GetNdfText()
        {
            var dateTime = (DateTime)Value;
            return NdfTextWriter.NdfTextEncoding.GetBytes(string.Format("TIME64(\"{0:O}\")", dateTime));
        }
    }
}
