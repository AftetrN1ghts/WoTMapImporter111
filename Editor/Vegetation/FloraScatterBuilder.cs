using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using WoTMapImporter.Editor.Data;
using WoTMapImporter.Editor.Image;
using WoTMapImporter.Editor.Mesh;
using WoTMapImporter.Editor.Package;
using WoTMapImporter.Editor.Terrain;
using WoTMapImporter.Editor.Utils;
using WoTMapImporter.Editor.Xml;
using WoTMapImporter.Runtime;

namespace WoTMapImporter.Editor.Vegetation
{
    /// <summary>
    /// Procedural ground grass/flora scatter for old WoT/BigWorld maps (0.8.x).
    ///
    /// BigWorld does not store grass as explicit placements: it scatters it at
    /// runtime from <c>spaces/&lt;map&gt;/flora.xml</c>.  Each ecotype in flora.xml is
    /// tied to a terrain surface texture (e.g. <c>Grass_N_2.dds</c>) and lists the
    /// grass meshes (<c>flora/&lt;name&gt;/*.visual</c>) that grow where that texture is
    /// dominant.  This builder reproduces that statically:
    ///
    ///   1. Parse flora.xml -&gt; ecotypes { terrain texture, visual meshes, scale }.
    ///   2. For each terrain chunk, sample the per-layer blend weights the terrain
    ///      importer already decodes to find the dominant grass ecotype per point.
    ///   3. Scatter mesh instances on a deterministic jittered grid, snapping to the
    ///      chunk height field, and merge them into one mesh per chunk+material.
    ///
    /// Density/probability are approximated (the game derives them from a vertex
    /// budget); everything else follows the map data.
    /// </summary>
    public static class FloraScatterBuilder
    {
        public sealed class Settings
        {
            public float Density = 0.25f;          // instances per square metre
            public int MaxInstancesPerChunk = 4000;
            public float WeightThreshold = 0.25f;  // min dominant blend weight to grow
            // Render flora through GPU instancing (store only per-instance matrices)
            // instead of baking a combined mesh per chunk.  Saves a large amount of
            // disk/scene space and draw calls.
            public bool UseGpuInstancing = true;
            public float MaxDrawDistance = 250f;   // coarse LOD: cull patches past this (0 = never)
        }

        // Deduplicate textures/materials across all ecotypes so an atlas or grass
        // material shared by several species is written to disk only once.
        private static readonly Dictionary<string, Texture2D> _texCache =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Material> _matCache =
            new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);

        public sealed class Result
        {
            public GameObject Root;
            public int Instances;
            public readonly List<string> Warnings = new List<string>();
        }

        private sealed class Ecotype
        {
            public string TerrainTexture;          // basename, lower-case
            public readonly List<string> Visuals = new List<string>();
            public readonly List<float> ScaleVariation = new List<float>();
        }

        private sealed class Species
        {
            public UnityEngine.Mesh Mesh;
            public Material Material;
            public float ScaleVariation;
        }

