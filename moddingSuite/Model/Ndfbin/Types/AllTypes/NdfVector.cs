using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Media.Media3D;
using moddingSuite.BL;
using moddingSuite.BL.Ndf;

namespace moddingSuite.Model.Ndfbin.Types.AllTypes
{
    public class NdfVector : NdfFlatValueWrapper
    {
        public NdfVector(Point3D value)
            : base(NdfType.Vector, value)
        {
        }

        public override byte[] GetBytes()
        {
            var pt = (Point3D)Value;

            var vector = new List<byte>();

            vector.AddRange(BitConverter.GetBytes((Single)pt.X));
            vector.AddRange(BitConverter.GetBytes((Single)pt.Y));
            vector.AddRange(BitConverter.GetBytes((Single)pt.Z));

            return vector.ToArray();
        }

        public override byte[] GetNdfText()
        {
            var pt = (Point3D)Value;
            string text = string.Format(
                CultureInfo.InvariantCulture,
                "({0}, {1}, {2})",
                pt.X,
                pt.Y,
                pt.Z);
            return NdfTextWriter.NdfTextEncoding.GetBytes(text);
        }
    }
}
