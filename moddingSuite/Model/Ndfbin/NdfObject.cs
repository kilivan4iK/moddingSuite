using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using moddingSuite.BL;
using moddingSuite.BL.Ndf;
using moddingSuite.Model.Ndfbin.Types;
using moddingSuite.ViewModel.Base;

namespace moddingSuite.Model.Ndfbin
{
    public class NdfObject : ViewModelBase, INdfScriptSerializable
    {
        private readonly ObservableCollection<NdfPropertyValue> _propertyValues =
            new ObservableCollection<NdfPropertyValue>();

        private NdfClass _class;
        private byte[] _data;
        private uint _id;
        private bool _isTopObject;
        public string Name
        {
            get { return $"{Id}"; }
        }
        public NdfClass Class
        {
            get { return _class; }
            set
            {
                _class = value;
                OnPropertyChanged("Class");
            }
        }

        public byte[] Data
        {
            get { return _data; }
            set
            {
                _data = value;
                OnPropertyChanged("Data");
            }
        }

        public ObservableCollection<NdfPropertyValue> PropertyValues
        {
            get { return _propertyValues; }
        }

        public uint Id
        {
            get { return _id; }
            set
            {
                _id = value;
                OnPropertyChanged("Name");
            }
        }

        public bool IsTopObject
        {
            get { return _isTopObject; }
            set
            {
                _isTopObject = value;
                OnPropertyChanged("IsTopObject");
            }
        }

        public long Offset { get; set; }

        #region INdfScriptSerializable Members

        public byte[] GetNdfText()
        {
            Encoding enc = NdfTextWriter.NdfTextEncoding;

            using (var ms = new MemoryStream())
            {
                byte[] buffer =
                    enc.GetBytes(string.Format("{0} is {1}\n", NdfTextWriter.GetObjectName(Id), Class.Name));

                ms.Write(buffer, 0, buffer.Length);
                buffer = enc.GetBytes("(\n");
                ms.Write(buffer, 0, buffer.Length);

                var propBuff = new List<byte>();

                foreach (NdfPropertyValue propVal in PropertyValues)
                {
                    if (propVal.Type == NdfType.Unset)
                        continue;

                    propBuff.AddRange(enc.GetBytes(string.Format("{0} = ", propVal.Property.Name)));
                    byte[] valueBuffer = propVal.Value.GetNdfText();
                    propBuff.AddRange(valueBuffer);

                    if (!EndsWithLineBreak(valueBuffer))
                        propBuff.AddRange(enc.GetBytes("\n"));

                    buffer = propBuff.ToArray();
                    propBuff.Clear();

                    ms.Write(buffer, 0, buffer.Length);
                }

                buffer = enc.GetBytes(")\n");
                ms.Write(buffer, 0, buffer.Length);

                return ms.ToArray();
            }
        }

        #endregion

        private static bool EndsWithLineBreak(byte[] data)
        {
            if (data == null || data.Length == 0)
                return false;

            int len = data.Length;
            if (data[len - 1] == (byte)'\n')
                return true;

            if (data[len - 1] == 0 && len >= 2 && data[len - 2] == (byte) '\n')
                return true;

            if (data.Length >= 4 &&
                data[len - 4] == (byte) '\r' &&
                data[len - 3] == 0 &&
                data[len - 2] == (byte) '\n' &&
                data[len - 1] == 0)
            {
                return true;
            }

            return false;
        }
    }
}