        public static Result Build(
            string outputPath, string spaceName, UniversalTerrain terrain,
            List<TerrainChunk> chunks, WoTPackageManager resMgr, Settings settings)
        {
            var result = new Result();
            if (settings == null) settings = new Settings();
            if (resMgr == null || chunks == null || chunks.Count == 0) return result;

            _texCache.Clear();
            _matCache.Clear();
            string sharedDir = $"{outputPath}/VegetationAssets/_Flora/_Shared".Replace('\\', '/');
            EnsureFolder(sharedDir);

            List<Ecotype> ecotypes;
            try
            {
                ecotypes = ParseFlora(spaceName, resMgr, result.Warnings);
            }
            catch (Exception e)
            {
                result.Warnings.Add($"flora.xml parse failed: {e.Message}");
                return result;
            }
            if (ecotypes.Count == 0)
            {
                result.Warnings.Add("No grass ecotypes found in flora.xml");
                return result;
            }

            // Decode the grass meshes referenced by the ecotypes once.
            var speciesCache = new Dictionary<string, Species>(StringComparer.OrdinalIgnoreCase);
            foreach (var eco in ecotypes)
            {
                for (int i = 0; i < eco.Visuals.Count; i++)
                {
                    string visual = eco.Visuals[i];
                    if (speciesCache.ContainsKey(visual)) continue;
                    var sp = LoadSpecies(outputPath, sharedDir, visual, resMgr, result.Warnings);
                    if (sp != null)
                    {
                        sp.ScaleVariation = i < eco.ScaleVariation.Count ? eco.ScaleVariation[i] : 0f;
                        speciesCache[visual] = sp;
                    }
                }
            }
            if (speciesCache.Count == 0)
            {
                result.Warnings.Add("No flora meshes could be decoded");
                return result;
            }

            var root = new GameObject("Flora");
            float chunkSize = terrain != null ? terrain.ChunkSize : 100f;

            foreach (var chunk in chunks)
            {
                if (chunk == null || chunk.HeightsTex == null || chunk.Layers == null || chunk.Layers.Count == 0)
                    continue;
                try
                {
                    result.Instances += ScatterChunk(chunk, chunkSize, ecotypes, speciesCache, settings, root, outputPath);
                }
                catch (Exception e)
                {
                    result.Warnings.Add($"Flora scatter failed for chunk {chunk.ChunkName}: {e.Message}");
                }
            }

            if (result.Instances == 0)
            {
                UnityEngine.Object.DestroyImmediate(root);
                result.Warnings.Add("Flora produced 0 instances (no matching grass surfaces?)");
                return result;
            }

            result.Root = root;
            WoTLogger.Info($"Flora: scattered {result.Instances} grass instances across {chunks.Count} chunks");
            return result;
        }

        // ============================ flora.xml ============================

        private static List<Ecotype> ParseFlora(string spaceName, WoTPackageManager resMgr, List<string> warnings)
        {
            var ecotypes = new List<Ecotype>();
            byte[] data = resMgr.ReadBytes($"spaces/{spaceName}/flora.xml");
            if (data == null)
            {
                warnings.Add($"flora.xml not found for space '{spaceName}'");
                return ecotypes;
            }

            XmlDocument doc = XmlUnpacker.ReadBytes(data);
            var ecotypesNode = doc?.DocumentElement?.SelectSingleNode("ecotypes") as XmlElement;
            if (ecotypesNode == null) return ecotypes;

            foreach (XmlNode child in ecotypesNode.ChildNodes)
            {
                var grass = child as XmlElement;
                if (grass == null || grass.LocalName != "grass") continue;

                string texture = FirstText(grass, "texture");
                if (string.IsNullOrEmpty(texture)) continue;

                var eco = new Ecotype { TerrainTexture = BaseName(texture) };
                foreach (XmlElement visual in grass.SelectNodes(".//visual"))
                {
                    string path = OwnText(visual);
                    if (string.IsNullOrEmpty(path)) continue;
                    eco.Visuals.Add(path.Trim().Replace('\\', '/'));
                    float sv = ParseFloat(FirstText(visual, "scaleVariation"), 0f);
                    eco.ScaleVariation.Add(sv);
                }
                if (eco.Visuals.Count > 0) ecotypes.Add(eco);
            }
            return ecotypes;
        }

        // ============================ mesh + material ============================

        private static Species LoadSpecies(string outputPath, string sharedDir, string visualResource, WoTPackageManager resMgr, List<string> warnings)
        {
            string primitives = RemoveExtension(visualResource) + ".primitives";
            byte[] primBytes = resMgr.ReadBytes(primitives);
            if (primBytes == null)
            {
                warnings.Add($"Flora primitives not found: {primitives}");
                return null;
            }

            MeshDataDecoder.DecodedMesh decoded;
            try { decoded = MeshDataDecoder.Decode(primBytes, null, null, -1); }
            catch (Exception e)
            {
                warnings.Add($"Flora primitives decode failed ({primitives}): {e.Message}");
                return null;
            }
            if (decoded?.Positions == null || decoded.Positions.Length == 0 ||
                decoded.Indices == null || decoded.Indices.Length == 0)
            {
                warnings.Add($"Flora primitives decoded empty mesh: {primitives}");
                return null;
            }

            string name = SafeAssetName(PathName(visualResource));
            string rootDir = $"{outputPath}/VegetationAssets/_Flora/{name}_{StableHash32(visualResource):X8}".Replace('\\', '/');
            EnsureFolder(rootDir);

            var mesh = new UnityEngine.Mesh
            {
                name = name + "_flora",
                // BigWorld primitives are Y-up like Unity; keep coordinates as-is.
                indexFormat = decoded.Positions.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
                vertices = decoded.Positions,
            };
            if (decoded.Uv != null && decoded.Uv.Length == decoded.Positions.Length) mesh.uv = decoded.Uv;
            mesh.triangles = decoded.Indices;
            mesh.RecalculateNormals();
            // Grass is alpha-cut and unlit-ish; tangents are not used by the flora
            // material, so skip them to keep the mesh asset smaller.
            mesh.RecalculateBounds();

            string meshPath = $"{rootDir}/{mesh.name}.asset";
            SaveAsset(mesh, meshPath);
            var meshAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Mesh>(meshPath) ?? mesh;

            Material mat = BuildMaterial(sharedDir, visualResource, resMgr, warnings);
            return new Species { Mesh = meshAsset, Material = mat };
        }

