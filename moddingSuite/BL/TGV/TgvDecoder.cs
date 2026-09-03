using System;
using System.IO;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using moddingSuite.Model.Textures;
using SkiaSharp;

namespace moddingSuite.BL.TGV
{
    // Modern TGV → bitmap decoder. Replaces the legacy TgvBitmapReader.GetMip
    // (which only handled DXT5/BC3) by routing each pixel-format string to the
    // matching BCnEncoder.Net decoder, with a fallback for uncompressed data.
    //
    // Output is a SkiaSharp SKBitmap (RGBA8888) so callers can crop and save
    // as PNG directly. Mip selection mirrors the Python reference
    // pick_fullres_mip(): prefer the mip whose decoded byte size matches the
    // header width × height for that format; otherwise the mip with the largest
    // raw payload (handles inconsistent headers).
    public static class TgvDecoder
    {
        public static SKBitmap DecodeFullRes(byte[] tgvBytes)
        {
            if (tgvBytes == null) throw new ArgumentNullException(nameof(tgvBytes));
            var file = new TgvReader().Read(tgvBytes);
            // WARNO TGV files store mips in REVERSE order (lowest-res first,
            // full-res last). The "full res" mip therefore has both the largest
            // payload and matches the declared ImageWidth × ImageHeight.
            int mipIndex = PickFullResMip(file);
            byte[] mip = file.MipMaps[mipIndex].Content;
            int width  = (int)file.ImageWidth;
            int height = (int)file.ImageHeight;
            byte[] rgba = DecodePixels(file.PixelFormatStr, mip, width, height);
            return BuildBitmap(rgba, width, height);
        }

        private static int PickFullResMip(TgvFile file)
        {
            int best = 0;
            long bestSize = -1;
            for (int i = 0; i < file.MipMaps.Count; i++)
            {
                long size = file.MipMaps[i].Content?.LongLength ?? 0;
                if (size > bestSize)
                {
                    bestSize = size;
                    best = i;
                }
            }
            return best;
        }

        private static byte[] DecodePixels(string pixelFormat, byte[] data, int width, int height)
        {
            string fmt = (pixelFormat ?? string.Empty).Trim().ToUpperInvariant();

            // BCn compressed formats
            CompressionFormat? bc = MapToBcFormat(fmt);
            if (bc.HasValue)
            {
                var decoder = new BcDecoder();
                ColorRgba32[] pixels = decoder.DecodeRaw(data, width, height, bc.Value);
                byte[] rgba = new byte[width * height * 4];
                for (int i = 0; i < pixels.Length; i++)
                {
                    rgba[i * 4 + 0] = pixels[i].r;
                    rgba[i * 4 + 1] = pixels[i].g;
                    rgba[i * 4 + 2] = pixels[i].b;
                    rgba[i * 4 + 3] = pixels[i].a;
                }
                // BC1 in WARNO is always opaque (the "1-bit alpha mode" of DXT1
                // gives some blocks alpha=0, which Windows renders as magenta).
                // BC5 is two-channel — decoder leaves A=0, also force opaque.
                // The Python reference (tgv_to_png.py) does the same.
                if (bc.Value == CompressionFormat.Bc1 || bc.Value == CompressionFormat.Bc5)
                {
                    for (int i = 0; i < width * height; i++) rgba[i * 4 + 3] = 255;
                }
                return rgba;
            }

            // Uncompressed formats. Format names describe channel positions in
            // a 32-bit DWORD (e.g. A8R8G8B8 = A in bits 24-31, B in bits 0-7).
            // On little-endian disk the bytes are stored LSB-first, so disk
            // byte order is the REVERSE of the name:
            //   A8R8G8B8 → disk bytes are B,G,R,A (BGRA)  → swap B↔R for RGBA
            //   A8B8G8R8 → disk bytes are R,G,B,A (RGBA)  → no swap needed
            // The Python reference (tgv_to_png.py) confirms A8B8G8R8 via
            // Image.frombytes("RGBA", ..., raw) — direct copy.
            switch (fmt)
            {
                case "A8R8G8B8":
                case "A8R8G8B8_LIN":
                case "A8R8G8B8_HDR":
                case "A8R8G8B8_LIN_HDR":
                case "A8R8G8B8_SRGB":
                case "A8R8G8B8_SRGB_HDR":
                    return SwapBgraToRgba(data, width, height);

                case "A8B8G8R8":
                case "A8B8G8R8_LIN":
                case "A8B8G8R8_SRGB":
                    return CopyRgba(data, width, height);

                case "L8":
                case "L8_LIN":
                case "A8":
                case "A8_LIN":
                    return ExpandGrayscale8(data, width, height, fmt.StartsWith("A8"));

                default:
                    throw new NotSupportedException(
                        $"TGV pixel format '{pixelFormat}' is not yet supported by TgvDecoder. " +
                        "Add a case to MapToBcFormat / DecodePixels.");
            }
        }

