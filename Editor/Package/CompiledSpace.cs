using System;
using System.Collections.Generic;
using System.IO;
using WoTMapImporter.Editor.Utils;

namespace WoTMapImporter.Editor.Package
{
    /// <summary>
    /// Parser for WoT's compiled "space.bin" static model data.
    ///
    /// This follows the reference Blender addon's universal_space.py more closely
    /// than the old minimal port:
    ///   - BSMI visibility masks are respected;
    ///   - BSMO render sets are grouped per placed model, not just per primitives file;
    ///   - every LOD in models_loddings/lod_renders is kept;
    ///   - BSMA material fx names and all material properties are resolved via BWST;
    ///   - destructible/fragile entries expose their destroyed_model_index.
    /// </summary>
    public sealed class CompiledSpace
    {
        public const uint CaptureTheFlagVisibility = 1u;

        // Legacy flat render instance kept for compatibility with older callers.
        public class ModelInstance
        {
            public string PrimsName;
            public string VertsDataName;
            public string PrimsDataName;
            public int PrimitiveGroup;
            public string DiffuseTexture;
            public float[] Transform;
        }

        public sealed class MaterialProperty
        {
            public string Name;
            public uint ValueType;
            public uint RawValue;
            public string StringValue;
            public float[] VectorValue;

            public override string ToString()
            {
                if (StringValue != null) return StringValue;
                if (VectorValue != null) return string.Join(",", VectorValue);
                return RawValue.ToString();
            }
        }

        public sealed class RenderMesh
        {
            public int RenderSetId;
            public int PrimitiveGroup;
            public int MaterialIndex;
            public string PrimsName;
            public string VertsDataName;
            public string PrimsDataName;
            public string FxName;
            public string Identifier;
            public bool IsDestroyedMaterial;
            public Dictionary<string, MaterialProperty> Props = new Dictionary<string, MaterialProperty>(StringComparer.OrdinalIgnoreCase);

            public string DiffuseTexture => GetString("diffuseMap");

            public string GetString(string name)
            {
                return Props.TryGetValue(name, out var p) ? p.StringValue : null;
            }

            public float[] GetVector(string name)
            {
                return Props.TryGetValue(name, out var p) ? p.VectorValue : null;
            }
        }

        public sealed class ModelLod
        {
            public int LodIndex;
            public float Distance;
            public List<RenderMesh> Meshes = new List<RenderMesh>();
        }

        public sealed class ModelPlacement
        {
            public int InstanceIndex;
            public int ModelId;
            public int DestroyedModelId = -1;
            public float[] Transform;
            public List<ModelLod> Lods = new List<ModelLod>();
            public List<ModelLod> DestroyedLods = new List<ModelLod>();
        }

        public sealed class SpeedTreePlacement
        {
            public int InstanceIndex;
            public string ResourceName;
            public uint ResourceFnv;
            public uint Seed;
            public bool CastsShadow;
            public bool CastsLocalShadow;
            public bool AlwaysDynamic;
            public uint VisibilityMask;
            public float[] Transform;
        }

        public readonly List<ModelInstance> Models = new List<ModelInstance>();
        public readonly List<ModelPlacement> Placements = new List<ModelPlacement>();
        public readonly List<SpeedTreePlacement> SpeedTrees = new List<SpeedTreePlacement>();

        private struct Row
        {
            public string Header;
            public uint Int1;
            public uint Position;
            public uint Length;
            public uint RowsNum;
        }

        private struct RawList
        {
            public int ElemSize;
            public int Count;
            public byte[][] Items;
        }

        private sealed class BsmiData
        {
            public float[][] Transforms = Array.Empty<float[]>();
            public uint[] ModelIds = Array.Empty<uint>();
            public uint[] VisibilityMasks = Array.Empty<uint>();
        }

        private sealed class RenderItem
        {
            public uint MaterialIndex;
            public uint PrimitiveIndex;
            public uint VertsNameFnv;
            public uint PrimsNameFnv;
        }

        private sealed class ModelInfoItem
        {
            public uint Type;
            public uint InfoIndex;
        }

        private sealed class FragileInfoItem
        {
            public uint DestroyedModelIndex;
            public uint EntryType;
        }

