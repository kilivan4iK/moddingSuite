using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using moddingSuite.Model.Edata;
using moddingSuite.Util;
using System.Runtime.InteropServices;
using moddingSuite.ViewModel.Base;

namespace moddingSuite.BL
{
    /// <summary>
    /// Thanks to Giovanni Condello. He created the "WargameEE DAT unpacker" which is the base for my EdataManager.
    /// TODO: implement virtual packages.
    /// </summary>
    public class EdataManager : ViewModelBase
    {
        public static readonly byte[] EdataMagic = { 0x65, 0x64, 0x61, 0x74 };
        private bool _v2UsesLegacyNamePadding = true;

        /// <summary>
        /// Creates a new Instance of a EdataManager.
        /// </summary>
        /// <param name="filePath">The package file which is to be managed.</param>
        public EdataManager(string filePath)
        {
            FilePath = filePath;
        }

        static EdataManager()
        {
            byte[] edataHeader = EdataMagic;
            byte[] ndfbinheader = { 0x45, 0x55, 0x47, 0x30, 0x00, 0x00, 0x00, 0x00, 0x43, 0x4E, 0x44, 0x46 };
            byte[] tradHeader = { 0x54, 0x52, 0x41, 0x44 };
            byte[] savHeader = { 0x53, 0x41, 0x56, 0x30, 0x00, 0x00, 0x00, 0x00 };
            byte[] tgvHeader = { 2 };
            byte[] meshHeader = { 0x4D, 0x45, 0x53, 0x48 };
            byte[] scenarioHeader = {0x53, 0x43, 0x45, 0x4E, 0x41, 0x52, 0x49, 0x4F, 0x0D, 0x0A};

            _knownHeaders = new List<KeyValuePair<EdataFileType, byte[]>>
                {
                    new KeyValuePair<EdataFileType, byte[]>(EdataFileType.Ndfbin, ndfbinheader),
                    new KeyValuePair<EdataFileType, byte[]>(EdataFileType.Package, edataHeader),
                    new KeyValuePair<EdataFileType, byte[]>(EdataFileType.Dictionary, tradHeader),
                    new KeyValuePair<EdataFileType, byte[]>(EdataFileType.Save, savHeader),
                    new KeyValuePair<EdataFileType, byte[]>(EdataFileType.Image, tgvHeader),
                    new KeyValuePair<EdataFileType, byte[]>(EdataFileType.Mesh, meshHeader),
                    new KeyValuePair<EdataFileType, byte[]>(EdataFileType.Scenario, scenarioHeader),
                };
        }

        /// <summary>
        /// The current packages path on the hdd.
        /// </summary>
        public string FilePath { get; protected set; }

        /// <summary>
        /// Header information of the current package.
        /// </summary>
        public EdataHeader Header { get; protected set; }

        /// <summary>
        /// The Files the current package holds.
        /// </summary>
        public ObservableCollection<EdataContentFile> Files { get; protected set; }

        /// <summary>
        /// Reads the raw data of a file inside the current package.
        /// </summary>
        /// <param name="ofFile">A EdataFile of the current manager</param>
        /// <returns>The data of the desired EdataFile.</returns>
        public virtual byte[] GetRawData(EdataContentFile ofFile)
        {
            //if (ofFile.Manager != this)
            //    throw new ArgumentException("ofFile must be created by this instance of EdataManager");

            byte[] buffer;

            using (FileStream fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long offset = checked((long)Header.FileOffset + ofFile.Offset);
                fs.Seek(offset, SeekOrigin.Begin);

                if (ofFile.Size > int.MaxValue)
                    throw new NotSupportedException(string.Format("File '{0}' is too large to load in memory ({1} bytes).", ofFile.Path, ofFile.Size));

                buffer = new byte[(int)ofFile.Size];
                fs.Read(buffer, 0, buffer.Length);
            }

            return buffer;
        }

