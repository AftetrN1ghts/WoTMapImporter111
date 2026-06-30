using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using WoTMapImporter.Editor.Image;
using WoTMapImporter.Editor.Package;
using WoTMapImporter.Editor.Utils;

namespace WoTMapImporter.Editor.Vegetation
{
    /// <summary>
    /// Minimal SpeedTree Runtime (*.srt) decoder for WoT vegetation.
    ///
    /// Scope of this first implementation:
    ///   - SRT 06.0.0 and SRT 07.0.0 headers;
    ///   - static 3D geometry (positions, normals, UV0, indices);
    ///   - LOD hierarchy;
    ///   - diffuse DDS material lookup via render-state texture string refs;
    ///   - no wind animation yet, no collision, billboard LOD rendering yet.
    ///
    /// The format is proprietary and game builds differ.  The decoder is deliberately
    /// defensive: on any unsupported layout it returns null and the caller keeps the
    /// placement placeholder instead of breaking the whole map import.
    /// </summary>
    public static class SrtMeshDecoder
    {
        private const int WindDataSize = 1308;
        private const int AdditionalV6Size = 31;
        private const int RenderStateV6Size = 680;
        private const int RenderStateV7Size = 804;
        private const int DrawCallSize = 40;
        private const int LodTableEntrySize = 24;
        private const int BoneSize = 48;
        private const int CollisionObjectSize = 36;
        private const int VfDescOffset = 33;
        private const int VfDescSize = 13;
        private const int StrideByteOffsetV6 = 663;
        private const int HorizontalBillboardSize = 84;

        private enum CoordSystem : byte
        {
            YUpRight = 0,
            ZUpRight = 1,
            YUpLeft = 2,
            ZUpLeft = 3,
        }

        public sealed class ImportResult
        {
            public GameObject Prefab;
            public readonly List<string> Warnings = new List<string>();
        }

        private sealed class DecodedSrt
        {
            public int Version;
            public CoordSystem CoordSystem;
            public bool TexcoordsFlipped;
            public List<string> Strings = new List<string>();
            public List<byte[]> RenderStates = new List<byte[]>();
            public List<LodInfo> Lods = new List<LodInfo>();
            public List<MeshPart> Meshes = new List<MeshPart>();
        }

        private sealed class LodInfo
        {
            public int LodIndex;
            public List<DrawCallInfo> DrawCalls = new List<DrawCallInfo>();
        }

        private sealed class DrawCallInfo
        {
            public int LodIndex;
            public int GeomIndex;
            public int RenderStateIndex;
            public int NumVertices;
            public int NumIndices;
            public bool Index32;
        }

        private sealed class MeshPart
        {
            public int LodIndex;
            public int GeomIndex;
            public int RenderStateIndex;
            public Vector3[] Positions;
            public Vector3[] Normals;
            public Vector2[] Uv;
            public int[] Indices;
        }

        private sealed class Reader
        {
            private readonly byte[] _data;
            private int _pos;
            private bool _bigEndian;

            public int Position { get => _pos; set => _pos = value; }
            public int Length => _data.Length;
            public bool BigEndian { get => _bigEndian; set => _bigEndian = value; }

            public Reader(byte[] data)
            {
                _data = data ?? Array.Empty<byte>();
            }

            public byte[] ReadBytes(int count)
            {
                if (count < 0 || _pos + count > _data.Length)
                    throw new EndOfStreamException($"SRT read past EOF at {_pos}, size={count}, len={_data.Length}");
                var b = new byte[count];
                Buffer.BlockCopy(_data, _pos, b, 0, count);
                _pos += count;
                return b;
            }

            public byte ReadByte()
            {
                if (_pos >= _data.Length) throw new EndOfStreamException("SRT read past EOF");
                return _data[_pos++];
            }

            public uint ReadUInt32()
            {
                var v = ReadUInt32At(_pos, _bigEndian);
                _pos += 4;
                return v;
            }

            public int ReadInt32() => unchecked((int)ReadUInt32());

            public ushort ReadUInt16()
            {
                if (_pos + 2 > _data.Length) throw new EndOfStreamException("SRT read past EOF");
                ushort v = _bigEndian
                    ? (ushort)((_data[_pos] << 8) | _data[_pos + 1])
                    : (ushort)(_data[_pos] | (_data[_pos + 1] << 8));
                _pos += 2;
                return v;
            }

            public float ReadSingle()
            {
                uint u = ReadUInt32();
                return BitConverter.ToSingle(BitConverter.GetBytes(u), 0);
            }

            public void Skip(int count)
            {
                if (count < 0 || _pos + count > _data.Length)
                    throw new EndOfStreamException($"SRT skip past EOF at {_pos}, size={count}, len={_data.Length}");
                _pos += count;
            }

            public void Align4()
            {
                while ((_pos & 3) != 0 && _pos < _data.Length) _pos++;
            }

            public uint ReadUInt32At(int offset) => ReadUInt32At(offset, _bigEndian);

            public float ReadSingleAt(int offset)
            {
                uint u = ReadUInt32At(offset, _bigEndian);
                return BitConverter.ToSingle(BitConverter.GetBytes(u), 0);
            }

            public byte ByteAt(int offset)
            {
                if (offset < 0 || offset >= _data.Length) return 0;
                return _data[offset];
            }

            public byte[] Slice(int offset, int length)
            {
                if (offset < 0 || length < 0 || offset + length > _data.Length)
                    return Array.Empty<byte>();
                var b = new byte[length];
                Buffer.BlockCopy(_data, offset, b, 0, length);
                return b;
            }

