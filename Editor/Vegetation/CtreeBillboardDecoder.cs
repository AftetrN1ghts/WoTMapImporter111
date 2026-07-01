using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using WoTMapImporter.Editor.Image;
using WoTMapImporter.Editor.Package;

namespace WoTMapImporter.Editor.Vegetation
{
    /// <summary>
    /// Fallback importer for old WoT SpeedTree resources (*.ctree / *.spt) when the
    /// original 32-bit SpeedTreeRT.dll is not available.
    ///
    /// It does not reconstruct the full procedural tree mesh.  Instead it extracts
    /// the referenced diffuse texture from the tree file and builds a textured
    /// cross-billboard prefab with the approximate ctree bounds.  This is much more
    /// useful than a green placeholder and works without any native DLL.
    /// </summary>
    public static class CtreeBillboardDecoder
    {
        public sealed class ImportResult
        {
            public GameObject Prefab;
            public readonly List<string> Warnings = new List<string>();
        }

        private struct BoundsInfo
        {
            public float Width;
            public float Height;
        }

        public static bool CanTryDecode(string resourceName, byte[] data)
        {
            if (data == null || data.Length < 64) return false;
            return resourceName.EndsWith(".ctree", StringComparison.OrdinalIgnoreCase) ||
                   resourceName.EndsWith(".spt", StringComparison.OrdinalIgnoreCase);
        }

        public static ImportResult ImportToPrefab(string outputPath, string resourceName, byte[] data, WoTPackageManager resMgr)
        {
            var result = new ImportResult();
            try
            {
                if (!CanTryDecode(resourceName, data))
                {
                    result.Warnings.Add("Unsupported old SpeedTree resource: " + resourceName);
                    return result;
                }

                BoundsInfo bounds = GuessBounds(resourceName, data);
                List<string> textureNames = ExtractTextureNames(data);
                string baseName = SafeAssetName(PathName(resourceName));
                string rootDir = $"{outputPath}/VegetationAssets/_DecodedCTreeBillboards/{baseName}_{StableHash32(resourceName):X8}".Replace('\\', '/');
                EnsureFolder(rootDir);

                Texture2D tex = LoadFirstTexture(rootDir, resourceName, textureNames, resMgr, result.Warnings, out string usedTextureName);
                if (tex == null)
                    result.Warnings.Add("CTree billboard texture not found for " + resourceName);

                var mesh = CreateCrossBillboardMesh(bounds.Width, bounds.Height);
                mesh.name = SafeAssetName(baseName + "_ctree_billboard_mesh");
                string meshPath = $"{rootDir}/Meshes/{mesh.name}_{StableHash32(resourceName + ":mesh"):X8}.asset";
                SaveAsset(mesh, meshPath);
                var meshAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Mesh>(meshPath) ?? mesh;

                Shader shader = Shader.Find("WoT/ObjectPBS") ??
                                Shader.Find("Universal Render Pipeline/Lit") ??
                                Shader.Find("Standard") ??
                                Shader.Find("Sprites/Default");
                var mat = new Material(shader)
                {
                    name = SafeAssetName(baseName + "_ctree_billboard_" + PathName(usedTextureName)),
                    doubleSidedGI = true,
                };
                if (tex != null)
                {
                    if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                    if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
                    mat.mainTexture = tex;
                }
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
                if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", 0.35f);
                if (mat.HasProperty("_AlphaClip")) mat.SetFloat("_AlphaClip", 1f);
                if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
                mat.SetOverrideTag("RenderType", "TransparentCutout");
                mat.SetOverrideTag("Queue", "AlphaTest");
                mat.renderQueue = (int)RenderQueue.AlphaTest;
                mat.EnableKeyword("_ALPHATEST_ON");

                string matPath = $"{rootDir}/Materials/{mat.name}_{StableHash32(resourceName + usedTextureName):X8}.mat";
                SaveAsset(mat, matPath);
                var matAsset = AssetDatabase.LoadAssetAtPath<Material>(matPath) ?? mat;

                var temp = new GameObject(baseName + "_CTreeBillboard");
                var lod0 = new GameObject("LOD0_billboard");
                lod0.transform.SetParent(temp.transform, false);
                lod0.AddComponent<MeshFilter>().sharedMesh = meshAsset;
                var mr = lod0.AddComponent<MeshRenderer>();
                mr.sharedMaterial = matAsset;
                mr.shadowCastingMode = ShadowCastingMode.On;
                mr.receiveShadows = true;

                string prefabPath = $"{rootDir}/{baseName}.prefab";
                result.Prefab = PrefabUtility.SaveAsPrefabAsset(temp, prefabPath);
                UnityEngine.Object.DestroyImmediate(temp);
            }
            catch (Exception e)
            {
                result.Warnings.Add($"CTree billboard import failed ({resourceName}): {e.Message}");
            }
            return result;
        }

