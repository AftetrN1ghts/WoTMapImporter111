using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityMesh = UnityEngine.Mesh;
using WoTMapImporter.Editor.Data;
using WoTMapImporter.Editor.Image;
using WoTMapImporter.Editor.Package;
using WoTMapImporter.Editor.Utils;

namespace WoTMapImporter.Editor.Terrain
{
    /// <summary>
    /// Builds WoT terrain as regular mesh chunks instead of Unity Terrain.
    ///
    /// This is closer to Simi4/WoT-Blender-Addons: every *.cdata chunk becomes a
    /// MeshRenderer with a custom material that samples the original WoT blend maps.
    /// The important benefit is that we do NOT pass through TerrainData.SetAlphamaps,
    /// because Unity normalizes alphamap weights and WoT does not.
    /// </summary>
    public static class TerrainMeshBuilder
    {
        private static Dictionary<string, Texture2D> _globalTextureCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, FastTextureSampler> _globalSamplerCache = new Dictionary<string, FastTextureSampler>(StringComparer.OrdinalIgnoreCase);

        public class BuildResult
        {
            public GameObject TerrainObject;
            public List<GameObject> ChunkObjects = new List<GameObject>();
            public List<UnityMesh> Meshes = new List<UnityMesh>();
            public List<string> Warnings = new List<string>();
        }

        public static BuildResult Build(
            string outputPath,
            MapInfo mapInfo,
            UniversalTerrain terrain,
            List<TerrainChunk> chunks,
            WoTPackageManager resMgr,
            bool loadWetness,
            bool loadNormals = true,
            int bakeResolution = 2048)
        {
            // Keep the baked albedo a sane power-of-two; guards against silly UI values.
            bakeResolution = Mathf.Clamp(Mathf.ClosestPowerOfTwo(bakeResolution), 512, 4096);
            _globalTextureCache.Clear();
            _globalSamplerCache.Clear();

            if (chunks == null || chunks.Count == 0)
                throw new Exception("No terrain chunks to build");

            EnsureFolder(outputPath);
            EnsureFolder(outputPath + "/Meshes");
            EnsureFolder(outputPath + "/Materials");
            EnsureFolder(outputPath + "/Textures");
            EnsureFolder(outputPath + "/Blends");
            EnsureFolder(outputPath + "/BakedChunks");

            // Automatically clean up old duplicated per-chunk textures from previous imports to eliminate disk bloat
            string texDir = outputPath + "/Textures";
            if (Directory.Exists(texDir))
            {
                try
                {
                    foreach (var file in Directory.GetFiles(texDir))
                    {
                        if (file.Contains("_layer") && !file.Contains("_global_layer_"))
                        {
                            AssetDatabase.DeleteAsset(file.Replace('\\', '/'));
                        }
                    }
                }
                catch { /* ignore cleanup errors */ }
            }

            chunks.Sort((a, b) =>
            {
                int by = a.ChunkPos.y.CompareTo(b.ChunkPos.y);
                return by != 0 ? by : a.ChunkPos.x.CompareTo(b.ChunkPos.x);
            });

            var result = new BuildResult();
            var root = new GameObject(mapInfo.Name + "_TerrainMeshChunks");
            result.TerrainObject = root;

            Texture2D globalMap = loadWetness ? LoadGlobalMapTexture(terrain, resMgr, outputPath) : null;

            // Use bounds from actually loaded chunks for mesh chunk-map UVs. If our
            // minimal BWT2 parser reads bounds differently from the client version,
            // metadata bounds produce exactly the symptom the user reported: every
            // next row starts at a shifted/half-duplicated part of the map texture.
            ComputeActualChunkBounds(chunks, terrain.ChunkSize, out int uvMinX, out int uvMaxX, out int uvMinY, out int uvMaxY);
            WoTLogger.Info($"Mesh terrain UV bounds from chunks: x[{uvMinX}..{uvMaxX}] y[{uvMinY}..{uvMaxY}] " +
                           $"metadata x[{terrain.MinX}..{terrain.MaxX}] y[{terrain.MinY}..{terrain.MaxY}]");

            var shader = Shader.Find("WoT/TerrainChunkBaked");
            if (shader == null)
                result.Warnings.Add("Shader 'WoT/TerrainChunkBaked' not found. Chunks will use URP/Standard material fallback.");

            int built = 0, skipped = 0;
            foreach (var chunk in chunks)
            {
                if (chunk == null || chunk.HeightsTex == null || chunk.Layers == null || chunk.Layers.Count == 0)
                {
                    skipped++;
                    result.Warnings.Add($"Chunk {chunk?.ChunkName ?? "<null>"}: missing heights/layers, skipped");
                    continue;
                }

                try
                {
                    UnityMesh mesh = BuildChunkMesh(chunk, terrain.ChunkSize);
                    string meshPath = outputPath + "/Meshes/" + SafeAssetName(mapInfo.Name + "_" + chunk.ChunkName + "_mesh") + ".asset";
                    SaveAsset(mesh, meshPath);
                    mesh = AssetDatabase.LoadAssetAtPath<UnityMesh>(meshPath) ?? mesh;

                    Material mat = BuildBakedChunkMaterial(shader, outputPath, mapInfo.Name, terrain, chunk, resMgr, globalMap,
                                                       uvMinX, uvMaxX, uvMinY, uvMaxY, loadNormals, bakeResolution);

                    var go = new GameObject(chunk.ChunkName + "_TerrainMesh");
                    go.transform.position = new Vector3(chunk.ChunkPos.x, 0f, chunk.ChunkPos.y);
                    go.transform.SetParent(root.transform, true);

                    var mf = go.AddComponent<MeshFilter>();
                    var mr = go.AddComponent<MeshRenderer>();
                    mf.sharedMesh = mesh;
                    mr.sharedMaterial = mat;

                    result.ChunkObjects.Add(go);
                    result.Meshes.Add(mesh);
                    built++;
                }
                catch (Exception e)
                {
                    skipped++;
                    result.Warnings.Add($"Chunk {chunk.ChunkName}: mesh build failed: {e.Message}");
                    WoTLogger.Warn($"Chunk {chunk.ChunkName}: mesh build failed: {e.Message}\n{e.StackTrace}");
                }
            }

            AssetDatabase.SaveAssets();
            string prefabPath = outputPath + "/" + root.name + ".prefab";
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);