            private uint ReadUInt32At(int offset, bool bigEndian)
            {
                if (offset < 0 || offset + 4 > _data.Length) throw new EndOfStreamException("SRT read past EOF");
                if (bigEndian)
                {
                    return ((uint)_data[offset] << 24) |
                           ((uint)_data[offset + 1] << 16) |
                           ((uint)_data[offset + 2] << 8) |
                           _data[offset + 3];
                }
                return _data[offset] |
                       ((uint)_data[offset + 1] << 8) |
                       ((uint)_data[offset + 2] << 16) |
                       ((uint)_data[offset + 3] << 24);
            }
        }

        public static bool IsSrt(byte[] data)
        {
            if (data == null || data.Length < 16) return false;
            string h = Encoding.ASCII.GetString(data, 0, 16).TrimEnd('\0', ' ');
            return h == "SRT 06.0.0" || h == "SRT 07.0.0";
        }

        public static ImportResult ImportToPrefab(
            string outputPath,
            string resourceName,
            byte[] data,
            WoTPackageManager resMgr)
        {
            var result = new ImportResult();
            try
            {
                var decoded = Decode(data);
                if (decoded == null || decoded.Meshes.Count == 0)
                {
                    result.Warnings.Add("SRT decoded no 3D meshes: " + resourceName);
                    return result;
                }

                WoTLogger.Info($"SRT decoder v4: {resourceName}, meshes={decoded.Meshes.Count}, lods={decoded.Lods.Count}, renderStates={decoded.RenderStates.Count}, strings={decoded.Strings.Count}");
                result.Prefab = BuildPrefab(outputPath, resourceName, decoded, resMgr, result.Warnings);
                if (result.Prefab == null)
                    result.Warnings.Add("SRT prefab build failed: " + resourceName);
            }
            catch (Exception e)
            {
                result.Warnings.Add($"SRT decode failed ({resourceName}): {e.Message}");
                WoTLogger.Warn($"SRT decode failed ({resourceName}): {e.Message}\n{e.StackTrace}");
            }
            return result;
        }

        private static DecodedSrt Decode(byte[] data)
        {
            if (!IsSrt(data)) return null;
            var r = new Reader(data);
            var srt = new DecodedSrt();

            string header = Encoding.ASCII.GetString(r.ReadBytes(16)).TrimEnd('\0', ' ');
            srt.Version = header == "SRT 07.0.0" ? 7 : 6;

            byte endianByte = r.ReadByte();
            srt.CoordSystem = (CoordSystem)r.ReadByte();
            srt.TexcoordsFlipped = r.ReadByte() == 1;
            r.ReadByte(); // reserved
            r.BigEndian = endianByte != 0;

            // Extents + LOD profile.
            r.Skip(6 * 4);
            r.Skip(4 + 4 * 4);

            r.Skip(WindDataSize);
            if (srt.Version == 6)
            {
                r.Skip(AdditionalV6Size);
                r.Align4();
            }

            ParseStringTable(r, srt);
            ParseCollisionObjects(r);
            ParseBillboards(r);
            ParseCustomData(r);
            ParseRenderStates(r, srt);
            ParseGeometryDescriptors(r, srt);
            ParseVertexIndexData(r, srt);

            return srt;
        }

        private static void ParseStringTable(Reader r, DecodedSrt srt)
        {
            if (srt.Version == 6)
            {
                r.Skip(4); // u32_0
                r.Skip(4); // u32_1
                r.Skip(4); // u32_2
                r.Skip(4); // f32_0
            }

            uint count = r.ReadUInt32();
            if (count > 10000 || r.Position + count * 8 > r.Length)
                throw new Exception("Invalid SRT string table count: " + count);

            var lengths = new List<uint>((int)count);
            for (uint i = 0; i < count; i++)
            {
                r.ReadUInt32(); // padded length / unused
                lengths.Add(r.ReadUInt32());
            }

            for (int i = 0; i < lengths.Count; i++)
            {
                uint len = lengths[i];
                if (len > 1_000_000 || r.Position + len > r.Length)
                    throw new Exception("SRT string out of range");
                var bytes = r.ReadBytes((int)len);
                int actual = bytes.Length;
                while (actual > 0 && bytes[actual - 1] == 0) actual--;
                srt.Strings.Add(Encoding.UTF8.GetString(bytes, 0, actual).Replace('\\', '/'));
            }
            r.Align4();
        }

        private static void ParseCollisionObjects(Reader r)
        {
            uint count = r.ReadUInt32();
            if (count > 1000 || r.Position + count * CollisionObjectSize > r.Length)
                throw new Exception("Invalid SRT collision object count: " + count);
            r.Skip((int)count * CollisionObjectSize);
        }

        private static void ParseBillboards(Reader r)
        {
            r.Skip(4); // width
            r.Skip(4); // top
            r.Skip(4); // bottom
            uint numBillboards = r.ReadUInt32();
            if (numBillboards > 10000) throw new Exception("Invalid SRT billboard count: " + numBillboards);
            r.Skip((int)numBillboards * 4 * 4); // texcoords
            r.Skip((int)numBillboards); // rotated flags
            r.Align4();

            uint numCutoutVerts = r.ReadUInt32();
            uint numCutoutIndices = r.ReadUInt32();
            if (numCutoutVerts > 1_000_000 || numCutoutIndices > 3_000_000)
                throw new Exception("Invalid SRT billboard cutout counts");
            r.Skip((int)numCutoutVerts * 2 * 4);
            r.Skip((int)numCutoutIndices * 2);
            r.Align4();

            r.Skip(HorizontalBillboardSize);
        }