        private sealed class BsmoData
        {
            public (uint begin, uint end)[] ModelsLoddings = Array.Empty<(uint, uint)>();
            public (uint begin, uint end)[] LodRenders = Array.Empty<(uint, uint)>();
            public float[] LodDistances = Array.Empty<float>();
            public RenderItem[] Renders = Array.Empty<RenderItem>();
            public ModelInfoItem[] ModelInfos = Array.Empty<ModelInfoItem>();
            public FragileInfoItem[] FragileInfos = Array.Empty<FragileInfoItem>();
        }

        private sealed class MaterialInfo
        {
            public int KeyFx;
            public int KeyFrom;
            public int KeyTo;
            public uint IdentifierFnv;
            public uint Identifier2Fnv;
        }

        private sealed class PropInfo
        {
            public uint PropFnv;
            public uint ValueType;
            public uint Value;
        }

        private sealed class BsmaData
        {
            public MaterialInfo[] Materials = Array.Empty<MaterialInfo>();
            public uint[] Fx = Array.Empty<uint>();
            public PropInfo[] Props = Array.Empty<PropInfo>();
            public float[][] Vectors = Array.Empty<float[]>();
        }

        // ---- public entry ----

        public static CompiledSpace Parse(byte[] bin)
        {
            var cs = new CompiledSpace();
            try { cs.ParseInternal(bin); }
            catch (Exception e)
            {
                WoTLogger.Error($"CompiledSpace parse failed: {e.Message}\n{e.StackTrace}");
            }
            return cs;
        }

        private void ParseInternal(byte[] bin)
        {
            using var ms = new MemoryStream(bin, false);
            using var br = new BinaryReader(ms);

            var rows = ReadBwtb(br);

            var strings = new Dictionary<uint, string>();
            if (rows.TryGetValue("BWST", out var bwstRow))
                strings = ReadBwst(br, bwstRow);

            // SpeedTree vegetation is independent from BSMI/BSMO static models.
            // Parse it even on spaces where ordinary model sections are absent or
            // broken, so the vegetation importer can still place trees.
            if (rows.TryGetValue("SpTr", out var sptrRow))
                ReadSpeedTrees(br, sptrRow, strings);

            if (!rows.TryGetValue("BSMI", out var bsmiRow) ||
                !rows.TryGetValue("BSMO", out var bsmoRow))
            {
                WoTLogger.Warn("space.bin has no BSMI/BSMO sections; no static models to load");
                return;
            }

            var bsmo = ReadBsmo(br, bsmoRow);
            var bsmi = ReadBsmi(br, bsmiRow, bsmo.ModelsLoddings.Length);
            BsmaData bsma = rows.TryGetValue("BSMA", out var bsmaRow)
                ? ReadBsma(br, bsmaRow) : new BsmaData();

            // Some versions move visibility masks out of BSMI into BWSV.
            if (rows.TryGetValue("BWSV", out var bwsvRow))
            {
                var masks = TryReadVisibilityMasks(br, bwsvRow, bsmi.Transforms.Length);
                if (masks != null && masks.Length > 0)
                    bsmi.VisibilityMasks = masks;
            }

            BuildModels(strings, bsmi, bsmo, bsma);
        }

        // =================== BWTB (root table) ===================

        private static Dictionary<string, Row> ReadBwtb(BinaryReader br)
        {
            var root = ReadRow(br);
            if (root.Header != "BWTB") throw new Exception($"Not a compiled space (header={root.Header})");

            var map = new Dictionary<string, Row>();
            for (uint i = 0; i < root.RowsNum; i++)
            {
                var r = ReadRow(br);
                map[r.Header] = r;
            }
            return map;
        }

        private static Row ReadRow(BinaryReader br)
        {
            string header = ReadHeader(br);
            uint int1 = br.ReadUInt32();
            uint position = br.ReadUInt32();
            br.ReadUInt32(); // int3
            uint length = br.ReadUInt32();
            uint rowsNum = br.ReadUInt32();
            return new Row { Header = header, Int1 = int1, Position = position, Length = length, RowsNum = rowsNum };
        }

        private static string ReadHeader(BinaryReader br)
        {
            var b = br.ReadBytes(4);
            return System.Text.Encoding.ASCII.GetString(b);
        }

        // =================== BWST (string table) ===================

