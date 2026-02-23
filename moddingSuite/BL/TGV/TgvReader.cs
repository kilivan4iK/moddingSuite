using moddingSuite.BL.Compressing;
using moddingSuite.BL.DDS;
using moddingSuite.Model.Textures;
using moddingSuite.Util;
using System;
using System.IO;
using System.Text;

namespace moddingSuite.BL.TGV
{
    public class TgvReader
    {
        public TgvFile Read(Stream ms)
        {
            var file = new TgvFile();

            var buffer = new byte[4];

            ms.Read(buffer, 0, buffer.Length);
            file.Version = BitConverter.ToUInt32(buffer, 0);

            ms.Read(buffer, 0, buffer.Length);
            file.IsCompressed = BitConverter.ToInt32(buffer, 0) > 0;

            ms.Read(buffer, 0, buffer.Length);
            file.Width = BitConverter.ToUInt32(buffer, 0);
            ms.Read(buffer, 0, buffer.Length);
            file.Height = BitConverter.ToUInt32(buffer, 0);

            ms.Read(buffer, 0, buffer.Length);
            file.ImageWidth = BitConverter.ToUInt32(buffer, 0);
            ms.Read(buffer, 0, buffer.Length);
            file.ImageHeight = BitConverter.ToUInt32(buffer, 0);

            buffer = new byte[2];

            ms.Read(buffer, 0, buffer.Length);
            file.MipMapCount = BitConverter.ToUInt16(buffer, 0);

            ms.Read(buffer, 0, buffer.Length);
            ushort pixelFormatLen = BitConverter.ToUInt16(buffer, 0);

            buffer = new byte[pixelFormatLen];

            ms.Read(buffer, 0, buffer.Length);
            file.PixelFormatStr = Encoding.ASCII.GetString(buffer);

            ms.Seek(Utils.RoundToNextDivBy4(pixelFormatLen) - pixelFormatLen, SeekOrigin.Current);

            buffer = new byte[16];
            ms.Read(buffer, 0, buffer.Length);
            file.SourceChecksum = (byte[])buffer.Clone();

            buffer = new byte[4];

            for (int i = 0; i < file.MipMapCount; i++)
            {
                ms.Read(buffer, 0, buffer.Length);
                uint offset = BitConverter.ToUInt32(buffer, 0);
                file.Offsets.Add(offset);
            }

            for (int i = 0; i < file.MipMapCount; i++)
            {
                ms.Read(buffer, 0, buffer.Length);
                uint offset = BitConverter.ToUInt32(buffer, 0);
                file.Sizes.Add(offset);
            }

            long mipDataBase = ms.Position;
            for (int i = 0; i < file.MipMapCount; i++)
                file.MipMaps.Add(ReadMip(ms, file, i, mipDataBase));

            file.Format = TranslatePixelFormat(file.PixelFormatStr);

            //if (file.Width != file.ImageWidth || file.Height != file.ImageHeight)
            //{
            //    throw new InvalidDataException("something interresting happened here");
            //}

            return file;
        }

