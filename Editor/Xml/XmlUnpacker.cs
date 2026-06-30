using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace WoTMapImporter.Editor.Xml
{
    /// <summary>
    /// Packed/plain BigWorld XML reader. Supports old WoT 0.6.x .chunk/.visual files.
    /// Important: BigWorld packed XML section layout is:
    /// [children_count][own_descriptor][child_descriptors][own_data][child_data...]
    /// </summary>
    public static class XmlUnpacker
    {
        public const uint PACKED_HEADER = 0x62a14e45;
        private const uint END_MASK = 0x0FFFFFFF;

        public static XmlDocument Read(Stream stream)
        {
            stream.Position = 0;
            var br = new BinaryReader(stream, Encoding.UTF8);

            uint header;
            try { header = br.ReadUInt32(); }
            catch { return null; }

            var doc = new XmlDocument();

            if (header == PACKED_HEADER)
            {
                // Header has 4 bytes magic + 1 byte sentinel/version before dictionary.
                stream.Position = 5;
                var dict = ReadDictionary(stream);
                var root = doc.CreateElement("root");
                doc.AppendChild(root);
                ReadElement(stream, br, dict, root);
            }
            else
            {
                stream.Position = 0;
                using var sr = new StreamReader(stream, Encoding.UTF8, true);
                doc.LoadXml(sr.ReadToEnd());
            }
            return doc;
        }

        public static XmlDocument ReadBytes(byte[] data)
        {
            using var ms = new MemoryStream(data, false);
            return Read(ms);
        }

        private static string[] ReadDictionary(Stream s)
        {
            var dict = new List<string>();
            while (true)
            {
                var sb = new StringBuilder();
                int b;
                while ((b = s.ReadByte()) != -1 && b != 0)
                    sb.Append((char)b);

                if (b == -1) break;
                if (sb.Length == 0) break;
                dict.Add(sb.ToString());
            }
            return dict.ToArray();
        }

        private struct Desc
        {
            public int Type;
            public uint End;
            public long Address;
        }

        private static Desc ReadDesc(Stream s, BinaryReader br)
        {
            uint et = br.ReadUInt32();
            return new Desc
            {
                Type = (int)((et >> 28) & 0xF),
                End = et & END_MASK,
                Address = s.Position,
            };
        }

        private static void ReadElement(Stream s, BinaryReader br, string[] dict, XmlElement parent)
        {
            if (s.Length - s.Position < 6)
                throw new Exception($"XMLUNPACKER_V2: malformed packed XML at {s.Position}/{s.Length}, parent={parent.LocalName}");

            ushort childCount = br.ReadUInt16();
            Desc ownDesc = ReadDesc(s, br);

            var children = new (ushort nameIdx, Desc desc)[childCount];
            for (int i = 0; i < childCount; i++)
            {
                ushort nameIdx = br.ReadUInt16();
                Desc d = ReadDesc(s, br);
                children[i] = (nameIdx, d);
            }

            long dataBase = ownDesc.Address + childCount * 6L;

            // First read this element's own data. This is required for old .visual:
            // <primitiveGroup> has own text index plus child <material>.
            s.Position = dataBase;
            uint offset = ReadDataBlock(s, br, dict, parent, ownDesc, 0);

            for (int i = 0; i < childCount; i++)
            {
                var (nameIdx, desc) = children[i];
                string name = (dict != null && nameIdx < dict.Length) ? dict[nameIdx] : "unknown";
                var elem = parent.OwnerDocument.CreateElement(name);
                parent.AppendChild(elem);

                s.Position = dataBase + offset;
                offset = ReadDataBlock(s, br, dict, elem, desc, offset);
            }

            s.Position = dataBase + offset;
        }

        private static uint ReadDataBlock(Stream s, BinaryReader br, string[] dict, XmlElement elem, Desc desc, uint previousOffset)
        {
            if (desc.End < previousOffset)
                throw new Exception($"XMLUNPACKER_V2: bad descriptor offsets end={desc.End}, previous={previousOffset}, type={desc.Type}, elem={elem.LocalName}");

            uint length = desc.End - previousOffset;
            if (desc.Type == 0)
            {
                ReadElement(s, br, dict, elem);
                return desc.End;
            }

            ReadDataValue(s, br, elem, desc.Type, length);
            return desc.End;
        }

        private static void ReadDataValue(Stream s, BinaryReader br, XmlElement elem, int type, uint length)
        {
            switch (type)
            {
                case 1:
                    elem.InnerText = ReadString(s, (int)length);
                    break;

                case 2:
                    elem.InnerText = ReadNumber(s, br, (int)length).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;

                case 3:
                {
                    int n = (int)length / 4;
                    if (length % 4 != 0)
                        throw new Exception($"XMLUNPACKER_V2: bad float length {length} in <{elem.LocalName}>");

                    if (n == 12)
                    {
                        var vals = new float[n];
                        for (int i = 0; i < n; i++) vals[i] = br.ReadSingle();
                        for (int row = 0; row < 4; row++)
                        {
                            var rowElem = elem.OwnerDocument.CreateElement($"row{row}");
                            rowElem.InnerText = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "{0} {1} {2}", vals[row  *3], vals[row*  3 + 1], vals[row * 3 + 2]);
                            elem.AppendChild(rowElem);
                        }
                    }
                    else
                    {
                        var sb = new StringBuilder();
                        for (int i = 0; i < n; i++)
                        {
                            if (i > 0) sb.Append(' ');
                            sb.Append(br.ReadSingle().ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                        }
                        elem.InnerText = sb.ToString();
                    }
                    break;
                }

                case 4:
                    elem.InnerText = length == 0 ? "false" : (br.ReadByte() == 1 ? "true" : "false");
                    break;

                case 5:
                    elem.InnerText = Convert.ToBase64String(br.ReadBytes((int)length));
                    break;

                default:
                    throw new Exception($"XMLUNPACKER_V2: unknown element type {type}, length={length}, elem=<{elem.LocalName}>");
            }
        }

        private static string ReadString(Stream s, int length)
        {
            if (length <= 0) return string.Empty;
            var buf = new byte[length];
            int read = s.Read(buf, 0, length);
            return Encoding.UTF8.GetString(buf, 0, read);
        }

        private static long ReadNumber(Stream s, BinaryReader br, int length)
        {
            if (length == 0) return 0;
            switch (length)
            {
                case 1: return br.ReadSByte();
                case 2: return br.ReadInt16();
                case 4: return br.ReadInt32();
                case 8: return br.ReadInt64();
                default:
                    throw new Exception($"XMLUNPACKER_V2: Unknown number length: {length} at stream={s.Position}, remaining={s.Length - s.Position}");
            }
        }

        public static string GetText(XmlElement e, string xpath, string fallback = "")
        {
            var node = e.SelectSingleNode(xpath);
            if (node == null) return fallback;
            return node.InnerText ?? fallback;
        }

        public static IEnumerable<XmlElement> GetChildren(XmlElement e, string name)
        {
            foreach (XmlNode n in e.ChildNodes)
                if (n is XmlElement xe && xe.LocalName == name)
                    yield return xe;
        }
    }
}
