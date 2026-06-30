using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using WoTMapImporter.Editor.Utils;

namespace WoTMapImporter.Editor.Mesh
{
    /// <summary>
    /// Parses WoT .primitives_processed (BigWorld mesh format).
    /// Ported to match Simi4/WoT-Blender-Addons' LoadDataMesh_v2:
    ///   - uses the exact vertices/indices data-section names from BSMO;
    ///   - cuts meshes by primitiveGroup instead of importing the whole file;
    ///   - supports uv2 side sections used by PBS_tiled/PBS_tiled_atlas materials;
    ///   - uses the real vertex strides from vertex_types.py.
    /// </summary>
    public static class MeshDataDecoder
    {
        public const uint MAGIC = 0x42A14E65;

        public struct Section
        {
            public string Name;
            public long Position;
            public int Length;
        }

        public struct PrimitiveGroup
        {
            public int StartIndex;
            public int PrimitiveCount;
            public int StartVertex;
            public int VertexCount;
        }

        public class DecodedMesh
        {
            public Vector3[] Positions;
            public Vector2[] Uv;
            public Vector2[] Uv2;
            public int[] Indices;
            public PrimitiveGroup[] PrimitiveGroups;
            public string VertexFormat;
            public string VerticesSection;
            public string IndicesSection;
        }

        /// <summary>
        /// Reads packed section table at end of file. Returns a list of (name, position, length).
        /// </summary>
        public static List<Section> ReadSections(BinaryReader br)
        {
            var result = new List<Section>();
            br.BaseStream.Seek(-4, SeekOrigin.End);
            int tableStart = br.ReadInt32();
            br.BaseStream.Seek(-4 - tableStart, SeekOrigin.End);

            long position = 4;
            while (br.BaseStream.Position < br.BaseStream.Length)
            {
                if (br.BaseStream.Length - br.BaseStream.Position < 4) break;
                int sectionSize = br.ReadInt32();
                if (sectionSize < 0) break;
                if (br.BaseStream.Length - br.BaseStream.Position < 16) break;
                br.ReadBytes(16); // reserved / padding
                if (br.BaseStream.Length - br.BaseStream.Position < 4) break;
                int nameLen = br.ReadInt32();
                if (nameLen <= 0 || nameLen > 1024 || br.BaseStream.Length - br.BaseStream.Position < nameLen)
                    break;
                string name = System.Text.Encoding.UTF8.GetString(br.ReadBytes(nameLen));
                result.Add(new Section { Name = name, Position = position, Length = sectionSize });

                position += sectionSize;
                if (sectionSize % 4 > 0) position += 4 - (sectionSize % 4);
                if (nameLen % 4 > 0)
                {
                    int pad = 4 - (nameLen % 4);
                    if (br.BaseStream.Length - br.BaseStream.Position >= pad)
                        br.ReadBytes(pad);
                }
            }
            return result;
        }

        public static DecodedMesh Decode(byte[] data)
        {
            return Decode(data, null, null, -1);
        }

        public static DecodedMesh Decode(
            byte[] data,
            string verticesSectionName,
            string indicesSectionName,
            int primitiveGroupIndex)
        {
            using var ms = new MemoryStream(data, false);
            using var br = new BinaryReader(ms);
            uint magic = br.ReadUInt32();
            if (magic != MAGIC) throw new Exception($"Not a WoT primitives file (magic {magic:X8})");

            var sections = ReadSections(br);

            Section? indicesSec = FindSection(sections, indicesSectionName, "indices");
            Section? verticesSec = FindSection(sections, verticesSectionName, "vertices");
            if (indicesSec == null || verticesSec == null)
                throw new Exception($"Missing indices/vertices section (vertices='{verticesSectionName}', indices='{indicesSectionName}')");

            var (allIndices, groups) = ReadIndices(br, indicesSec.Value);
            var (positions, uvs, vertexFormat) = ReadVertices(br, verticesSec.Value);
            Vector2[] uv2 = ReadUv2IfPresent(br, sections, verticesSec.Value.Name, positions.Length);

            int[] indices = allIndices;
            if (primitiveGroupIndex >= 0)
            {
                if (primitiveGroupIndex >= groups.Length)
                    throw new Exception($"Primitive group {primitiveGroupIndex} out of range (groups={groups.Length})");
                CutPrimitiveGroup(groups[primitiveGroupIndex], allIndices, positions, uvs, uv2,
                                  out positions, out uvs, out uv2, out indices);
            }
            else
            {
                // Full-mesh import still uses the BigWorld -> Unity winding swap.
                SwapWinding(indices);
            }

            return new DecodedMesh
            {
                Positions = positions,
                Uv = uvs,
                Uv2 = uv2,
                Indices = indices,
                PrimitiveGroups = groups,
                VertexFormat = vertexFormat,
                VerticesSection = verticesSec.Value.Name,
                IndicesSection = indicesSec.Value.Name,
            };
        }