        private static Dictionary<uint, string> ReadBwst(BinaryReader br, Row row)
        {
            var result = new Dictionary<uint, string>();
            if (row.Length == 0) return result;
            br.BaseStream.Position = row.Position;

            uint sz = br.ReadUInt32();
            uint cnt = br.ReadUInt32();
            var entries = new (uint hash, uint offset, uint length)[cnt];
            for (uint i = 0; i < cnt; i++)
                entries[i] = (br.ReadUInt32(), br.ReadUInt32(), br.ReadUInt32());

            br.ReadUInt32(); // stringsSize
            long stringsStart = br.BaseStream.Position;
            foreach (var e in entries)
            {
                br.BaseStream.Position = stringsStart + e.offset;
                var bytes = br.ReadBytes((int)e.length);
                result[e.hash] = Latin1(bytes);
            }
            return result;
        }

        // =================== raw list helpers ===================

        private static RawList ReadRawList(BinaryReader br)
        {
            int elemSize = (int)br.ReadUInt32();
            int count = (int)br.ReadUInt32();
            if (elemSize < 0 || count < 0 || count > 10_000_000)
                throw new Exception($"Invalid list header elemSize={elemSize} count={count}");
            var arr = new byte[count][];
            for (int i = 0; i < count; i++)
                arr[i] = br.ReadBytes(elemSize);
            return new RawList { ElemSize = elemSize, Count = count, Items = arr };
        }

        private static List<RawList> ReadAllLists(BinaryReader br, Row row)
        {
            var lists = new List<RawList>();
            long end = row.Position + row.Length;
            br.BaseStream.Position = row.Position;
            while (br.BaseStream.Position + 8 <= end)
            {
                long before = br.BaseStream.Position;
                try
                {
                    var list = ReadRawList(br);
                    if (br.BaseStream.Position > end)
                    {
                        br.BaseStream.Position = before;
                        break;
                    }
                    lists.Add(list);
                }
                catch
                {
                    br.BaseStream.Position = before;
                    break;
                }
            }
            return lists;
        }

        private static uint FirstUInt(byte[] b, int offset = 0)
        {
            return b != null && b.Length >= offset + 4 ? BitConverter.ToUInt32(b, offset) : 0u;
        }

        private static int FirstInt(byte[] b, int offset = 0)
        {
            return b != null && b.Length >= offset + 4 ? BitConverter.ToInt32(b, offset) : 0;
        }

        private static float FirstFloat(byte[] b, int offset = 0)
        {
            return b != null && b.Length >= offset + 4 ? BitConverter.ToSingle(b, offset) : 0f;
        }


        // =================== SpTr (SpeedTree vegetation) ===================

        private void ReadSpeedTrees(BinaryReader br, Row row, Dictionary<uint, string> strings)
        {
            try
            {
                var lists = ReadAllLists(br, row);
                int listIndex = -1;
                for (int i = 0; i < lists.Count; i++)
                {
                    // WoT 0.9.12: 76 bytes (no visibility mask).
                    // WoT 0.9.20+ / 1.0+: 80 bytes.
                    if (lists[i].ElemSize == 76 || lists[i].ElemSize == 80)
                    {
                        listIndex = i;
                        break;
                    }
                }

                if (listIndex < 0)
                {
                    WoTLogger.Warn($"SpTr: no speedtree placement list found (lists={lists.Count})");
                    return;
                }

                var raw = lists[listIndex];
                int skippedVisibility = 0;
                int unresolved = 0;
                for (int i = 0; i < raw.Count; i++)
                {
                    var b = raw.Items[i];
                    if (b == null || b.Length < 76) continue;

                    var t = new float[16];
                    Buffer.BlockCopy(b, 0, t, 0, 16 * 4);

                    uint resourceFnv = FirstUInt(b, 64);
                    uint seed = FirstUInt(b, 68);
                    uint flags = FirstUInt(b, 72);
                    uint visibilityMask = b.Length >= 80 ? FirstUInt(b, 76) : 0xffffffffu;
                    if ((visibilityMask & CaptureTheFlagVisibility) == 0)
                    {
                        skippedVisibility++;
                        continue;
                    }

                    strings.TryGetValue(resourceFnv, out var resourceName);
                    if (string.IsNullOrEmpty(resourceName)) unresolved++;

                    bool castsShadow = (flags & 0x1u) != 0;
                    bool castsLocalShadow;
                    bool alwaysDynamic;
                    bool modernSrtLayout = !string.IsNullOrEmpty(resourceName) &&
                                           resourceName.EndsWith(".srt", StringComparison.OrdinalIgnoreCase);
                    if (modernSrtLayout)
                    {
                        // WoT 1.0+ .srt entries: bit0 castsShadow,
                        // bit1 castsLocalShadow, bit2 alwaysDynamic.
                        castsLocalShadow = (flags & 0x2u) != 0;
                        alwaysDynamic = (flags & 0x4u) != 0;
                    }
                    else
                    {
                        // WoT 0.9.x .spt entries: bit1 is reflectionVisible, bit2 is
                        // castsLocalShadow, bit3 editorOnly/castsShadow.  v0.9.20 also
                        // uses row.Int1=3, so the extension is a safer discriminator.
                        castsLocalShadow = (flags & 0x4u) != 0;
                        alwaysDynamic = false;
                    }

                    SpeedTrees.Add(new SpeedTreePlacement
                    {
                        InstanceIndex = i,
                        ResourceName = resourceName,
                        ResourceFnv = resourceFnv,
                        Seed = seed,
                        CastsShadow = castsShadow,
                        CastsLocalShadow = castsLocalShadow,
                        AlwaysDynamic = alwaysDynamic,
                        VisibilityMask = visibilityMask,
                        Transform = t,
                    });
                }

                WoTLogger.Info($"SpTr: speedtrees={SpeedTrees.Count}, unresolvedNames={unresolved}" +
                               (skippedVisibility > 0 ? $", skipped by visibility={skippedVisibility}" : string.Empty));
            }
            catch (Exception e)
            {
                WoTLogger.Warn($"SpTr parse failed: {e.Message}");
            }
        }