        private static Material BuildMaterial(string sharedDir, string visualResource, WoTPackageManager resMgr, List<string> warnings)
        {
            string diffuse = ReadDiffuseFromVisual(visualResource, resMgr);

            // Materials with the same diffuse are identical; reuse one asset.
            string matKey = diffuse ?? "__no_texture__";
            if (_matCache.TryGetValue(matKey, out var cachedMat) && cachedMat != null)
                return cachedMat;

            Texture2D tex = diffuse != null ? LoadTexture(sharedDir, diffuse, resMgr, warnings) : null;

            Shader shader = Shader.Find("WoT/ObjectPBS") ?? Shader.Find("Universal Render Pipeline/Lit")
                            ?? Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
            string matName = SafeAssetName((diffuse != null ? PathName(diffuse) : PathName(visualResource)) + "_flora_mat");
            var mat = new Material(shader) { name = matName, enableInstancing = true };
            if (tex != null)
            {
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
                mat.mainTexture = tex;
            }
            else
            {
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(0.42f, 0.6f, 0.3f, 1f));
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", new Color(0.42f, 0.6f, 0.3f, 1f));
            }
            if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", 0.5f);
            if (mat.HasProperty("_AlphaClip")) mat.SetFloat("_AlphaClip", 1f);
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
            mat.SetOverrideTag("RenderType", "TransparentCutout");
            mat.renderQueue = (int)RenderQueue.AlphaTest;
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.doubleSidedGI = true;

