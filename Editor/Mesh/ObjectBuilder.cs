using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using WoTMapImporter.Editor.Package;
using WoTMapImporter.Editor.Utils;

namespace WoTMapImporter.Editor.Mesh
{
    /// <summary>
    /// Builds static GameObjects from WoT .primitives_processed meshes and the
    /// compiled-space model hierarchy.
    ///
    /// Повторяющиеся объекты (одинаковый ModelId) сохраняются как префабы
    /// в папку Prefabs/ и затем инстанцируются на карте через PrefabUtility.
    /// Уникальные объекты (встречаются один раз) создаются напрямую без префаба.
    ///
    /// Normal map (_BumpMap / _NormalMap) читается из свойств материала.
    /// </summary>
    public static class ObjectBuilder
    {
        public class BuildResult
        {
            public GameObject Root;
            public List<GameObject> CreatedObjects = new List<GameObject>();
            public List<string> Warnings = new List<string>();
        }

        public struct ObjectTransform
        {
            public Vector4 Row0, Row1, Row2, Row3;
        }

        private sealed class BuildContext
        {
            public string OutputPath;
            public WoTPackageManager ResMgr;
            public BuildResult Result;
            public readonly Dictionary<string, UnityEngine.Mesh> MeshCache     = new Dictionary<string, UnityEngine.Mesh>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, Material>         MaterialCache  = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, Texture2D>        TextureCache   = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, List<AtlasEntry>> AtlasCache     = new Dictionary<string, List<AtlasEntry>>(StringComparer.OrdinalIgnoreCase);
            // ModelId → готовый prefab asset
            public readonly Dictionary<int, GameObject>          PrefabCache    = new Dictionary<int, GameObject>();
        }

        private sealed class AtlasEntry
        {
            public uint X0, X1, Y0, Y1;
            public string Path;
        }

        // =================== главный Build ===================

        public static BuildResult Build(
            string outputPath,
            CompiledSpace space,
            WoTPackageManager resMgr)
        {
            var ctx = new BuildContext
            {
                OutputPath = outputPath,
                ResMgr     = resMgr,
                Result     = new BuildResult(),
            };

            ctx.Result.Root = new GameObject("StaticObjects");
            if (space == null || space.Placements.Count == 0)
            {
                ctx.Result.Warnings.Add("CompiledSpace has no object placements");
                return ctx.Result;
            }

            // --- Считаем сколько раз встречается каждый ModelId ---
            var modelIdCount = new Dictionary<int, int>();
            foreach (var p in space.Placements)
            {
                if (!modelIdCount.ContainsKey(p.ModelId)) modelIdCount[p.ModelId] = 0;
                modelIdCount[p.ModelId]++;
            }

            EnsureFolder(outputPath + "/Prefabs");

            int lodVariants      = 0;
            int destroyedVariants = 0;
            int renderObjects    = 0;
            int prefabsCreated   = 0;
            int prefabInstances  = 0;

            foreach (var placement in space.Placements)
            {
                bool usesPrefab = true; // old-client mode: save every unique object as prefab, even if used once

                if (placement.DestroyedLods.Count > 0)
                {
                    // ---- Разрушаемый объект ----
                    GameObject prefabSource = null;
                    if (usesPrefab)
                        prefabSource = GetOrCreateDestructiblePrefab(ctx, placement, ref lodVariants, ref destroyedVariants, ref renderObjects, ref prefabsCreated);

                    GameObject instRoot;
                    if (prefabSource != null)
                    {
                        instRoot = (GameObject)PrefabUtility.InstantiatePrefab(prefabSource, ctx.Result.Root.transform);
                        prefabInstances++;
                    }
                    else
                    {
                        instRoot = BuildDestructibleObject(ctx, placement, Path.GetFileNameWithoutExtension(GetPlacementBaseName(placement)),
                            ref lodVariants, ref destroyedVariants, ref renderObjects);
                        instRoot.transform.SetParent(ctx.Result.Root.transform, false);
                    }

                    ApplyWoTTransform(instRoot.transform, placement.Transform);
                    ctx.Result.CreatedObjects.Add(instRoot);
                    continue;
                }

                // ---- Обычный объект ----
                {
                    GameObject prefabSource = null;
                    if (usesPrefab)
                        prefabSource = GetOrCreateRegularPrefab(ctx, placement, ref lodVariants, ref renderObjects, ref prefabsCreated);

                    GameObject instRoot;
                    if (prefabSource != null)
                    {
                        instRoot = (GameObject)PrefabUtility.InstantiatePrefab(prefabSource, ctx.Result.Root.transform);
                        prefabInstances++;
                    }
                    else
                    {
                        instRoot = BuildRegularObject(ctx, placement, GetPlacementBaseName(placement),
                            ref lodVariants, ref renderObjects);
                        instRoot.transform.SetParent(ctx.Result.Root.transform, false);
                    }

                    ApplyWoTTransform(instRoot.transform, placement.Transform);
                    ctx.Result.CreatedObjects.Add(instRoot);
                }
            }

            ctx.Result.Warnings.Add(
                $"Objects imported: {space.Placements.Count} placements, " +
                $"{renderObjects} render objects, {lodVariants} multi-LOD variants, " +
                $"{destroyedVariants} destroyed variants, " +
                $"{prefabsCreated} prefabs created, {prefabInstances} prefab instances");

            return ctx.Result;
        }

        // =================== prefab helpers ===================