        private static void ParseCustomData(Reader r)
        {
            r.Skip(5 * 4);
        }

        private static void ParseRenderStates(Reader r, DecodedSrt srt)
        {
            int blockSize = srt.Version == 7 ? RenderStateV7Size : RenderStateV6Size;
            uint stateCount = r.ReadUInt32();
            bool hasSecondary = r.ReadUInt32() == 1;
            bool hasTertiary = r.ReadUInt32() == 1;
            r.ReadUInt32(); // render mode

            if (stateCount > 4096 || r.Position + stateCount * blockSize > r.Length)
                throw new Exception("Invalid SRT render state count: " + stateCount);

            int primaryBase = r.Position;
            for (int i = 0; i < stateCount; i++)
                srt.RenderStates.Add(r.Slice(primaryBase + i * blockSize, blockSize));
            r.Skip((int)stateCount * blockSize);

            if (hasSecondary) r.Skip((int)stateCount * blockSize);
            if (hasTertiary) r.Skip((int)stateCount * blockSize);

            int copyCount = 1 + (hasSecondary ? 1 : 0) + (hasTertiary ? 1 : 0);
            r.Skip(copyCount * blockSize);
        }

        private static void ParseGeometryDescriptors(Reader r, DecodedSrt srt)
        {
            uint numLods = r.ReadUInt32();
            if (numLods > 256) throw new Exception("Invalid SRT LOD count: " + numLods);

            int lodTableBase = r.Position;
            r.Skip((int)numLods * LodTableEntrySize);

            for (int lodIdx = 0; lodIdx < numLods; lodIdx++)
            {
                int lodStart = lodTableBase + lodIdx * LodTableEntrySize;
                uint numDrawCalls = r.ReadUInt32At(lodStart + 0);
                uint numBones = r.ReadUInt32At(lodStart + 12);
                if (numDrawCalls > 4096 || numBones > 4096)
                    throw new Exception("Invalid SRT geometry descriptor counts");

                var lod = new LodInfo { LodIndex = lodIdx };
                for (int geomIdx = 0; geomIdx < numDrawCalls; geomIdx++)
                {
                    int drawBase = r.Position;
                    uint[] w = new uint[10];
                    for (int k = 0; k < 10; k++) w[k] = r.ReadUInt32();
                    lod.DrawCalls.Add(new DrawCallInfo
                    {
                        LodIndex = lodIdx,
                        GeomIndex = geomIdx,
                        RenderStateIndex = unchecked((int)w[2]),
                        NumVertices = unchecked((int)w[3]),
                        NumIndices = unchecked((int)w[6]),
                        Index32 = (w[7] & 0xffu) != 0,
                    });
                }

                r.Skip((int)numBones * BoneSize);
                srt.Lods.Add(lod);
            }
        }

        private static void ParseVertexIndexData(Reader r, DecodedSrt srt)
        {
            foreach (var lod in srt.Lods)
            {
                foreach (var dc in lod.DrawCalls)
                {
                    if (dc.RenderStateIndex < 0 || dc.RenderStateIndex >= srt.RenderStates.Count)
                        continue;
                    if (dc.NumVertices <= 0 || dc.NumIndices <= 0 || dc.NumVertices > 5_000_000 || dc.NumIndices > 15_000_000)
                        continue;

                    var vf = srt.RenderStates[dc.RenderStateIndex];
                    int stride = srt.Version == 7
                        ? unchecked((int)ReadUInt32(vf, 0, false))
                        : (StrideByteOffsetV6 < vf.Length ? vf[StrideByteOffsetV6] : 0);
                    if (stride <= 0 || stride > 512) continue;

                    int vertexBytes = checked(dc.NumVertices * stride);
                    if (r.Position + vertexBytes > r.Length) break;
                    byte[] vb = r.ReadBytes(vertexBytes);

                    int indexSize = dc.Index32 ? 4 : 2;
                    int indexBytes = checked(dc.NumIndices * indexSize);
                    if (r.Position + indexBytes > r.Length) break;
                    byte[] ib = r.ReadBytes(indexBytes);
                    r.Align4();

                    var positions = new Vector3[dc.NumVertices];
                    var normals = new Vector3[dc.NumVertices];
                    var uvs = new Vector2[dc.NumVertices];
                    for (int i = 0; i < dc.NumVertices; i++)
                    {
                        int baseOffset = i * stride;
                        var pos = DecodeSemantic(vb, baseOffset, stride, vf, 0);
                        var nrm = DecodeSemantic(vb, baseOffset, stride, vf, 1);
                        var uv = DecodeSemantic(vb, baseOffset, stride, vf, 3);
                        if (uv.Count < 2) uv = DecodeSemantic(vb, baseOffset, stride, vf, 10);
                        if (uv.Count < 2) uv = DecodeSemantic(vb, baseOffset, stride, vf, 14);

                        if (pos.Count < 3 && baseOffset + 12 <= vb.Length)
                            pos = new List<float> { ReadSingle(vb, baseOffset, false), ReadSingle(vb, baseOffset + 4, false), ReadSingle(vb, baseOffset + 8, false) };
                        if (pos.Count < 3) pos = new List<float> { 0, 0, 0 };
                        if (nrm.Count < 3) nrm = new List<float> { 0, 1, 0 };
                        if (uv.Count < 2) uv = new List<float> { 0, 0 };

                        positions[i] = ConvertPosition(new Vector3(pos[0], pos[1], pos[2]), srt.CoordSystem);
                        normals[i] = ConvertDirection(new Vector3(nrm[0], nrm[1], nrm[2]), srt.CoordSystem).normalized;
                        float v = uv[1];
                        // Blender reference uses 1-v.  If a platform says texcoords
                        // are already flipped, keep them as-is.
                        if (!srt.TexcoordsFlipped) v = 1f - v;
                        uvs[i] = new Vector2(uv[0], v);
                    }

                    var indices = new int[dc.NumIndices];
                    for (int i = 0; i < dc.NumIndices; i++)
                    {
                        int off = i * indexSize;
                        indices[i] = indexSize == 4
                            ? unchecked((int)ReadUInt32(ib, off, false))
                            : ReadUInt16(ib, off, false);
                    }
                    // Different SRT exporters/game builds may differ in handedness.
                    // Choose winding by comparing geometric face normals with imported
                    // vertex normals instead of blindly flipping every mesh.
                    if (NeedsWindingFlip(positions, normals, indices))
                        SwapTriangleWinding(indices);

                    srt.Meshes.Add(new MeshPart
                    {
                        LodIndex = dc.LodIndex,
                        GeomIndex = dc.GeomIndex,
                        RenderStateIndex = dc.RenderStateIndex,
                        Positions = positions,
                        Normals = normals,
                        Uv = uvs,
                        Indices = indices,
                    });
                }
            }
        }

