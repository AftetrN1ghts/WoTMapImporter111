using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using WoTMapImporter.Editor.Package;
using WoTMapImporter.Editor.Utils;

namespace WoTMapImporter.Editor.Vegetation
{
    /// <summary>
    /// Builds the SpeedTree/vegetation hierarchy from compiled-space SpTr records.
    ///
    /// WoT stores placed vegetation in the SpTr section of space.bin.  Modern maps
    /// usually reference SpeedTree runtime files (*.srt).  Unity's native SpeedTree
    /// importer handles Unity-exported *.st/*.spm assets, while WoT's *.srt files may
    /// not be directly importable in every Unity version/project.  Therefore this
    /// builder does three things:
    ///   1) copies the raw WoT tree resource into Assets for inspection/optional import;
    ///   2) uses a matching converted prefab/model if one is available next to it;
    ///   3) creates an obvious billboard placeholder so placement can still be checked.
    ///
    /// To replace placeholders with real trees, put a converted asset with the same
    /// base name near the copied resource, for example:
    ///   Assets/.../VegetationAssets/vegetation/Conifers/Spruce/Spruce_24m.prefab
    /// or Spruce_24m.fbx / Spruce_24m.st / Spruce_24m.spm.
    /// Re-importing the map will then instance that asset at all SpTr transforms.
    /// </summary>
    public static class VegetationBuilder
    {
        public sealed class BuildResult
        {
            public GameObject Root;
            public readonly List<GameObject> CreatedObjects = new List<GameObject>();
            public readonly List<string> Warnings = new List<string>();
        }

        private sealed class BuildContext
        {
            public string OutputPath;
            public WoTPackageManager ResMgr;
            public BuildResult Result;
            public readonly Dictionary<string, SpeciesAsset> SpeciesCache = new Dictionary<string, SpeciesAsset>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> WarnedSpecies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> CopiedSidecarDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public UnityEngine.Mesh PlaceholderMesh;
            public Material PlaceholderMaterial;
        }

        private sealed class SpeciesAsset
        {
            public GameObject Prefab;
            public string AssetPath;
            public bool IsPlaceholder;
        }

        private static readonly string[] ConvertedExtensions = { ".prefab", ".fbx", ".st", ".spm" };
        private static readonly string[] TextureExtensions = { ".dds", ".png", ".tga", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };
        private const bool CreateMissingVegetationPlaceholders = false;

        public static BuildResult Build(
            string outputPath,
            CompiledSpace space,
            WoTPackageManager resMgr)
        {
            var ctx = new BuildContext
            {
                OutputPath = outputPath.Replace('\\', '/'),
                ResMgr = resMgr,
                Result = new BuildResult(),
            };

            ctx.Result.Root = new GameObject("Vegetation");
            if (space == null || space.SpeedTrees.Count == 0)
            {
                ctx.Result.Warnings.Add("CompiledSpace has no SpeedTree/SpTr vegetation placements");
                return ctx.Result;
            }

            int placed = 0;
            int placeholders = 0;
            int missingName = 0;

            foreach (var tree in space.SpeedTrees)
            {
                if (string.IsNullOrEmpty(tree.ResourceName))
                {
                    missingName++;
                    continue;
                }

                var species = GetOrCreateSpeciesAsset(ctx, tree.ResourceName);
                if (species == null || species.Prefab == null)
                    continue;

                GameObject inst = InstantiateSpecies(species.Prefab);
                if (inst == null)
                    continue;

                inst.name = SafeAssetName($"{PathName(tree.ResourceName)}_{tree.InstanceIndex:D5}");
                inst.transform.SetParent(ctx.Result.Root.transform, false);
                ApplyWoTTransform(inst.transform, tree.Transform);
                ApplyTreeRenderFlags(inst, tree);
                RemoveTreeColliders(inst);

                // Do not force static batching: all instances of a species share the
                // same mesh+material, so GPU instancing (enabled on the materials)
                // draws them from a single shared mesh instead of duplicating combined
                // geometry into the scene, which is far smaller on disk/in memory.
                GameObjectUtility.SetStaticEditorFlags(inst,
                    GameObjectUtility.GetStaticEditorFlags(inst) & ~StaticEditorFlags.BatchingStatic);

                ctx.Result.CreatedObjects.Add(inst);
                placed++;
                if (species.IsPlaceholder) placeholders++;
            }

            ctx.Result.Warnings.Add($"Vegetation imported: {placed}/{space.SpeedTrees.Count} SpTr placements, species={ctx.SpeciesCache.Count}, placeholders={placeholders}" +
                                    (missingName > 0 ? $", unresolved resource names={missingName}" : string.Empty));
            if (placeholders > 0)
            {
                ctx.Result.Warnings.Add(
                    "Some WoT SpeedTree files could not be loaded as Unity GameObjects and were replaced with green billboard placeholders. " +
                    "Convert the corresponding .srt/.spt trees to Unity-compatible .st/.spm/.fbx/prefab assets with the same base name under the copied VegetationAssets folder, then import again.");
            }
            return ctx.Result;
        }