        /// <summary>
        /// Initiates the parsing of the current Edata file.
        /// </summary>
        public void ParseEdataFile()
        {
            Header = ReadEdataHeader();

            switch (Header.Version)
            {
                case 1:
                    Files = ReadEdatV1Dictionary();
                    break;
                case 2:
                    Files = ReadEdatV2Dictionary();
                    break;
                default:
                    throw new NotSupportedException(string.Format("Edata Version {0} is currently not supported", Header.Version));
            }
        }

        /// <summary>
        /// Reads the header of the current package.
        /// </summary>
        /// <returns>A instance of the current header.</returns>
        protected EdataHeader ReadEdataHeader()
        {
            EdataHeader header;

            var buffer = new byte[Marshal.SizeOf(typeof(EdataHeader))];

            using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                fs.Read(buffer, 0, buffer.Length);

            header = Utils.ByteArrayToStructure<EdataHeader>(buffer);

            if (header.Version > 2)
                throw new NotSupportedException(string.Format("Edata version {0} not supported", header.Version));

            return header;
        }

        /// <summary>
        /// The only tricky part about that algorythm is that you have to skip one byte if the length of the File/Dir name PLUS nullbyte is an odd number.
        /// </summary>
        /// <returns>A Collection of the Files found in the Dictionary</returns>
        protected ObservableCollection<EdataContentFile> ReadEdatV2Dictionary()
        {
            Exception legacyLayoutException = null;

            try
            {
                _v2UsesLegacyNamePadding = true;
                return ReadEdatV2Dictionary(true);
            }
            catch (Exception ex) when (IsRecoverableV2DictionaryException(ex))
            {
                legacyLayoutException = ex;
            }

            try
            {
                _v2UsesLegacyNamePadding = false;
                return ReadEdatV2Dictionary(false);
            }
            catch (Exception ex) when (IsRecoverableV2DictionaryException(ex))
            {
                throw new InvalidDataException(
                    string.Format("Could not parse EDAT V2 dictionary with either legacy or WARNO layout. Legacy parse error: {0}", legacyLayoutException == null ? "n/a" : legacyLayoutException.Message),
                    ex);
            }
        }

        protected ObservableCollection<EdataContentFile> ReadEdatV2Dictionary(bool useLegacyNamePadding)
        {
            var files = new ObservableCollection<EdataContentFile>();
            var dirs = new List<EdataDir>();
            var endings = new List<long>();

            using (FileStream fileStream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fileStream.Seek(Header.DictOffset, SeekOrigin.Begin);

                long dirEnd = Header.DictOffset + Header.DictLength;
                uint id = 0;

                while (fileStream.Position < dirEnd)
                {
                    var buffer = ReadDictionaryBytes(fileStream, 4, dirEnd, "fileGroupId");
                    int fileGroupId = BitConverter.ToInt32(buffer, 0);

                    if (fileGroupId == 0)
                    {
                        var file = new EdataContentFile();
                        buffer = ReadDictionaryBytes(fileStream, 4, dirEnd, "file.FileEntrySize");
                        file.FileEntrySize = BitConverter.ToInt32(buffer, 0);

                        buffer = ReadDictionaryBytes(fileStream, 8, dirEnd, "file.Offset");
                        file.Offset = BitConverter.ToInt64(buffer, 0);

                        buffer = ReadDictionaryBytes(fileStream, 8, dirEnd, "file.Size");
                        file.Size = BitConverter.ToInt64(buffer, 0);

                        var checkSum = ReadDictionaryBytes(fileStream, 16, dirEnd, "file.Checksum");
                        file.Checksum = checkSum;

                        file.Name = ReadDictionaryString(fileStream, dirEnd);
                        file.Path = MergePath(dirs, file.Name);

                        SkipLegacyV2NamePadding(fileStream, dirEnd, useLegacyNamePadding, file.Name);

                        file.Id = id;
                        id++;

                        ValidateV2FileEntry(file, fileStream.Length);
                        ResolveFileType(fileStream, file);

                        files.Add(file);

                        while (endings.Count > 0 && fileStream.Position == endings.Last())
                        {
                            dirs.Remove(dirs.Last());
                            endings.Remove(endings.Last());
                        }
                    }
                    else if (fileGroupId > 0)
                    {
                        var dir = new EdataDir();

                        buffer = ReadDictionaryBytes(fileStream, 4, dirEnd, "dir.FileEntrySize");
                        dir.FileEntrySize = BitConverter.ToInt32(buffer, 0);

                        if (dir.FileEntrySize != 0)
                            endings.Add(dir.FileEntrySize + fileStream.Position - 8);
                        else if (endings.Count > 0)
                            endings.Add(endings.Last());

                        dir.Name = ReadDictionaryString(fileStream, dirEnd);

                        SkipLegacyV2NamePadding(fileStream, dirEnd, useLegacyNamePadding, dir.Name);

                        dirs.Add(dir);
                    }
                    else
                    {
                        throw new InvalidDataException(string.Format("Invalid fileGroupId '{0}' in EDAT V2 dictionary at position {1}.", fileGroupId, fileStream.Position - 4));
                    }
                }
            }
            return files;
        }