        // =================== BSMI (instances) ===================

        private static BsmiData ReadBsmi(BinaryReader br, Row row, int modelCount)
        {
            var d = new BsmiData();
            br.BaseStream.Position = row.Position;

            var transformsRaw = ReadRawList(br);
            d.Transforms = new float[transformsRaw.Count][];
            for (int i = 0; i < transformsRaw.Count; i++)
            {
                var t = new float[16];
                if (transformsRaw.Items[i].Length >= 16 * 4)
                    Buffer.BlockCopy(transformsRaw.Items[i], 0, t, 0, 16 * 4);
                d.Transforms[i] = t;
            }

            // chunk_models, not needed for geometry import
            if (br.BaseStream.Position + 8 <= row.Position + row.Length)
                ReadRawList(br);

            RawList field2 = default;
            RawList field3 = default;
            bool hasField2 = false, hasField3 = false;
            if (br.BaseStream.Position + 8 <= row.Position + row.Length)
            { field2 = ReadRawList(br); hasField2 = true; }
            if (br.BaseStream.Position + 8 <= row.Position + row.Length)
            { field3 = ReadRawList(br); hasField3 = true; }

            uint[] f2 = hasField2 ? FirstUInts(field2) : Array.Empty<uint>();
            uint[] f3 = hasField3 ? FirstUInts(field3) : Array.Empty<uint>();

            bool f2LooksModel = LooksLikeModelIds(f2, modelCount, d.Transforms.Length);
            bool f3LooksModel = LooksLikeModelIds(f3, modelCount, d.Transforms.Length);

            // Version layout:
            // 0.9.12/0.9.16: field2=bsmo_models_id, field3=visibility/animation;
            // 0.9.20..1.2 : field2=visibility, field3=bsmo_models_id;
            // 1.5+        : field2=visibility, field3=<model_id,count>.
            bool useField3AsModels = false;
            if (hasField3 && field3.ElemSize == 8 && f3LooksModel)
                useField3AsModels = true;
            else if (row.Int1 >= 2 && f3LooksModel)
                useField3AsModels = true;
            else if (!f2LooksModel && f3LooksModel)
                useField3AsModels = true;

            if (useField3AsModels)
            {
                d.ModelIds = f3;
                d.VisibilityMasks = f2.Length == d.Transforms.Length ? f2 : AllVisible(d.Transforms.Length);
            }
            else
            {
                d.ModelIds = f2LooksModel ? f2 : f3;
                if (hasField3 && f3.Length == d.Transforms.Length && !ReferenceEquals(d.ModelIds, f3) && row.Int1 != 1)
                    d.VisibilityMasks = f3;
                else
                    // In 0.9.12 field3 is animations_id, not visibility; importing all
                    // is safer than accidentally hiding animated objects.
                    d.VisibilityMasks = AllVisible(d.Transforms.Length);
            }

            if (d.ModelIds.Length != d.Transforms.Length)
            {
                int n = Math.Min(d.ModelIds.Length, d.Transforms.Length);
                Array.Resize(ref d.ModelIds, n);
                Array.Resize(ref d.Transforms, n);
                Array.Resize(ref d.VisibilityMasks, n);
            }

            return d;
        }