        private static Section? FindSection(List<Section> sections, string requested, string fallbackContains)
        {
            if (!string.IsNullOrEmpty(requested))
            {
                foreach (var s in sections)
                    if (string.Equals(s.Name, requested, StringComparison.OrdinalIgnoreCase))
                        return s;

                // Some rows contain a full "file/section" path.  The primitive file
                // table contains only the section suffix; keep the same forgiving
                // behaviour as the Blender code's SplitName().
                string suffix = requested.Replace('\\', '/');
                int slash = suffix.LastIndexOf('/');
                if (slash >= 0) suffix = suffix.Substring(slash + 1);
                foreach (var s in sections)
                    if (string.Equals(s.Name, suffix, StringComparison.OrdinalIgnoreCase))
                        return s;
            }

            foreach (var s in sections)
                if (s.Name.IndexOf(fallbackContains, StringComparison.OrdinalIgnoreCase) >= 0)
                    return s;
            return null;
        }

        private static (int[] indices, PrimitiveGroup[] groups) ReadIndices(BinaryReader br, Section sec)
        {
            br.BaseStream.Seek(sec.Position, SeekOrigin.Begin);
            string indexFmt = ReadCString(br, 64);
            int nIndices = br.ReadInt32();
            int nTriangleGroups = br.ReadUInt16();

            int uintSize;
            if (indexFmt == "list") uintSize = 2;
            else if (indexFmt == "list32") uintSize = 4;
            else if (indexFmt.StartsWith("list32", StringComparison.OrdinalIgnoreCase)) uintSize = 4;
            else if (indexFmt.StartsWith("list", StringComparison.OrdinalIgnoreCase)) uintSize = 2;
            else throw new Exception($"Unsupported index format: {indexFmt}");

            int groupOffset = nIndices * uintSize + 72;
            br.BaseStream.Seek(sec.Position + groupOffset, SeekOrigin.Begin);
            var groups = new PrimitiveGroup[nTriangleGroups];
            for (int i = 0; i < nTriangleGroups; i++)
            {
                groups[i] = new PrimitiveGroup
                {
                    StartIndex = br.ReadInt32(),
                    PrimitiveCount = br.ReadInt32(),
                    StartVertex = br.ReadInt32(),
                    VertexCount = br.ReadInt32(),
                };
            }

            br.BaseStream.Seek(sec.Position + 72, SeekOrigin.Begin);
            byte[] rawIndices = br.ReadBytes(nIndices * uintSize);
            if (rawIndices.Length < nIndices * uintSize)
                throw new Exception("Truncated index buffer");

            var indices = new int[nIndices];
            if (uintSize == 2)
            {
                for (int i = 0; i < nIndices; i++) indices[i] = BitConverter.ToUInt16(rawIndices, i * 2);
            }
            else
            {
                for (int i = 0; i < nIndices; i++) indices[i] = BitConverter.ToInt32(rawIndices, i * 4);
            }
            return (indices, groups);
        }