        protected ObservableCollection<EdataContentFile> ReadEdatV1Dictionary()
        {
            var files = new ObservableCollection<EdataContentFile>();
            var dirs = new List<EdataDir>();
            var endings = new List<long>();

            using (FileStream fileStream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fileStream.Seek(Header.DictOffset, SeekOrigin.Begin);

                long dirEnd = Header.DictOffset + Header.DictLength;
                uint id = 0;

                while (fileStream.Position < dirEnd)
                {
                    var buffer = new byte[4];
                    fileStream.Read(buffer, 0, 4);
                    int fileGroupId = BitConverter.ToInt32(buffer, 0);

                    if (fileGroupId == 0)
                    {
                        var file = new EdataContentFile();
                        fileStream.Read(buffer, 0, 4);
                        file.FileEntrySize = BitConverter.ToInt32(buffer, 0);

                        //buffer = new byte[8];  - it's [4] now, so no need to change
                        fileStream.Read(buffer, 0, 4);
                        file.Offset = BitConverter.ToUInt32(buffer, 0);

                        fileStream.Read(buffer, 0, 4);
                        file.Size = BitConverter.ToUInt32(buffer, 0);

                        //var checkSum = new byte[16];
                        //fileStream.Read(checkSum, 0, checkSum.Length);
                        //file.Checksum = checkSum;
                        fileStream.Seek(1, SeekOrigin.Current);  //instead, skip 1 byte - as in WEE DAT unpacker

                        file.Name = Utils.ReadString(fileStream);
                        file.Path = MergePath(dirs, file.Name);

                        if ((file.Name.Length + 1) % 2 == 0)
                            fileStream.Seek(1, SeekOrigin.Current);

                        file.Id = id;
                        id++;

                        ResolveFileType(fileStream, file);

                        files.Add(file);

                        while (endings.Count > 0 && fileStream.Position == endings.Last())
                        {
                            dirs.Remove(dirs.Last());
                            endings.Remove(endings.Last());
                        }
                    }
                    else if (fileGroupId > 0)
                    {
                        var dir = new EdataDir();

                        fileStream.Read(buffer, 0, 4);
                        dir.FileEntrySize = BitConverter.ToInt32(buffer, 0);

                        if (dir.FileEntrySize != 0)
                            endings.Add(dir.FileEntrySize + fileStream.Position - 8);
                        else if (endings.Count > 0)
                            endings.Add(endings.Last());

                        dir.Name = Utils.ReadString(fileStream);

                        if ((dir.Name.Length + 1) % 2 == 1)
                            fileStream.Seek(1, SeekOrigin.Current);

                        dirs.Add(dir);
                    }
                }
            }
            return files;
        }