            WoTLogger.Info($"Mesh terrain built: {built}/{chunks.Count} chunks, skipped={skipped}");
            _globalTextureCache.Clear();
            _globalSamplerCache.Clear();
            return result;
        }

        private static void ComputeActualChunkBounds(
            List<TerrainChunk> chunks,
            float chunkSize,
            out int minX,
            out int maxX,
            out int minY,
            out int maxY)
        {
            minX = int.MaxValue;
            maxX = int.MinValue;
            minY = int.MaxValue;
            maxY = int.MinValue;
            foreach (var c in chunks)
            {
                if (c == null) continue;
                int x = Mathf.RoundToInt(c.ChunkPos.x / chunkSize);
                int y = Mathf.RoundToInt(c.ChunkPos.y / chunkSize);
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            if (minX > maxX) { minX = maxX = 0; }
            if (minY > maxY) { minY = maxY = 0; }
        }

        private static UnityMesh BuildChunkMesh(TerrainChunk chunk, float chunkSize)
        {
            int w = chunk.HeightsTex.width;
            int h = chunk.HeightsTex.height;
            var heights = TerrainBuilder.DecodeHeightPixels(chunk.HeightsTex.GetPixels32(), w, h);

            var vertices = new Vector3[w * h];
            var uvs = new Vector2[w * h];

            for (int y = 0; y < h; y++)
            {
                float z = h <= 1 ? 0f : (y / (float)(h - 1)) * chunkSize;
                for (int x = 0; x < w; x++)
                {
                    float vx = w <= 1 ? 0f : (x / (float)(w - 1)) * chunkSize;
                    int i = y * w + x;
                    vertices[i] = new Vector3(vx, heights[i], z);
                    uvs[i] = new Vector2(w <= 1 ? 0f : x / (float)(w - 1), h <= 1 ? 0f : y / (float)(h - 1));
                }
            }

            var triangles = new int[(w - 1) * (h - 1) * 6];
            int ti = 0;
            for (int y = 0; y < h - 1; y++)
            {
                for (int x = 0; x < w - 1; x++)
                {
                    int i = y * w + x;
                    // Unity horizontal plane is X/Z, Y is height. This winding gives up-facing normals.
                    triangles[ti++] = i;
                    triangles[ti++] = i + w;
                    triangles[ti++] = i + 1;
                    triangles[ti++] = i + 1;
                    triangles[ti++] = i + w;
                    triangles[ti++] = i + w + 1;
                }
            }

            var mesh = new UnityMesh
            {
                name = chunk.ChunkName + "_terrain_mesh",
                indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
            };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0, true);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Material BuildBakedChunkMaterial(
            Shader shader,
            string outputPath,
            string mapName,
            UniversalTerrain terrain,
            TerrainChunk chunk,
            WoTPackageManager resMgr,
            Texture2D globalMap,
            int uvMinX,
            int uvMaxX,
            int uvMinY,
            int uvMaxY,
            bool loadNormals,
            int bakeResolution)
        {
            int layerCount = Mathf.Min(chunk.Layers.Count, 16);
            var layerTextures = new Texture2D[layerCount];
            var layerMap = new Vector4[16];
            int loaded = 0, missing = 0;

            for (int i = 0; i < layerCount; i++)
            {
                layerTextures[i] = LoadLayerTexture(resMgr, outputPath, mapName, chunk.ChunkName, i, chunk.Layers[i].Name, false, true);
                layerMap[i] = ComputeBlenderLayerMapping(chunk.Layers[i]);
                if (layerTextures[i] != null) loaded++;
                else missing++;
            }

            Texture2D baked = BakeChunkAlbedo(chunk, terrain.ChunkSize, layerTextures, layerMap, bakeResolution, uvMinX, uvMaxX, uvMinY, uvMaxY);
            baked.name = SafeAssetName(mapName + "_" + chunk.ChunkName + "_baked_albedo");
            string texPath = outputPath + "/BakedChunks/" + baked.name + ".asset";
            SaveAsset(baked, texPath);
            baked = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath) ?? baked;
            baked.wrapMode = TextureWrapMode.Clamp;
            // Trilinear + anisotropy removes the up-close blur and the distance
            // shimmer the old bilinear/no-mip baked chunks had.
            baked.filterMode = FilterMode.Trilinear;
            baked.anisoLevel = 8;

            Material mat;
            if (shader != null) mat = new Material(shader);
            else mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
            mat.name = SafeAssetName(mapName + "_" + chunk.ChunkName + "_baked_mat");

            mat.SetTexture("_MainTex", baked);
            mat.SetTexture("_BaseMap", baked); // URP/Lit fallback
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Brightness", 1f);
            mat.SetFloat("_UVRotation", 0f);
            mat.SetFloat("_UVFlipX", 0f);
            mat.SetFloat("_UVFlipY", 0f);

            // Feed the WoT per-chunk normals (terrain2/normals in the .cdata) into the
            // shader so the terrain actually gets surface detail instead of a flat
            // "bump" default.  Previously this texture was decoded but never assigned.
            if (loadNormals && chunk.NormalsTex != null)
            {
                var nrm = PersistChunkNormal(chunk.NormalsTex, outputPath, mapName, chunk.ChunkName);
                if (nrm != null)
                {
                    mat.SetTexture("_NormalMap", nrm);
                    mat.SetTexture("_BumpMap", nrm); // URP/Lit fallback
                    mat.EnableKeyword("_NORMALMAP");
                    mat.SetFloat("_NormalStrength", 1f);
                }
            }

            string matPath = outputPath + "/Materials/" + mat.name + ".mat";
            SaveAsset(mat, matPath);
            var persistedMat = AssetDatabase.LoadAssetAtPath<Material>(matPath) ?? mat;

            WoTLogger.Info($"Chunk {chunk.ChunkName}: BAKED mesh material {baked.width}x{baked.height}, layers={layerCount}, textures loaded={loaded}, missing={missing}, blends={chunk.BlendTextures?.Count ?? 0}, newFmt={chunk.IsNewBlendFormat}");
            return persistedMat;
        }