        /// <summary>
        /// Возвращает готовый prefab для обычного объекта с данным ModelId.
        /// Если prefab ещё не создан — строит временный GameObject, сохраняет
        /// его как prefab и кладёт в кеш.
        /// </summary>
        private static GameObject GetOrCreateRegularPrefab(
            BuildContext ctx,
            CompiledSpace.ModelPlacement placement,
            ref int lodVariants, ref int renderObjects, ref int prefabsCreated)
        {
            if (ctx.PrefabCache.TryGetValue(placement.ModelId, out var cached))
                return cached;

            string baseName = GetPlacementBaseName(placement);
            int dummy1 = 0, dummy2 = 0;
            var template = BuildRegularObject(ctx, placement, baseName, ref dummy1, ref dummy2);
            lodVariants   += dummy1;
            renderObjects += dummy2;

            var prefab = SaveAsPrefab(template, ctx.OutputPath + "/Prefabs/" + SafeAssetName(baseName) + ".prefab");
            UnityEngine.Object.DestroyImmediate(template);
            ctx.PrefabCache[placement.ModelId] = prefab;
            prefabsCreated++;
            return prefab;
        }

        /// <summary>
        /// Возвращает готовый prefab для разрушаемого объекта с данным ModelId.
        /// </summary>
        private static GameObject GetOrCreateDestructiblePrefab(
            BuildContext ctx,
            CompiledSpace.ModelPlacement placement,
            ref int lodVariants, ref int destroyedVariants, ref int renderObjects, ref int prefabsCreated)
        {
            if (ctx.PrefabCache.TryGetValue(placement.ModelId, out var cached))
                return cached;

            string baseName = GetPlacementBaseName(placement);
            int dL = 0, dD = 0, dR = 0;
            var template = BuildDestructibleObject(ctx, placement, baseName, ref dL, ref dD, ref dR);
            lodVariants       += dL;
            destroyedVariants += dD;
            renderObjects     += dR;

            var prefab = SaveAsPrefab(template, ctx.OutputPath + "/Prefabs/" + SafeAssetName(baseName) + ".prefab");
            UnityEngine.Object.DestroyImmediate(template);
            ctx.PrefabCache[placement.ModelId] = prefab;
            prefabsCreated++;
            return prefab;
        }

        // =================== object builders ===================

        private static GameObject BuildRegularObject(
            BuildContext ctx,
            CompiledSpace.ModelPlacement placement,
            string baseName,
            ref int lodVariants, ref int renderObjects)
        {
            var instRoot = new GameObject(baseName);

            if (placement.Lods.Count > 0)
            {
                var intactRoot = new GameObject("Intact");
                intactRoot.transform.SetParent(instRoot.transform, false);
                renderObjects += BuildLodVariant(ctx, intactRoot, placement.Lods, baseName + "_intact");
                if (placement.Lods.Count > 1) lodVariants++;
            }

            return instRoot;
        }

        private static GameObject BuildDestructibleObject(
            BuildContext ctx,
            CompiledSpace.ModelPlacement placement,
            string baseName,
            ref int lodVariants, ref int destroyedVariants, ref int renderObjects)
        {
            var objectRoot  = new GameObject(baseName);
            var logicalRoot = new GameObject("root");
            logicalRoot.transform.SetParent(objectRoot.transform, false);

            var mainModelRoot = new GameObject("MainModel");
            mainModelRoot.transform.SetParent(logicalRoot.transform, false);
            renderObjects += BuildLodVariant(ctx, mainModelRoot, placement.Lods, baseName + "_main");
            if (placement.Lods.Count > 1) lodVariants++;

            var destroyedModelRoot = new GameObject("DestroyedModel");
            destroyedModelRoot.transform.SetParent(logicalRoot.transform, false);
            renderObjects += BuildLodVariant(ctx, destroyedModelRoot, placement.DestroyedLods, baseName + "_destroyed");
            destroyedModelRoot.SetActive(false);
            destroyedVariants++;
            if (placement.DestroyedLods.Count > 1) lodVariants++;

            CreateCollisionTrigger(logicalRoot.transform, mainModelRoot);

            return objectRoot;
        }

        // =================== prefab save ===================

        private static GameObject SaveAsPrefab(GameObject go, string prefabPath)
        {
            prefabPath = prefabPath.Replace('\\', '/');
            EnsureFolder(Path.GetDirectoryName(prefabPath).Replace('\\', '/'));

            // Если уже есть — перезаписываем через SaveAsPrefabAsset
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (existing != null)
                return PrefabUtility.SaveAsPrefabAsset(go, prefabPath);

            return PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
        }

        // =================== имя объекта ===================

        /// <summary>Базовое имя (без InstanceIndex) — используется как имя prefab.</summary>
        private static string GetPlacementBaseName(CompiledSpace.ModelPlacement placement)
        {
            var mesh = FirstRenderMesh(placement.Lods) ?? FirstRenderMesh(placement.DestroyedLods);
            string baseName = mesh != null && !string.IsNullOrEmpty(mesh.PrimsName)
                ? PathName(mesh.PrimsName)
                : $"model_{placement.ModelId:D5}";
            return SafeAssetName(baseName);
        }

        private static string GetPlacementObjectName(CompiledSpace.ModelPlacement placement)
            => SafeAssetName($"{GetPlacementBaseName(placement)}_{placement.InstanceIndex:D5}");

        private static CompiledSpace.RenderMesh FirstRenderMesh(List<CompiledSpace.ModelLod> lods)
        {
            if (lods == null) return null;
            foreach (var lod in lods)
                if (lod != null && lod.Meshes != null && lod.Meshes.Count > 0)
                    return lod.Meshes[0];
            return null;
        }

        // =================== collision trigger ===================

        private const float TriggerBoundsScale = 0.75f;
        private const float TriggerMinSize      = 0.25f;