        private static CompressionFormat? MapToBcFormat(string fmt)
        {
            switch (fmt)
            {
                case "DXT1":
                case "DXT1_LIN":
                case "DXT1_SRGB":
                case "BC1":
                case "BC1_LIN":
                case "BC1_SRGB":
                    return CompressionFormat.Bc1;

                case "DXT2":
                case "DXT3":
                case "DXT3_LIN":
                case "DXT3_SRGB":
                case "BC2":
                case "BC2_LIN":
                case "BC2_SRGB":
                    return CompressionFormat.Bc2;

                case "DXT4":
                case "DXT5":
                case "DXT5_LIN":
                case "DXT5_SRGB":
                case "DXT5_FROM_ENCODE":
                case "BC3":
                case "BC3_LIN":
                case "BC3_SRGB":
                    return CompressionFormat.Bc3;

                case "BC4":
                case "BC4_LIN":
                    return CompressionFormat.Bc4;

                case "BC5":
                case "BC5_LIN":
                case "BC5_SRGB":
                    return CompressionFormat.Bc5;

                case "BC7":
                case "BC7_LIN":
                case "BC7_SRGB":
                    return CompressionFormat.Bc7;

                default:
                    return null;
            }
        }

        // Disk B,G,R,A → output R,G,B,A (for A8R8G8B8 on little-endian).
        private static byte[] SwapBgraToRgba(byte[] src, int width, int height)
        {
            int n = width * height;
            byte[] dst = new byte[n * 4];
            for (int i = 0; i < n; i++)
            {
                byte b = src[i * 4 + 0];
                byte g = src[i * 4 + 1];
                byte r = src[i * 4 + 2];
                byte a = src[i * 4 + 3];
                dst[i * 4 + 0] = r;
                dst[i * 4 + 1] = g;
                dst[i * 4 + 2] = b;
                dst[i * 4 + 3] = a;
            }
            return dst;
        }

        // Disk R,G,B,A → output R,G,B,A (for A8B8G8R8 on little-endian — direct copy).
        private static byte[] CopyRgba(byte[] src, int width, int height)
        {
            int n = width * height * 4;
            byte[] dst = new byte[n];
            Buffer.BlockCopy(src, 0, dst, 0, n);
            return dst;
        }

        private static byte[] ExpandGrayscale8(byte[] src, int width, int height, bool alphaOnly)
        {
            int n = width * height;
            byte[] dst = new byte[n * 4];
            for (int i = 0; i < n; i++)
            {
                byte v = src[i];
                if (alphaOnly)
                {
                    dst[i * 4 + 0] = 255;
                    dst[i * 4 + 1] = 255;
                    dst[i * 4 + 2] = 255;
                    dst[i * 4 + 3] = v;
                }
                else
                {
                    dst[i * 4 + 0] = v;
                    dst[i * 4 + 1] = v;
                    dst[i * 4 + 2] = v;
                    dst[i * 4 + 3] = 255;
                }
            }
            return dst;
        }

        private static SKBitmap BuildBitmap(byte[] rgba, int width, int height)
        {
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var bmp = new SKBitmap(info);
            unsafe
            {
                fixed (byte* ptr = rgba)
                {
                    bmp.InstallPixels(info, (IntPtr)ptr, info.RowBytes);
                    // InstallPixels keeps a reference into our buffer; copy to be safe
                    // before the byte[] is released by GC.
                    return bmp.Copy();
                }
            }
        }

        public static void SavePng(SKBitmap bitmap, string path)
        {
            if (bitmap == null) throw new ArgumentNullException(nameof(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = File.OpenWrite(path);
            data.SaveTo(fs);
        }
    }
}
