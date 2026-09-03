using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace moddingSuite.BL.Ess
{
    // High-quality ESS -> WAV decoder for WARNO ESCR-wrapped streams.
    //
    // Replaces the legacy EssReader which only decoded mono correctly. This
    // implementation handles both mono and stereo (the right channel is offset
    // by a 41-bit separator inside the bitstream — without skipping it the
    // legacy code produced loud noise on the right channel), and applies a
    // short fade-out plus tail silence so voice lines end cleanly.
    public static class WarnoEssDecoder
    {
        private const int FadeOutMilliseconds = 80;
        private const int TailSilenceMilliseconds = 120;

        public static byte[] DecodeToWav(byte[] essData)
        {
            if (essData == null) throw new ArgumentNullException(nameof(essData));
            if (essData.Length < 64) throw new InvalidDataException("ESS stream is too small.");

            string magic = Encoding.ASCII.GetString(essData, 0, 4);
            if (magic != "ESCR")
                throw new InvalidDataException("Unsupported ESS header (expected 'ESCR').");

            int headerSize = ReadInt32BE(essData, 4);
            int channels = ReadInt32LE(essData, 8);
            int sampleRate = ReadInt32LE(essData, 16);
            int frameCount = ReadInt32LE(essData, 20);
            int firstBlockSize = ReadInt32BE(essData, headerSize + 16);

            if (channels != 1 && channels != 2)
                throw new InvalidDataException("Only mono and stereo ESS streams are supported.");
            if (Encoding.ASCII.GetString(essData, headerSize, 4) != "ESSH")
                throw new InvalidDataException("Unsupported ESSH sub-header.");

            var scannedOffsets = new List<int>();
            int offsetPos = headerSize + 20;
            int previousOffset = -1;
            while (offsetPos + 4 <= essData.Length)
            {
                int offset = ReadInt32BE(essData, offsetPos);
                if (previousOffset != -1 && offset <= previousOffset) break;
                scannedOffsets.Add(offset);

                int dataStartRelative = 20 + (scannedOffsets.Count * 4);
                if (offset == essData.Length - (headerSize + dataStartRelative)) break;

                previousOffset = offset;
                offsetPos += 4;
            }

            if (scannedOffsets.Count < 2)
                throw new InvalidDataException("Could not parse ESS block offsets.");

            int dataStart = headerSize + 20 + (scannedOffsets.Count * 4);

            byte[] wav;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                WriteWavHeader(writer, channels, sampleRate, frameCount);

                if (channels == 1)
                    DecodeMono(essData, dataStart, scannedOffsets, frameCount, writer, firstBlockSize);
                else
                    DecodeStereo(essData, dataStart, scannedOffsets, frameCount, writer, firstBlockSize);

                writer.Flush();
                wav = ms.ToArray();
            }

            return SmoothEnd(wav, channels, sampleRate, frameCount);
        }

        private static void DecodeMono(byte[] fileData, int dataStart, List<int> scannedOffsets, int frameCount, BinaryWriter writer, int firstBlockSize)
        {
            var blockEnds = new List<int>(scannedOffsets.Count + 1) { firstBlockSize };
            blockEnds.AddRange(scannedOffsets);

            int remainingFrames = frameCount;
            int previousEnd = 0;

            for (int blockIndex = 0; blockIndex < blockEnds.Count; blockIndex++)
            {
                int end = blockEnds[blockIndex];
                int chunkSize = end - previousEnd;
                int framesThisBlock = (blockIndex < blockEnds.Count - 1) ? 1024 : remainingFrames;

                var chunk = new byte[chunkSize];
                Buffer.BlockCopy(fileData, dataStart + previousEnd, chunk, 0, chunkSize);

                short[] samples = DecodeSingleChannelChunk(chunk, framesThisBlock, blockIndex);
                foreach (var sample in samples) writer.Write(sample);

                previousEnd = end;
                remainingFrames -= framesThisBlock;
            }
        }

        private static void DecodeStereo(byte[] fileData, int dataStart, List<int> scannedOffsets, int frameCount, BinaryWriter writer, int firstBlockSize)
        {
            const int FramesPerBlock = 512;
            var blockEnds = new List<int>(scannedOffsets.Count + 1) { firstBlockSize };
            blockEnds.AddRange(scannedOffsets);

            int remainingFrames = frameCount;
            int previousEnd = 0;

            for (int blockIndex = 0; blockIndex < blockEnds.Count; blockIndex++)
            {
                int end = blockEnds[blockIndex];
                int framesThisBlock = Math.Min(FramesPerBlock, remainingFrames);

                var chunk = new byte[end - previousEnd];
                Buffer.BlockCopy(fileData, dataStart + previousEnd, chunk, 0, chunk.Length);

                short[][] channelsData = DecodeStereoChunk(chunk, framesThisBlock, blockIndex);
                for (int frameIndex = 0; frameIndex < framesThisBlock; frameIndex++)
                {
                    writer.Write(channelsData[0][frameIndex]);
                    writer.Write(channelsData[1][frameIndex]);
                }

                previousEnd = end;
                remainingFrames -= framesThisBlock;
            }
        }

        private static short[][] DecodeStereoChunk(byte[] chunk, int framesThisBlock, int blockIndex)
        {
            if (chunk.Length < 40)
                throw new InvalidDataException("Invalid stereo chunk size in block " + blockIndex);

            var bitData = new byte[chunk.Length - 40];
            Buffer.BlockCopy(chunk, 40, bitData, 0, bitData.Length);

            int headerOffset = 0;
            int bitOffset = 0;
            var channelsData = new short[2][];

            for (int channelIndex = 0; channelIndex < 2; channelIndex++)
            {
                // 41-bit separator between left and right bitstreams in WARNO's
                // ESCR-wrapped ESS. The legacy decoder did not skip it, which
                // produced loud noise on the right channel.
                if (channelIndex > 0) bitOffset += 41;

                try
                {
                    channelsData[channelIndex] = DecodeBlock(chunk, ref headerOffset, bitData, ref bitOffset, framesThisBlock);
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException(
                        $"Decode failed at stereo block {blockIndex}, channel {channelIndex}, " +
                        $"chunkSize {chunk.Length}, frames {framesThisBlock}, bitData {bitData.Length}", ex);
                }
            }
            return channelsData;
        }

        private static short[] DecodeSingleChannelChunk(byte[] chunk, int framesThisBlock, int blockIndex)
        {
            if (chunk.Length < 20)
                throw new InvalidDataException("Invalid mono chunk size in block " + blockIndex);

            var bitData = new byte[chunk.Length - 20];
            Buffer.BlockCopy(chunk, 20, bitData, 0, bitData.Length);

            int headerOffset = 0;
            int bitOffset = 0;
            try
            {
                return DecodeBlock(chunk, ref headerOffset, bitData, ref bitOffset, framesThisBlock);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(
                    $"Decode failed at mono chunk {blockIndex}, chunkSize {chunk.Length}, " +
                    $"frames {framesThisBlock}, bitData {bitData.Length}", ex);
            }
        }

        private static void WriteWavHeader(BinaryWriter writer, int channels, int sampleRate, int frameCount)
        {
            const int bitsPerSample = 16;
            int blockAlign = channels * (bitsPerSample / 8);
            int byteRate = sampleRate * blockAlign;
            int dataSize = frameCount * blockAlign;

            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write((short)blockAlign);
            writer.Write((short)bitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);
        }

        private static byte[] SmoothEnd(byte[] wavData, int channels, int sampleRate, int frameCount)
        {
            const int dataOffset = 44;
            int blockAlign = channels * 2;
            int dataSize = frameCount * blockAlign;

            if (wavData.Length < dataOffset + dataSize)
                throw new InvalidDataException("Invalid WAV size after conversion.");

            int fadeFrames = Math.Min(frameCount, (sampleRate * FadeOutMilliseconds) / 1000);
            if (fadeFrames > 1)
            {
                int firstFadeFrame = frameCount - fadeFrames;
                for (int fadeFrameIndex = 0; fadeFrameIndex < fadeFrames; fadeFrameIndex++)
                {
                    double scale = (double)(fadeFrames - 1 - fadeFrameIndex) / (fadeFrames - 1);
                    int frameIndex = firstFadeFrame + fadeFrameIndex;

                    for (int channelIndex = 0; channelIndex < channels; channelIndex++)
                    {
                        int sampleOffset = dataOffset + (frameIndex * blockAlign) + (channelIndex * 2);
                        short sample = BitConverter.ToInt16(wavData, sampleOffset);
                        short faded = (short)Math.Round(sample * scale);
                        wavData[sampleOffset] = (byte)(faded & 0xFF);
                        wavData[sampleOffset + 1] = (byte)((faded >> 8) & 0xFF);
                    }
                }
            }

            int tailFrames = (sampleRate * TailSilenceMilliseconds) / 1000;
            int tailSize = tailFrames * blockAlign;
            int newDataSize = dataSize + tailSize;
            Array.Resize(ref wavData, dataOffset + newDataSize);

            WriteInt32LE(wavData, 4, 36 + newDataSize);
            WriteInt32LE(wavData, 40, newDataSize);
            return wavData;
        }

        private static short[] DecodeBlock(byte[] chunk, ref int headerOffset, byte[] bitData, ref int bitOffset, int frameCount)
        {
            int[] encoded = new int[frameCount];
            short[] decoded = new short[frameCount];

            int num9 = ReadInt16BE(chunk, ref headerOffset);
            int num10 = ReadInt16BE(chunk, ref headerOffset);
            int num11 = ReadInt16BE(chunk, ref headerOffset);
            int num12 = ReadInt16BE(chunk, ref headerOffset);
            int num13 = ReadInt16BE(chunk, ref headerOffset);
            int num14 = ReadInt16BE(chunk, ref headerOffset);
            int num15 = ReadInt16BE(chunk, ref headerOffset);
            int num16 = ReadInt16BE(chunk, ref headerOffset);
            int num17 = ReadInt16BE(chunk, ref headerOffset);
            int num18 = ReadInt16BE(chunk, ref headerOffset);
            int num20 = bitOffset - 1;

            int sampleIndex = 0;
            while (sampleIndex < frameCount)
            {
                int num21 = GetBit(bitData, bitOffset);
                if (num21 > 0)
                {
                    int num22 = 0;
                    int num23 = bitOffset - num20 - 1;
                    num20 = bitOffset;

                    int num30;
                    if (num23 >= 2)
                    {
                        int num8;
                        if (num23 >= 4)
                        {
                            if (num23 >= 6)
                            {
                                if (num23 == 24)
                                {
                                    int num24 = 0;
                                    for (int k = 0; k < 16; k++)
                                    {
                                        bitOffset++;
                                        num24 = (num24 * 2) + GetBit(bitData, bitOffset);
                                        num20++;
                                    }
                                    num23 = num24 + 24;
                                }

                                int num25 = num18;
                                int num26 = num17;
                                num22 = num16 + num26 + ((num25 + 1) * ((num23 >> 1) - 2));
                                num8 = num25 + num22;
                                num16 += 6 * FloorDiv(num16 + 2048, 2048);
                                num17 = num26 + 6 * FloorDiv(num26 + 1024, 1024);
                                num18 = num25 + 6 * FloorDiv(num25 + 512, 512);
                            }
                            else
                            {
                                int num27 = num17;
                                int num28 = num18;
                                num22 = num27 + num16;
                                num8 = num28 + num22;
                                num16 += 6 * FloorDiv(num16 + 2048, 2048);
                                num17 = num27 + 6 * FloorDiv(num27 + 1024, 1024);
                                num18 = num28 - (2 * FloorDiv(num28 + 510, 512));
                            }
                        }
                        else
                        {
                            num22 = num16;
                            int num29 = num17;
                            num8 = num29 + num16;
                            num16 = num22 + 6 * FloorDiv(num22 + 2048, 2048);
                            num17 = num29 - (2 * FloorDiv(num29 + 1022, 1024));
                        }

                        num30 = num8;
                    }
                    else
                    {
                        num30 = num16;
                        num16 -= 2 * FloorDiv(num16 + 2046, 2048);
                    }

                    int num31 = (num30 + num22) >> 1;
                    if (num31 > num16)
                    {
                        if (num31 > num17)
                        {
                            if (num31 <= num18)
                                num18 -= 2 * FloorDiv(num18 + 510, 512);
                        }
                        else
                            num17 -= 2 * FloorDiv(num17 + 1022, 1024);
                    }
                    else
                        num16 -= 2 * FloorDiv(num16 + 2046, 2048);

                    int num32 = num30 - num22;
                    if (num32 > 64)
                    {
                        int num33 = (num32 / 4) + 1;
                        bitOffset++;
                        num21 = GetBit(bitData, bitOffset);
                        bitOffset++;
                        int num34 = (num21 * 2) + GetBit(bitData, bitOffset);
                        num20 += 2;
                        num31 = num22 + (num33 * num34);
                    }

                    if ((num23 & 1) > 0) num31 = ~num31;

                    encoded[sampleIndex] = num31;
                    sampleIndex++;
                }
                bitOffset++;
            }

            for (sampleIndex = 0; sampleIndex < frameCount; sampleIndex++)
            {
                int num35 = encoded[sampleIndex];
                int num27 = num11;
                int num36 = (2 * ((2 * num13) - num15)) - (5 * num14);
                int num37 = (2 * num11) - num12;
                int num38 = num14;
                num14 = num13;
                num13 = num35 + (((num10 * num36) + 128) >> 8);

                if ((num36 | num35) != 0)
                    num10 += ((((num36 ^ num35) & -536870913) | 0x40000000) >> 29);

                int sample = num13 + (((num9 * num37) + 128) >> 8);
                if (sample > short.MaxValue) sample = short.MaxValue;
                if (sample < short.MinValue) sample = short.MinValue;

                if ((num13 | num37) != 0)
                    num9 += ((((num13 ^ num37) & -536870913) | 0x40000000) >> 29);

                num12 = num27;
                decoded[sampleIndex] = (short)sample;
                num11 = sample;
                num15 = num38;
            }

            return decoded;
        }

        private static int GetBit(byte[] data, int bitIndex) =>
            (data[bitIndex / 8] >> (7 - (bitIndex % 8))) & 1;

        private static int FloorDiv(int value, int divisor)
        {
            int quotient = value / divisor;
            int remainder = value % divisor;
            if (remainder != 0 && ((value < 0) != (divisor < 0))) quotient--;
            return quotient;
        }

        private static short ReadInt16BE(byte[] data, ref int offset)
        {
            short value = (short)((data[offset] << 8) | data[offset + 1]);
            offset += 2;
            return value;
        }

        private static int ReadInt32BE(byte[] data, int offset) =>
            (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

        private static int ReadInt32LE(byte[] data, int offset) =>
            data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);

        private static void WriteInt32LE(byte[] data, int offset, int value)
        {
            data[offset] = (byte)(value & 0xFF);
            data[offset + 1] = (byte)((value >> 8) & 0xFF);
            data[offset + 2] = (byte)((value >> 16) & 0xFF);
            data[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }
}