        // Persists the per-chunk WoT normal texture (decoded from terrain2/normals)
        // as an asset with mipmaps/anisotropy so the baked terrain shader can sample it.
        private static Texture2D PersistChunkNormal(Texture2D src, string outputPath, string mapName, string chunkName)
        {
            if (src == null) return null;

            var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, true, true)
            {
                name = SafeAssetName(mapName + "_" + chunkName + "_baked_normal"),
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
            };
            tex.SetPixels32(src.GetPixels32());
            tex.Apply(true, false);

            string path = outputPath + "/BakedChunks/" + tex.name + ".asset";
            SaveAsset(tex, path);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path) ?? tex;
        }

        private class FastTextureSampler
        {
            public readonly Color32[] Pixels;
            public readonly int Width;
            public readonly int Height;
            public readonly float WMinus1;
            public readonly float HMinus1;

            public FastTextureSampler(Texture2D tex)
            {
                if (tex != null)
                {
                    Pixels = tex.GetPixels32();
                    Width = tex.width;
                    Height = tex.height;
                    WMinus1 = Width - 1;
                    HMinus1 = Height - 1;
                }
            }

            public Color SampleRepeat(float u, float v)
            {
                if (Pixels == null || Width == 0) return Color.clear;
                float uRep = u - (float)Math.Floor(u);
                float vRep = v - (float)Math.Floor(v);
                float px = uRep * WMinus1;
                float py = vRep * HMinus1;
                int x0 = (int)px;
                int y0 = (int)py;
                int x1 = x0 < (Width - 1) ? x0 + 1 : 0;
                int y1 = y0 < (Height - 1) ? y0 + 1 : 0;
                float tx = px - x0;
                float ty = py - y0;

                Color32 c00 = Pixels[y0 * Width + x0];
                Color32 c10 = Pixels[y0 * Width + x1];
                Color32 c01 = Pixels[y1 * Width + x0];
                Color32 c11 = Pixels[y1 * Width + x1];

                float r0 = c00.r + (c10.r - c00.r) * tx;
                float g0 = c00.g + (c10.g - c00.g) * tx;
                float b0 = c00.b + (c10.b - c00.b) * tx;

                float r1 = c01.r + (c11.r - c01.r) * tx;
                float g1 = c01.g + (c11.g - c01.g) * tx;
                float b1 = c01.b + (c11.b - c01.b) * tx;

                return new Color(
                    (r0 + (r1 - r0) * ty) * 0.003921569f,
                    (g0 + (g1 - g0) * ty) * 0.003921569f,
                    (b0 + (b1 - b0) * ty) * 0.003921569f,
                    1f);
            }

            public Color SampleClamp(float u, float v)
            {
                if (Pixels == null || Width == 0) return Color.clear;
                float px = (u < 0f ? 0f : (u > 1f ? 1f : u)) * WMinus1;
                float py = (v < 0f ? 0f : (v > 1f ? 1f : v)) * HMinus1;
                int x0 = (int)px;
                int y0 = (int)py;
                int x1 = x0 < (Width - 1) ? x0 + 1 : x0;
                int y1 = y0 < (Height - 1) ? y0 + 1 : y0;
                float tx = px - x0;
                float ty = py - y0;

                Color32 c00 = Pixels[y0 * Width + x0];
                Color32 c10 = Pixels[y0 * Width + x1];
                Color32 c01 = Pixels[y1 * Width + x0];
                Color32 c11 = Pixels[y1 * Width + x1];

                float r0 = c00.r + (c10.r - c00.r) * tx;
                float g0 = c00.g + (c10.g - c00.g) * tx;
                float b0 = c00.b + (c10.b - c00.b) * tx;
                float a0 = c00.a + (c10.a - c00.a) * tx;

                float r1 = c01.r + (c11.r - c01.r) * tx;
                float g1 = c01.g + (c11.g - c01.g) * tx;
                float b1 = c01.b + (c11.b - c01.b) * tx;
                float a1 = c01.a + (c11.a - c01.a) * tx;

                return new Color(
                    (r0 + (r1 - r0) * ty) * 0.003921569f,
                    (g0 + (g1 - g0) * ty) * 0.003921569f,
                    (b0 + (b1 - b0) * ty) * 0.003921569f,
                    (a0 + (a1 - a0) * ty) * 0.003921569f);
            }
        }

        private struct LayerBakeData
        {
            public int LocalLayerIndex;
            public FastTextureSampler SplatSampler;
            public FastTextureSampler BlendSampler;
            public bool IsNewBlendFormat;
            public bool IsGlobalMapLayer;
            public float CosA;
            public float SinA;
            public float ScaleX;
            public float ScaleY;
        }

        private static Texture2D BakeChunkAlbedo(
            TerrainChunk chunk,
            float chunkSize,
            Texture2D[] layerTextures,
            Vector4[] layerMap,
            int resolution,
            int uvMinX,
            int uvMaxX,
            int uvMinY,
            int uvMaxY)
        {
            // mipChain=true so distant chunks get proper mip filtering (no shimmer).
            var outTex = new Texture2D(resolution, resolution, TextureFormat.RGBA32, true, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
            };

            var pixels = new Color32[resolution * resolution];
            int layerCount = Mathf.Min(layerTextures != null ? layerTextures.Length : 0, 16);

            // Pre-cache texture samplers and pre-filter active layers
            var cachedBlends = new FastTextureSampler[chunk.BlendTextures != null ? chunk.BlendTextures.Count : 0];
            for (int i = 0; i < cachedBlends.Length; i++)
                cachedBlends[i] = new FastTextureSampler(chunk.BlendTextures[i]);

            var activeLayers = new List<LayerBakeData>();
            FastTextureSampler fallbackSampler = null;

            int chunkX = Mathf.RoundToInt(chunk.ChunkPos.x / chunkSize);
            int chunkY = Mathf.RoundToInt(chunk.ChunkPos.y / chunkSize);
            float numX = Mathf.Max(uvMaxX - uvMinX + 1, 1);
            float numY = Mathf.Max(uvMaxY - uvMinY + 1, 1);
            float chunkST_X = 1f / numX;
            float chunkST_Y = 1f / numY;
            float chunkST_Z = (chunkX - uvMinX) / numX;
            float chunkST_W = (chunkY - uvMinY) / numY;

            for (int li = 0; li < layerCount; li++)
            {
                if (layerTextures[li] == null) continue;
                
                string cacheKey = chunk.Layers != null && li < chunk.Layers.Count && !string.IsNullOrEmpty(chunk.Layers[li].Name) ? chunk.Layers[li].Name.Replace('\\', '/') : layerTextures[li].name;
                if (!_globalSamplerCache.TryGetValue(cacheKey, out var splatSampler))
                {
                    splatSampler = new FastTextureSampler(layerTextures[li]);
                    _globalSamplerCache[cacheKey] = splatSampler;
                }

                if (fallbackSampler == null) fallbackSampler = splatSampler;

                int localIdx = chunk.GlobalToLocalLayerIndices != null && li < chunk.GlobalToLocalLayerIndices.Count ? chunk.GlobalToLocalLayerIndices[li] : li;
                if (localIdx < 0) continue;

                FastTextureSampler blendSampler = null;
                if (chunk.IsNewBlendFormat)
                {
                    int bi = localIdx / 2;
                    if (bi < cachedBlends.Length) blendSampler = cachedBlends[bi];
                }
                else
                {
                    if (localIdx < cachedBlends.Length) blendSampler = cachedBlends[localIdx];
                }

                if (blendSampler == null) continue;

                bool isGlobal = chunk.Layers != null && li < chunk.Layers.Count && !string.IsNullOrEmpty(chunk.Layers[li].Name) &&
                    (chunk.Layers[li].Name.IndexOf("color_tex", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     chunk.Layers[li].Name.IndexOf("border", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     chunk.Layers[li].Name.IndexOf("bound", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     chunk.Layers[li].Name.IndexOf("red_line", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     chunk.Layers[li].Name.IndexOf("barier", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     chunk.Layers[li].Name.IndexOf("barrier", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     chunk.Layers[li].Name.IndexOf("edge", StringComparison.OrdinalIgnoreCase) >= 0);

                Vector4 map = layerMap[li];
                activeLayers.Add(new LayerBakeData
                {
                    LocalLayerIndex = localIdx,
                    SplatSampler = splatSampler,
                    BlendSampler = blendSampler,
                    IsNewBlendFormat = chunk.IsNewBlendFormat,
                    IsGlobalMapLayer = isGlobal,
                    CosA = Mathf.Cos(map.x),
                    SinA = Mathf.Sin(map.x),
                    ScaleX = Mathf.Abs(map.z) > 1e-6f ? map.z : 1f,
                    ScaleY = Mathf.Abs(map.w) > 1e-6f ? map.w : 1f,
                });
            }

            var activeLayerArray = activeLayers.ToArray();
            float chunkPosX = chunk.ChunkPos.x;
            float chunkPosY = chunk.ChunkPos.y;
            float resInv = 1f / resolution;

            // Multithreaded execution across all CPU cores
            System.Threading.Tasks.Parallel.For(0, resolution, y =>
            {
                float v = (y + 0.5f) * resInv;
                float blendV = 1f - v; // Blender Mapping Scale=(1,-1,1) for blend maps
                int rowOffset = y * resolution;

                for (int x = 0; x < resolution; x++)
                {
                    float u = (x + 0.5f) * resInv;
                    float bakeU = u;
                    float bakeV = v;

                    Vector3 acc = Vector3.zero;
                    float wsum = 0f;

                    for (int i = 0; i < activeLayerArray.Length; i++)
                    {
                        var data = activeLayerArray[i];
                        float w = 0f;
                        Vector3 oldFmtWeight = Vector3.zero;

                        if (data.IsNewBlendFormat)
                        {
                            Color c = data.BlendSampler.SampleClamp(bakeU, blendV);
                            w = (data.LocalLayerIndex & 1) == 0 ? c.a : c.g;
                        }
                        else
                        {
                            Color c = data.BlendSampler.SampleClamp(bakeU, blendV);
                            oldFmtWeight = new Vector3(c.r, c.g, c.b);
                            w = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                        }

                        if (w <= 1e-5f) continue;

                        Color col;
                        if (data.IsGlobalMapLayer)
                        {
                            // Map color tint and red boundary lines globally across the entire terrain grid.
                            // Invert V to match DirectX DDS top-to-bottom layout for global textures.
                            Vector2 luv = new Vector2(bakeU * chunkST_X + chunkST_Z, 1f - (bakeV * chunkST_Y + chunkST_W));
                            col = data.SplatSampler.SampleClamp(luv.x, luv.y);
                        }
                        else
                        {
                            float wx = chunkPosX + bakeU * chunkSize;
                            float wz = chunkPosY + bakeV * chunkSize;
                            float rx = wx * data.CosA - wz * data.SinA;
                            float ry = wx * data.SinA + wz * data.CosA;
                            Vector2 luv = new Vector2(WrapWoT(rx / data.ScaleX), WrapWoT(ry / data.ScaleY));
                            col = data.SplatSampler.SampleRepeat(luv.x, luv.y);
                        }

                        if (data.IsNewBlendFormat)
                        {
                            acc.x += col.r * w;
                            acc.y += col.g * w;
                            acc.z += col.b * w;
                            wsum += w;
                        }
                        else
                        {
                            acc.x += col.r * oldFmtWeight.x;
                            acc.y += col.g * oldFmtWeight.y;
                            acc.z += col.b * oldFmtWeight.z;
                            wsum += w;
                        }
                    }

                    if (wsum <= 1e-4f && activeLayerArray.Length > 0 && fallbackSampler != null)
                    {
                        var data = activeLayerArray[0];
                        Color c;
                        if (data.IsGlobalMapLayer)
                        {
                            Vector2 luv = new Vector2(bakeU * chunkST_X + chunkST_Z, 1f - (bakeV * chunkST_Y + chunkST_W));
                            c = fallbackSampler.SampleClamp(luv.x, luv.y);
                        }
                        else
                        {
                            float wx = chunkPosX + bakeU * chunkSize;
                            float wz = chunkPosY + bakeV * chunkSize;
                            float rx = wx * data.CosA - wz * data.SinA;
                            float ry = wx * data.SinA + wz * data.CosA;
                            Vector2 luv = new Vector2(WrapWoT(rx / data.ScaleX), WrapWoT(ry / data.ScaleY));
                            c = fallbackSampler.SampleRepeat(luv.x, luv.y);
                        }
                        acc = new Vector3(c.r, c.g, c.b);
                    }

                    acc.x = acc.x < 0f ? 0f : (acc.x > 1f ? 1f : acc.x);
                    acc.y = acc.y < 0f ? 0f : (acc.y > 1f ? 1f : acc.y);
                    acc.z = acc.z < 0f ? 0f : (acc.z > 1f ? 1f : acc.z);

                    pixels[rowOffset + x] = new Color32(
                        (byte)Mathf.RoundToInt(acc.x * 255f),
                        (byte)Mathf.RoundToInt(acc.y * 255f),
                        (byte)Mathf.RoundToInt(acc.z * 255f),
                        255);
                }
            });

            outTex.SetPixels32(pixels);
            outTex.Apply(true, false); // generate mipmaps
            return outTex;
        }

        private static float WrapWoT(float x)
        {
            const float mn = 0.0625f;
            const float mx = 0.9375f;
            const float span = mx - mn;
            return mn + Mathf.Repeat((x - mn) / span, 1f) * span;
        }

        private static Material BuildChunkMaterial(
            Shader shader,
            string outputPath,
            string mapName,
            UniversalTerrain terrain,
            TerrainChunk chunk,
            WoTPackageManager resMgr,
            Texture2D globalMap,
            int uvMinX,
            int uvMaxX,
            int uvMinY,
            int uvMaxY)
        {
            Material mat;
            if (shader != null)
                mat = new Material(shader);
            else
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));

            mat.name = SafeAssetName(mapName + "_" + chunk.ChunkName + "_mesh_mat");

            // Persist and bind original WoT blend maps. New format: one blend texture controls two layers.
            int blendCount = Mathf.Min(chunk.BlendTextures != null ? chunk.BlendTextures.Count : 0, 8);
            for (int i = 0; i < blendCount; i++)
            {
                var tex = chunk.BlendTextures[i];
                if (tex == null) continue;
                tex.name = SafeAssetName(mapName + "_" + chunk.ChunkName + "_blend" + i);
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
                string path = outputPath + "/Blends/" + tex.name + ".asset";
                SaveAsset(tex, path);
                var persisted = AssetDatabase.LoadAssetAtPath<Texture2D>(path) ?? tex;
                mat.SetTexture("_Blend" + i, persisted);
            }

            // Load and bind layer tile textures.
            int layerCount = Mathf.Min(chunk.Layers.Count, 16);
            int loaded = 0, missing = 0;
            for (int i = 0; i < layerCount; i++)
            {
                var layer = chunk.Layers[i];
                Texture2D splat = LoadLayerTexture(resMgr, outputPath, mapName, chunk.ChunkName, i, layer.Name, false);
                if (splat != null)
                {
                    mat.SetTexture("_Splat" + i, splat);
                    loaded++;
                }
                else
                {
                    missing++;
                    mat.SetTexture("_Splat" + i, Texture2D.whiteTexture);
                    WoTLogger.Warn($"Chunk {chunk.ChunkName}: layer texture not found: {layer.Name}");
                }
            }

            var layerU = new Vector4[16];
            var layerV = new Vector4[16];
            var layerMode = new Vector4[16];
            var layerMap = new Vector4[16];
            for (int i = 0; i < 16; i++)
            {
                if (i < chunk.Layers.Count)
                {
                    layerU[i] = chunk.Layers[i].UProjection;
                    layerV[i] = chunk.Layers[i].VProjection;
                    layerMap[i] = ComputeBlenderLayerMapping(chunk.Layers[i]);
                    // Blender terrain_loader.py old-format hack:
                    // if not new_blend_format and 'color_tex' in layer.name:
                    //     mapping_layer_node = chunk_uv_math_node
                    // These textures are whole-map/color textures, not small tiled
                    // material textures. Treating them as tiled layers creates the
                    // visible shifted strip/frame duplication shown in Unity.
                    if (!chunk.IsNewBlendFormat && !string.IsNullOrEmpty(chunk.Layers[i].Name) &&
                        chunk.Layers[i].Name.IndexOf("color_tex", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        layerMode[i].x = 1f;
                    }
                }
                else
                {
                    layerU[i] = new Vector4(1f, 0f, 0f, 0f);
                    layerV[i] = new Vector4(0f, 0f, 1f, 0f);
                    layerMap[i] = new Vector4(0f, 0f, 1f, 1f);
                }
            }

            mat.SetFloat("_NumLayers", layerCount);
            mat.SetFloat("_NumBlends", blendCount);
            mat.SetFloat("_NewBlendFormat", chunk.IsNewBlendFormat ? 1f : 0f);
            mat.SetVectorArray("_LayerU", layerU);
            mat.SetVectorArray("_LayerV", layerV);
            mat.SetVectorArray("_LayerMode", layerMode);
            mat.SetVectorArray("_LayerMap", layerMap);

            int chunkX = Mathf.RoundToInt(chunk.ChunkPos.x / terrain.ChunkSize);
            int chunkY = Mathf.RoundToInt(chunk.ChunkPos.y / terrain.ChunkSize);
            float numX = Mathf.Max(uvMaxX - uvMinX + 1, 1);
            float numY = Mathf.Max(uvMaxY - uvMinY + 1, 1);
            mat.SetVector("_ChunkUV_ST", new Vector4(
                1f / numX,
                1f / numY,
                (chunkX - uvMinX) / numX,
                (chunkY - uvMinY) / numY));

            mat.SetFloat("_Brightness", 1f);

            // Always set this. It is used both by optional global AM and by old-format
            // color_tex layers that are mapped over the whole terrain.
            mat.SetVector("_TerrainGlobal", new Vector4(
                uvMinX * terrain.ChunkSize,
                uvMinY * terrain.ChunkSize,
                Mathf.Max((uvMaxX - uvMinX + 1) * terrain.ChunkSize, 1f),
                Mathf.Max((uvMaxY - uvMinY + 1) * terrain.ChunkSize, 1f)));

            if (globalMap != null)
            {
                mat.SetTexture("_GlobalMap", globalMap);
                mat.SetFloat("_UseGlobalMap", 1f);
            }
            else
            {
                mat.SetFloat("_UseGlobalMap", 0f);
            }

            string matPath = outputPath + "/Materials/" + mat.name + ".mat";
            SaveAsset(mat, matPath);
            var persistedMat = AssetDatabase.LoadAssetAtPath<Material>(matPath) ?? mat;

            WoTLogger.Info($"Chunk {chunk.ChunkName}: mesh material layers={layerCount} textures loaded={loaded} missing={missing} blends={blendCount} newFmt={chunk.IsNewBlendFormat}");
            return persistedMat;
        }

        private static Vector4 ComputeBlenderLayerMapping(TerrainLayerDef layer)
        {
            // Port of Blender terrain_loader.py:
            //   c1 = u.xyz.cross(v.xyz).normalized()
            //   m.col[0] = uProjection
            //   m.col[1] = (c1.x,c1.y,c1.z,0)
            //   m.col[2] = vProjection
            //   m.invert()
            //   rotation.z = -m.to_euler().y
            //   scale = (-m.to_scale().x, -m.to_scale().z)
            // Unity extraction is not bit-identical to mathutils, but it preserves
            // the important inverse-matrix rotation/scale behaviour and avoids the
            // direct-dot projection that caused row-strip duplication.
            Vector3 u = new Vector3(layer.UProjection.x, layer.UProjection.y, layer.UProjection.z);
            Vector3 v = new Vector3(layer.VProjection.x, layer.VProjection.y, layer.VProjection.z);
            Vector3 c1 = Vector3.Cross(u, v);
            if (c1.sqrMagnitude < 1e-10f)
                c1 = Vector3.up;
            else
                c1.Normalize();

            Matrix4x4 m = Matrix4x4.identity;
            m.SetColumn(0, layer.UProjection);
            m.SetColumn(1, new Vector4(c1.x, c1.y, c1.z, 0f));
            m.SetColumn(2, layer.VProjection);
            Matrix4x4 inv = m.inverse;

            Quaternion q = inv.rotation;
            Vector3 e = q.eulerAngles;
            float rotRad = -e.y * Mathf.Deg2Rad;

            // Matrix4x4.lossyScale can be unreliable for some sheared matrices;
            // column magnitudes match Blender's to_scale() use better here.
            float sx = -new Vector3(inv.m00, inv.m10, inv.m20).magnitude;
            float sy = -new Vector3(inv.m02, inv.m12, inv.m22).magnitude;
            if (Mathf.Abs(sx) < 1e-6f) sx = 1f;
            if (Mathf.Abs(sy) < 1e-6f) sy = 1f;

            return new Vector4(rotRad, 0f, sx, sy);
        }

        private static Texture2D LoadLayerTexture(
            WoTPackageManager resMgr,
            string outputPath,
            string mapName,
            string chunkName,
            int layerIndex,
            string textureName,
            bool linear,
            bool readable = false)
        {
            if (string.IsNullOrEmpty(textureName)) return null;
            string cacheKey = textureName.Replace('\\', '/');
            if (_globalTextureCache.TryGetValue(cacheKey, out var cached))
                return cached;

            byte[] data = resMgr.ReadBytes(textureName) ?? TryAlternatePaths(resMgr, textureName);
            if (data == null) return null;

            Texture2D tex = LoadTexture(data, textureName, linear, readable);
            if (tex == null) return null;

            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Bilinear;
            tex.name = SafeAssetName(mapName + "_global_layer_" + Path.GetFileNameWithoutExtension(textureName));

            string path = outputPath + "/Textures/" + tex.name + ".asset";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(tex, path);
            var persisted = AssetDatabase.LoadAssetAtPath<Texture2D>(path) ?? tex;
            _globalTextureCache[cacheKey] = persisted;
            return persisted;
        }

        private static Texture2D LoadGlobalMapTexture(UniversalTerrain terrain, WoTPackageManager resMgr, string outputPath)
        {
            if (terrain == null || string.IsNullOrEmpty(terrain.GlobalMap))
                return null;

            byte[] data = resMgr.ReadBytes(terrain.GlobalMap) ?? TryAlternatePaths(resMgr, terrain.GlobalMap);
            if (data == null)
            {
                WoTLogger.Warn($"Global terrain AM map not found: {terrain.GlobalMap}");
                return null;
            }

            Texture2D tex = LoadTexture(data, terrain.GlobalMap, true);
            if (tex == null)
            {
                WoTLogger.Warn($"Global terrain AM decode failed: {terrain.GlobalMap}");
                return null;
            }

            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.name = SafeAssetName("WoT_GlobalAM_" + Path.GetFileNameWithoutExtension(terrain.GlobalMap));
            string path = outputPath + "/Textures/" + tex.name + ".asset";
            SaveAsset(tex, path);
            var persisted = AssetDatabase.LoadAssetAtPath<Texture2D>(path) ?? tex;
            WoTLogger.Info($"Loaded global terrain AM map for mesh terrain: {terrain.GlobalMap}");
            return persisted;
        }

        private static Texture2D LoadTexture(byte[] data, string name, bool linear, bool readable = false)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext == ".dds" && data.Length >= 4 && BitConverter.ToUInt32(data, 0) == DdsDecoder.MAGIC)
            {
                try
                {
                    // Baking needs CPU-readable pixels. Most WoT terrain textures are
                    // DXT1/DXT5, which our readable decoder supports.
                    if (readable)
                        return DdsDecoder.ReadReadable(data, Path.GetFileNameWithoutExtension(name));
                    return DdsDecoder.Read(data, Path.GetFileNameWithoutExtension(name), linear);
                }
                catch (Exception e)
                {
                    WoTLogger.Warn($"DDS load failed ({name}): {e.Message}");
                    if (readable)
                    {
                        try { return DdsDecoder.Read(data, Path.GetFileNameWithoutExtension(name), linear); }
                        catch { /* fall through */ }
                    }
                }
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear)
            {
                name = Path.GetFileNameWithoutExtension(name),
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
            };
            if (tex.LoadImage(data, false))
                return tex;

            UnityEngine.Object.DestroyImmediate(tex);
            return null;
        }

        private static byte[] TryAlternatePaths(WoTPackageManager resMgr, string name)
        {
            string n = name.Replace('\\', '/').ToLowerInvariant();
            var candidates = new List<string>();
            if (!n.StartsWith("content/")) candidates.Add("content/" + n);
            if (n.StartsWith("/")) candidates.Add(n.TrimStart('/'));
            if (!n.EndsWith(".dds")) candidates.Add(n + ".dds");
            candidates.Add(Path.GetFileName(n));

            foreach (var c in candidates)
            {
                var data = resMgr.ReadBytes(c);
                if (data != null)
                {
                    WoTLogger.Info($"Resolved terrain texture via alternate path: '{name}' -> '{c}'");
                    return data;
                }
            }
            return null;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            string name = Path.GetFileName(folder);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder(parent, name);
        }

        private static void SaveAsset(UnityEngine.Object asset, string path)
        {
            var old = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (old != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(asset, path);
        }

        private static string SafeAssetName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Replace('/', '_').Replace('\\', '_').Replace(':', '_').Replace(' ', '_');
        }
    }
}