        private static void CreateCollisionTrigger(Transform logicalRoot, GameObject mainModelRoot)
        {
            var triggerRoot = new GameObject("CollisionTrigger");
            triggerRoot.transform.SetParent(logicalRoot, false);

            var colliderGo = new GameObject("Collider");
            colliderGo.transform.SetParent(triggerRoot.transform, false);

            var box = colliderGo.AddComponent<BoxCollider>();
            box.isTrigger = true;

            if (TryCalculateLocalBounds(logicalRoot, mainModelRoot, out var bounds))
            {
                Vector3 size = bounds.size * TriggerBoundsScale;
                size.x = Mathf.Max(size.x, TriggerMinSize);
                size.y = Mathf.Max(size.y, TriggerMinSize);
                size.z = Mathf.Max(size.z, TriggerMinSize);
                box.center = bounds.center;
                box.size   = size;
            }
            else
            {
                box.center = Vector3.zero;
                box.size   = Vector3.one;
            }
        }

        private static bool TryCalculateLocalBounds(Transform root, GameObject modelRoot, out Bounds bounds)
        {
            bounds = new Bounds(Vector3.zero, Vector3.zero);
            if (root == null || modelRoot == null) return false;
            bool has = false;
            foreach (var mf in modelRoot.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                Matrix4x4 toRoot = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                EncapsulateTransformedBounds(ref bounds, ref has, mf.sharedMesh.bounds, toRoot);
            }
            return has;
        }

        private static void EncapsulateTransformedBounds(ref Bounds dst, ref bool has, Bounds src, Matrix4x4 m)
        {
            Vector3 c = src.center, e = src.extents;
            for (int ix = -1; ix <= 1; ix += 2)
                for (int iy = -1; iy <= 1; iy += 2)
                    for (int iz = -1; iz <= 1; iz += 2)
                    {
                        Vector3 p = m.MultiplyPoint3x4(c + Vector3.Scale(e, new Vector3(ix, iy, iz)));
                        if (!has) { dst = new Bounds(p, Vector3.zero); has = true; }
                        else dst.Encapsulate(p);
                    }
        }

        // =================== LOD variant builder ===================