        private static uint[] FirstUInts(RawList list)
        {
            var arr = new uint[list.Count];
            for (int i = 0; i < list.Count; i++)
                arr[i] = FirstUInt(list.Items[i], 0);
            return arr;
        }

        private static bool LooksLikeModelIds(uint[] vals, int modelCount, int instanceCount)
        {
            if (vals == null || vals.Length == 0 || vals.Length != instanceCount || modelCount <= 0)
                return false;
            int ok = 0;
            foreach (var v in vals)
            {
                if (v < modelCount) ok++;
                else if (v == uint.MaxValue) return false;
            }
            return ok >= Math.Max(1, vals.Length * 3 / 4);
        }

        private static uint[] AllVisible(int n)
        {
            var a = new uint[n];
            for (int i = 0; i < n; i++) a[i] = 0xffffffffu;
            return a;
        }

        private static uint[] TryReadVisibilityMasks(BinaryReader br, Row row, int expectedCount)
        {
            try
            {
                var lists = ReadAllLists(br, row);
                foreach (var l in lists)
                {
                    if (l.ElemSize == 4 && (expectedCount <= 0 || l.Count == expectedCount))
                        return FirstUInts(l);
                }
            }
            catch (Exception e)
            {
                WoTLogger.Warn($"BWSV visibility parse failed: {e.Message}");
            }
            return null;
        }

        // =================== BSMO (models) ===================

        private static BsmoData ReadBsmo(BinaryReader br, Row row)
        {
            var d = new BsmoData();
            var lists = ReadAllLists(br, row);
            if (lists.Count == 0) return d;

            d.ModelsLoddings = new (uint, uint)[lists[0].Count];
            for (int i = 0; i < lists[0].Count; i++)
                d.ModelsLoddings[i] = (FirstUInt(lists[0].Items[i], 0), FirstUInt(lists[0].Items[i], 4));

            int renderIdx = -1;
            for (int i = 1; i < lists.Count; i++)
            {
                if (lists[i].ElemSize == 28)
                {
                    renderIdx = i;
                    break;
                }
            }
            if (renderIdx < 1)
            {
                WoTLogger.Warn("BSMO: renders list (elemSize=28) not found");
                return d;
            }

            var lodRenderList = lists[renderIdx - 1];
            d.LodRenders = new (uint, uint)[lodRenderList.Count];
            for (int i = 0; i < lodRenderList.Count; i++)
                d.LodRenders[i] = (FirstUInt(lodRenderList.Items[i], 0), FirstUInt(lodRenderList.Items[i], 4));

            if (renderIdx >= 2 && lists[renderIdx - 2].ElemSize == 4)
            {
                var lodDist = lists[renderIdx - 2];
                d.LodDistances = new float[lodDist.Count];
                for (int i = 0; i < lodDist.Count; i++)
                    d.LodDistances[i] = FirstFloat(lodDist.Items[i]);
            }

            var rendersRaw = lists[renderIdx];
            d.Renders = new RenderItem[rendersRaw.Count];
            for (int i = 0; i < rendersRaw.Count; i++)
            {
                var b = rendersRaw.Items[i];
                d.Renders[i] = new RenderItem
                {
                    MaterialIndex = FirstUInt(b, 8),
                    PrimitiveIndex = FirstUInt(b, 12),
                    VertsNameFnv = FirstUInt(b, 16),
                    PrimsNameFnv = FirstUInt(b, 20),
                };
            }

            int modelInfoIdx = renderIdx - 4;
            if (modelInfoIdx >= 0 && modelInfoIdx < lists.Count && lists[modelInfoIdx].ElemSize == 8 &&
                lists[modelInfoIdx].Count == d.ModelsLoddings.Length)
            {
                var mi = lists[modelInfoIdx];
                d.ModelInfos = new ModelInfoItem[mi.Count];
                for (int i = 0; i < mi.Count; i++)
                    d.ModelInfos[i] = new ModelInfoItem { Type = FirstUInt(mi.Items[i], 0), InfoIndex = FirstUInt(mi.Items[i], 4) };
            }

            // Fragile/structure info is after renders and has destroyed_model_index
            // at byte 28 for both 36-byte and 40-byte versions.
            for (int li = renderIdx + 1; li < lists.Count; li++)
            {
                var l = lists[li];
                if (l.ElemSize != 36 && l.ElemSize != 40) continue;

                int plausible = 0;
                var tmp = new FragileInfoItem[l.Count];
                for (int i = 0; i < l.Count; i++)
                {
                    uint destroyed = FirstUInt(l.Items[i], 28);
                    uint entryType = FirstUInt(l.Items[i], 32);
                    tmp[i] = new FragileInfoItem { DestroyedModelIndex = destroyed, EntryType = entryType };
                    if (destroyed < d.ModelsLoddings.Length && entryType <= 3) plausible++;
                }
                if (l.Count == 0 || plausible > 0)
                {
                    d.FragileInfos = tmp;
                    break;
                }
            }

            WoTLogger.Info($"BSMO: models={d.ModelsLoddings.Length}, lods={d.LodRenders.Length}, renders={d.Renders.Length}, modelInfo={d.ModelInfos.Length}, fragile={d.FragileInfos.Length}");
            return d;
        }