        private static List<float> DecodeSemantic(byte[] vertexBlob, int vertexBase, int stride, byte[] vfBlock, int semanticId)
        {
            int descStart = VfDescSize * (semanticId + VfDescOffset);
            if (descStart < 0 || descStart + VfDescSize > vfBlock.Length) return new List<float>();

            int compType = vfBlock[descStart + 0];
            int componentCount = 0;
            for (int i = 1; i <= 4; i++)
                if (vfBlock[descStart + i] != 0xff) componentCount++;
            if (componentCount <= 0) return new List<float>();

            int componentSize = compType == 0 ? 4 : compType == 1 ? 2 : 1;
            var values = new List<float>(componentCount);
            for (int i = 9; i <= 12 && values.Count < componentCount; i++)
            {
                int off = vfBlock[descStart + i];
                if (off == 0xff || off < 0 || off >= stride) continue;
                int src = vertexBase + off;
                if (src + componentSize > vertexBlob.Length)
                {
                    values.Add(0f);
                    continue;
                }
                values.Add(DecodeComponent(vertexBlob, src, compType));
            }
            return values;
        }

        private static float DecodeComponent(byte[] data, int offset, int compType)
        {
            switch (compType)
            {
                case 0: return ReadSingle(data, offset, false);
                case 1: return HalfToFloat(ReadUInt16(data, offset, false));
                case 2: return (data[offset] / 255f) * 2f - 1f;
                default: return 0f;
            }
        }

        private static Dictionary<int, int> BuildUnityLodMap(List<MeshPart> meshes)
        {
            var complexity = new Dictionary<int, long>();
            if (meshes != null)
            {
                foreach (var m in meshes)
                {
                    if (m == null) continue;
                    long score = (m.Positions != null ? m.Positions.Length : 0) * 10L +
                                 (m.Indices != null ? m.Indices.Length : 0);
                    if (!complexity.ContainsKey(m.LodIndex)) complexity[m.LodIndex] = 0;
                    complexity[m.LodIndex] += score;
                }
            }

            var lods = new List<int>(complexity.Keys);
            lods.Sort((a, b) =>
            {
                int c = complexity[b].CompareTo(complexity[a]); // highest detail first
                if (c != 0) return c;
                return b.CompareTo(a); // if equal, prefer later source LOD as nearer
            });

            var map = new Dictionary<int, int>();
            for (int i = 0; i < lods.Count; i++) map[lods[i]] = i;
            return map;
        }

