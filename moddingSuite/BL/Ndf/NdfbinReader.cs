using System.Collections.ObjectModel;
using System.IO;
using moddingSuite.BL.Compressing;
using moddingSuite.Model.Ndfbin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using K4os.Compression.LZ4;
using moddingSuite.Model.Ndfbin.Types;
using moddingSuite.Model.Ndfbin.Types.AllTypes;

namespace moddingSuite.BL.Ndf
{
    public class NdfbinReader : INdfReader
    {
        public NdfBinary Read(byte[] data)
        {
            var ndf = new NdfBinary();

            using (var ms = new MemoryStream(data))
            {
                ndf.Header = ReadHeader(ms);
            }

            data = DecompressBodyIfNeeded(data, ndf.Header);

            using (var ms = new MemoryStream(data))
            {
                ndf.Footer = ReadFooter(ms, ndf.Header);
                ndf.Classes = ReadClasses(ms, ndf);
                ReadProperties(ms, ndf);

                ndf.Strings = ReadStrings(ms, ndf);
                ndf.Trans = ReadTrans(ms, ndf);


                ndf.TopObjects = new HashSet<uint>(ReadUIntList(ms, ndf, "TOPO"));
                ndf.Import = ReadUIntList(ms, ndf, "IMPR");
                ndf.Export = ReadUIntList(ms, ndf, "EXPR");

                ndf.Instances = ReadObjects(ms, ndf);
            }

            return ndf;
        }

        public byte[] GetUncompressedNdfbinary(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            {
                var header = ReadHeader(ms);
                data = DecompressBodyIfNeeded(data, header);
            }

            return data;
        }

        private byte[] DecompressBodyIfNeeded(byte[] data, NdfHeader header)
        {
            if (header == null || !header.IsCompressedBody)
                return data;

            if (header.HeaderSize > (ulong)data.Length)
                throw new InvalidDataException("Invalid NDF header size.");

            using (var ms = new MemoryStream(data))
            {
                using (var uncompStream = new MemoryStream())
                {
                    ms.Seek(0, SeekOrigin.Begin);

                    int headerSize = checked((int)header.HeaderSize);
                    var headBuffer = new byte[headerSize];
                    ms.Read(headBuffer, 0, headBuffer.Length);
                    uncompStream.Write(headBuffer, 0, headBuffer.Length);

                    ms.Seek((long)header.HeaderSize, SeekOrigin.Begin);

                    var buffer = new byte[4];
                    ms.Read(buffer, 0, buffer.Length);
                    int expectedBodySize = BitConverter.ToInt32(buffer, 0);

                    int compressedPayloadLength = checked((int)(ms.Length - ms.Position));
                    if (compressedPayloadLength < 0)
                        throw new InvalidDataException("Invalid compressed NDF payload length.");

                    var compressedPayload = new byte[compressedPayloadLength];
                    ms.Read(compressedPayload, 0, compressedPayload.Length);

                    byte[] uncompressedBody = DecompressBodyPayload(compressedPayload, expectedBodySize, header.CompressionFlag);

                    uncompStream.Write(uncompressedBody, 0, uncompressedBody.Length);

                    return uncompStream.ToArray();
                }
            }
        }

        private byte[] DecompressBodyPayload(byte[] compressedPayload, int expectedBodySize, uint compressionFlag)
        {
            switch (compressionFlag)
            {
                case 1:
                    return DecompressLz4(compressedPayload, expectedBodySize);
                case 128:
                    return Compressor.Decomp(compressedPayload);
                default:
                    // Fallback for unknown variants: try legacy zlib first, then WARNO LZ4.
                    try
                    {
                        return Compressor.Decomp(compressedPayload);
                    }
                    catch
                    {
                        return DecompressLz4(compressedPayload, expectedBodySize);
                    }
            }
        }

        private byte[] DecompressLz4(byte[] compressedPayload, int expectedBodySize)
        {
            if (expectedBodySize <= 0)
                throw new InvalidDataException("Invalid expected LZ4 body size.");

            var output = new byte[expectedBodySize];
            int decodedLength = LZ4Codec.Decode(compressedPayload, 0, compressedPayload.Length, output, 0, output.Length);

            if (decodedLength <= 0)
                throw new InvalidDataException("LZ4 decompression failed.");

            if (decodedLength == expectedBodySize)
                return output;

            var trimmed = new byte[decodedLength];
            Buffer.BlockCopy(output, 0, trimmed, 0, decodedLength);
            return trimmed;
        }

