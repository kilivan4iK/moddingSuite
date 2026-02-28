using System;
using System.Drawing;
using moddingSuite.BL.Ndf;

namespace moddingSuite.Model.Ndfbin.Types.AllTypes
{
    public class NdfColor32 : NdfFlatValueWrapper
    {
        public NdfColor32(Color value)
            : base(NdfType.Color32, value)
        {
        }

        public override byte[] GetBytes()
        {
            var col = (Color) Value;

            var colorArray = new[] { col.B, col.G, col.R, col.A};

            return colorArray;
        }

        public override byte[] GetNdfText()
        {
            var col = (Color)Value;
            string text = string.Format("RGBA({0},{1},{2},{3})", col.R, col.G, col.B, col.A);
            return NdfTextWriter.NdfTextEncoding.GetBytes(text);
        }
    }
}