        private static BoundsInfo GuessBounds(string resourceName, byte[] data)
        {
            // CTree header starts with bounding min/max floats in old WoT files:
            // int32 count, vec3 min, vec3 max, then extra tree size data.
            if (resourceName.EndsWith(".ctree", StringComparison.OrdinalIgnoreCase) && data.Length >= 28)
            {
                float minX = ReadSingle(data, 4), minY = ReadSingle(data, 8), minZ = ReadSingle(data, 12);
                float maxX = ReadSingle(data, 16), maxY = ReadSingle(data, 20), maxZ = ReadSingle(data, 24);
                if (IsFinite(minX) && IsFinite(minY) && IsFinite(minZ) &&
                    IsFinite(maxX) && IsFinite(maxY) && IsFinite(maxZ) &&
                    maxY > minY && maxX > minX && maxZ > minZ)
                {
                    float width = Mathf.Clamp(Mathf.Max(maxX - minX, maxZ - minZ), 0.5f, 80f);
                    float height = Mathf.Clamp(maxY - minY, 0.5f, 120f);
                    return new BoundsInfo { Width = width, Height = height };
                }
            }

            return new BoundsInfo { Width = 5.0f, Height = 7.0f };
        }

        private static List<string> ExtractTextureNames(byte[] data)
        {
            var result = new List<string>();
            if (data == null) return result;

            // Most strings in .ctree/.spt are int32 length + ascii bytes.
            for (int off = 0; off + 8 < data.Length; off++)
            {
                int len = BitConverter.ToInt32(data, off);
                if (len <= 0 || len > 260 || off + 4 + len > data.Length) continue;
                if (!LooksAscii(data, off + 4, len)) continue;
                string s = Encoding.ASCII.GetString(data, off + 4, len).TrimEnd('\0').Replace('\\', '/');
                AddTextureName(result, s);
            }

            // Fallback: raw ascii scan, useful for partially different builds.
            int i = 0;
            while (i < data.Length)
            {
                while (i < data.Length && !IsPrintable(data[i])) i++;
                int start = i;
                while (i < data.Length && IsPrintable(data[i])) i++;
                int len = i - start;
                if (len >= 4 && len <= 300)
                {
                    string s = Encoding.ASCII.GetString(data, start, len).Replace('\\', '/');
                    AddTextureName(result, s);
                }
            }

            result.Sort((a, b) => TextureScore(b).CompareTo(TextureScore(a)));
            return result;
        }

        private static void AddTextureName(List<string> dst, string s)
        {
            if (string.IsNullOrEmpty(s) || !IsTexture(s) || LooksLikeNonDiffuse(s)) return;
            foreach (var e in dst)
                if (string.Equals(e, s, StringComparison.OrdinalIgnoreCase)) return;
            dst.Add(s);
        }

        private static int TextureScore(string s)
        {
            s = (s ?? string.Empty).ToLowerInvariant();
            int score = 0;
            if (s.Contains("compositemap")) score += 100;
            if (s.Contains("diffuse") || s.Contains("diff") || s.Contains("albedo")) score += 50;
            if (s.Contains("leaf") || s.Contains("leaves")) score += 10;
            if (s.EndsWith(".dds")) score += 5;
            return score;
        }

        private static Texture2D LoadFirstTexture(string rootDir, string resourceName, List<string> textureNames, WoTPackageManager resMgr, List<string> warnings, out string usedTextureName)
        {
            usedTextureName = null;
            if (resMgr == null) return null;

            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in textureNames ?? new List<string>())
            {
                foreach (string candidate in TextureCandidates(resourceName, name))
                {
                    if (!tried.Add(candidate)) continue;
                    Texture2D tex = LoadTexture(rootDir, candidate, resMgr, warnings);
                    if (tex == null) continue;
                    usedTextureName = candidate;
                    return tex;
                }
            }

