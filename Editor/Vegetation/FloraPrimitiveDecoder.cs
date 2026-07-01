using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using WoTMapImporter.Editor.Image;
using WoTMapImporter.Editor.Mesh;
using WoTMapImporter.Editor.Package;
using WoTMapImporter.Editor.Utils;

namespace WoTMapImporter.Editor.Vegetation
{
    /// <summary>
    /// Fallback for old WoT 0.8.x vegetation.
    /// Some 0.8.x installs keep the renderable vegetation meshes in shared_content.pkg
    /// under flora/<plant_name>/*.primitives(_processed), while speedtree/<map>/*.spt
    /// is only the placement/procedural SpeedTree side.  This importer searches those
    /// flora primitives by the SpeedTree basename and builds a simple prefab.
    /// </summary>
    public static class FloraPrimitiveDecoder
    {
        public sealed class ImportResult
        {
            public GameObject Prefab;
            public string UsedResource;
            public readonly List<string> Warnings = new List<string>();
        }

        public static ImportResult ImportToPrefab(string outputPath, string speedTreeResource, WoTPackageManager resMgr)
        {
            var result = new ImportResult();
            if (resMgr == null || string.IsNullOrEmpty(speedTreeResource)) return result;

            try
            {
                string primitive = FindFloraPrimitive(speedTreeResource, resMgr);
                if (string.IsNullOrEmpty(primitive))
                {
                    result.Warnings.Add("Flora primitive not found for " + speedTreeResource);
                    return result;
                }

                byte[] data = resMgr.ReadBytes(primitive);
                if (data == null)
                {
                    result.Warnings.Add("Flora primitive bytes not found: " + primitive);
                    return result;
                }

                MeshDataDecoder.DecodedMesh decoded;
                try { decoded = MeshDataDecoder.Decode(data, null, null, -1); }
                catch (Exception e)
                {
                    result.Warnings.Add($"Flora primitive decode failed ({primitive}): {e.Message}");
                    return result;
                }

                if (decoded == null || decoded.Positions == null || decoded.Positions.Length == 0 || decoded.Indices == null || decoded.Indices.Length == 0)
                {
                    result.Warnings.Add("Flora primitive decoded empty mesh: " + primitive);
                    return result;
                }

                result.Prefab = BuildPrefab(outputPath, speedTreeResource, primitive, decoded, resMgr, result.Warnings);
                result.UsedResource = primitive;
                if (result.Prefab != null)
                    WoTLogger.Info($"Flora primitive vegetation: {speedTreeResource} -> {primitive}, vertices={decoded.Positions.Length}, tris={decoded.Indices.Length / 3}");
            }
            catch (Exception e)
            {
                result.Warnings.Add($"Flora primitive import failed ({speedTreeResource}): {e.Message}");
                WoTLogger.Warn($"Flora primitive import failed ({speedTreeResource}): {e.Message}\n{e.StackTrace}");
            }
            return result;
        }