        // =================== BSMA (materials) ===================

        private static BsmaData ReadBsma(BinaryReader br, Row row)
        {
            var d = new BsmaData();
            if (row.Length == 0) return d;
            long end = row.Position + row.Length;
            br.BaseStream.Position = row.Position;

            // materials: [key_fx, key_from, key_to, identifier_fnv, optional identifier2_fnv]
            if (br.BaseStream.Position + 8 <= end)
            {
                var raw = ReadRawList(br);
                d.Materials = new MaterialInfo[raw.Count];
                for (int i = 0; i < raw.Count; i++)
                {
                    var b = raw.Items[i];
                    d.Materials[i] = new MaterialInfo
                    {
                        KeyFx = FirstInt(b, 0),
                        KeyFrom = FirstInt(b, 4),
                        KeyTo = FirstInt(b, 8),
                        IdentifierFnv = FirstUInt(b, 12),
                        Identifier2Fnv = b.Length >= 20 ? FirstUInt(b, 16) : 0u,
                    };
                }
            }

            // fx: u32 fnv list
            if (br.BaseStream.Position + 8 <= end)
            {
                var raw = ReadRawList(br);
                d.Fx = FirstUInts(raw);
            }

            // props: PropertyInfo (prop_fnv, value_type, value)
            if (br.BaseStream.Position + 8 <= end)
            {
                var raw = ReadRawList(br);
                d.Props = new PropInfo[raw.Count];
                for (int i = 0; i < raw.Count; i++)
                {
                    var b = raw.Items[i];
                    d.Props[i] = new PropInfo
                    {
                        PropFnv = FirstUInt(b, 0),
                        ValueType = FirstUInt(b, 4),
                        Value = FirstUInt(b, 8),
                    };
                }
            }

            // matrices: not needed here
            if (br.BaseStream.Position + 8 <= end)
            {
                try { ReadRawList(br); }
                catch { return d; }
            }

            // vectors: value_type 5 references these.
            if (br.BaseStream.Position + 8 <= end)
            {
                try
                {
                    var raw = ReadRawList(br);
                    d.Vectors = new float[raw.Count][];
                    for (int i = 0; i < raw.Count; i++)
                    {
                        var v = new float[4];
                        if (raw.Items[i].Length >= 16)
                            Buffer.BlockCopy(raw.Items[i], 0, v, 0, 16);
                        d.Vectors[i] = v;
                    }
                }
                catch { /* Textures block or version difference; ignore. */ }
            }

            WoTLogger.Info($"BSMA: materials={d.Materials.Length}, fx={d.Fx.Length}, props={d.Props.Length}, vectors={d.Vectors.Length}");
            return d;
        }

        // =================== assemble ===================