        private static SpeciesAsset GetOrCreateSpeciesAsset(BuildContext ctx, string resourceName)
        {
            string resource = NormalizeResource(resourceName);
            if (ctx.SpeciesCache.TryGetValue(resource, out var cached))
                return cached;

            var species = new SpeciesAsset();

            // Prefer user-provided converted assets.  This is the intended path for
            // WoT *.srt files when Unity cannot import them directly.
            species.Prefab = TryFindConvertedGameObject(ctx, resource, out species.AssetPath);

            // Copy the raw WoT resource and try Unity's importer as a best effort.
            if (species.Prefab == null)
            {
                string rawAssetPath = CopyResourceToAssets(ctx, resource, out string resolvedResource, out byte[] rawBytes);
                if (!string.IsNullOrEmpty(rawAssetPath))
                {
                    species.AssetPath = rawAssetPath;
                    CopySidecarTexturesOnce(ctx, resolvedResource);

                    // Preferred paths for original WoT trees:
                    //   - 0.8.x may keep renderable meshes in shared_content.pkg under
                    //     flora/<plant_name>/*.primitives(_processed), while the chunk
                    //     references speedtree/<map>/<plant>.spt. Try flora first.
                    //   - 1.0+ usually uses SpeedTree runtime *.srt.
                    //   - *.ctree decoder is kept as optional/experimental fallback.
                    var floraImport = FloraPrimitiveDecoder.ImportToPrefab(ctx.OutputPath, resource, ctx.ResMgr);
                    if (floraImport.Prefab != null)
                    {
                        species.Prefab = floraImport.Prefab;
                        species.AssetPath = AssetDatabase.GetAssetPath(floraImport.Prefab);
                    }
                    foreach (var w in floraImport.Warnings)
                        ctx.Result.Warnings.Add(w);

                    if (species.Prefab == null && resolvedResource.EndsWith(".ctree", StringComparison.OrdinalIgnoreCase) &&
                        rawBytes != null && CtreeMeshDecoder.IsCtree(rawBytes))
                    {
                        var ctreeImport = CtreeMeshDecoder.ImportToPrefab(ctx.OutputPath, resolvedResource, rawBytes, ctx.ResMgr);
                        if (ctreeImport.Prefab != null)
                        {
                            species.Prefab = ctreeImport.Prefab;
                            species.AssetPath = AssetDatabase.GetAssetPath(ctreeImport.Prefab);
                        }
                        foreach (var w in ctreeImport.Warnings)
                            ctx.Result.Warnings.Add(w);
                    }
                    else if (species.Prefab == null && resolvedResource.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) &&
                             rawBytes != null && SrtMeshDecoder.IsSrt(rawBytes))
                    {
                        var srtImport = SrtMeshDecoder.ImportToPrefab(ctx.OutputPath, resolvedResource, rawBytes, ctx.ResMgr);
                        if (srtImport.Prefab != null)
                        {
                            species.Prefab = srtImport.Prefab;
                            species.AssetPath = AssetDatabase.GetAssetPath(srtImport.Prefab);
                        }
                        foreach (var w in srtImport.Warnings)
                            ctx.Result.Warnings.Add(w);
                    }
                    else if (resolvedResource.EndsWith(".spt", StringComparison.OrdinalIgnoreCase))
                    {
                        species.Prefab = TryImportSptViaDumper(ctx, rawAssetPath, resolvedResource);
                        if (species.Prefab != null) species.AssetPath = AssetDatabase.GetAssetPath(species.Prefab);
                    }

                    // Fallback/best-effort: if Unity can import the raw resource (or
                    // the user installed an importer), use that.
                    if (species.Prefab == null)
                    {
                        AssetDatabase.ImportAsset(rawAssetPath, ImportAssetOptions.ForceSynchronousImport);
                        species.Prefab = AssetDatabase.LoadAssetAtPath<GameObject>(rawAssetPath);
                    }
                }
            }