        private static GameObject BuildPrefab(
            string outputPath,
            string resourceName,
            DecodedSrt srt,
            WoTPackageManager resMgr,
            List<string> warnings)
        {
            string baseName = SafeAssetName(PathName(resourceName));
            string rootDir = $"{outputPath}/VegetationAssets/_DecodedSRT/{baseName}_{StableHash32(resourceName):X8}".Replace('\\', '/');
            EnsureFolder(rootDir);

            var tempRoot = new GameObject(baseName + "_SRT");
            var materialCache = new Dictionary<int, Material>();
            var lodRoots = new Dictionary<int, GameObject>();
            var lodRenderers = new Dictionary<int, List<Renderer>>();
            var unityLodMap = BuildUnityLodMap(srt.Meshes);

            foreach (var part in srt.Meshes)
            {
                int unityLodIndex = unityLodMap.TryGetValue(part.LodIndex, out var mapped)
                    ? mapped
                    : part.LodIndex;

                if (!lodRoots.TryGetValue(unityLodIndex, out var lodRoot))
                {
                    // SpeedTree/WoT often stores LODs in the opposite order from
                    // Unity, and sometimes LOD0 is the far/billboard mesh.  Unity's
                    // LOD0 must be the most detailed mesh, so we rank source LODs by
                    // total vertex count and keep the original source id in the name.
                    lodRoot = new GameObject($"LOD{unityLodIndex}_src{part.LodIndex}");
                    lodRoot.transform.SetParent(tempRoot.transform, false);
                    lodRoots[unityLodIndex] = lodRoot;
                    lodRenderers[unityLodIndex] = new List<Renderer>();
                }

                var mesh = new UnityEngine.Mesh
                {
                    name = SafeAssetName($"{baseName}_lod{unityLodIndex}_src{part.LodIndex}_geom{part.GeomIndex}"),
                    indexFormat = part.Positions.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
                };
                mesh.vertices = part.Positions;
                mesh.normals = part.Normals;
                mesh.uv = part.Uv;
                mesh.triangles = part.Indices;
                if (mesh.normals == null || mesh.normals.Length != mesh.vertexCount) mesh.RecalculateNormals();
                try { mesh.RecalculateTangents(); } catch { /* optional */ }
                mesh.RecalculateBounds();

                string meshPath = $"{rootDir}/Meshes/{mesh.name}_{StableHash32(unityLodIndex + ":" + part.LodIndex + ":" + part.GeomIndex + ":" + resourceName):X8}.asset";
                SaveAsset(mesh, meshPath);
                var meshAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Mesh>(meshPath) ?? mesh;

                var go = new GameObject($"geom{part.GeomIndex}_rs{part.RenderStateIndex}");
                go.transform.SetParent(lodRoot.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = meshAsset;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = GetOrCreateMaterial(rootDir, resourceName, part.RenderStateIndex, srt, resMgr, materialCache, warnings);
                lodRenderers[unityLodIndex].Add(renderer);
            }

            // Add a Unity LODGroup when possible, but keep LOD0 visible in editor.
            if (lodRoots.Count > 1)
            {
                var group = tempRoot.AddComponent<LODGroup>();
                var lods = new List<LOD>();
                var keys = new List<int>(lodRoots.Keys);
                keys.Sort();
                for (int i = 0; i < keys.Count; i++)
                {
                    float transition = Mathf.Clamp01(0.65f / (i + 1));
                    if (i == keys.Count - 1) transition = 0.02f;
                    lods.Add(new LOD(transition, lodRenderers[keys[i]].ToArray()));
                }
                group.SetLODs(lods.ToArray());
                group.RecalculateBounds();
            }
            else
            {
                foreach (var kv in lodRoots)
                    kv.Value.SetActive(kv.Key == 0 || lodRoots.Count == 1);
            }

            RemoveAllColliders(tempRoot);
            string prefabPath = $"{rootDir}/{baseName}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(tempRoot, prefabPath);
            UnityEngine.Object.DestroyImmediate(tempRoot);
            return prefab;
        }

        private static Material GetOrCreateMaterial(
            string rootDir,
            string resourceName,
            int renderStateIndex,
            DecodedSrt srt,
            WoTPackageManager resMgr,
            Dictionary<int, Material> cache,
            List<string> warnings)
        {
            if (cache.TryGetValue(renderStateIndex, out var cached)) return cached;

            List<string> textureNames = FindTextureNames(renderStateIndex, srt);
            Texture2D tex = LoadFirstTexture(rootDir, resourceName, textureNames, resMgr, warnings, out string usedTextureName);
            Color tint = GuessDiffuseTint(renderStateIndex, usedTextureName, srt);

            Shader shader = Shader.Find("WoT/ObjectPBS") ??
                            Shader.Find("Universal Render Pipeline/Lit") ??
                            Shader.Find("HDRP/Lit") ??
                            Shader.Find("Standard") ??
                            Shader.Find("Sprites/Default");
            var mat = new Material(shader)
            {
                name = SafeAssetName($"{PathName(resourceName)}_rs{renderStateIndex}_{PathName(usedTextureName)}"),
            };
            if (tex != null)
            {
                SetTextureIfExists(mat, "_BaseMap", tex);
                SetTextureIfExists(mat, "_MainTex", tex);
            }
            SetColorIfExists(mat, "_BaseColor", tint);
            SetColorIfExists(mat, "_Color", tint);
            SetFloatIfExists(mat, "_Cutoff", 0.35f);
            SetFloatIfExists(mat, "_AlphaClip", 1f);
            mat.SetOverrideTag("RenderType", "TransparentCutout");
            mat.SetOverrideTag("Queue", "AlphaTest");
            SetupAlphaCutout(mat);
            DisableBackfaceCulling(mat);
            mat.doubleSidedGI = true;

            string matPath = $"{rootDir}/Materials/{mat.name}_{StableHash32(usedTextureName + renderStateIndex):X8}.mat";
            SaveAsset(mat, matPath);
            var matAsset = AssetDatabase.LoadAssetAtPath<Material>(matPath) ?? mat;
            cache[renderStateIndex] = matAsset;
            return matAsset;
        }

        private static List<string> FindTextureNames(int renderStateIndex, DecodedSrt srt)
        {
            var result = new List<string>();
            if (renderStateIndex < 0 || renderStateIndex >= srt.RenderStates.Count) return result;
            byte[] block = srt.RenderStates[renderStateIndex];

            // SpeedTree render states begin with ApTextures[8].  The first decoder
            // version looked only at three slots, which left many bark/trunk render
            // states untextured.  Read all eight first, then scan the whole block for
            // additional string refs used by slightly different game builds.
            var primary = new List<int>();
            int slots = Math.Min(8, block.Length / 4);
            for (int off = 0; off < slots * 4; off += 4)
                primary.Add(unchecked((int)ReadUInt32(block, off, false)));

            AddTextureRefs(primary, srt, result, likelyDiffuseOnly: true, allowBareNames: true);
            AddTextureRefs(primary, srt, result, likelyDiffuseOnly: false, allowBareNames: true);

            var scanned = new List<int>();
            for (int off = 0; off + 4 <= block.Length; off += 4)
            {
                int idx = unchecked((int)ReadUInt32(block, off, false));
                if (idx >= 0 && idx < srt.Strings.Count) scanned.Add(idx);
            }
            AddTextureRefs(scanned, srt, result, likelyDiffuseOnly: true, allowBareNames: false);
            AddTextureRefs(scanned, srt, result, likelyDiffuseOnly: false, allowBareNames: false);
            return result;
        }

        private static void AddTextureRefs(List<int> refs, DecodedSrt srt, List<string> dst, bool likelyDiffuseOnly, bool allowBareNames)
        {
            if (refs == null || srt == null || dst == null) return;
            foreach (int i in refs)
            {
                if (i < 0 || i >= srt.Strings.Count) continue;
                string n = srt.Strings[i];
                if (allowBareNames)
                {
                    if (!LooksLikeTextureName(n, likelyDiffuseOnly)) continue;
                }
                else
                {
                    if (likelyDiffuseOnly ? !IsLikelyDiffuse(n) : !IsTexture(n)) continue;
                }
                bool exists = false;
                foreach (var d in dst)
                {
                    if (string.Equals(d, n, StringComparison.OrdinalIgnoreCase))
                    { exists = true; break; }
                }
                if (!exists) dst.Add(n);
            }
        }

        private static bool LooksLikeTextureName(string name, bool diffuseOnly)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant().Replace('\\', '/');
            if (n.Contains("shader") || n.Contains("speedtree") || n.Contains(".fx") || n.Contains(".xml"))
                return false;
            bool hasExt = IsTexture(n);
            bool hasTextureWord = n.Contains("bark") || n.Contains("branch") || n.Contains("trunk") ||
                                  n.Contains("stem") || n.Contains("leaf") || n.Contains("leaves") ||
                                  n.Contains("foliage") || n.Contains("needle") || n.Contains("diff") ||
                                  n.Contains("albedo") || n.Contains("atlas") || n.Contains("_d") || n.Contains("/d");
            if (!hasExt && !hasTextureWord) return false;
            if (!diffuseOnly) return true;
            return !LooksLikeNonDiffuseTexture(n);
        }