            string matPath = $"{sharedDir}/{mat.name}_{StableHash32(matKey):X8}.mat";
            SaveAsset(mat, matPath);
            var saved = AssetDatabase.LoadAssetAtPath<Material>(matPath) ?? mat;
            _matCache[matKey] = saved;
            return saved;
        }

        private static string ReadDiffuseFromVisual(string visualResource, WoTPackageManager resMgr)
        {
            try
            {
                byte[] data = resMgr.ReadBytes(visualResource);
                if (data == null) return null;
                XmlDocument doc = XmlUnpacker.ReadBytes(data);
                if (doc?.DocumentElement == null) return null;

                string first = null;
                foreach (XmlElement prop in doc.DocumentElement.SelectNodes(".//property"))
                {
                    var texNode = prop.SelectSingleNode("Texture");
                    if (texNode == null) continue;
                    string path = texNode.InnerText?.Trim();
                    if (string.IsNullOrEmpty(path)) continue;
                    if (first == null) first = path;
                    if ((OwnText(prop) ?? string.Empty).IndexOf("diffuse", StringComparison.OrdinalIgnoreCase) >= 0)
                        return path.Replace('\\', '/');
                }
                return first?.Replace('\\', '/');
            }
            catch { return null; }
        }

        private static Texture2D LoadTexture(string sharedDir, string resource, WoTPackageManager resMgr, List<string> warnings)
        {
            if (_texCache.TryGetValue(resource, out var cachedTex) && cachedTex != null)
                return cachedTex;

            byte[] bytes = resMgr.ReadBytes(resource);
            if (bytes == null) return null;
            try
            {
                Texture2D tex;
                if (bytes.Length >= 4 && BitConverter.ToUInt32(bytes, 0) == DdsDecoder.MAGIC)
                {
                    try { tex = DdsDecoder.ReadDecompressed(bytes, PathName(resource), false, true); }
                    catch { tex = DdsDecoder.Read(bytes, PathName(resource), false); }
                }
                else
                {
                    tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                    if (!tex.LoadImage(bytes, false)) { UnityEngine.Object.DestroyImmediate(tex); return null; }
                }
                tex.name = SafeAssetName(PathName(resource) + "_" + StableHash32(resource).ToString("X8"));
                tex.wrapMode = TextureWrapMode.Repeat;
                tex.filterMode = FilterMode.Bilinear;
                string texPath = $"{sharedDir}/{tex.name}.asset";
                SaveAsset(tex, texPath);
                var saved = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath) ?? tex;
                _texCache[resource] = saved;
                return saved;
            }
            catch (Exception e)
            {
                warnings?.Add($"Flora texture load failed ({resource}): {e.Message}");
                return null;
            }
        }

        // ============================ scatter ============================

        private static int ScatterChunk(
            TerrainChunk chunk, float chunkSize, List<Ecotype> ecotypes,
            Dictionary<string, Species> speciesCache, Settings settings,
            GameObject root, string outputPath)
        {
            int hw = chunk.HeightsTex.width;
            int hh = chunk.HeightsTex.height;
            float[] heights = TerrainBuilder.DecodeHeightPixels(chunk.HeightsTex.GetPixels32(), hw, hh);

            // Map each ecotype to a terrain layer index in this chunk.
            var ecoLayer = new int[ecotypes.Count];
            bool any = false;
            for (int e = 0; e < ecotypes.Count; e++)
            {
                ecoLayer[e] = FindLayer(chunk, ecotypes[e].TerrainTexture);
                if (ecoLayer[e] >= 0) any = true;
            }
            if (!any) return 0;

            var blendSamplers = new BlendSampler[chunk.BlendTextures != null ? chunk.BlendTextures.Count : 0];
            for (int i = 0; i < blendSamplers.Length; i++)
                blendSamplers[i] = new BlendSampler(chunk.BlendTextures[i]);

            int cells = Mathf.Clamp(Mathf.RoundToInt(Mathf.Sqrt(settings.Density) * chunkSize), 1, 512);

            // Deterministic RNG per chunk so re-imports are stable.
            int chunkX = Mathf.RoundToInt(chunk.ChunkPos.x / chunkSize);
            int chunkY = Mathf.RoundToInt(chunk.ChunkPos.y / chunkSize);
            var rng = new System.Random(unchecked((chunkX * 73856093) ^ (chunkY * 19349663)));

            var groups = new Dictionary<string, InstanceGroup>();
            int placed = 0;

            for (int cy = 0; cy < cells && placed < settings.MaxInstancesPerChunk; cy++)
            {
                for (int cx = 0; cx < cells && placed < settings.MaxInstancesPerChunk; cx++)
                {
                    float u = (cx + (float)rng.NextDouble()) / cells;
                    float v = (cy + (float)rng.NextDouble()) / cells;

                    // Pick dominant grass ecotype at this point (flora "chooseMax").
                    int bestEco = -1;
                    float bestW = settings.WeightThreshold;
                    for (int e = 0; e < ecotypes.Count; e++)
                    {
                        int li = ecoLayer[e];
                        if (li < 0) continue;
                        float w = SampleWeight(chunk, li, blendSamplers, u, v);
                        if (w > bestW) { bestW = w; bestEco = e; }
                    }
                    if (bestEco < 0) continue;

                    // Sparser toward the edges of the surface.
                    if (rng.NextDouble() > Mathf.Clamp01(bestW)) continue;

                    var eco = ecotypes[bestEco];
                    string visual = eco.Visuals[rng.Next(eco.Visuals.Count)];
                    if (!speciesCache.TryGetValue(visual, out var sp)) continue;

                    float height = SampleHeight(heights, hw, hh, u, v);
                    var pos = new Vector3(chunk.ChunkPos.x + u * chunkSize, height, chunk.ChunkPos.y + v * chunkSize);
                    float yaw = (float)rng.NextDouble() * 360f;
                    float scale = 1f + ((float)rng.NextDouble() * 2f - 1f) * sp.ScaleVariation * 0.5f;
                    if (scale < 0.1f) scale = 0.1f;
                    var trs = Matrix4x4.TRS(pos, Quaternion.Euler(0f, yaw, 0f), Vector3.one * scale);

                    if (!groups.TryGetValue(visual, out var group))
                    {
                        group = new InstanceGroup { Species = sp };
                        groups[visual] = group;
                    }
                    group.Matrices.Add(trs);
                    placed++;
                }
            }

            if (placed == 0) return 0;

            var chunkGo = new GameObject($"Flora_{chunk.ChunkName}");
            chunkGo.transform.SetParent(root.transform, false);

            if (settings.UseGpuInstancing)
            {
                foreach (var kv in groups)
                    EmitInstanced(chunkGo, kv.Value, settings);
            }
            else
            {
                string dir = $"{outputPath}/VegetationAssets/_Flora/Chunks".Replace('\\', '/');
                EnsureFolder(dir);
                foreach (var kv in groups)
                    EmitBaked(chunkGo, dir, chunk.ChunkName, kv.Key, kv.Value);
            }
            return placed;
        }

        // GPU-instanced path: store only per-instance matrices, no baked geometry.
        private static void EmitInstanced(GameObject chunkGo, InstanceGroup group, Settings settings)
        {
            if (group.Matrices.Count == 0 || group.Species.Mesh == null) return;

            var go = new GameObject(SafeAssetName(group.Species.Mesh.name) + "_instances");
            go.transform.SetParent(chunkGo.transform, false);

            Bounds meshB = group.Species.Mesh.bounds;
            var bounds = new Bounds(group.Matrices[0].GetColumn(3), Vector3.zero);
            foreach (var m in group.Matrices)
                bounds.Encapsulate(new Bounds((Vector3)m.GetColumn(3), meshB.size));

            var r = go.AddComponent<WoTInstancedRenderer>();
            r.mesh = group.Species.Mesh;
            r.material = group.Species.Material;
            r.instances = group.Matrices.ToArray();
            r.shadowCasting = ShadowCastingMode.Off;
            r.receiveShadows = true;
            r.maxDrawDistance = settings.MaxDrawDistance;
            r.localBounds = bounds;
        }

        // Fallback: bake one combined mesh per chunk+species (larger on disk).
        private static void EmitBaked(GameObject chunkGo, string dir, string chunkName, string visual, InstanceGroup group)
        {
            var batch = new MeshBatch { Species = group.Species };
            foreach (var m in group.Matrices) batch.Add(group.Species.Mesh, m);
            UnityEngine.Mesh combined = batch.Build($"{chunkName}_{SafeAssetName(PathName(visual))}");
            if (combined == null) return;
            string meshPath = $"{dir}/{combined.name}.asset";
            SaveAsset(combined, meshPath);
            combined = AssetDatabase.LoadAssetAtPath<UnityEngine.Mesh>(meshPath) ?? combined;

            var go = new GameObject(combined.name);
            go.transform.SetParent(chunkGo.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = combined;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = group.Species.Material;
            mr.shadowCastingMode = ShadowCastingMode.Off;
        }

        private static int FindLayer(TerrainChunk chunk, string textureBase)
        {
            for (int i = 0; i < chunk.Layers.Count; i++)
            {
                if (string.Equals(BaseName(chunk.Layers[i].Name), textureBase, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private static float SampleWeight(TerrainChunk chunk, int layerIndex, BlendSampler[] blends, float u, float v)
        {
            int localIdx = chunk.GlobalToLocalLayerIndices != null && layerIndex < chunk.GlobalToLocalLayerIndices.Count
                ? chunk.GlobalToLocalLayerIndices[layerIndex] : layerIndex;
            if (localIdx < 0) return 0f;

            float bv = 1f - v; // blend maps are stored V-flipped (matches terrain baker)
            if (chunk.IsNewBlendFormat)
            {
                int bi = localIdx / 2;
                if (bi >= blends.Length) return 0f;
                Color c = blends[bi].Sample(u, bv);
                return (localIdx & 1) == 0 ? c.a : c.g;
            }
            if (localIdx >= blends.Length) return 0f;
            Color col = blends[localIdx].Sample(u, bv);
            return Mathf.Max(col.r, Mathf.Max(col.g, col.b));
        }

        private static float SampleHeight(float[] heights, int w, int h, float u, float v)
        {
            float fx = Mathf.Clamp01(u) * (w - 1);
            float fy = Mathf.Clamp01(v) * (h - 1);
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            int x1 = Mathf.Min(x0 + 1, w - 1), y1 = Mathf.Min(y0 + 1, h - 1);
            float tx = fx - x0, ty = fy - y0;
            float h00 = heights[y0 * w + x0], h10 = heights[y0 * w + x1];
            float h01 = heights[y1 * w + x0], h11 = heights[y1 * w + x1];
            return Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), ty);
        }

        private sealed class BlendSampler
        {
            private readonly Color32[] _px;
            private readonly int _w, _h;
            public BlendSampler(Texture2D tex)
            {
                _px = tex.GetPixels32();
                _w = tex.width;
                _h = tex.height;
            }
            public Color Sample(float u, float v)
            {
                int x = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(u) * (_w - 1)), 0, _w - 1);
                int y = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(v) * (_h - 1)), 0, _h - 1);
                Color32 c = _px[y * _w + x];
                return new Color(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f);
            }
        }

        private sealed class InstanceGroup
        {
            public Species Species;
            public readonly List<Matrix4x4> Matrices = new List<Matrix4x4>();
        }

        private sealed class MeshBatch
        {
            public Species Species;
            private readonly List<Vector3> _verts = new List<Vector3>();
            private readonly List<Vector3> _normals = new List<Vector3>();
            private readonly List<Vector2> _uvs = new List<Vector2>();
            private readonly List<int> _tris = new List<int>();

            public void Add(UnityEngine.Mesh src, Matrix4x4 trs)
            {
                var sv = src.vertices;
                var sn = src.normals;
                var su = src.uv;
                var st = src.triangles;
                int baseIdx = _verts.Count;
                for (int i = 0; i < sv.Length; i++)
                {
                    _verts.Add(trs.MultiplyPoint3x4(sv[i]));
                    _normals.Add(i < sn.Length ? trs.MultiplyVector(sn[i]).normalized : Vector3.up);
                    _uvs.Add(i < su.Length ? su[i] : Vector2.zero);
                }
                for (int i = 0; i < st.Length; i++)
                    _tris.Add(baseIdx + st[i]);
            }

            public UnityEngine.Mesh Build(string name)
            {
                if (_verts.Count == 0) return null;
                var mesh = new UnityEngine.Mesh
                {
                    name = name,
                    indexFormat = _verts.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
                };
                mesh.SetVertices(_verts);
                mesh.SetNormals(_normals);
                mesh.SetUVs(0, _uvs);
                mesh.SetTriangles(_tris, 0, true);
                mesh.RecalculateBounds();
                try { mesh.RecalculateTangents(); } catch { }
                return mesh;
            }
        }

        // ============================ helpers ============================

        private static string FirstText(XmlElement parent, string name)
        {
            var node = parent.SelectSingleNode(name);
            return node?.InnerText?.Trim();
        }

        // BigWorld packed elements can carry their own value plus child elements.
        // XmlUnpacker stores the own value as the element's leading text node.
        private static string OwnText(XmlElement e)
        {
            foreach (XmlNode n in e.ChildNodes)
                if (n.NodeType == XmlNodeType.Text) return n.Value?.Trim();
            return e.HasChildNodes ? null : e.InnerText?.Trim();
        }

        private static float ParseFloat(string s, float fallback)
            => float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;

        private static string BaseName(string s)
            => string.IsNullOrEmpty(s) ? string.Empty : PathName(s).ToLowerInvariant();

        private static string RemoveExtension(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int dot = s.LastIndexOf('.');
            int slash = s.LastIndexOf('/');
            return dot > slash ? s.Substring(0, dot) : s;
        }

        private static string PathName(string s)
        {
            string n = (s ?? string.Empty).Replace('\\', '/');
            int idx = n.LastIndexOf('/');
            return Path.GetFileNameWithoutExtension(idx >= 0 ? n.Substring(idx + 1) : n);
        }

        private static string SafeAssetName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unnamed";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
        }

        private static uint StableHash32(string s)
        {
            unchecked { uint h = 2166136261u; if (s != null) foreach (char c in s) { h ^= char.ToLowerInvariant(c); h *= 16777619u; } return h; }
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