        private static (Vector3[] positions, Vector2[] uvs, string vertexFormat) ReadVertices(BinaryReader br, Section sec)
        {
            br.BaseStream.Seek(sec.Position, SeekOrigin.Begin);
            string headerOrFormat = ReadCString(br, 64);
            string vertexFormat = headerOrFormat;
            if (headerOrFormat.Contains("BPVT"))
            {
                br.ReadInt32(); // version
                vertexFormat = ReadCString(br, 64);
            }

            int vertexCount = br.ReadInt32();
            var vf = ResolveVertexFormat(vertexFormat, headerOrFormat);
            byte[] raw = br.ReadBytes(vertexCount * vf.Stride);
            if (raw.Length < vertexCount * vf.Stride)
                throw new Exception($"Truncated vertex buffer ({vertexFormat}, stride={vf.Stride}, count={vertexCount})");

            var positions = new Vector3[vertexCount];
            var uvs = new Vector2[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                int off = i * vf.Stride;
                positions[i] = new Vector3(
                    BitConverter.ToSingle(raw, off + 0),
                    BitConverter.ToSingle(raw, off + 4),
                    BitConverter.ToSingle(raw, off + 8));
                uvs[i] = new Vector2(
                    BitConverter.ToSingle(raw, off + vf.UvOffset),
                    1f - BitConverter.ToSingle(raw, off + vf.UvOffset + 4));
            }
            return (positions, uvs, vertexFormat);
        }

        private struct VertexFormatInfo
        {
            public int Stride;
            public int UvOffset;
        }

        private static VertexFormatInfo ResolveVertexFormat(string vertexFormat, string headerOrFormat)
        {
            string vf = (vertexFormat ?? string.Empty).ToLowerInvariant();
            string hs = (headerOrFormat ?? string.Empty).ToLowerInvariant();
            string all = vf + "|" + hs;

            // Values are from Simi4/WoT-Blender-Addons/tank_viewer/vertex_types.py.
            if (all.Contains("set3/xyznuviiiwwtbpc")) return new VertexFormatInfo { Stride = 40, UvOffset = 16 };
            if (all.Contains("set3/xyznuvtbpc")) return new VertexFormatInfo { Stride = 32, UvOffset = 16 };
            if (all.Contains("set3/xyznuvpc")) return new VertexFormatInfo { Stride = 24, UvOffset = 16 };
            if (all.Contains("xyznuviiiwwtb")) return new VertexFormatInfo { Stride = 37, UvOffset = 16 };
            if (all.Contains("xyznuvtb")) return new VertexFormatInfo { Stride = 32, UvOffset = 16 };
            if (all.Contains("xyznuv")) return new VertexFormatInfo { Stride = 32, UvOffset = 24 };

            throw new Exception($"Unsupported vertex format: '{vertexFormat}' (header='{headerOrFormat}')");
        }

