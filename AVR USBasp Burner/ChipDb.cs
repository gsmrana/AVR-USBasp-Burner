using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace AVR_USBasp_Burner
{
    [Serializable()]
    [XmlRoot("ChipCollection")]
    public class ChipCollection
    {
        [XmlArray("AtmelChips")]
        [XmlArrayItem("Chip", typeof(Chip))]
        public List<Chip> Chips { get; set; }
    }

    [Serializable()]
    public class Chip
    {
        public string Name { get; set; }
        public int FlashSize { get; set; }
        public int EepromSize { get; set; }

        [XmlElement(ElementName = "Signature")]
        public string SignatureHex { get; set; }
        [XmlIgnore]
        public int Signature
        {
            get { return int.Parse(SignatureHex, NumberStyles.AllowHexSpecifier); }
            set { SignatureHex = string.Format("{0:X6}", value); }
        }

        public int PageSize { get; set; }
        public bool LFUSE { get; set; }
        public bool HFUSE { get; set; }
        public bool EFUSE { get; set; }
        public bool LockBits { get; set; }
        public bool CalibBits { get; set; }
        public int PinCount { get; set; }
    }

    public class ChipDb
    {
        #region Data and Property

        private ChipCollection _chipCollection;

        public  IEnumerable<Chip> Chips
        {
            get { return _chipCollection.Chips; }
        }

        #endregion

        #region Public Method

        public void Load(string filename)
        {
            var serializer = new XmlSerializer(typeof(ChipCollection));
            var reader = new StreamReader(filename);
            _chipCollection = (ChipCollection)serializer.Deserialize(reader);
            reader.Close();          
        }

        public void Add(Chip newchip)
        {
            if (_chipCollection.Chips.SingleOrDefault(c => c.Name == newchip.Name) != null)
                throw new Exception("Chip Name Already Exist!");

            if (_chipCollection.Chips.SingleOrDefault(c => c.Signature == newchip.Signature) != null)
                throw new Exception("Chip Signature Already Exist!");

            _chipCollection.Chips.Add(newchip);
        }

        public void Remove(Chip chip)
        {
            _chipCollection.Chips.Remove(chip);
        }

        public void Save(string filename)
        {
            var serializer = new XmlSerializer(typeof(ChipCollection));
            var writer = new StreamWriter(filename);
            serializer.Serialize(writer, _chipCollection);
            writer.Close();
        }

        #endregion

    }



}