            // Re-check converted assets after sidecar import; useful if a .st/.spm was
            // already present but Unity had not imported it before this run.
            if (species.Prefab == null)
                species.Prefab = TryFindConvertedGameObject(ctx, resource, out species.AssetPath);

            if (species.Prefab == null)
            {
                if (!CreateMissingVegetationPlaceholders)
                {
                    WarnSpeciesOnce(ctx, resource, "No Unity-importable SpeedTree asset found for " + resource + "; skipped");
                    ctx.SpeciesCache[resource] = null;
                    return null;
                }

                species.Prefab = CreatePlaceholderPrefab(ctx, resource);
                species.AssetPath = AssetDatabase.GetAssetPath(species.Prefab);
                species.IsPlaceholder = true;
                WarnSpeciesOnce(ctx, resource, "No Unity-importable SpeedTree asset found for " + resource + "; using billboard placeholder");
            }

            ctx.SpeciesCache[resource] = species;
            return species;
        }

        private static GameObject TryImportSptViaDumper(BuildContext ctx, string sptAssetPath, string resolvedResource)
        {
            try
            {
                string fullSpt = ToFullProjectPath(sptAssetPath);
                string rootDir = $"{ctx.OutputPath}/VegetationAssets/_DecodedSPT/{SafeAssetName(PathName(resolvedResource))}_{StableHash32(resolvedResource):X8}".Replace('\\', '/');
                EnsureFolder(rootDir);

                string objAssetPath = $"{rootDir}/{SafeAssetName(PathName(resolvedResource))}.obj";
                string fullObj = ToFullProjectPath(objAssetPath);

                var existing = AssetDatabase.LoadAssetAtPath<GameObject>(objAssetPath);
                if (existing != null) return existing;

                string dumperExe = ToFullProjectPath($"{ctx.OutputPath}/VegetationAssets/_DecodedSPT/SptDumper.exe");
                EnsureFolder($"{ctx.OutputPath}/VegetationAssets/_DecodedSPT");

                if (!File.Exists(dumperExe))
                {
                    string cscPath = @"C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe";
                    if (!File.Exists(cscPath)) cscPath = @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe";
                    
                    string[] csFiles = AssetDatabase.FindAssets("SptDumper");
                    string csPath = csFiles.Length > 0 ? AssetDatabase.GUIDToAssetPath(csFiles[0]) : "Assets/WoTMapImporter11/Editor/Vegetation/SptDumper.cs";
                    string fullCs = ToFullProjectPath(csPath);

                    if (File.Exists(cscPath) && File.Exists(fullCs))
                    {
                        var psiCsc = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = cscPath,
                            Arguments = $"/target:exe /platform:x86 /out:\"{dumperExe}\" \"{fullCs}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                        };
                        using (var pCsc = System.Diagnostics.Process.Start(psiCsc))
                        {
                            string cscOut = pCsc.StandardOutput.ReadToEnd();
                            string cscErr = pCsc.StandardError.ReadToEnd();
                            pCsc.WaitForExit();
                            if (pCsc.ExitCode != 0)
                            {
                                WoTLogger.Warn($"Failed to compile SptDumper: {cscOut}\n{cscErr}");
                            }
                        }
                    }
                }

                if (File.Exists(dumperExe))
                {
                    string dllPath = "";
                    string unpackDir = ctx.ResMgr.UnpackDir;
                    if (!string.IsNullOrEmpty(unpackDir))
                    {
                        string gameRoot = Path.GetDirectoryName(unpackDir);
                        dllPath = Path.Combine(gameRoot, "SpeedTreeRT.dll");
                        if (!File.Exists(dllPath)) dllPath = Path.Combine(gameRoot, "SpeedTreeRT_wo.dll");
                        if (!File.Exists(dllPath)) dllPath = Path.Combine(gameRoot, "SpeedTreeRT_wo_32.dll");
                    }
                    if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
                    {
                        dllPath = "SpeedTreeRT.dll";
                    }

                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = dumperExe,
                        Arguments = $"\"{fullSpt}\" \"{fullObj}\" \"{dllPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using (var p = System.Diagnostics.Process.Start(psi))
                    {
                        string outStr = p.StandardOutput.ReadToEnd();
                        string errStr = p.StandardError.ReadToEnd();
                        p.WaitForExit();
                        if (p.ExitCode != 0 || !File.Exists(fullObj))
                        {
                            WoTLogger.Warn($"SptDumper failed for {resolvedResource}: {outStr}\n{errStr}");
                            return null;
                        }
                    }

                    AssetDatabase.ImportAsset(objAssetPath, ImportAssetOptions.ForceSynchronousImport);
                    
                    var loadedObj = AssetDatabase.LoadAssetAtPath<GameObject>(objAssetPath);
                    if (loadedObj != null)
                    {
                        var tempInst = UnityEngine.Object.Instantiate(loadedObj);
                        tempInst.name = SafeAssetName(PathName(resolvedResource));
                        foreach (var mr in tempInst.GetComponentsInChildren<MeshRenderer>(true))
                        {
                            if (mr.sharedMaterial != null)
                            {
                                string matName = mr.sharedMaterial.name;
                                Shader sh = Shader.Find("WoT/ObjectPBS") ?? Shader.Find("Universal Render Pipeline/Lit");
                                var newMat = new Material(sh) { name = matName };
                                if (mr.sharedMaterial.mainTexture != null)
                                {
                                    newMat.mainTexture = mr.sharedMaterial.mainTexture;
                                    newMat.SetTexture("_BaseMap", mr.sharedMaterial.mainTexture);
                                    newMat.SetTexture("_MainTex", mr.sharedMaterial.mainTexture);
                                }
                                newMat.SetFloat("_AlphaClip", 1f);
                                newMat.SetFloat("_Cutoff", 0.35f);
                                newMat.SetFloat("_Cull", 0f);
                                newMat.SetOverrideTag("RenderType", "TransparentCutout");
                                newMat.SetOverrideTag("Queue", "AlphaTest");
                                newMat.renderQueue = 2450;
                                
                                string matAssetPath = $"{rootDir}/{matName}_{StableHash32(matName + resolvedResource):X8}.mat";
                                SaveAsset(newMat, matAssetPath);
                                mr.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(matAssetPath) ?? newMat;
                            }
                        }
                        
                        string prefabPath = $"{rootDir}/{SafeAssetName(PathName(resolvedResource))}.prefab";
                        var prefab = PrefabUtility.SaveAsPrefabAsset(tempInst, prefabPath);
                        UnityEngine.Object.DestroyImmediate(tempInst);
                        return prefab;
                    }
                }
            }
            catch (Exception ex)
            {
                WoTLogger.Warn($"Error running SptDumper for {resolvedResource}: {ex.Message}");
            }
            return null;
        }