        private static Vector2[] ReadUv2IfPresent(
            BinaryReader br, List<Section> sections, string verticesSectionName, int vertexCount)
        {
            var uv2Candidates = new List<string>();
            if (!string.IsNullOrEmpty(verticesSectionName))
            {
                // Exact rule from WoT-Blender-Addons map_viewer/LoadDataMesh_v2.py:
                // uv2_name = vertices_name[:-8] + 'uv2'
                if (verticesSectionName.Length >= 8)
                    uv2Candidates.Add(verticesSectionName.Substring(0, verticesSectionName.Length - 8) + "uv2");

                // Be more defensive for assets whose stream is named vertices0,
                // lod0_vertices, etc.  Never blindly take the first uv2 section if
                // several exist, because that gives a valid but wrong UV map.
                int idx = verticesSectionName.LastIndexOf("vertices", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                    uv2Candidates.Add(verticesSectionName.Substring(0, idx) + "uv2" + verticesSectionName.Substring(idx + "vertices".Length));
            }

            Section? uv2Sec = null;
            foreach (string candidate in uv2Candidates)
            {
                foreach (var s in sections)
                    if (string.Equals(s.Name, candidate, StringComparison.OrdinalIgnoreCase))
                    { uv2Sec = s; break; }
                if (uv2Sec != null) break;
            }

            if (uv2Sec == null)
            {
                Section? onlyUv2 = null;
                int uv2Count = 0;
                foreach (var s in sections)
                {
                    if (s.Name.IndexOf("uv2", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    onlyUv2 = s;
                    uv2Count++;
                }
                if (uv2Count == 1) uv2Sec = onlyUv2;
            }
            if (uv2Sec == null) return null;

            try
            {
                br.BaseStream.Seek(uv2Sec.Value.Position, SeekOrigin.Begin);
                string headerOrFormat = ReadCString(br, 64);
                int count;
                bool processed = headerOrFormat.Contains("BPVS");
                if (processed)
                {
                    br.ReadInt32(); // version
                    string uv2Format = ReadCString(br, 64);
                    count = br.ReadInt32();
                    if (!uv2Format.Equals("set3/uv2pc", StringComparison.OrdinalIgnoreCase))
                    {
                        WoTLogger.Warn($"Unsupported uv2 format: {uv2Format}");
                        return null;
                    }
                }
                else
                {
                    // Unprocessed uv2 section is raw float2 data without header.
                    br.BaseStream.Seek(uv2Sec.Value.Position, SeekOrigin.Begin);
                    count = uv2Sec.Value.Length / 8;
                }

                int n = Math.Min(count, vertexCount);
                var uv2 = new Vector2[vertexCount];
                for (int i = 0; i < n; i++)
                {
                    float u = br.ReadSingle();
                    float v = br.ReadSingle();
                    uv2[i] = new Vector2(u, 1f - v);
                }
                return uv2;
            }
            catch (Exception e)
            {
                WoTLogger.Warn($"Failed to read uv2 section '{uv2Sec.Value.Name}': {e.Message}");
                return null;
            }
        }

        private static void CutPrimitiveGroup(
            PrimitiveGroup pg,
            int[] allIndices,
            Vector3[] allPositions,
            Vector2[] allUv,
            Vector2[] allUv2,
            out Vector3[] positions,
            out Vector2[] uv,
            out Vector2[] uv2,
            out int[] indices)
        {
            if (pg.StartVertex < 0 || pg.VertexCount < 0 || pg.StartVertex + pg.VertexCount > allPositions.Length)
                throw new Exception($"Invalid primitive-group vertex range start={pg.StartVertex} count={pg.VertexCount} vertices={allPositions.Length}");

            positions = new Vector3[pg.VertexCount];
            uv = new Vector2[pg.VertexCount];
            Array.Copy(allPositions, pg.StartVertex, positions, 0, pg.VertexCount);
            Array.Copy(allUv, pg.StartVertex, uv, 0, pg.VertexCount);
            if (allUv2 != null)
            {
                uv2 = new Vector2[pg.VertexCount];
                Array.Copy(allUv2, pg.StartVertex, uv2, 0, pg.VertexCount);
            }
            else uv2 = null;

            int indexCount = pg.PrimitiveCount * 3;
            if (pg.StartIndex < 0 || indexCount < 0 || pg.StartIndex + indexCount > allIndices.Length)
                throw new Exception($"Invalid primitive-group index range start={pg.StartIndex} count={indexCount} indices={allIndices.Length}");

            indices = new int[indexCount];
            for (int i = 0; i < indexCount; i++)
                indices[i] = allIndices[pg.StartIndex + i] - pg.StartVertex;
            SwapWinding(indices);
        }

        private static void SwapWinding(int[] indices)
        {
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int tmp = indices[i];
                indices[i] = indices[i + 2];
                indices[i + 2] = tmp;
            }
        }

        private static string ReadCString(BinaryReader br, int maxLen)
        {
            var bytes = br.ReadBytes(maxLen);
            int nullIdx = Array.IndexOf<byte>(bytes, 0);
            if (nullIdx < 0) nullIdx = bytes.Length;
            return System.Text.Encoding.UTF8.GetString(bytes, 0, nullIdx);
        }
    }
}