        /// <summary>
        /// Merges a filename in its dictionary tree.
        /// </summary>
        /// <param name="dirs"></param>
        /// <param name="fileName"></param>
        /// <returns>The valid Path inside the package.</returns>
        protected string MergePath(IEnumerable<EdataDir> dirs, string fileName)
        {
            var b = new StringBuilder();

            foreach (var dir in dirs)
                b.Append(dir.Name);

            b.Append(fileName);

            return b.ToString();
        }

        /// <summary>
        /// Replaces a file and rebuilds the Edata File with
        /// </summary>
        /// <param name="oldFile">The EdataFile object which is to be replaced.</param>
        /// <param name="newContent">The data of the new File including Header and content.</param>
        /// <returns>The data of the completly rebuilt EdataFile. This has to be saved back to the file.</returns>
        protected string ReplaceRebuildV2(EdataContentFile oldFile, byte[] newContent)
        {
            var reserveBuffer = new byte[200];

            var tmp = new FileInfo(FilePath);

            var tmpPath = Path.Combine(tmp.DirectoryName, string.Format("{0}_{1}", tmp.FullName, "temp"));

            if (!File.Exists(tmpPath))
                using (File.Create(tmpPath)) { }

            using (var fs = new FileStream(FilePath, FileMode.Open))
            {
                using (var newFile = new FileStream(tmpPath, FileMode.Truncate))
                {
                    var headerPart = new byte[Header.FileOffset];
                    fs.Read(headerPart, 0, headerPart.Length);
                    newFile.Write(headerPart, 0, headerPart.Length);

                    fs.Seek(Header.FileOffset, SeekOrigin.Begin);

                    uint filesContentLength = 0;
                    byte[] fileBuffer;

                    foreach (EdataContentFile file in Files)
                    {
                        long oldOffset = file.Offset;
                        file.Offset = newFile.Position - Header.FileOffset;

                        if (file == oldFile)
                        {
                            fileBuffer = newContent;
                            file.Size = newContent.Length;
                            file.Checksum = MD5.Create().ComputeHash(fileBuffer);
                        }
                        else
                        {
                            fileBuffer = new byte[file.Size];
                            fs.Seek(oldOffset + Header.FileOffset, SeekOrigin.Begin);
                            fs.Read(fileBuffer, 0, fileBuffer.Length);
                        }


                        newFile.Write(fileBuffer, 0, fileBuffer.Length);
                        newFile.Write(reserveBuffer, 0, reserveBuffer.Length);

                        filesContentLength += (uint)fileBuffer.Length + (uint)reserveBuffer.Length;
                    }

                    newFile.Seek(0x25, SeekOrigin.Begin);
                    newFile.Write(BitConverter.GetBytes(filesContentLength), 0, 4);


                    newFile.Seek(Header.DictOffset, SeekOrigin.Begin);
                    long dirEnd = Header.DictOffset + Header.DictLength;
                    uint id = 0;

                    while (newFile.Position < dirEnd)
                    {
                        var buffer = new byte[4];
                        newFile.Read(buffer, 0, 4);
                        int fileGroupId = BitConverter.ToInt32(buffer, 0);

                        if (fileGroupId == 0)
                        {
                            EdataContentFile curFile = Files[(int)id];

                            // FileEntrySize
                            newFile.Seek(4, SeekOrigin.Current);

                            buffer = BitConverter.GetBytes(curFile.Offset);
                            newFile.Write(buffer, 0, buffer.Length);

                            buffer = BitConverter.GetBytes(curFile.Size);
                            newFile.Write(buffer, 0, buffer.Length);

                            byte[] checkSum = curFile.Checksum;
                            newFile.Write(checkSum, 0, checkSum.Length);

                            string name = Utils.ReadString(newFile);

                            if (ShouldUseLegacyV2Padding(name))
                                newFile.Seek(1, SeekOrigin.Current);

                            id++;
                        }
                        else if (fileGroupId > 0)
                        {
                            newFile.Seek(4, SeekOrigin.Current);
                            string name = Utils.ReadString(newFile);

                            if (ShouldUseLegacyV2Padding(name))
                                newFile.Seek(1, SeekOrigin.Current);
                        }
                    }

                    newFile.Seek(Header.DictOffset, SeekOrigin.Begin);
                    var dirBuffer = new byte[Header.DictLength];
                    newFile.Read(dirBuffer, 0, dirBuffer.Length);

                    byte[] dirCheckSum = MD5.Create().ComputeHash(dirBuffer);

                    newFile.Seek(0x31, SeekOrigin.Begin);

                    newFile.Write(dirCheckSum, 0, dirCheckSum.Length);
                }
            }

            return tmpPath;
        }