        private static int BuildLodVariant(
            BuildContext ctx,
            GameObject variantRoot,
            List<CompiledSpace.ModelLod> lods,
            string namePrefix)
        {
            int renderObjectCount = 0;

            for (int li = 0; li < lods.Count; li++)
            {
                var lod = lods[li];
                string lodName = lod.Distance > 0.0001f
                    ? $"LOD{lod.LodIndex}_d{lod.Distance:0.###}"
                    : $"LOD{lod.LodIndex}";
                var lodRoot = new GameObject(lodName);
                lodRoot.transform.SetParent(variantRoot.transform, false);

                foreach (var renderMesh in lod.Meshes)
                {
                    var mesh     = GetOrCreateMesh(ctx, renderMesh);
                    if (mesh == null) continue;
                    var material = GetOrCreateMaterial(ctx, renderMesh, MeshHasUsefulUv2(mesh));

                    var go = new GameObject($"{namePrefix}_r{renderMesh.RenderSetId}_pg{renderMesh.PrimitiveGroup}");
                    go.transform.SetParent(lodRoot.transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh       = mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterial = material;
                    renderObjectCount++;
                }
            }

            if (lods.Count > 1)
                AttachLodGroup(variantRoot, lods);

            return renderObjectCount;
        }

        private static void AttachLodGroup(GameObject variantRoot, List<CompiledSpace.ModelLod> lods)
        {
            var lodGroup  = variantRoot.AddComponent<LODGroup>();
            var unityLods = new LOD[lods.Count];

            for (int i = 0; i < lods.Count; i++)
            {
                Transform lodChild = null;
                foreach (Transform child in variantRoot.transform)
                {
                    if (child.name.StartsWith($"LOD{lods[i].LodIndex}", StringComparison.OrdinalIgnoreCase))
                    { lodChild = child; break; }
                }

                Renderer[] renderers = lodChild != null
                    ? lodChild.GetComponentsInChildren<Renderer>(false)
                    : Array.Empty<Renderer>();

                unityLods[i] = new LOD(ComputeLodTransition(i, lods.Count), renderers);
            }

            lodGroup.SetLODs(unityLods);
            lodGroup.RecalculateBounds();
        }

        private static float ComputeLodTransition(int index, int count)
        {
            float[] thresholds = { 0.79f, 0.55f, 0.39f, 0.18f, 0.09f, 0.04f };
            if (index >= count - 1) return 0.01f;
            if (index < thresholds.Length) return thresholds[index];
            return Mathf.Max(0.01f, thresholds[thresholds.Length - 1] * 0.5f);
        }

        private static bool MeshHasUsefulUv2(UnityEngine.Mesh mesh)
        {
            if (mesh == null) return false;
            var uv2 = mesh.uv2;
            if (uv2 == null || uv2.Length != mesh.vertexCount) return false;
            for (int i = 0; i < uv2.Length; i++)
                if (Mathf.Abs(uv2[i].x) > 1e-6f || Mathf.Abs(uv2[i].y) > 1e-6f) return true;
            return false;
        }

        // =================== mesh creation ===================

        private static UnityEngine.Mesh GetOrCreateMesh(BuildContext ctx, CompiledSpace.RenderMesh renderMesh)
        {
            string key = $"{renderMesh.PrimsName}|{renderMesh.VertsDataName}|{renderMesh.PrimsDataName}|{renderMesh.PrimitiveGroup}";
            if (ctx.MeshCache.TryGetValue(key, out var cached)) return cached;

            byte[] data = ctx.ResMgr.ReadBytes(renderMesh.PrimsName) ?? TryAlternateBytes(ctx.ResMgr, renderMesh.PrimsName);
            if (data == null)
            {
                string msg = $"prims not found: {renderMesh.PrimsName}";
                ctx.Result.Warnings.Add(msg); WoTLogger.Warn(msg); return null;
            }

            MeshDataDecoder.DecodedMesh decoded;
            try { decoded = MeshDataDecoder.Decode(data, renderMesh.VertsDataName, renderMesh.PrimsDataName, renderMesh.PrimitiveGroup); }
            catch (Exception e)
            {
                string msg = $"Failed to decode {renderMesh.PrimsName}: {e.Message}";
                ctx.Result.Warnings.Add(msg); WoTLogger.Warn(msg); return null;
            }

            var vertices = new Vector3[decoded.Positions.Length];
            for (int i = 0; i < decoded.Positions.Length; i++)
            {
                var p = decoded.Positions[i];
                vertices[i] = new Vector3(p.x, p.z, p.y);
            }

            string hashKey = key;
            var umesh = new UnityEngine.Mesh
            {
                name        = SafeAssetName($"{PathName(renderMesh.PrimsName)}_{renderMesh.VertsDataName}_{renderMesh.PrimsDataName}_pg{renderMesh.PrimitiveGroup}_{StableHash32(hashKey):X8}"),
                indexFormat = vertices.Length > 65535
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16,
            };
            umesh.vertices  = vertices;
            umesh.uv        = decoded.Uv ?? Array.Empty<Vector2>();
            if (decoded.Uv2 != null) umesh.uv2 = decoded.Uv2;
            umesh.triangles = decoded.Indices ?? Array.Empty<int>();
            umesh.RecalculateNormals();
            try { umesh.RecalculateTangents(); } catch { }
            umesh.RecalculateBounds();

            string meshPath = $"{ctx.OutputPath}/Meshes/{umesh.name}.asset";
            SaveAsset(umesh, meshPath);
            var persisted = AssetDatabase.LoadAssetAtPath<UnityEngine.Mesh>(meshPath) ?? umesh;
            ctx.MeshCache[key] = persisted;
            return persisted;
        }

        // =================== material creation ===================

        private static Material GetOrCreateMaterial(BuildContext ctx, CompiledSpace.RenderMesh renderMesh, bool useUv2Blend)
        {
            string key = $"{renderMesh.MaterialIndex}|{renderMesh.FxName}|{renderMesh.Identifier}|uv2={useUv2Blend}|{PropsHash(renderMesh)}";
            if (ctx.MaterialCache.TryGetValue(key, out var cached)) return cached;

            Shader sh = Shader.Find("WoT/ObjectPBS") ??
                        Shader.Find("Universal Render Pipeline/Lit") ??
                        Shader.Find("HDRP/Lit") ??
                        Shader.Find("Standard") ??
                        Shader.Find("Sprites/Default");
            var mat = new Material(sh)
            {
                name = SafeAssetName($"WoT_Mat_{renderMesh.MaterialIndex}_{PathName(renderMesh.PrimsName)}_{renderMesh.PrimitiveGroup}_{StableHash32(key):X8}"),
            };
            SetFloatIfExists(mat, "_UseUv2Blend", useUv2Blend ? 1f : 0f);

            string fx = renderMesh.FxName ?? string.Empty;
            if      (fx.IndexOf("PBS_tiled_atlas", StringComparison.OrdinalIgnoreCase) >= 0) SetupAtlasMaterial(ctx, mat, renderMesh);
            else if (fx.IndexOf("PBS_tiled",       StringComparison.OrdinalIgnoreCase) >= 0) SetupTiledMaterial(ctx, mat, renderMesh);
            else                                                                               SetupDiffuseMaterial(ctx, mat, renderMesh);

            ApplyNormalMap(ctx, mat, renderMesh);
            ApplyAlphaMode(mat, renderMesh);

            if (mat.HasProperty("_ObjectColor"))
                mat.SetColor("_ObjectColor", renderMesh.IsDestroyedMaterial ? new Color(0.85f, 0.85f, 0.85f, 1f) : Color.white);
            if (mat.HasProperty("_FxMode")) mat.SetFloat("_FxMode", 0f);

            string matPath = $"{ctx.OutputPath}/Materials/{mat.name}.mat";
            SaveAsset(mat, matPath);
            var persisted = AssetDatabase.LoadAssetAtPath<Material>(matPath) ?? mat;
            ctx.MaterialCache[key] = persisted;
            return persisted;
        }

        private static void ApplyAlphaMode(Material mat, CompiledSpace.RenderMesh rm)
        {
            bool alphaTest  = string.Equals(rm.GetString("alphaTestEnable"), "true", StringComparison.OrdinalIgnoreCase) || string.Equals(rm.GetString("alphaTestEnable"), "1", StringComparison.OrdinalIgnoreCase);
            bool alphaBlend = string.Equals(rm.GetString("alphaBlendEnable"), "true", StringComparison.OrdinalIgnoreCase) || string.Equals(rm.GetString("alphaBlendEnable"), "1", StringComparison.OrdinalIgnoreCase) || string.Equals(rm.GetString("transparent"), "true", StringComparison.OrdinalIgnoreCase);

            if (rm.FxName != null && rm.FxName.IndexOf("alpha", StringComparison.OrdinalIgnoreCase) >= 0)
                alphaTest = true;
            if (rm.DiffuseTexture != null && (rm.DiffuseTexture.IndexOf("fence", StringComparison.OrdinalIgnoreCase) >= 0 || rm.DiffuseTexture.IndexOf("net", StringComparison.OrdinalIgnoreCase) >= 0 || rm.DiffuseTexture.IndexOf("tree", StringComparison.OrdinalIgnoreCase) >= 0 || rm.DiffuseTexture.IndexOf("bush", StringComparison.OrdinalIgnoreCase) >= 0 || rm.DiffuseTexture.IndexOf("grill", StringComparison.OrdinalIgnoreCase) >= 0 || rm.DiffuseTexture.IndexOf("wire", StringComparison.OrdinalIgnoreCase) >= 0 || rm.DiffuseTexture.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0 || rm.DiffuseTexture.IndexOf("window", StringComparison.OrdinalIgnoreCase) >= 0))
                alphaTest = true;

            string alphaRefStr = rm.GetString("alphaReference");
            float cutoff = 0.5f;
            if (!string.IsNullOrEmpty(alphaRefStr) && float.TryParse(alphaRefStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsedRef))
            {
                cutoff = parsedRef > 1.0f ? parsedRef / 255.0f : parsedRef;
            }

            if (alphaBlend)
            {
                SetFloatIfExists(mat, "_AlphaClip", 0f);
                SetFloatIfExists(mat, "_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                SetFloatIfExists(mat, "_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                SetFloatIfExists(mat, "_ZWrite", 0f);
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.SetOverrideTag("Queue", "Transparent");
                mat.renderQueue = 3000;
            }
            else if (alphaTest)
            {
                SetFloatIfExists(mat, "_AlphaClip", 1f);
                SetFloatIfExists(mat, "_Cutoff", cutoff);
                SetFloatIfExists(mat, "_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                SetFloatIfExists(mat, "_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
                SetFloatIfExists(mat, "_ZWrite", 1f);
                mat.SetOverrideTag("RenderType", "TransparentCutout");
                mat.SetOverrideTag("Queue", "AlphaTest");
                mat.renderQueue = 2450;
            }
            else
            {
                SetFloatIfExists(mat, "_AlphaClip", 0f);
                SetFloatIfExists(mat, "_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                SetFloatIfExists(mat, "_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
                SetFloatIfExists(mat, "_ZWrite", 1f);
                mat.SetOverrideTag("RenderType", "Opaque");
                mat.SetOverrideTag("Queue", "Geometry");
                mat.renderQueue = 2000;
            }
        }

        private static void ApplyNormalMap(BuildContext ctx, Material mat, CompiledSpace.RenderMesh renderMesh)
        {
            Texture2D normalTex =
                LoadTextureFromProp(ctx, renderMesh, "normalMap",  true) ??
                LoadTextureFromProp(ctx, renderMesh, "bumpMap",    true) ??
                LoadTextureFromProp(ctx, renderMesh, "normalMap2", true);
            if (normalTex == null) return;

            SetTextureIfExists(mat, "_BumpMap",    normalTex);
            SetTextureIfExists(mat, "_NormalMap",  normalTex);
            SetTextureIfExists(mat, "_NormalMapOS", normalTex);
            if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", 1f);
            if (!mat.IsKeywordEnabled("_NORMALMAP")) { mat.EnableKeyword("_NORMALMAP"); mat.EnableKeyword("_DETAIL_MULX2"); }
        }

        private static void SetupDiffuseMaterial(BuildContext ctx, Material mat, CompiledSpace.RenderMesh renderMesh)
        {
            if (mat.HasProperty("_Mode")) mat.SetFloat("_Mode", 0f);
            Texture2D tex = LoadTextureFromProp(ctx, renderMesh, "diffuseMap",  false)
                         ?? LoadTextureFromProp(ctx, renderMesh, "albedoMap",   false)
                         ?? LoadTextureFromProp(ctx, renderMesh, "diffuseMap2", false);
            if (tex != null) { SetTextureIfExists(mat, "_MainTex", tex); SetTextureIfExists(mat, "_BaseMap", tex); }
            else             { SetColorIfExists(mat, "_BaseColor", Color.gray); SetColorIfExists(mat, "_Color", Color.gray); }
        }

        private static void SetupTiledMaterial(BuildContext ctx, Material mat, CompiledSpace.RenderMesh renderMesh)
        {
            if (mat.HasProperty("_Mode")) mat.SetFloat("_Mode", 1f);
            Texture2D t0    = LoadTextureFromProp(ctx, renderMesh, "albedoHeightTile0", false);
            Texture2D t1    = LoadTextureFromProp(ctx, renderMesh, "albedoHeightTile1", false);
            Texture2D t2    = LoadTextureFromProp(ctx, renderMesh, "albedoHeightTile2", false);
            Texture2D blend = LoadTextureFromProp(ctx, renderMesh, "blendMask",         true, true);
            if (t0 != null) { SetTextureIfExists(mat, "_Tile0", t0); SetTextureIfExists(mat, "_MainTex", t0); SetTextureIfExists(mat, "_BaseMap", t0); }
            if (t1 != null) SetTextureIfExists(mat, "_Tile1", t1);
            if (t2 != null) SetTextureIfExists(mat, "_Tile2", t2);
            if (blend != null) SetTextureIfExists(mat, "_BlendMask", blend);
            SetVectorIfExists(mat, "_Tile0Tint", ToVector4(renderMesh.GetVector("g_tile0Tint"), Color.white));
            SetVectorIfExists(mat, "_Tile1Tint", ToVector4(renderMesh.GetVector("g_tile1Tint"), Color.white));
            SetVectorIfExists(mat, "_Tile2Tint", ToVector4(renderMesh.GetVector("g_tile2Tint"), Color.white));
        }

        private static void SetupAtlasMaterial(BuildContext ctx, Material mat, CompiledSpace.RenderMesh renderMesh)
        {
            float[] atlasIndexes = renderMesh.GetVector("g_atlasIndexes") ?? new float[] { 0, 1, 2, 3 };
            float[] atlasSizes   = renderMesh.GetVector("g_atlasSizes")   ?? new float[] { 1, 1, 1, 1 };
            Vector4 idx  = ToVector4(atlasIndexes, new Vector4(0, 1, 2, 3));
            Vector4 grid = new Vector4(
                atlasSizes.Length > 2 && Mathf.Abs(atlasSizes[2]) > 0.0001f ? atlasSizes[2] : 1f,
                atlasSizes.Length > 3 && Mathf.Abs(atlasSizes[3]) > 0.0001f ? atlasSizes[3] : 1f,
                0f, 0f);
            SetVectorIfExists(mat, "_AtlasIndexes", idx);
            SetVectorIfExists(mat, "_AtlasGrid",    grid);
            SetVectorIfExists(mat, "_Tile0Tint", ToVector4(renderMesh.GetVector("g_tile0Tint"), Color.white));
            SetVectorIfExists(mat, "_Tile1Tint", ToVector4(renderMesh.GetVector("g_tile1Tint"), Color.white));
            SetVectorIfExists(mat, "_Tile2Tint", ToVector4(renderMesh.GetVector("g_tile2Tint"), Color.white));

            string atlasName = renderMesh.GetString("atlasAlbedoHeight");
            bool loadedFromAtlas = false;
            if (!string.IsNullOrEmpty(atlasName) && atlasName.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase))
            {
                var entries = LoadAtlas(ctx, atlasName + "_processed");
                if (entries != null && entries.Count > 0)
                {
                    Texture2D t0 = LoadAtlasTile(ctx, entries, (int)idx.x, false);
                    Texture2D t1 = LoadAtlasTile(ctx, entries, (int)idx.y, false);
                    Texture2D t2 = LoadAtlasTile(ctx, entries, (int)idx.z, false);
                    if (t0 != null) { SetTextureIfExists(mat, "_Tile0", t0); SetTextureIfExists(mat, "_MainTex", t0); SetTextureIfExists(mat, "_BaseMap", t0); }
                    if (t1 != null) SetTextureIfExists(mat, "_Tile1", t1);
                    if (t2 != null) SetTextureIfExists(mat, "_Tile2", t2);
                    loadedFromAtlas = true;
                }
            }
            if (!loadedFromAtlas)
            {
                Texture2D t = LoadTextureByName(ctx, atlasName, false);
                if (t != null) { SetTextureIfExists(mat, "_Tile0", t); SetTextureIfExists(mat, "_Tile1", t); SetTextureIfExists(mat, "_Tile2", t); SetTextureIfExists(mat, "_MainTex", t); SetTextureIfExists(mat, "_BaseMap", t); }
            }

            Texture2D blend = LoadTextureFromProp(ctx, renderMesh, "atlasBlend", true, true);
            if (blend != null) { SetTextureIfExists(mat, "_AtlasBlend", blend); SetTextureIfExists(mat, "_BlendMask", blend); }
            if (mat.HasProperty("_Mode")) mat.SetFloat("_Mode", 3f);
        }

        // =================== legacy entry point ===================

        public static BuildResult Build(
            string outputPath,
            string primsName,
            string vertsName,
            List<ObjectTransform> transforms,
            List<string> texturePaths,
            WoTPackageManager resMgr)
        {
            var result = new BuildResult();
            byte[] data = resMgr.ReadBytes(primsName) ?? TryAlternateBytes(resMgr, primsName);
            if (data == null) { result.Warnings.Add($"prims not found: {primsName}"); return result; }

            MeshDataDecoder.DecodedMesh decoded;
            try { decoded = MeshDataDecoder.Decode(data, vertsName, null, -1); }
            catch (Exception e) { result.Warnings.Add($"Failed to decode {primsName}: {e.Message}"); return result; }

            var vertices = new Vector3[decoded.Positions.Length];
            for (int i = 0; i < decoded.Positions.Length; i++)
                vertices[i] = new Vector3(decoded.Positions[i].x, decoded.Positions[i].z, decoded.Positions[i].y);

            var umesh = new UnityEngine.Mesh
            {
                name        = SafeAssetName(PathName(primsName)),
                indexFormat = vertices.Length > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16,
            };
            umesh.vertices  = vertices;
            umesh.uv        = decoded.Uv;
            if (decoded.Uv2 != null) umesh.uv2 = decoded.Uv2;
            umesh.triangles = decoded.Indices;
            umesh.RecalculateNormals();
            umesh.RecalculateBounds();

            Material mat = CreatePipelineMaterial("WoT_DefaultMat");
            if (texturePaths != null && texturePaths.Count > 0 && !string.IsNullOrEmpty(texturePaths[0]))
            {
                byte[] texData = resMgr.ReadBytes(texturePaths[0]) ?? TryAlternateBytes(resMgr, texturePaths[0]);
                if (texData != null) { var tex = LoadTex(texData, texturePaths[0], false); if (tex != null) SetTextureIfExists(mat, "_MainTex", tex); }
            }

            result.Root = new GameObject(PathName(primsName));
            if (transforms != null)
            {
                int i = 0;
                foreach (var t in transforms)
                {
                    var inst = new GameObject(PathName(primsName) + "_inst_" + i++);
                    inst.transform.SetParent(result.Root.transform, false);
                    inst.AddComponent<MeshFilter>().sharedMesh       = umesh;
                    inst.AddComponent<MeshRenderer>().sharedMaterial = mat;
                    ApplyWoTTransform(inst.transform, ToFloatArray(t));
                    result.CreatedObjects.Add(inst);
                }
            }
            else
            {
                result.Root.AddComponent<MeshFilter>().sharedMesh       = umesh;
                result.Root.AddComponent<MeshRenderer>().sharedMaterial = mat;
                result.CreatedObjects.Add(result.Root);
            }

            string meshPath = $"{outputPath}/Meshes/{umesh.name}.asset";
            SaveAsset(umesh, meshPath);
            return result;
        }

        // =================== transform ===================

        private static void ApplyWoTTransform(Transform tr, float[] rowMajor)
        {
            Matrix4x4 m  = WoTMatrixToUnity(rowMajor);
            Vector3 c0 = new Vector3(m.m00, m.m10, m.m20);
            Vector3 c1 = new Vector3(m.m01, m.m11, m.m21);
            Vector3 c2 = new Vector3(m.m02, m.m12, m.m22);
            float sx = c0.magnitude, sy = c1.magnitude, sz = c2.magnitude;
            if (sx < 1e-7f) sx = 1f; if (sy < 1e-7f) sy = 1f; if (sz < 1e-7f) sz = 1f;
            if (Vector3.Dot(c0, Vector3.Cross(c1, c2)) < 0f) { sx = -sx; c0 = -c0; }
            Quaternion rot = Quaternion.identity;
            try { rot = Quaternion.LookRotation(c2 / sz, c1 / sy); } catch { }
            tr.localPosition = new Vector3(m.m03, m.m13, m.m23);
            tr.localRotation = rot;
            tr.localScale    = new Vector3(sx, sy, sz);
        }

        private static Matrix4x4 WoTMatrixToUnity(float[] t)
        {
            if (t == null || t.Length < 16) return Matrix4x4.identity;
            Matrix4x4 src = Matrix4x4.identity;
            src.SetRow(0, new Vector4(t[0],  t[1],  t[2],  t[3]));
            src.SetRow(1, new Vector4(t[4],  t[5],  t[6],  t[7]));
            src.SetRow(2, new Vector4(t[8],  t[9],  t[10], t[11]));
            src.SetRow(3, new Vector4(t[12], t[13], t[14], t[15]));
            Matrix4x4 flip = Matrix4x4.identity;
            flip.m11 = 0f; flip.m12 = 1f; flip.m21 = 1f; flip.m22 = 0f;
            return flip * src.transpose * flip;
        }

        private static float[] ToFloatArray(ObjectTransform t) => new[]
        {
            t.Row0.x, t.Row0.y, t.Row0.z, t.Row0.w,
            t.Row1.x, t.Row1.y, t.Row1.z, t.Row1.w,
            t.Row2.x, t.Row2.y, t.Row2.z, t.Row2.w,
            t.Row3.x, t.Row3.y, t.Row3.z, t.Row3.w,
        };

        // =================== textures ===================

        private static Texture2D LoadTextureFromProp(BuildContext ctx, CompiledSpace.RenderMesh rm, string prop, bool linear, bool pngToDds = false)
        {
            string name = rm.GetString(prop);
            if (string.IsNullOrEmpty(name)) return null;
            if (pngToDds && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4) + ".dds";
            return LoadTextureByName(ctx, name, linear);
        }

        private static Texture2D LoadTextureByName(BuildContext ctx, string name, bool linear)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string cacheKey = (linear ? "L|" : "C|") + name.ToLowerInvariant().Replace('\\', '/');
            if (ctx.TextureCache.TryGetValue(cacheKey, out var cached)) return cached;

            byte[] data = TryResolveTexture(ctx.ResMgr, name, out string resolvedName);
            if (data == null) { WoTLogger.Warn($"Texture not found: {name}"); return null; }

            Texture2D tex = LoadTex(data, resolvedName, linear);
            if (tex == null) { WoTLogger.Warn($"Texture decode failed: {resolvedName}"); return null; }
            tex.name       = SafeAssetName(PathName(resolvedName) + "_" + StableHash32(resolvedName).ToString("X8"));
            tex.wrapMode   = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Bilinear;

            string texPath = $"{ctx.OutputPath}/Textures/{tex.name}.asset";
            SaveAsset(tex, texPath);
            var persisted = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath) ?? tex;
            ctx.TextureCache[cacheKey] = persisted;
            return persisted;
        }

        private static Texture2D LoadAtlasTile(BuildContext ctx, List<AtlasEntry> entries, int index, bool linear)
        {
            if (entries == null || index < 0 || index >= entries.Count) return null;
            return LoadTextureByName(ctx, entries[index].Path, linear);
        }

        private static List<AtlasEntry> LoadAtlas(BuildContext ctx, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string key = name.ToLowerInvariant().Replace('\\', '/');
            if (ctx.AtlasCache.TryGetValue(key, out var cached)) return cached;

            byte[] data = ctx.ResMgr.ReadBytes(name) ?? TryAlternateBytes(ctx.ResMgr, name);
            if (data == null) return null;

            var entries = new List<AtlasEntry>();
            try
            {
                using var ms = new MemoryStream(data, false);
                using var br = new BinaryReader(ms);
                uint version = br.ReadUInt32();
                br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32();
                uint magic = br.ReadUInt32();
                br.ReadUInt32();
                ulong ddsChunkSize = br.ReadUInt64();
                if (version != 1 || magic == 0) return null;
                br.BaseStream.Seek((long)ddsChunkSize, SeekOrigin.Current);
                while (br.BaseStream.Position + 16 <= br.BaseStream.Length)
                {
                    var e = new AtlasEntry { X0 = br.ReadUInt32(), X1 = br.ReadUInt32(), Y0 = br.ReadUInt32(), Y1 = br.ReadUInt32() };
                    var bytes = new List<byte>();
                    while (br.BaseStream.Position < br.BaseStream.Length) { byte b = br.ReadByte(); if (b == 0) break; bytes.Add(b); }
                    if (bytes.Count == 0) break;
                    string path = System.Text.Encoding.UTF8.GetString(bytes.ToArray()).Replace('\\', '/');
                    if (Path.HasExtension(path)) path = Path.ChangeExtension(path, ".dds").Replace('\\', '/');
                    e.Path = path;
                    entries.Add(e);
                }
            }
            catch (Exception e) { WoTLogger.Warn($"Atlas parse failed ({name}): {e.Message}"); return null; }

            ctx.AtlasCache[key] = entries;
            return entries;
        }

        private static byte[] TryResolveTexture(WoTPackageManager resMgr, string name, out string resolved)
        {
            resolved = name;
            foreach (var c in TextureCandidates(name)) { var d = resMgr.ReadBytes(c); if (d != null) { resolved = c; return d; } }
            return null;
        }

        private static IEnumerable<string> TextureCandidates(string name)
        {
            string n = name.Replace('\\', '/').TrimStart('/');
            yield return n;
            if (n.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) yield return n.Substring(0, n.Length - 4) + ".dds";
            if (n.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)) yield return n.Substring(0, n.Length - 4) + ".dds";
            if (!Path.HasExtension(n)) { yield return n + ".dds"; yield return n + ".png"; }
            if (!n.StartsWith("content/", StringComparison.OrdinalIgnoreCase))
            {
                yield return "content/" + n;
                if (n.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) yield return "content/" + n.Substring(0, n.Length - 4) + ".dds";
            }
            yield return Path.GetFileName(n);
        }