        private void BuildModels(
            Dictionary<uint, string> strings,
            BsmiData bsmi, BsmoData bsmo, BsmaData bsma)
        {
            int n = Math.Min(bsmi.Transforms.Length, bsmi.ModelIds.Length);
            int skippedVisibility = 0;

            for (int i = 0; i < n; i++)
            {
                uint mask = (bsmi.VisibilityMasks != null && i < bsmi.VisibilityMasks.Length)
                    ? bsmi.VisibilityMasks[i] : 0xffffffffu;
                if ((mask & CaptureTheFlagVisibility) == 0)
                {
                    skippedVisibility++;
                    continue;
                }

                int modelId = (int)bsmi.ModelIds[i];
                if (modelId < 0 || modelId >= bsmo.ModelsLoddings.Length) continue;

                var placement = new ModelPlacement
                {
                    InstanceIndex = i,
                    ModelId = modelId,
                    Transform = bsmi.Transforms[i],
                };
                placement.Lods = BuildLodsForModel(strings, bsmo, bsma, modelId, false);
                placement.DestroyedModelId = ResolveDestroyedModelId(bsmo, modelId);
                if (placement.DestroyedModelId >= 0)
                    placement.DestroyedLods = BuildLodsForModel(strings, bsmo, bsma, placement.DestroyedModelId, true);

                if (placement.Lods.Count == 0 && placement.DestroyedLods.Count == 0)
                    continue;

                Placements.Add(placement);

                // Legacy flat list: LOD0 intact only, matching old behaviour.
                if (placement.Lods.Count > 0)
                {
                    foreach (var mesh in placement.Lods[0].Meshes)
                    {
                        Models.Add(new ModelInstance
                        {
                            PrimsName = mesh.PrimsName,
                            VertsDataName = mesh.VertsDataName,
                            PrimsDataName = mesh.PrimsDataName,
                            PrimitiveGroup = mesh.PrimitiveGroup,
                            DiffuseTexture = mesh.DiffuseTexture,
                            Transform = placement.Transform,
                        });
                    }
                }
            }

            WoTLogger.Info($"CompiledSpace: {Placements.Count} visible model placements, {Models.Count} legacy LOD0 render instances" +
                           (skippedVisibility > 0 ? $", skipped by visibility={skippedVisibility}" : string.Empty));
        }

        private static List<ModelLod> BuildLodsForModel(
            Dictionary<uint, string> strings, BsmoData bsmo, BsmaData bsma,
            int modelId, bool destroyed)
        {
            var lods = new List<ModelLod>();
            if (modelId < 0 || modelId >= bsmo.ModelsLoddings.Length) return lods;

            var (lodBegin, lodEnd) = bsmo.ModelsLoddings[modelId];
            if (lodBegin >= bsmo.LodRenders.Length) return lods;
            if (lodEnd >= bsmo.LodRenders.Length) lodEnd = (uint)bsmo.LodRenders.Length - 1;
            if (lodEnd < lodBegin) lodEnd = lodBegin;

            for (uint lodId = lodBegin; lodId <= lodEnd; lodId++)
            {
                var (rsBegin, rsEnd) = bsmo.LodRenders[lodId];
                if (rsBegin >= bsmo.Renders.Length) continue;
                if (rsEnd >= bsmo.Renders.Length) rsEnd = (uint)bsmo.Renders.Length - 1;
                if (rsEnd < rsBegin) rsEnd = rsBegin;

                var lod = new ModelLod
                {
                    LodIndex = (int)(lodId - lodBegin),
                    Distance = lodId < bsmo.LodDistances.Length ? bsmo.LodDistances[lodId] : 0f,
                };

                for (uint rsetId = rsBegin; rsetId <= rsEnd; rsetId++)
                {
                    var r = bsmo.Renders[rsetId];
                    if (r.MaterialIndex < bsma.Materials.Length &&
                        bsma.Materials[r.MaterialIndex].KeyFx == -1)
                        continue;

                    var mesh = BuildRenderMesh(strings, bsma, r, (int)rsetId, destroyed);
                    if (mesh != null)
                        lod.Meshes.Add(mesh);
                }

                if (lod.Meshes.Count > 0)
                    lods.Add(lod);
            }

            return lods;
        }