        protected string ReplaceRebuildV1(EdataContentFile oldFile, byte[] newContent)
        {
            //var reserveBuffer = new byte[200]; // RUSE doesn't like the reserve buffer between files.

            var tmp = new FileInfo(FilePath);

            var tmpPath = Path.Combine(tmp.DirectoryName, string.Format("{0}_{1}", tmp.FullName, "temp"));

            if (!File.Exists(tmpPath))
                using (File.Create(tmpPath)) { }

            using (var fs = new FileStream(FilePath, FileMode.Open))
            {
                using (var newFile = new FileStream(tmpPath, FileMode.Truncate))
                {
                    byte[] oldHead = new byte[Header.FileOffset];
                    fs.Read(oldHead, 0, (int)Header.FileOffset);
                    newFile.Write(oldHead, 0, oldHead.Length);

                    byte[] fileBuffer;
                    uint filesContentLength = 0;

                    // Write Filecontent and replace selected file
                    foreach (var file in Files)
                    {
                        long oldOffset = file.Offset;
                        file.Offset = newFile.Position - Header.FileOffset;

                        if (file == oldFile)
                        {
                            fileBuffer = newContent;
                            file.Size = newContent.Length;
                        }
                        else
                        {
                            fileBuffer = new byte[file.Size];
                            fs.Seek(oldOffset + Header.FileOffset, SeekOrigin.Begin);
                            fs.Read(fileBuffer, 0, fileBuffer.Length);
                        }

                        newFile.Write(fileBuffer, 0, fileBuffer.Length);
                        //newFile.Write(reserveBuffer, 0, reserveBuffer.Length);

                        filesContentLength += (uint)fileBuffer.Length; // +(uint)reserveBuffer.Length;
                    }

                    newFile.Seek(Header.DictOffset, SeekOrigin.Begin);
                    long dirEnd = Header.DictOffset + Header.DictLength;
                    uint id = 0;

                    var buffer = new byte[4];

                    while (newFile.Position < dirEnd)
                    {
                        newFile.Read(buffer, 0, 4);
                        int fileGroupId = BitConverter.ToInt32(buffer, 0);

                        if (fileGroupId == 0)
                        {
                            EdataContentFile curFile = Files[(int)id];

                            // FileEntrySize
                            newFile.Seek(4, SeekOrigin.Current);

                            buffer = BitConverter.GetBytes((uint)curFile.Offset);
                            newFile.Write(buffer, 0, buffer.Length);

                            buffer = BitConverter.GetBytes((uint)curFile.Size);
                            newFile.Write(buffer, 0, buffer.Length);

                            newFile.Seek(1, SeekOrigin.Current);

                            string name = Utils.ReadString(newFile);

                            if ((name.Length + 1) % 2 == 0)
                                newFile.Seek(1, SeekOrigin.Current);

                            id++;
                        }
                        else if (fileGroupId > 0)
                        {
                            newFile.Seek(4, SeekOrigin.Current);
                            string name = Utils.ReadString(newFile);

                            if ((name.Length + 1) % 2 == 1)
                                newFile.Seek(1, SeekOrigin.Current);
                        }
                    }

                    newFile.Seek(Header.DictOffset, SeekOrigin.Begin);
                    var dirBuffer = new byte[Header.DictLength];
                    newFile.Read(dirBuffer, 0, dirBuffer.Length);
                    byte[] dirCheckSum = MD5.Create().ComputeHash(dirBuffer);

                    newFile.Seek(8, SeekOrigin.Begin);
                    newFile.Write(dirCheckSum, 0, dirCheckSum.Length);

                    newFile.Seek(13, SeekOrigin.Current);
                    newFile.Write(BitConverter.GetBytes(filesContentLength), 0, 4);
                }
            }

            return tmpPath;
        }