        private static uint StableHash32(string s) { unchecked { uint h = 2166136261u; if (s == null) return h; foreach (char c in s) { h ^= (uint)char.ToLowerInvariant(c); h *= 16777619u; } return h; } }

        private static void SaveAsset(UnityEngine.Object obj, string path)
        {
            path = path.Replace('\\', '/');
            EnsureFolder(Path.GetDirectoryName(path)?.Replace('\\', '/'));
            var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (existing != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(obj, path);
            AssetDatabase.ImportAsset(path);
        }

        private static GameObject InstantiateSpecies(GameObject prefab)
        {
            if (prefab == null) return null;
            try
            {
                var obj = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if (obj != null) return obj;
            }
            catch
            {
                // Model assets are not always prefab assets; fall back to Instantiate.
            }
            return UnityEngine.Object.Instantiate(prefab);
        }

        private static void ApplyTreeRenderFlags(GameObject go, CompiledSpace.SpeedTreePlacement tree)
        {
            if (go == null) return;
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                r.shadowCastingMode = tree.CastsShadow ? ShadowCastingMode.On : ShadowCastingMode.Off;
                r.receiveShadows = true;
            }
        }


        private static void RemoveTreeColliders(GameObject go)
        {
            if (go == null) return;
            var colliders = go.GetComponentsInChildren<Collider>(true);
            foreach (var c in colliders)
            {
                if (c != null) UnityEngine.Object.DestroyImmediate(c);
            }
        }