        public TgvFile Read(byte[] data)
        {
            using (var ms = new MemoryStream(data))
                return Read(ms);

            //var file = new TgvFile();

            //using (var ms = new MemoryStream(data))
            //{
            //    var buffer = new byte[4];

            //    ms.Read(buffer, 0, buffer.Length);
            //    file.Version = BitConverter.ToUInt32(buffer, 0);

            //    ms.Read(buffer, 0, buffer.Length);
            //    file.IsCompressed = BitConverter.ToInt32(buffer, 0) > 0;

            //    ms.Read(buffer, 0, buffer.Length);
            //    file.Width = BitConverter.ToUInt32(buffer, 0);
            //    ms.Read(buffer, 0, buffer.Length);
            //    file.Height = BitConverter.ToUInt32(buffer, 0);

            //    ms.Read(buffer, 0, buffer.Length);
            //    file.ImageWidth = BitConverter.ToUInt32(buffer, 0);
            //    ms.Read(buffer, 0, buffer.Length);
            //    file.ImageHeight = BitConverter.ToUInt32(buffer, 0);

            //    buffer = new byte[2];

            //    ms.Read(buffer, 0, buffer.Length);
            //    file.MipMapCount = BitConverter.ToUInt16(buffer, 0);

            //    ms.Read(buffer, 0, buffer.Length);
            //    ushort pixelFormatLen = BitConverter.ToUInt16(buffer, 0);

            //    buffer = new byte[pixelFormatLen];

            //    ms.Read(buffer, 0, buffer.Length);
            //    file.PixelFormatStr = Encoding.ASCII.GetString(buffer);

            //    ms.Seek(Utils.RoundToNextDivBy4(pixelFormatLen) - pixelFormatLen, SeekOrigin.Current);

            //    buffer = new byte[16];
            //    ms.Read(buffer, 0, buffer.Length);
            //    file.SourceChecksum = (byte[])buffer.Clone();

            //    buffer = new byte[4];

            //    for (int i = 0; i < file.MipMapCount; i++)
            //    {
            //        ms.Read(buffer, 0, buffer.Length);
            //        uint offset = BitConverter.ToUInt32(buffer, 0);
            //        file.Offsets.Add(offset);
            //    }

            //    for (int i = 0; i < file.MipMapCount; i++)
            //    {
            //        ms.Read(buffer, 0, buffer.Length);
            //        uint offset = BitConverter.ToUInt32(buffer, 0);
            //        file.Sizes.Add(offset);
            //    }

            //    for (int i = 0; i < file.MipMapCount; i++)
            //        file.MipMaps.Add(ReadMip(i, data, file));
            //}

            //file.Format = TranslatePixelFormat(file.PixelFormatStr);

            ////if (file.Width != file.ImageWidth || file.Height != file.ImageHeight)
            ////{
            ////    throw new InvalidDataException("something interresting happened here");
            ////}

            //return file;
        }