            // Directory fallback for files like CompositeMap_Diffuse.dds.
            string dir = Path.GetDirectoryName(Normalize(resourceName))?.Replace('\\', '/') ?? string.Empty;
            foreach (string ext in new[] { ".dds", ".png", ".tga", ".jpg", ".jpeg" })
            {
                foreach (string f in resMgr.GetFilesWithExtension(ext))
                {
                    string n = Normalize(f);
                    bool sameDir = !string.IsNullOrEmpty(dir) &&
                                   (n.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase) ||
                                    n.StartsWith("content/" + dir + "/", StringComparison.OrdinalIgnoreCase));
                    if (!sameDir || LooksLikeNonDiffuse(n)) continue;
                    if (!n.Contains("composite") && !n.Contains("diff")) continue;
                    Texture2D tex = LoadTexture(rootDir, n, resMgr, warnings);
                    if (tex == null) continue;
                    usedTextureName = n;
                    return tex;
                }
            }
            return null;
        }

        private static Texture2D LoadTexture(string rootDir, string candidate, WoTPackageManager resMgr, List<string> warnings)
        {
            byte[] bytes = resMgr.ReadBytes(candidate);
            if (bytes == null) return null;
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
                        return null;
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
                warnings?.Add($"CTree texture load failed ({candidate}): {e.Message}");
                return null;
            }
        }

        private static IEnumerable<string> TextureCandidates(string resourceName, string textureName)
        {
            string tex = Normalize(textureName);
            string resDir = Path.GetDirectoryName(Normalize(resourceName))?.Replace('\\', '/') ?? string.Empty;
            string baseNoExt = RemoveExtension(tex);

            foreach (string ext in new[] { Path.GetExtension(tex), ".dds", ".png", ".tga", ".jpg", ".jpeg" })
            {
                if (string.IsNullOrEmpty(ext)) continue;
                string c = baseNoExt + ext.ToLowerInvariant();
                yield return c;
                if (!c.StartsWith("content/", StringComparison.OrdinalIgnoreCase)) yield return "content/" + c;
                if (!string.IsNullOrEmpty(resDir) && c.IndexOf('/') < 0) yield return resDir + "/" + c;
                if (!string.IsNullOrEmpty(resDir) && c.IndexOf('/') < 0) yield return "content/" + resDir + "/" + c;
            }
        }

        private static UnityEngine.Mesh CreateCrossBillboardMesh(float width, float height)
        {
            float hw = width * 0.5f;
            var verts = new[]
            {
                new Vector3(-hw, 0f, 0f), new Vector3( hw, 0f, 0f), new Vector3( hw, height, 0f), new Vector3(-hw, height, 0f),
                new Vector3(0f, 0f, -hw), new Vector3(0f, 0f,  hw), new Vector3(0f, height,  hw), new Vector3(0f, height, -hw),
            };
            var uvs = new[]
            {
                new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1),
                new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1),
            };
            var tris = new[]
            {
                0,2,1, 0,3,2, 0,1,2, 0,2,3,
                4,6,5, 4,7,6, 4,5,6, 4,6,7,
            };
            var mesh = new UnityEngine.Mesh();
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static bool LooksAscii(byte[] data, int offset, int len)
        {
            for (int i = 0; i < len; i++)
            {
                byte b = data[offset + i];
                if (b == 0) continue;
                if (!IsPrintable(b)) return false;
            }
            return true;
        }

        private static bool IsPrintable(byte b) => b >= 32 && b <= 126;
        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        private static float ReadSingle(byte[] data, int offset) => BitConverter.ToSingle(data, offset);

        private static bool IsTexture(string name)
        {
            string n = (name ?? string.Empty).ToLowerInvariant();
            return n.EndsWith(".dds") || n.EndsWith(".png") || n.EndsWith(".tga") || n.EndsWith(".jpg") || n.EndsWith(".jpeg");
        }

        private static bool LooksLikeNonDiffuse(string name)
        {
            string n = (name ?? string.Empty).ToLowerInvariant();
            return n.Contains("normal") || n.Contains("_n.") || n.Contains("_nm") ||
                   n.Contains("spec") || n.Contains("rough") || n.Contains("metal") ||
                   n.Contains("height") || n.Contains("ao");
        }

        private static string Normalize(string s) => (s ?? string.Empty).Trim().TrimStart('/').Replace('\\', '/').ToLowerInvariant();

        private static string RemoveExtension(string s)
        {
            s = (s ?? string.Empty).Replace('\\', '/');
            int slash = s.LastIndexOf('/');
            int dot = s.LastIndexOf('.');
            if (dot > slash) return s.Substring(0, dot);
            return s;
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
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
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