        /// <summary>
        /// Reads the header data of the compiled Ndf binary.
        /// </summary>
        /// <returns>A valid instance of the Headerfile.</returns>
        protected NdfHeader ReadHeader(Stream ms)
        {
            var header = new NdfHeader();

            var buffer = new byte[4];
            ms.Read(buffer, 0, buffer.Length);

            if (BitConverter.ToUInt32(buffer, 0) != 809981253)
                throw new InvalidDataException("No EUG0 found on top of this file!");

            ms.Read(buffer, 0, buffer.Length);
            uint cndfOrReserved = BitConverter.ToUInt32(buffer, 0);

            uint cndf;
            if (cndfOrReserved == 1178881603)
            {
                cndf = cndfOrReserved;
            }
            else
            {
                // WARNO uses a non-zero reserved field between EUG0 and CNDF.
                ms.Read(buffer, 0, buffer.Length);
                cndf = BitConverter.ToUInt32(buffer, 0);
            }

            if (cndf != 1178881603)
                throw new InvalidDataException("No CNDF (Compiled NDF)!");

            ms.Read(buffer, 0, buffer.Length);
            header.CompressionFlag = BitConverter.ToUInt32(buffer, 0);
            header.IsCompressedBody = header.CompressionFlag != 0;

            buffer = new byte[8];

            ms.Read(buffer, 0, buffer.Length);
            header.FooterOffset = BitConverter.ToUInt64(buffer, 0);

            ms.Read(buffer, 0, buffer.Length);
            header.HeaderSize = BitConverter.ToUInt64(buffer, 0);

            ms.Read(buffer, 0, buffer.Length);
            header.FullFileSizeUncomp = BitConverter.ToUInt64(buffer, 0);

            return header;
        }

        /// <summary>
        /// Reads the footer data which is the Ndfbin Dictionary.
        /// </summary>
        /// <returns></returns>
        protected NdfFooter ReadFooter(Stream ms, NdfHeader head)
        {
            var footer = new NdfFooter();

            ms.Seek((long)head.FooterOffset, SeekOrigin.Begin);

            var dwdBuffer = new byte[4];
            var qwdbuffer = new byte[8];

            ms.Read(dwdBuffer, 0, dwdBuffer.Length);
            if (BitConverter.ToUInt32(dwdBuffer, 0) != 809717588)
                throw new InvalidDataException("Footer doesnt start with TOC0");


            ms.Read(dwdBuffer, 0, dwdBuffer.Length);
            uint footerEntryCount = BitConverter.ToUInt32(dwdBuffer, 0);

            for (int i = 0; i < footerEntryCount; i++)
            {
                var entry = new NdfFooterEntry();

                ms.Read(qwdbuffer, 0, qwdbuffer.Length);
                entry.Name = Encoding.ASCII.GetString(qwdbuffer).TrimEnd('\0');

                ms.Read(qwdbuffer, 0, qwdbuffer.Length);
                entry.Offset = BitConverter.ToInt64(qwdbuffer, 0);

                ms.Read(qwdbuffer, 0, qwdbuffer.Length);
                entry.Size = BitConverter.ToInt64(qwdbuffer, 0);

                footer.Entries.Add(entry);
            }

            return footer;
        }

        /// <summary>
        /// Reads the Classes dictionary.
        /// </summary>
        /// <param name="ms"></param>
        /// <param name="owner"></param>
        /// <returns></returns>
        protected ObservableCollection<NdfClass> ReadClasses(Stream ms, NdfBinary owner)
        {
            var classes = new ObservableCollection<NdfClass>();

            NdfFooterEntry classEntry = owner.Footer.Entries.Single(x => x.Name == "CLAS");

            ms.Seek(classEntry.Offset, SeekOrigin.Begin);

            uint i = 0;
            var buffer = new byte[4];

            while (ms.Position < classEntry.Offset + classEntry.Size)
            {
                var nclass = new NdfClass(owner, i);

                ms.Read(buffer, 0, buffer.Length);
                int strLen = BitConverter.ToInt32(buffer, 0);

                var strBuffer = new byte[strLen];
                ms.Read(strBuffer, 0, strBuffer.Length);

                nclass.Name = Encoding.GetEncoding("ISO-8859-1").GetString(strBuffer);

                i++;
                classes.Add(nclass);
            }

            return classes;
        }

        /// <summary>
        /// Reads the Properties dictionary and relates each one to its owning class.
        /// </summary>
        /// <param name="ms"></param>
        /// <param name="owner"></param>
        protected void ReadProperties(Stream ms, NdfBinary owner)
        {
            NdfFooterEntry propEntry = owner.Footer.Entries.Single(x => x.Name == "PROP");
            ms.Seek(propEntry.Offset, SeekOrigin.Begin);

            int i = 0;
            var buffer = new byte[4];
            while (ms.Position < propEntry.Offset + propEntry.Size)
            {
                var property = new NdfProperty(i);

                ms.Read(buffer, 0, buffer.Length);
                int strLen = BitConverter.ToInt32(buffer, 0);

                var strBuffer = new byte[strLen];
                ms.Read(strBuffer, 0, strBuffer.Length);

                property.Name = Encoding.GetEncoding("ISO-8859-1").GetString(strBuffer);

                ms.Read(buffer, 0, buffer.Length);

                NdfClass cls = owner.Classes.Single(x => x.Id == BitConverter.ToUInt32(buffer, 0));
                property.Class = cls;

                cls.Properties.Add(property);

                i++;
            }
        }