        /// <summary>
        /// Replaces a file in the current Edata package and saves the changes back.
        /// </summary>
        /// <param name="oldFile">The EdataFile object which is to be replaced.</param>
        /// <param name="newContent">The data of the new File including Header and content.</param>
        public virtual void ReplaceFile(EdataContentFile oldFile, byte[] newContent)
        {
            if (!File.Exists(FilePath))
                throw new InvalidOperationException("The Edata file does not exist anymore.");

            string newFile;

            switch (Header.Version)
            {
                case 1:
                    newFile = ReplaceRebuildV1(oldFile, newContent);
                    break;
                case 2:
                    newFile = ReplaceRebuildV2(oldFile, newContent);
                    break;
                default:
                    throw new NotSupportedException(string.Format("Edata Version {0} is currently not supported", Header.Version));
            }

            var oldFileInfo = new FileInfo(FilePath);

            var backupFile = Path.Combine(oldFileInfo.DirectoryName, "to_delete.dat");

            File.Move(FilePath, backupFile);
            File.Move(newFile, FilePath);

            File.Delete(backupFile);
        }

        protected void ResolveFileType(FileStream fs, EdataContentFile file)
        {
            // save original offset
            long origOffset = fs.Position;

            try
            {
                long absoluteOffset = checked((long)Header.FileOffset + file.Offset);
                if (absoluteOffset < 0 || absoluteOffset >= fs.Length)
                {
                    file.FileType = GetFileTypeFromFileName(file.Name);
                    return;
                }

                fs.Seek(absoluteOffset, SeekOrigin.Begin);

                var headerBuffer = new byte[12];
                int bytesToRead = (int)Math.Min(headerBuffer.Length, fs.Length - absoluteOffset);
                if (bytesToRead <= 0)
                {
                    file.FileType = GetFileTypeFromFileName(file.Name);
                    return;
                }

                fs.Read(headerBuffer, 0, bytesToRead);
                file.FileType = GetFileTypeFromHeaderData(headerBuffer);

                if (file.FileType == EdataFileType.Unknown)
                    file.FileType = GetFileTypeFromFileName(file.Name);
            }
            catch (IOException)
            {
                file.FileType = GetFileTypeFromFileName(file.Name);
            }
            catch (OverflowException)
            {
                file.FileType = GetFileTypeFromFileName(file.Name);
            }

            // set offset back to original
            fs.Seek(origOffset, SeekOrigin.Begin);
        }

        private static List<KeyValuePair<EdataFileType, byte[]>> _knownHeaders;

        public static IEnumerable<KeyValuePair<EdataFileType, byte[]>> KnownHeaders
        {
            get
            {
                return _knownHeaders;
            }
        }

        public static EdataFileType GetFileTypeFromHeaderData(byte[] headerData)
        {
            foreach (var knownHeader in KnownHeaders)
            {
                if (knownHeader.Value.Length < headerData.Length)
                {
                    var tmp = new byte[knownHeader.Value.Length];
                    Array.Copy(headerData, tmp, knownHeader.Value.Length);
                    //headerData = tmp;

                    if (Utils.ByteArrayCompare(tmp, knownHeader.Value))
                        return knownHeader.Key;
                }
                else
                    if (Utils.ByteArrayCompare(headerData, knownHeader.Value))
                        return knownHeader.Key;
            }

            return EdataFileType.Unknown;
        }