        private static bool IsLikelyDiffuse(string name)
        {
            if (!IsTexture(name)) return false;
            string n = name.ToLowerInvariant();
            return !LooksLikeNonDiffuseTexture(n);
        }

        private static bool LooksLikeNonDiffuseTexture(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            n = n.ToLowerInvariant();
            return n.Contains("_nm") || n.Contains("_n.") || n.Contains("normal") ||
                   n.Contains("_sm") || n.Contains("spec") || n.Contains("rough") ||
                   n.Contains("metal") || n.Contains("_dam") || n.Contains("_dnm") ||
                   n.Contains("height") || n.Contains("ao");
        }

        private static bool IsTexture(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.EndsWith(".dds") || n.EndsWith(".png") || n.EndsWith(".tga") || n.EndsWith(".jpg") || n.EndsWith(".jpeg");
        }

        private static Texture2D LoadFirstTexture(
            string rootDir, string resourceName, List<string> textureNames, WoTPackageManager resMgr,
            List<string> warnings, out string usedTextureName)
        {
            usedTextureName = null;
            if (textureNames == null) return null;
            foreach (var name in textureNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                var tex = LoadTexture(rootDir, resourceName, name, resMgr, warnings);
                if (tex == null) continue;
                usedTextureName = name;
                return tex;
            }

            // Some WoT SRTs keep incomplete/bare texture references.  As a fallback,
            // scan the tree directory for diffuse-looking DDS/PNG files.
            foreach (var name in NearbyDiffuseTextureNames(resourceName, resMgr))
            {
                var tex = LoadTexture(rootDir, resourceName, name, resMgr, warnings);
                if (tex == null) continue;
                usedTextureName = name;
                return tex;
            }
            return null;
        }

        private static Color GuessDiffuseTint(int renderStateIndex, string textureName, DecodedSrt srt)
        {
            if (!string.IsNullOrEmpty(textureName)) return Color.white;

            // Fallback only when texture lookup failed.  It avoids completely white
            // trunks/leaves while we continue improving exact material decoding.
            if (renderStateIndex >= 0 && renderStateIndex < srt.RenderStates.Count)
            {
                string refs = string.Join(" ", FindTextureNames(renderStateIndex, srt)).ToLowerInvariant();
                if (refs.Contains("bark") || refs.Contains("branch") || refs.Contains("trunk") || refs.Contains("stem"))
                    return new Color(0.45f, 0.32f, 0.20f, 1f);
                if (refs.Contains("leaf") || refs.Contains("leaves") || refs.Contains("foliage") || refs.Contains("needle"))
                    return new Color(0.45f, 0.65f, 0.32f, 1f);
            }
            return new Color(0.55f, 0.55f, 0.55f, 1f);
        }