        /// <summary>
        /// This method is stream and order dependant, don't use outside.
        /// </summary>
        /// <param name="ms"></param>
        /// <param name="file"></param>
        /// <param name="id"></param>
        /// <returns></returns>
        private TgvMipMap ReadMip(Stream ms, TgvFile file, int id, long mipDataBase)
        {
            if (id < 0 || id >= file.MipMapCount)
                throw new ArgumentException("id");

            uint rawOffset = file.Offsets[id];
            uint rawSize = file.Sizes[id];

            long absoluteOffset = rawOffset;
            long relativeOffset = mipDataBase + rawOffset;
            bool hasRelativeCandidate = relativeOffset != absoluteOffset;

            Exception lastError = null;

            for (int attempt = 0; attempt < (hasRelativeCandidate ? 2 : 1); attempt++)
            {
                long candidateOffset = attempt == 0 ? absoluteOffset : relativeOffset;

                if (candidateOffset < 0 || candidateOffset >= ms.Length)
                    continue;

                long available = ms.Length - candidateOffset;
                int bytesToRead = (int)Math.Min((long)rawSize, available);
                if (bytesToRead <= 0)
                    continue;

                ms.Seek(candidateOffset, SeekOrigin.Begin);
                var chunk = new byte[bytesToRead];
                ms.Read(chunk, 0, chunk.Length);

                try
                {
                    return ParseMipChunk(file, id, rawOffset, rawSize, chunk);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            throw new InvalidDataException(
                string.Format("Could not decode mip {0} (offset {1}, size {2}).", id, rawOffset, rawSize),
                lastError);
        }

        private TgvMipMap ParseMipChunk(TgvFile file, int id, uint rawOffset, uint rawSize, byte[] chunk)
        {
            var mipMap = new TgvMipMap(rawOffset, rawSize, 0);

            if (!file.IsCompressed)
            {
                mipMap.Content = chunk;
                return mipMap;
            }

            byte[] decodedContent;
            int mipWidth;

            if (!TryDecodeCompressedChunk(file, id, chunk, out decodedContent, out mipWidth))
                throw new InvalidDataException("Unable to decode compressed mip chunk.");

            mipMap.MipWidth = mipWidth;
            mipMap.Content = decodedContent;
            return mipMap;
        }

        private bool TryDecodeCompressedChunk(TgvFile file, int id, byte[] chunk, out byte[] decodedContent, out int mipWidth)
        {
            decodedContent = null;
            mipWidth = GetFallbackMipWidth(file, id);

            if (chunk.Length >= 8 && IsZstdMarker(chunk))
            {
                int declaredSize = BitConverter.ToInt32(chunk, 4);
                if (TryDecompressZstdPayload(chunk, 8, declaredSize, out decodedContent))
                    return true;
            }

            // Some variants can store a raw ZSTD frame without an explicit marker.
            if (IsZstdFrame(chunk, 0) && TryDecompressZstdPayload(chunk, 0, 0, out decodedContent))
                return true;

            if (chunk.Length >= 8 && IsZipoMarker(chunk))
            {
                int zipoMipWidth = BitConverter.ToInt32(chunk, 4);
                if (TryDecompressPayload(chunk, 8, out decodedContent))
                {
                    mipWidth = zipoMipWidth > 0 ? zipoMipWidth : mipWidth;
                    return true;
                }
            }

            // WARNO can contain chunks without the ZIPO preamble.
            if (TryDecompressPayload(chunk, 0, out decodedContent))
                return true;

            // Fallback for chunks that still use an 8-byte preamble with non-ZIPO marker.
            if (chunk.Length > 8 && TryDecompressPayload(chunk, 8, out decodedContent))
                return true;

            return false;
        }

        private bool IsZipoMarker(byte[] chunk)
        {
            return chunk.Length >= 4 &&
                   chunk[0] == 0x5A &&
                   chunk[1] == 0x49 &&
                   chunk[2] == 0x50 &&
                   chunk[3] == 0x4F;
        }

        private bool IsZstdMarker(byte[] chunk)
        {
            return chunk.Length >= 4 &&
                   chunk[0] == 0x5A &&
                   chunk[1] == 0x53 &&
                   chunk[2] == 0x54 &&
                   chunk[3] == 0x44;
        }

        private bool IsZstdFrame(byte[] chunk, int offset)
        {
            return offset >= 0 &&
                   chunk.Length >= offset + 4 &&
                   chunk[offset] == 0x28 &&
                   chunk[offset + 1] == 0xB5 &&
                   chunk[offset + 2] == 0x2F &&
                   chunk[offset + 3] == 0xFD;
        }

        private bool TryDecompressZstdPayload(byte[] chunk, int payloadOffset, int declaredSize, out byte[] content)
        {
            content = null;

            if (payloadOffset < 0 || payloadOffset >= chunk.Length)
                return false;

            try
            {
                int payloadLength = chunk.Length - payloadOffset;
                var payload = new byte[payloadLength];
                Buffer.BlockCopy(chunk, payloadOffset, payload, 0, payloadLength);

                using (var decompressor = new ZstdSharp.Decompressor())
                    content = decompressor.Unwrap(payload).ToArray();

                if (content == null || content.Length == 0)
                    return false;

                // Keep the data even if the size field is zero or mismatched.
                if (declaredSize > 0 && content.Length != declaredSize)
                    return true;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryDecompressPayload(byte[] chunk, int payloadOffset, out byte[] content)
        {
            content = null;

            if (payloadOffset < 0 || payloadOffset >= chunk.Length)
                return false;

            try
            {
                int payloadLength = chunk.Length - payloadOffset;
                var payload = new byte[payloadLength];
                Buffer.BlockCopy(chunk, payloadOffset, payload, 0, payloadLength);

                content = moddingSuite.BL.Compressing.Compressor.Decomp(payload);
                return content != null && content.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private int GetFallbackMipWidth(TgvFile file, int id)
        {
            int mipWidth = (int)(file.ImageWidth >> id);
            return Math.Max(1, mipWidth);
        }

        protected PixelFormats TranslatePixelFormat(string pixelFormat)
        {
            switch (pixelFormat)
            {
                case "A8R8G8B8_HDR":
                case "A8R8G8B8_LIN":
                case "A8R8G8B8_LIN_HDR":
                case "A8R8G8B8":
                    return PixelFormats.R8G8B8A8_UNORM;
                case "A8B8G8R8":
                case "A8B8G8R8_LIN":
                    return PixelFormats.B8G8R8A8_UNORM;
                case "A8B8G8R8_SRGB":
                    return PixelFormats.B8G8R8A8_UNORM_SRGB;
                case "X8R8G8B8":
                case "X8R8G8B8_LE":
                    return PixelFormats.B8G8R8X8_UNORM;
                case "X8R8G8B8_SRGB":
                    return PixelFormats.B8G8R8X8_UNORM_SRGB;

                case "A8R8G8B8_SRGB":
                case "A8R8G8B8_SRGB_HDR":
                    return PixelFormats.R8G8B8A8_UNORM_SRGB;

                case "A16B16G16R16":
                case "A16B16G16R16_EDRAM":
                    return PixelFormats.R16G16B16A16_UNORM;

                case "A16B16G16R16F":
                case "A16B16G16R16F_LIN":
                    return PixelFormats.R16G16B16A16_FLOAT;

                case "A32B32G32R32F":
                case "A32B32G32R32F_LIN":
                    return PixelFormats.R32G32B32A32_FLOAT;

                case "A8":
                case "A8_LIN":
                    return PixelFormats.A8_UNORM;
                case "A8P8":
                    return PixelFormats.A8P8;
                case "P8":
                    return PixelFormats.P8;
                case "L8":
                case "L8_LIN":
                    return PixelFormats.R8_UNORM;
                case "L16":
                case "L16_LIN":
                    return PixelFormats.R16_UNORM;
                case "D16_LOCKABLE":
                case "D16":
                case "D16F":
                    return PixelFormats.D16_UNORM;
                case "V8U8":
                    return PixelFormats.R8G8_SNORM;
                case "V16U16":
                    return PixelFormats.R16G16_SNORM;

                case "DXT1":
                case "DXT1_LIN":
                case "BC1":
                case "BC1_LIN":
                    return PixelFormats.BC1_UNORM;
                case "DXT1_SRGB":
                case "BC1_SRGB":
                    return PixelFormats.BC1_UNORM_SRGB;
                case "DXT2":
                case "DXT3":
                case "DXT3_LIN":
                case "BC2":
                case "BC2_LIN":
                    return PixelFormats.BC2_UNORM;
                case "DXT3_SRGB":
                case "BC2_SRGB":
                    return PixelFormats.BC2_UNORM_SRGB;
                case "DXT4":
                case "DXT5":
                case "DXT5_LIN":
                case "DXT5_FROM_ENCODE":
                case "BC3":
                case "BC3_LIN":
                    return PixelFormats.BC3_UNORM;
                case "DXT5_SRGB":
                case "BC3_SRGB":
                    return PixelFormats.BC3_UNORM_SRGB;
                case "BC5":
                case "BC5_LIN":
                case "BC5_SRGB":
                    return PixelFormats.BC5_UNORM;
                case "BC7":
                case "BC7_LIN":
                    return PixelFormats.BC7_UNORM;
                case "BC7_SRGB":
                    return PixelFormats.BC7_UNORM_SRGB;

                case "R5G6B5_LIN":
                case "R5G6B5":
                case "R8G8B8":
                case "X1R5G5B5":
                case "X1R5G5B5_LIN":
                case "A1R5G5B5":
                case "A4R4G4B4":
                case "R3G3B2":
                case "A8R3G3B2":
                case "X4R4G4B4":
                case "A8L8":
                case "A4L4":
                case "L6V5U5":
                case "X8L8V8U8":
                case "Q8W8U8V8":
                case "W11V11U10":
                case "UYVY":
                case "YUY2":
                case "D32":
                case "D32F_LOCKABLE":
                case "D15S1":
                case "D24S8":
                case "R16F":
                case "R32F":
                case "R32F_LIN":
                case "A2R10G10B10":
                case "D24X8":
                case "D24X8F":
                case "D24X4S4":
                case "G16R16":
                case "G16R16_EDRAM":
                case "G16R16F":
                case "G16R16F_LIN":
                case "G32R32F":
                case "G32R32F_LIN":
                case "A2R10G10B10_LE":
                case "CTX1":
                case "CTX1_LIN":
                case "DXN":
                case "DXN_LIN":
                case "INTZ":
                case "RAWZ":
                case "DF24":
                case "PIXNULL":
                    throw new NotSupportedException(string.Format("Pixelformat {0} not supported", pixelFormat));

                default:
                    throw new NotSupportedException(string.Format("Unknown Pixelformat {0} ", pixelFormat));
            }
        }
    }
}