        private static byte[] TryAlternateBytes(WoTPackageManager resMgr, string name)
        {
            string n = name.Replace('\\', '/').TrimStart('/');
            var candidates = new List<string>();
            if (!n.StartsWith("content/", StringComparison.OrdinalIgnoreCase)) candidates.Add("content/" + n);
            if (!n.EndsWith("_processed", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".primitives", StringComparison.OrdinalIgnoreCase))
                candidates.Add(n + "_processed");
            candidates.Add(Path.GetFileName(n));
            foreach (var c in candidates) { var d = resMgr.ReadBytes(c); if (d != null) return d; }
            return null;
        }

        private static Texture2D LoadTex(byte[] data, string name, bool linear)
        {
            try
            {
                if (data.Length >= 4 && BitConverter.ToUInt32(data, 0) == Image.DdsDecoder.MAGIC)
                {
                    try { return Image.DdsDecoder.ReadDecompressed(data, Path.GetFileNameWithoutExtension(name), linear, true); }
                    catch (Exception de)
                    {
                        WoTLogger.Warn($"DDS decompressed load failed ({name}): {de.Message}; fallback to raw");
                        return Image.DdsDecoder.Read(data, Path.GetFileNameWithoutExtension(name), linear);
                    }
                }
            }
            catch (Exception e) { WoTLogger.Warn($"DDS load failed ({name}): {e.Message}"); }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear)
                { name = Path.GetFileNameWithoutExtension(name), wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
            if (tex.LoadImage(data, false)) return tex;
            UnityEngine.Object.DestroyImmediate(tex);
            return null;
        }