        private static IEnumerable<string> NearbyDiffuseTextureNames(string resourceName, WoTPackageManager resMgr)
        {
            string res = Normalize(resourceName);
            string dir = Path.GetDirectoryName(res)?.Replace('\\', '/') ?? string.Empty;
            if (string.IsNullOrEmpty(dir) || resMgr == null) yield break;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string ext in new[] { ".dds", ".png", ".tga", ".jpg", ".jpeg" })
            {
                foreach (string f in resMgr.GetFilesWithExtension(ext))
                {
                    string n = Normalize(f);
                    bool sameDir = n.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase) ||
                                   n.StartsWith("content/" + dir + "/", StringComparison.OrdinalIgnoreCase);
                    if (!sameDir || !IsLikelyDiffuse(n)) continue;
                    if (seen.Add(n)) yield return n;
                }
            }
        }

        private static Texture2D LoadTexture(string rootDir, string resourceName, string textureName, WoTPackageManager resMgr, List<string> warnings)
        {
            foreach (string candidate in TextureCandidates(resourceName, textureName))
            {
                byte[] bytes = resMgr.ReadBytes(candidate);
                if (bytes == null) continue;

                try
                {
                    Texture2D tex;
                    if (bytes.Length >= 4 && BitConverter.ToUInt32(bytes, 0) == DdsDecoder.MAGIC)
                    {
                        try { tex = DdsDecoder.ReadDecompressed(bytes, Path.GetFileNameWithoutExtension(candidate), false, true); }
                        catch { tex = DdsDecoder.Read(bytes, Path.GetFileNameWithoutExtension(candidate), false); }
                    }
                    else
                    {
                        tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
                        {
                            name = Path.GetFileNameWithoutExtension(candidate),
                            wrapMode = TextureWrapMode.Repeat,
                        };
                        if (!tex.LoadImage(bytes, false))
                        {
                            UnityEngine.Object.DestroyImmediate(tex);
                            continue;
                        }
                    }

                    tex.name = SafeAssetName(PathName(candidate) + "_" + StableHash32(candidate).ToString("X8"));
                    tex.wrapMode = TextureWrapMode.Repeat;
                    tex.filterMode = FilterMode.Bilinear;
                    string texPath = $"{rootDir}/Textures/{tex.name}.asset";
                    SaveAsset(tex, texPath);
                    return AssetDatabase.LoadAssetAtPath<Texture2D>(texPath) ?? tex;
                }
                catch (Exception e)
                {
                    warnings?.Add($"SRT texture load failed ({candidate}): {e.Message}");
                }
            }
            return null;
        }

        private static IEnumerable<string> TextureCandidates(string resourceName, string textureName)
        {
            string tex = Normalize(textureName);
            string resDir = Path.GetDirectoryName(Normalize(resourceName))?.Replace('\\', '/') ?? string.Empty;
            yield return tex;
            if (!tex.StartsWith("content/", StringComparison.OrdinalIgnoreCase)) yield return "content/" + tex;
            if (!string.IsNullOrEmpty(resDir) && tex.IndexOf('/') < 0) yield return resDir + "/" + tex;
            if (!string.IsNullOrEmpty(resDir) && tex.IndexOf('/') < 0) yield return "content/" + resDir + "/" + tex;

            if (!Path.HasExtension(tex))
            {
                foreach (string ext in new[] { ".dds", ".png", ".tga" })
                {
                    string withExt = tex + ext;
                    yield return withExt;
                    if (!withExt.StartsWith("content/", StringComparison.OrdinalIgnoreCase)) yield return "content/" + withExt;
                    if (!string.IsNullOrEmpty(resDir) && withExt.IndexOf('/') < 0) yield return resDir + "/" + withExt;
                    if (!string.IsNullOrEmpty(resDir) && withExt.IndexOf('/') < 0) yield return "content/" + resDir + "/" + withExt;
                }
            }

            if (tex.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) || tex.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                string dds = Path.ChangeExtension(tex, ".dds").Replace('\\', '/');
                yield return dds;
                if (!dds.StartsWith("content/", StringComparison.OrdinalIgnoreCase)) yield return "content/" + dds;
                if (!string.IsNullOrEmpty(resDir) && dds.IndexOf('/') < 0) yield return resDir + "/" + dds;
                if (!string.IsNullOrEmpty(resDir) && dds.IndexOf('/') < 0) yield return "content/" + resDir + "/" + dds;
            }
        }

        private static Vector3 ConvertPosition(Vector3 v, CoordSystem coord)
        {
            switch (coord)
            {
                case CoordSystem.ZUpRight:
                case CoordSystem.ZUpLeft:
                    return new Vector3(v.x, v.z, v.y);
                default:
                    return v;
            }
        }

        private static Vector3 ConvertDirection(Vector3 v, CoordSystem coord) => ConvertPosition(v, coord);

        private static bool NeedsWindingFlip(Vector3[] positions, Vector3[] normals, int[] indices)
        {
            if (positions == null || normals == null || indices == null || positions.Length == 0)
                return false;

            double sum = 0.0;
            int samples = 0;
            int step = Math.Max(3, (indices.Length / 300) * 3);
            for (int i = 0; i + 2 < indices.Length; i += step)
            {
                int i0 = indices[i];
                int i1 = indices[i + 1];
                int i2 = indices[i + 2];
                if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= positions.Length || i1 >= positions.Length || i2 >= positions.Length)
                    continue;

                Vector3 e1 = positions[i1] - positions[i0];
                Vector3 e2 = positions[i2] - positions[i0];
                Vector3 face = Vector3.Cross(e1, e2);
                if (face.sqrMagnitude < 1e-12f) continue;
                face.Normalize();

                Vector3 n = normals[i0] + normals[i1] + normals[i2];
                if (n.sqrMagnitude < 1e-12f) continue;
                n.Normalize();

                sum += Vector3.Dot(face, n);
                samples++;
            }

            return samples > 0 && sum / samples < 0.0;
        }

        private static void SwapTriangleWinding(int[] indices)
        {
            if (indices == null) return;
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int t = indices[i];
                indices[i] = indices[i + 2];
                indices[i + 2] = t;
            }
        }

        private static void RemoveAllColliders(GameObject root)
        {
            if (root == null) return;
            var colliders = root.GetComponentsInChildren<Collider>(true);
            foreach (var c in colliders)
                if (c != null) UnityEngine.Object.DestroyImmediate(c);
        }

        private static void SetupAlphaCutout(Material mat)
        {
            if (mat == null) return;
            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 0f); // URP opaque
            if (mat.HasProperty("_AlphaClip")) mat.SetFloat("_AlphaClip", 1f);
            if (mat.HasProperty("_Mode")) mat.SetFloat("_Mode", 1f); // Standard cutout
            mat.renderQueue = (int)RenderQueue.AlphaTest;
            mat.EnableKeyword("_ALPHATEST_ON");
        }

        private static void DisableBackfaceCulling(Material mat)
        {
            if (mat == null) return;
            // URP/HDRP/Standard-compatible best effort.  This avoids invisible or
            // dark bark when a particular SRT mesh chunk has opposite winding.
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
            if (mat.HasProperty("_CullMode")) mat.SetFloat("_CullMode", 0f);
            if (mat.HasProperty("_DoubleSidedEnable")) mat.SetFloat("_DoubleSidedEnable", 1f);
            mat.SetInt("_Cull", 0);
        }

        private static void SetTextureIfExists(Material mat, string prop, Texture tex)
        {
            if (tex != null && mat.HasProperty(prop)) mat.SetTexture(prop, tex);
            if (tex != null && (prop == "_MainTex" || prop == "_BaseMap")) mat.mainTexture = tex;
        }

        private static void SetColorIfExists(Material mat, string prop, Color color)
        {
            if (mat.HasProperty(prop)) mat.SetColor(prop, color);
        }

        private static void SetFloatIfExists(Material mat, string prop, float value)
        {
            if (mat.HasProperty(prop)) mat.SetFloat(prop, value);
        }

        private static uint ReadUInt32(byte[] data, int offset, bool bigEndian)
        {
            if (data == null || offset < 0 || offset + 4 > data.Length) return 0;
            if (bigEndian)
            {
                return ((uint)data[offset] << 24) |
                       ((uint)data[offset + 1] << 16) |
                       ((uint)data[offset + 2] << 8) |
                       data[offset + 3];
            }
            return data[offset] |
                   ((uint)data[offset + 1] << 8) |
                   ((uint)data[offset + 2] << 16) |
                   ((uint)data[offset + 3] << 24);
        }

        private static ushort ReadUInt16(byte[] data, int offset, bool bigEndian)
        {
            if (data == null || offset < 0 || offset + 2 > data.Length) return 0;
            return bigEndian
                ? (ushort)((data[offset] << 8) | data[offset + 1])
                : (ushort)(data[offset] | (data[offset + 1] << 8));
        }

        private static float ReadSingle(byte[] data, int offset, bool bigEndian)
        {
            uint u = ReadUInt32(data, offset, bigEndian);
            return BitConverter.ToSingle(BitConverter.GetBytes(u), 0);
        }

        private static float HalfToFloat(ushort h)
        {
            int sign = (h >> 15) & 0x00000001;
            int exp = (h >> 10) & 0x0000001f;
            int mant = h & 0x000003ff;

            if (exp == 0)
            {
                if (mant == 0) return sign == 0 ? 0f : -0f;
                return (float)((sign == 1 ? -1 : 1) * Math.Pow(2, -14) * (mant / 1024.0));
            }
            if (exp == 31)
            {
                if (mant == 0) return sign == 0 ? float.PositiveInfinity : float.NegativeInfinity;
                return float.NaN;
            }
            return (float)((sign == 1 ? -1 : 1) * Math.Pow(2, exp - 15) * (1.0 + mant / 1024.0));
        }

        private static string Normalize(string s)
        {
            return (s ?? string.Empty).Trim().TrimStart('/').Replace('\\', '/').ToLowerInvariant();
        }

        private static string PathName(string s)
        {
            string n = (s ?? string.Empty).Replace('\\', '/');
            int idx = n.LastIndexOf('/');
            string baseN = idx >= 0 ? n.Substring(idx + 1) : n;
            return Path.GetFileNameWithoutExtension(baseN);
        }

        private static string SafeAssetName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unnamed";
            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
        }

        private static uint StableHash32(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                if (s != null)
                {
                    for (int i = 0; i < s.Length; i++)
                    {
                        h ^= char.ToLowerInvariant(s[i]);
                        h *= 16777619u;
                    }
                }
                return h;
            }
        }

        private static void SaveAsset(UnityEngine.Object obj, string path)
        {
            path = path.Replace('\\', '/');
            EnsureFolder(Path.GetDirectoryName(path)?.Replace('\\', '/'));
            var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (existing != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(obj, path);
            AssetDatabase.ImportAsset(path);
        }

        private static void EnsureFolder(string folderPath)
        {
            folderPath = folderPath?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folderPath) || AssetDatabase.IsValidFolder(folderPath)) return;
            string parent = Path.GetDirectoryName(folderPath)?.Replace('\\', '/') ?? "Assets";
            string leaf = Path.GetFileName(folderPath);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            if (!AssetDatabase.IsValidFolder(folderPath)) AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