        /// <summary>
        /// Reads the string list.
        /// </summary>
        /// <param name="ms"></param>
        /// <param name="owner"></param>
        /// <returns></returns>
        protected ObservableCollection<NdfStringReference> ReadStrings(Stream ms, NdfBinary owner)
        {
            var strings = new ObservableCollection<NdfStringReference>();

            NdfFooterEntry stringEntry = owner.Footer.Entries.Single(x => x.Name == "STRG");
            ms.Seek(stringEntry.Offset, SeekOrigin.Begin);

            int i = 0;
            var buffer = new byte[4];
            while (ms.Position < stringEntry.Offset + stringEntry.Size)
            {
                var nstring = new NdfStringReference { Id = i };

                ms.Read(buffer, 0, buffer.Length);
                int strLen = BitConverter.ToInt32(buffer, 0);

                var strBuffer = new byte[strLen];
                ms.Read(strBuffer, 0, strBuffer.Length);

                nstring.Value = Encoding.GetEncoding("ISO-8859-1").GetString(strBuffer);

                i++;
                strings.Add(nstring);
            }

            return strings;
        }

        /// <summary>
        /// Reads the trans list
        /// </summary>
        /// <param name="ms"></param>
        /// <param name="owner"></param>
        /// <returns></returns>
        protected ObservableCollection<NdfTranReference> ReadTrans(Stream ms, NdfBinary owner)
        {
            var trans = new ObservableCollection<NdfTranReference>();

            NdfFooterEntry stringEntry = owner.Footer.Entries.Single(x => x.Name == "TRAN");
            ms.Seek(stringEntry.Offset, SeekOrigin.Begin);

            int i = 0;
            var buffer = new byte[4];
            while (ms.Position < stringEntry.Offset + stringEntry.Size)
            {
                var ntran = new NdfTranReference { Id = i };

                ms.Read(buffer, 0, buffer.Length);
                int strLen = BitConverter.ToInt32(buffer, 0);

                var strBuffer = new byte[strLen];
                ms.Read(strBuffer, 0, strBuffer.Length);

                ntran.Value = Encoding.GetEncoding("ISO-8859-1").GetString(strBuffer);

                i++;
                trans.Add(ntran);
            }

            // TODO: Trans is actually more a tree than a list, this is still not fully implemented/reversed.

            return trans;
        }

        /// <summary>
        /// Reads the amount of instances this file contains.
        /// </summary>
        /// <param name="ms"></param>
        /// <param name="owner"></param>
        /// <returns></returns>
        protected uint ReadChunk(Stream ms, NdfBinary owner)
        {
            NdfFooterEntry chnk = owner.Footer.Entries.Single(x => x.Name == "CHNK");
            ms.Seek(chnk.Offset, SeekOrigin.Begin);

            var buffer = new byte[4];

            ms.Read(buffer, 0, buffer.Length);
            ms.Read(buffer, 0, buffer.Length);

            return BitConverter.ToUInt32(buffer, 0);
        }

        /// <summary>
        /// Reads a list of UInt32, this is needed for the topobjects, import and export tables.
        /// </summary>
        /// <param name="ms"></param>
        /// <param name="owner"></param>
        /// <param name="lst"></param>
        /// <returns></returns>
        protected List<uint> ReadUIntList(Stream ms, NdfBinary owner, string lst)
        {
            var uintList = new List<uint>();

            NdfFooterEntry uintEntry = owner.Footer.Entries.Single(x => x.Name == lst);
            ms.Seek(uintEntry.Offset, SeekOrigin.Begin);

            var buffer = new byte[4];
            while (ms.Position < uintEntry.Offset + uintEntry.Size)
            {
                ms.Read(buffer, 0, buffer.Length);
                uintList.Add(BitConverter.ToUInt32(buffer, 0));
            }

            return uintList;
        }

        /// <summary>
        /// Reads the object instances.
        /// </summary>
        /// <param name="ms"></param>
        /// <param name="owner"></param>
        /// <returns></returns>
        protected List<NdfObject> ReadObjects(Stream ms, NdfBinary owner)
        {
            var objects = new List<NdfObject>();

            uint instanceCount = ReadChunk(ms, owner);

            NdfFooterEntry objEntry = owner.Footer.Entries.Single(x => x.Name == "OBJE");
            ms.Seek(objEntry.Offset, SeekOrigin.Begin);

            for (uint i = 0; i < instanceCount; i++)
            {
                long objOffset = ms.Position;
                try
                {
                    NdfObject obj = ReadObject(ms, i, owner);

                    obj.Offset = objOffset;

                    objects.Add(obj);
                }catch(Exception e)
                {
                    throw e;
                }
            }

            return objects;
        }