        private static string FindFloraPrimitive(string speedTreeResource, WoTPackageManager resMgr)
        {
            string baseName = PathName(speedTreeResource).ToLowerInvariant();
            if (string.IsNullOrEmpty(baseName)) return null;

            var direct = new List<string>();
            foreach (string ext in new[] { ".primitives_processed", ".primitives" })
            {
                direct.Add($"flora/{baseName}/{baseName}{ext}");
                direct.Add($"flora/{baseName}/{baseName}_lod0{ext}");
                direct.Add($"flora/{baseName}/{baseName}_0{ext}");
                direct.Add($"content/flora/{baseName}/{baseName}{ext}");
                direct.Add($"content/flora/{baseName}/{baseName}_lod0{ext}");
                direct.Add($"content/flora/{baseName}/{baseName}_0{ext}");
            }
            foreach (string p in direct)
                if (resMgr.Exists(p)) return p;

            var matches = new List<string>();
            foreach (string ext in new[] { ".primitives_processed", ".primitives" })
            {
                foreach (string file in resMgr.GetFilesWithExtension(ext))
                {
                    string n = Normalize(file);
                    if (n.IndexOf("/flora/", StringComparison.OrdinalIgnoreCase) < 0 && !n.StartsWith("flora/", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string fileBase = PathName(n).ToLowerInvariant();
                    string parent = ParentName(n).ToLowerInvariant();

                    if (fileBase == baseName || parent == baseName || fileBase.StartsWith(baseName + "_") || fileBase.StartsWith(baseName + "-"))
                        matches.Add(n);
                }
            }

            if (matches.Count == 0) return null;
            matches.Sort((a, b) =>
            {
                int sa = PrimitivePathScore(a, baseName);
                int sb = PrimitivePathScore(b, baseName);
                int c = sb.CompareTo(sa);
                if (c != 0) return c;
                return a.Length.CompareTo(b.Length);
            });
            return matches[0];
        }

        private static int PrimitivePathScore(string path, string baseName)
        {
            string n = Normalize(path);
            string fileBase = PathName(n).ToLowerInvariant();
            string parent = ParentName(n).ToLowerInvariant();
            int score = 0;
            if (parent == baseName) score += 100;
            if (fileBase == baseName) score += 100;
            if (fileBase.StartsWith(baseName + "_lod0")) score += 60;
            if (fileBase.StartsWith(baseName + "_0")) score += 40;
            if (n.EndsWith(".primitives_processed", StringComparison.OrdinalIgnoreCase)) score += 20;
            if (n.Contains("lod0")) score += 10;
            return score;
        }

        private static GameObject BuildPrefab(string outputPath, string speedTreeResource, string primitiveResource, MeshDataDecoder.DecodedMesh decoded, WoTPackageManager resMgr, List<string> warnings)
        {
            string baseName = SafeAssetName(PathName(speedTreeResource));
            string rootDir = $"{outputPath}/VegetationAssets/_DecodedFlora/{baseName}_{StableHash32(primitiveResource):X8}".Replace('\\', '/');
            EnsureFolder(rootDir);

            var positions = new Vector3[decoded.Positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                var p = decoded.Positions[i];
                positions[i] = new Vector3(p.x, p.z, p.y);
            }

            var mesh = new UnityEngine.Mesh
            {
                name = SafeAssetName(baseName + "_flora"),
                indexFormat = positions.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
            };
            mesh.vertices = positions;
            if (decoded.Uv != null) mesh.uv = decoded.Uv;
            if (decoded.Uv2 != null) mesh.uv2 = decoded.Uv2;
            mesh.triangles = decoded.Indices;
            mesh.RecalculateNormals();
            try { mesh.RecalculateTangents(); } catch { }
            mesh.RecalculateBounds();

            string meshPath = $"{rootDir}/Meshes/{mesh.name}_{StableHash32(primitiveResource):X8}.asset";
            SaveAsset(mesh, meshPath);
            var meshAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Mesh>(meshPath) ?? mesh;

            Material mat = CreateMaterial(rootDir, speedTreeResource, primitiveResource, resMgr, warnings);

            var temp = new GameObject(baseName + "_Flora");
            temp.AddComponent<MeshFilter>().sharedMesh = meshAsset;
            var mr = temp.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;

            string prefabPath = $"{rootDir}/{baseName}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(temp, prefabPath);
            UnityEngine.Object.DestroyImmediate(temp);
            return prefab;
        }

        private static Material CreateMaterial(string rootDir, string speedTreeResource, string primitiveResource, WoTPackageManager resMgr, List<string> warnings)
        {
            Texture2D tex = LoadNearbyTexture(rootDir, speedTreeResource, primitiveResource, resMgr, warnings);
            Shader shader = Shader.Find("WoT/ObjectPBS") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
            var mat = new Material(shader) { name = SafeAssetName(PathName(speedTreeResource) + "_flora_mat") };
            if (tex != null)
            {
                SetTextureIfExists(mat, "_BaseMap", tex);
                SetTextureIfExists(mat, "_MainTex", tex);
            }
            else
            {
                SetColorIfExists(mat, "_BaseColor", new Color(0.45f, 0.65f, 0.32f, 1f));
                SetColorIfExists(mat, "_Color", new Color(0.45f, 0.65f, 0.32f, 1f));
            }
            SetFloatIfExists(mat, "_Cutoff", 0.35f);
            SetFloatIfExists(mat, "_AlphaClip", 1f);
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
            mat.SetOverrideTag("RenderType", "TransparentCutout");
            mat.renderQueue = (int)RenderQueue.AlphaTest;
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.doubleSidedGI = true;

            string matPath = $"{rootDir}/Materials/{mat.name}_{StableHash32(primitiveResource):X8}.mat";
            SaveAsset(mat, matPath);
            return AssetDatabase.LoadAssetAtPath<Material>(matPath) ?? mat;
        }

        private static Texture2D LoadNearbyTexture(string rootDir, string speedTreeResource, string primitiveResource, WoTPackageManager resMgr, List<string> warnings)
        {
            string baseName = PathName(speedTreeResource).ToLowerInvariant();
            string floraDir = Path.GetDirectoryName(Normalize(primitiveResource))?.Replace('\\', '/') ?? string.Empty;
            string speedDir = Path.GetDirectoryName(Normalize(speedTreeResource))?.Replace('\\', '/') ?? string.Empty;

            var candidates = new List<string>();
            foreach (string dir in new[] { floraDir, speedDir })
            {
                if (string.IsNullOrEmpty(dir)) continue;
                candidates.Add(dir + "/" + baseName + ".dds");
                candidates.Add(dir + "/" + baseName + "_diffuse.dds");
                candidates.Add(dir + "/" + baseName + "_d.dds");
                candidates.Add(dir + "/compositemap_diffuse.dds");
                candidates.Add(dir + "/compositemap.dds");
            }

            foreach (string ext in new[] { ".dds", ".png", ".tga" })
            {
                foreach (string f in resMgr.GetFilesWithExtension(ext))
                {
                    string n = Normalize(f);
                    if (!string.IsNullOrEmpty(floraDir) && n.StartsWith(floraDir + "/", StringComparison.OrdinalIgnoreCase) && IsLikelyDiffuse(n)) candidates.Add(n);
                    if (!string.IsNullOrEmpty(speedDir) && n.StartsWith(speedDir + "/", StringComparison.OrdinalIgnoreCase) && IsLikelyDiffuse(n)) candidates.Add(n);
                }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string c in candidates)
            {
                if (string.IsNullOrEmpty(c) || !seen.Add(c)) continue;
                byte[] bytes = resMgr.ReadBytes(c);
                if (bytes == null) continue;
                try
                {
                    Texture2D tex;
                    if (bytes.Length >= 4 && BitConverter.ToUInt32(bytes, 0) == DdsDecoder.MAGIC)
                    {
                        try { tex = DdsDecoder.ReadDecompressed(bytes, Path.GetFileNameWithoutExtension(c), false, true); }
                        catch { tex = DdsDecoder.Read(bytes, Path.GetFileNameWithoutExtension(c), false); }
                    }
                    else
                    {
                        tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                        if (!tex.LoadImage(bytes, false)) { UnityEngine.Object.DestroyImmediate(tex); continue; }
                    }
                    tex.name = SafeAssetName(PathName(c) + "_" + StableHash32(c).ToString("X8"));
                    tex.wrapMode = TextureWrapMode.Repeat;
                    tex.filterMode = FilterMode.Bilinear;
                    string texPath = $"{rootDir}/Textures/{tex.name}.asset";
                    SaveAsset(tex, texPath);
                    return AssetDatabase.LoadAssetAtPath<Texture2D>(texPath) ?? tex;
                }
                catch (Exception e) { warnings?.Add($"Flora texture load failed ({c}): {e.Message}"); }
            }
            return null;
        }

        private static bool IsLikelyDiffuse(string name)
        {
            string n = (name ?? string.Empty).ToLowerInvariant();
            return (n.EndsWith(".dds") || n.EndsWith(".png") || n.EndsWith(".tga")) &&
                   !n.Contains("normal") && !n.Contains("_n.") && !n.Contains("_nm") && !n.Contains("spec") && !n.Contains("height");
        }

        private static void SetTextureIfExists(Material mat, string prop, Texture tex)
        {
            if (tex != null && mat.HasProperty(prop)) mat.SetTexture(prop, tex);
            if (tex != null && (prop == "_MainTex" || prop == "_BaseMap")) mat.mainTexture = tex;
        }
        private static void SetColorIfExists(Material mat, string prop, Color color) { if (mat.HasProperty(prop)) mat.SetColor(prop, color); }
        private static void SetFloatIfExists(Material mat, string prop, float value) { if (mat.HasProperty(prop)) mat.SetFloat(prop, value); }

        private static string Normalize(string s) => (s ?? string.Empty).Trim().TrimStart('/').Replace('\\', '/').ToLowerInvariant();
        private static string PathName(string s)
        {
            string n = (s ?? string.Empty).Replace('\\', '/');
            int idx = n.LastIndexOf('/');
            return Path.GetFileNameWithoutExtension(idx >= 0 ? n.Substring(idx + 1) : n);
        }
        private static string ParentName(string s)
        {
            string n = (s ?? string.Empty).Replace('\\', '/');
            int slash = n.LastIndexOf('/');
            if (slash <= 0) return string.Empty;
            n = n.Substring(0, slash);
            slash = n.LastIndexOf('/');
            return slash >= 0 ? n.Substring(slash + 1) : n;
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