        private static RenderMesh BuildRenderMesh(
            Dictionary<uint, string> strings, BsmaData bsma, RenderItem r,
            int renderSetId, bool destroyed)
        {
            string vertsFull = strings.TryGetValue(r.VertsNameFnv, out var vn) ? vn : null;
            string primsFull = strings.TryGetValue(r.PrimsNameFnv, out var pn) ? pn : null;
            if (string.IsNullOrEmpty(primsFull)) return null;

            SplitName(primsFull, out string primsName, out string primsData);
            SplitName(vertsFull ?? primsFull, out string vertsName, out string vertsData);

            primsName = primsName.Replace(".primitives", ".primitives_processed");
            vertsName = vertsName.Replace(".primitives", ".primitives_processed");
            if (!string.Equals(primsName, vertsName, StringComparison.OrdinalIgnoreCase))
                WoTLogger.Warn($"BSMO render {renderSetId}: verts/prims file differ ('{vertsName}' vs '{primsName}'), using prims file");

            var mesh = new RenderMesh
            {
                RenderSetId = renderSetId,
                PrimitiveGroup = (int)r.PrimitiveIndex,
                MaterialIndex = (int)r.MaterialIndex,
                PrimsName = primsName,
                VertsDataName = vertsData,
                PrimsDataName = primsData,
            };

            if (r.MaterialIndex < bsma.Materials.Length)
            {
                var mat = bsma.Materials[r.MaterialIndex];
                mesh.Identifier = strings.TryGetValue(mat.IdentifierFnv, out var id) ? id : null;
                mesh.IsDestroyedMaterial = destroyed || (!string.IsNullOrEmpty(mesh.Identifier) && mesh.Identifier.StartsWith("d_", StringComparison.OrdinalIgnoreCase));
                if (mat.KeyFx >= 0 && mat.KeyFx < bsma.Fx.Length)
                    mesh.FxName = strings.TryGetValue(bsma.Fx[mat.KeyFx], out var fx) ? fx : null;

                if (mat.KeyFrom >= 0 && mat.KeyTo >= mat.KeyFrom)
                {
                    int to = Math.Min(mat.KeyTo, bsma.Props.Length - 1);
                    for (int k = mat.KeyFrom; k <= to; k++)
                    {
                        var p = bsma.Props[k];
                        string propName = strings.TryGetValue(p.PropFnv, out var pname) ? pname : $"fnv_{p.PropFnv:X8}";
                        var prop = new MaterialProperty
                        {
                            Name = propName,
                            ValueType = p.ValueType,
                            RawValue = p.Value,
                        };
                        if (p.ValueType == 6)
                            prop.StringValue = strings.TryGetValue(p.Value, out var s) ? s : null;
                        else if (p.ValueType == 5 && p.Value < bsma.Vectors.Length)
                            prop.VectorValue = bsma.Vectors[p.Value];

                        mesh.Props[propName] = prop;
                    }
                }
            }

            return mesh;
        }

        private static int ResolveDestroyedModelId(BsmoData bsmo, int modelId)
        {
            if (modelId < 0 || modelId >= bsmo.ModelInfos.Length)
                return -1;
            uint infoIndex = bsmo.ModelInfos[modelId].InfoIndex;
            if (infoIndex >= bsmo.FragileInfos.Length)
                return -1;
            uint destroyed = bsmo.FragileInfos[infoIndex].DestroyedModelIndex;
            if (destroyed == uint.MaxValue || destroyed >= bsmo.ModelsLoddings.Length || destroyed == modelId)
                return -1;
            return (int)destroyed;
        }

        private static void SplitName(string full, out string name, out string dataName)
        {
            if (string.IsNullOrEmpty(full))
            {
                name = string.Empty;
                dataName = string.Empty;
                return;
            }
            full = full.Replace('\\', '/');
            int idx = full.LastIndexOf('/');
            if (idx >= 0) { name = full.Substring(0, idx); dataName = full.Substring(idx + 1); }
            else { name = full; dataName = string.Empty; }
        }

        // FNV-1a 32-bit (WoT uses fnv1a_64 & 0xffffffff -> equivalent low 32 bits).
        public static uint Fnv1a32(string s)
        {
            ulong hval = 0xcbf29ce484222325UL;
            const ulong prime = 0x100000001b3UL;
            foreach (char c in s)
            {
                hval ^= (byte)c;
                hval *= prime;
            }
            return (uint)(hval & 0xffffffff);
        }

        private static string Latin1(byte[] bytes)
        {
            var chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
            return new string(chars);
        }
    }
}