        /// <summary>
        /// Reads one object instance.
        /// </summary>
        /// <param name="ms"></param>
        /// <param name="index"></param>
        /// <param name="owner"></param>
        /// <returns></returns>
        protected NdfObject ReadObject(Stream ms, uint index, NdfBinary owner)
        {
            var instance = new NdfObject { Id = index };

            if (owner.TopObjects.Contains(index))
                instance.IsTopObject = true;

            var buffer = new byte[4];
            ms.Read(buffer, 0, buffer.Length);
            int classId = BitConverter.ToInt32(buffer, 0);

            if (owner.Classes.Count < classId)
                throw new InvalidDataException("Object without class found.");

            NdfClass cls = instance.Class = owner.Classes[classId];

            cls.Instances.Add(instance);

            // Read properties
            for (; ; )
            {
                ms.Read(buffer, 0, buffer.Length);
                uint propertyId = BitConverter.ToUInt32(buffer, 0);

                if (propertyId == 0xABABABAB)
                    break;

                var propVal = new NdfPropertyValue(instance)
                    {
                        Property = cls.Properties.SingleOrDefault(x => x.Id == propertyId)
                    };

                if (propVal.Property == null)
                    // throw new InvalidDataException("Found a value for a property which doens't exist in this class.");
                    foreach (var c in owner.Classes) 
                        foreach (var p in c.Properties)
                            if (p.Id == propertyId)
                            {
                                propVal.Property = p;
                                break;
                            }

                instance.PropertyValues.Add(propVal);
                try
                {
                    NdfValueWrapper res = ReadValue(ms, owner);
                    propVal.Value = res;
                }
                catch(Exception e)
                {
                    throw e;
                }
                
            }

            owner.AddEmptyProperties(instance);

            return instance;
        }

        /// <summary>
        /// Reads the value of a Property inside a object instance.
        /// </summary>
        /// <param name="ms"></param>
        /// <param name="binary"></param>
        /// <returns>A NdfValueWrapper Instance.</returns>
        protected NdfValueWrapper ReadValue(Stream ms, NdfBinary binary)
        {
            uint contBufferlen;
            NdfValueWrapper value;
            var buffer = new byte[4];

            ms.Read(buffer, 0, buffer.Length);
            NdfType type=NdfTypeManager.GetType(buffer);


            if (type == NdfType.Unknown)
            {
                using (var file = File.Create("dump.bin"))
                {
                    var k = 64;
                    var buf = new byte[k];
                    ms.Read(buf, 0, k);
                    file.Write(buf, 0, k);
                    file.Flush();
                    Console.WriteLine("dumped");
                }

                throw new InvalidDataException("Unknown datatypes are not supported!");

            }
            if (type == NdfType.Reference)
            {
                ms.Read(buffer, 0, buffer.Length);
                type = NdfTypeManager.GetType(buffer);
            }

            switch (type)
            {
                case NdfType.WideString:
                case NdfType.List:
                case NdfType.MapList:
                case NdfType.Blob:
                case NdfType.ZipBlob:
                    ms.Read(buffer, 0, buffer.Length);
                    contBufferlen = BitConverter.ToUInt32(buffer, 0);

                    if (type == NdfType.ZipBlob)
                        if (ms.ReadByte() != 1)
                            throw new InvalidDataException("has to be checked.");
                    break;
                default:
                    contBufferlen = NdfTypeManager.SizeofType(type);
                    break;
            }

            switch (type)
            {
                case NdfType.MapList:
                case NdfType.List:
                    NdfCollection lstValue = type == NdfType.List ? new NdfCollection() : new NdfMapList();

                    for (int i = 0; i < contBufferlen; i++)
                    {
                        CollectionItemValueHolder res;
                        if (type == NdfType.List)
                            res = new CollectionItemValueHolder(ReadValue(ms, binary), binary);
                        else
                            res = new CollectionItemValueHolder(
                                new NdfMap(
                                    new MapValueHolder(ReadValue(ms, binary), binary),
                                    new MapValueHolder(ReadValue(ms, binary), binary),
                                    binary), binary);

                        lstValue.Add(res);
                    }

                    value = lstValue;
                    break;
                case NdfType.Map:
                    value = new NdfMap(
                        new MapValueHolder(ReadValue(ms, binary), binary),
                        new MapValueHolder(ReadValue(ms, binary), binary),
                        binary);
                    break;
                default:
                    var contBuffer = new byte[contBufferlen];
                    ms.Read(contBuffer, 0, contBuffer.Length);

                    value = NdfTypeManager.GetValue(contBuffer, type, binary);
                    break;
            }

            return value;
        }
    }
}