        // =================== material utils ===================

        private static Material CreatePipelineMaterial(string name)
        {
            Shader sh = Shader.Find("WoT/ObjectPBS") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("HDRP/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
            return new Material(sh) { name = name };
        }

        private static void SetTextureIfExists(Material mat, string prop, Texture tex)
        {
            if (tex != null && mat.HasProperty(prop)) mat.SetTexture(prop, tex);
            if (tex != null && (prop == "_MainTex" || prop == "_BaseMap")) mat.mainTexture = tex;
        }
        private static void SetColorIfExists(Material mat, string prop, Color c)  { if (mat.HasProperty(prop)) mat.SetColor(prop, c); }
        private static void SetVectorIfExists(Material mat, string prop, Vector4 v){ if (mat.HasProperty(prop)) mat.SetVector(prop, v); }
        private static void SetFloatIfExists(Material mat, string prop, float v)   { if (mat.HasProperty(prop)) mat.SetFloat(prop, v); }

        private static Vector4 ToVector4(float[] v, Color fb)   => v == null || v.Length == 0 ? new Vector4(fb.r, fb.g, fb.b, fb.a)  : new Vector4(v.Length>0?v[0]:fb.r, v.Length>1?v[1]:fb.g, v.Length>2?v[2]:fb.b, v.Length>3?v[3]:fb.a);
        private static Vector4 ToVector4(float[] v, Vector4 fb) => v == null || v.Length == 0 ? fb : new Vector4(v.Length>0?v[0]:fb.x, v.Length>1?v[1]:fb.y, v.Length>2?v[2]:fb.z, v.Length>3?v[3]:fb.w);

        // =================== hash / name utils ===================

        private static string PropsHash(CompiledSpace.RenderMesh mesh)
        {
            unchecked
            {
                uint h = 2166136261u;
                foreach (var kv in mesh.Props)
                {
                    h = HashStringStep(h, kv.Key);
                    if (kv.Value.StringValue != null) h = HashStringStep(h, kv.Value.StringValue);
                    h ^= kv.Value.RawValue; h *= 16777619u;
                }
                return h.ToString("X8");
            }
        }

        private static uint StableHash32(string s) { unchecked { return HashStringStep(2166136261u, s ?? string.Empty); } }

        private static uint HashStringStep(uint h, string s)
        {
            unchecked { if (s == null) return h; foreach (char c in s) { h ^= (uint)char.ToLowerInvariant(c); h *= 16777619u; } return h; }
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

        // =================== asset helpers ===================

        private static void SaveAsset(UnityEngine.Object obj, string path)
        {
            path = path.Replace('\\', '/');
            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (existing != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(obj, path);
            AssetDatabase.ImportAsset(path);
        }

        private static void EnsureFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || AssetDatabase.IsValidFolder(folderPath)) return;
            string parent = Path.GetDirectoryName(folderPath).Replace('\\', '/');
            string leaf   = Path.GetFileName(folderPath);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            if (!AssetDatabase.IsValidFolder(folderPath)) AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