        // ===================== resource copying / lookup =====================

        private static GameObject TryFindConvertedGameObject(BuildContext ctx, string resource, out string assetPath)
        {
            assetPath = null;
            string relNoExt = RemoveExtension(resource);

            foreach (var ext in ConvertedExtensions)
            {
                string p = $"{ctx.OutputPath}/VegetationAssets/{relNoExt}{ext}".Replace('\\', '/');
                if (IsGeneratedDecodedSrtPath(p)) continue;
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                if (go != null)
                {
                    assetPath = p;
                    return go;
                }
            }

            // Also allow placing converted assets in a flatter folder by basename.
            string baseName = PathName(resource);
            foreach (var ext in ConvertedExtensions)
            {
                string p = $"{ctx.OutputPath}/VegetationAssets/{baseName}{ext}".Replace('\\', '/');
                if (IsGeneratedDecodedSrtPath(p)) continue;
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                if (go != null)
                {
                    assetPath = p;
                    return go;
                }
            }

            // Last resort: project-wide search by exact basename.  This lets a user
            // keep a shared vegetation library elsewhere in Assets.
            string[] guids = AssetDatabase.FindAssets(baseName + " t:GameObject");
            foreach (var guid in guids)
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (IsGeneratedDecodedSrtPath(p)) continue;
                string n = Path.GetFileNameWithoutExtension(p);
                if (!string.Equals(n, baseName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                if (go != null)
                {
                    assetPath = p;
                    return go;
                }
            }

            return null;
        }


        private static bool IsGeneratedDecodedSrtPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            string p = assetPath.Replace('\\', '/');
            return p.IndexOf("/VegetationAssets/_DecodedSRT/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   p.IndexOf("/VegetationAssets/_DecodedCTREE/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   p.IndexOf("/VegetationAssets/_DecodedFlora/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string CopyResourceToAssets(BuildContext ctx, string resource, out string resolvedResource, out byte[] rawBytes)
        {
            resolvedResource = resource;
            rawBytes = null;
            foreach (var candidate in ResourceCandidates(resource))
            {
                byte[] data = ctx.ResMgr.ReadBytes(candidate);
                if (data == null) continue;

                resolvedResource = NormalizeResource(candidate);
                rawBytes = data;
                string assetPath = $"{ctx.OutputPath}/VegetationAssets/{candidate}".Replace('\\', '/');
                assetPath = SanitizeAssetPath(assetPath);
                WriteBytesIfDifferent(assetPath, data);
                return assetPath;
            }

            WarnSpeciesOnce(ctx, resource, "Vegetation resource not found in packages: " + resource);
            return null;
        }

        private static void CopySidecarTexturesOnce(BuildContext ctx, string resource)
        {
            string dir = Path.GetDirectoryName(resource)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(dir) || !ctx.CopiedSidecarDirs.Add(dir))
                return;

            // Copy texture files from the same vegetation directory.  Unity SpeedTree
            // importers need textures to exist next to/near the model asset.
            int copied = 0;
            foreach (string ext in TextureExtensions)
            {
                foreach (string file in ctx.ResMgr.GetFilesWithExtension(ext))
                {
                    string n = NormalizeResource(file);
                    if (!n.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase))
                        continue;
                    byte[] data = ctx.ResMgr.ReadBytes(n);
                    if (data == null) continue;
                    string assetPath = SanitizeAssetPath($"{ctx.OutputPath}/VegetationAssets/{n}".Replace('\\', '/'));
                    WriteBytesIfDifferent(assetPath, data);
                    copied++;
                }
            }

            if (copied > 0)
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        private static IEnumerable<string> ResourceCandidates(string resource)
        {
            string n = NormalizeResource(resource);
            string baseNoExt = RemoveExtension(n);

            // Old maps usually store <spt>AppleTree.spt</spt> in chunk XML,
            // while the actually renderable, already-compiled resource is the sibling
            // AppleTree.ctree.  Prefer it before falling back to DLL-based *.spt.
            yield return baseNoExt + ".ctree";
            yield return baseNoExt + ".srt";
            yield return baseNoExt + ".fbx";
            yield return baseNoExt + ".prefab";
            yield return baseNoExt + ".st";
            yield return baseNoExt + ".spm";
            yield return baseNoExt + ".spt";
            yield return n;

            if (!n.StartsWith("content/", StringComparison.OrdinalIgnoreCase))
            {
                string cn = "content/" + baseNoExt;
                yield return cn + ".ctree";
                yield return cn + ".srt";
                yield return cn + ".fbx";
                yield return cn + ".prefab";
                yield return cn + ".st";
                yield return cn + ".spm";
                yield return cn + ".spt";
                yield return "content/" + n;
            }
        }

        private static void WriteBytesIfDifferent(string assetPath, byte[] data)
        {
            assetPath = assetPath.Replace('\\', '/');
            EnsureFolder(Path.GetDirectoryName(assetPath)?.Replace('\\', '/'));

            string full = ToFullProjectPath(assetPath);
            if (File.Exists(full))
            {
                var existing = File.ReadAllBytes(full);
                if (existing.Length == data.Length)
                {
                    bool same = true;
                    for (int i = 0; i < existing.Length; i++)
                    {
                        if (existing[i] != data[i]) { same = false; break; }
                    }
                    if (same) return;
                }
            }

            File.WriteAllBytes(full, data);
        }

        private static string ToFullProjectPath(string assetPath)
        {
            assetPath = assetPath.Replace('\\', '/');
            if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Asset path must be under Assets/: " + assetPath);
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        // ===================== placeholders =====================

        private static GameObject CreatePlaceholderPrefab(BuildContext ctx, string resource)
        {
            EnsurePlaceholderAssets(ctx);

            string prefabPath = $"{ctx.OutputPath}/VegetationAssets/_Placeholders/{SafeAssetName(PathName(resource))}_placeholder.prefab";
            prefabPath = prefabPath.Replace('\\', '/');
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (existing != null) return existing;

            EnsureFolder(Path.GetDirectoryName(prefabPath)?.Replace('\\', '/'));
            var temp = new GameObject(SafeAssetName(PathName(resource)) + "_placeholder");
            var mf = temp.AddComponent<MeshFilter>();
            mf.sharedMesh = ctx.PlaceholderMesh;
            var mr = temp.AddComponent<MeshRenderer>();
            mr.sharedMaterial = ctx.PlaceholderMaterial;

            var prefab = PrefabUtility.SaveAsPrefabAsset(temp, prefabPath);
            UnityEngine.Object.DestroyImmediate(temp);
            return prefab;
        }

        private static void EnsurePlaceholderAssets(BuildContext ctx)
        {
            if (ctx.PlaceholderMesh != null && ctx.PlaceholderMaterial != null)
                return;

            string dir = $"{ctx.OutputPath}/VegetationAssets/_Placeholders".Replace('\\', '/');
            EnsureFolder(dir);

            string meshPath = dir + "/WoT_VegetationPlaceholderMesh.asset";
            ctx.PlaceholderMesh = AssetDatabase.LoadAssetAtPath<UnityEngine.Mesh>(meshPath);
            if (ctx.PlaceholderMesh == null)
            {
                ctx.PlaceholderMesh = CreateCrossBillboardMesh();
                ctx.PlaceholderMesh.name = "WoT_VegetationPlaceholderMesh";
                AssetDatabase.CreateAsset(ctx.PlaceholderMesh, meshPath);
            }

            string matPath = dir + "/WoT_VegetationPlaceholder.mat";
            ctx.PlaceholderMaterial = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (ctx.PlaceholderMaterial == null)
            {
                Shader sh = Shader.Find("Universal Render Pipeline/Unlit") ??
                            Shader.Find("Unlit/Color") ??
                            Shader.Find("Standard") ??
                            Shader.Find("Sprites/Default");
                ctx.PlaceholderMaterial = new Material(sh) { name = "WoT_VegetationPlaceholder" };
                if (ctx.PlaceholderMaterial.HasProperty("_BaseColor"))
                    ctx.PlaceholderMaterial.SetColor("_BaseColor", new Color(0.1f, 0.65f, 0.18f, 0.65f));
                if (ctx.PlaceholderMaterial.HasProperty("_Color"))
                    ctx.PlaceholderMaterial.SetColor("_Color", new Color(0.1f, 0.65f, 0.18f, 0.65f));
                AssetDatabase.CreateAsset(ctx.PlaceholderMaterial, matPath);
            }
        }

        private static UnityEngine.Mesh CreateCrossBillboardMesh()
        {
            // Unit-height cross planes.  The WoT transform scale controls actual size.
            var verts = new[]
            {
                new Vector3(-0.35f, 0f, 0f), new Vector3( 0.35f, 0f, 0f), new Vector3( 0.35f, 1f, 0f), new Vector3(-0.35f, 1f, 0f),
                new Vector3(0f, 0f, -0.35f), new Vector3(0f, 0f,  0.35f), new Vector3(0f, 1f,  0.35f), new Vector3(0f, 1f, -0.35f),
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
            var mesh = new UnityEngine.Mesh { name = "WoT_VegetationPlaceholderMesh" };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ===================== transform conversion =====================

        private static void ApplyWoTTransform(Transform tr, float[] rowMajor)
        {
            Matrix4x4 m = WoTMatrixToUnity(rowMajor);
            Vector3 c0 = new Vector3(m.m00, m.m10, m.m20);
            Vector3 c1 = new Vector3(m.m01, m.m11, m.m21);
            Vector3 c2 = new Vector3(m.m02, m.m12, m.m22);

            float sx = c0.magnitude;
            float sy = c1.magnitude;
            float sz = c2.magnitude;
            if (sx < 1e-7f) sx = 1f;
            if (sy < 1e-7f) sy = 1f;
            if (sz < 1e-7f) sz = 1f;

            float det = Vector3.Dot(c0, Vector3.Cross(c1, c2));
            if (det < 0f)
            {
                sx = -sx;
                c0 = -c0;
            }

            Quaternion rot = Quaternion.identity;
            try { rot = Quaternion.LookRotation(c2 / sz, c1 / sy); }
            catch { /* keep identity for degenerate transforms */ }

            tr.localPosition = new Vector3(m.m03, m.m13, m.m23);
            tr.localRotation = rot;
            tr.localScale = new Vector3(sx, sy, sz);
        }

        private static Matrix4x4 WoTMatrixToUnity(float[] t)
        {
            if (t == null || t.Length < 16) return Matrix4x4.identity;

            Matrix4x4 src = Matrix4x4.identity;
            src.SetRow(0, new Vector4(t[0], t[1], t[2], t[3]));
            src.SetRow(1, new Vector4(t[4], t[5], t[6], t[7]));
            src.SetRow(2, new Vector4(t[8], t[9], t[10], t[11]));
            src.SetRow(3, new Vector4(t[12], t[13], t[14], t[15]));

            Matrix4x4 flip = Matrix4x4.identity;
            flip.m11 = 0f; flip.m12 = 1f;
            flip.m21 = 1f; flip.m22 = 0f;
            return flip * src.transpose * flip;
        }

        // ===================== small utilities =====================

        private static void WarnSpeciesOnce(BuildContext ctx, string resource, string msg)
        {
            if (ctx.WarnedSpecies.Add(resource))
            {
                ctx.Result.Warnings.Add(msg);
                WoTLogger.Warn(msg);
            }
        }

        private static string NormalizeResource(string s)
        {
            return (s ?? string.Empty).Trim().TrimStart('/').Replace('\\', '/').ToLowerInvariant();
        }

        private static string RemoveExtension(string s)
        {
            s = s.Replace('\\', '/');
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
            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
        }

        private static string SanitizeAssetPath(string path)
        {
            path = path.Replace('\\', '/');
            string[] parts = path.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i == 0 && parts[i] == "Assets") continue;
                parts[i] = SafeAssetName(parts[i]);
            }
            return string.Join("/", parts);
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