        public static EdataFileType GetFileTypeFromFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return EdataFileType.Unknown;

            string ext = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(ext))
                return EdataFileType.Unknown;

            ext = ext.ToLowerInvariant();

            switch (ext)
            {
                case ".tgv":
                    return EdataFileType.Image;
                case ".ndfbin":
                    return EdataFileType.Ndfbin;
                case ".dic":
                    return EdataFileType.Dictionary;
                case ".mesh":
                    return EdataFileType.Mesh;
                case ".scenario":
                case ".scen":
                    return EdataFileType.Scenario;
                case ".sav":
                    return EdataFileType.Save;
                case ".wmv":
                    return EdataFileType.Video;
                case ".dat":
                case ".edat":
                case ".apk":
                case ".cpk":
                case ".mpk":
                case ".spk":
                    return EdataFileType.Package;
                default:
                    return EdataFileType.Unknown;
            }
        }

        private static bool IsRecoverableV2DictionaryException(Exception ex)
        {
            return ex is IOException ||
                   ex is EndOfStreamException ||
                   ex is InvalidDataException ||
                   ex is OverflowException ||
                   ex is ArgumentOutOfRangeException;
        }

        private static byte[] ReadDictionaryBytes(Stream stream, int length, long dictionaryEnd, string fieldName)
        {
            if (stream.Position + length > dictionaryEnd)
                throw new InvalidDataException(string.Format("Cannot read {0} in EDAT dictionary. Position={1}, required={2}, dictionaryEnd={3}", fieldName, stream.Position, length, dictionaryEnd));

            var buffer = new byte[length];
            int read = stream.Read(buffer, 0, length);
            if (read != length)
                throw new EndOfStreamException(string.Format("Unexpected EOF while reading {0} from EDAT dictionary.", fieldName));

            return buffer;
        }

        private static string ReadDictionaryString(Stream stream, long dictionaryEnd)
        {
            var bytes = new List<byte>();

            while (stream.Position < dictionaryEnd)
            {
                int next = stream.ReadByte();
                if (next < 0)
                    throw new EndOfStreamException("Unexpected EOF while reading string from EDAT dictionary.");

                if (next == 0)
                    return Encoding.ASCII.GetString(bytes.ToArray());

                bytes.Add((byte)next);
            }

            throw new InvalidDataException("Unterminated string in EDAT dictionary.");
        }

        private static void SkipLegacyV2NamePadding(Stream stream, long dictionaryEnd, bool useLegacyNamePadding, string name)
        {
            if (!useLegacyNamePadding || name.Length % 2 != 0)
                return;

            if (stream.Position >= dictionaryEnd)
                throw new InvalidDataException("Expected a legacy V2 padding byte but reached end of dictionary.");

            stream.Seek(1, SeekOrigin.Current);
        }

        private bool ShouldUseLegacyV2Padding(string name)
        {
            return _v2UsesLegacyNamePadding && name.Length % 2 == 0;
        }

        private void ValidateV2FileEntry(EdataContentFile file, long packageLength)
        {
            if (file.Offset < 0)
                throw new InvalidDataException(string.Format("Invalid file offset '{0}' for '{1}'.", file.Offset, file.Path));

            if (file.Size < 0)
                throw new InvalidDataException(string.Format("Invalid file size '{0}' for '{1}'.", file.Size, file.Path));

            long absoluteOffset = checked((long)Header.FileOffset + file.Offset);
            if (absoluteOffset < 0 || absoluteOffset > packageLength)
                throw new InvalidDataException(string.Format("File '{0}' points outside package bounds. Absolute offset={1}, package size={2}.", file.Path, absoluteOffset, packageLength));

            if (file.Size > packageLength - absoluteOffset)
                throw new InvalidDataException(string.Format("File '{0}' size exceeds package bounds. Offset={1}, size={2}, package size={3}.", file.Path, absoluteOffset, file.Size, packageLength));
        }
    }
}
