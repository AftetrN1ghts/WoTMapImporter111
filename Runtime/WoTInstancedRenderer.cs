using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace WoTMapImporter.Runtime
{
    /// <summary>
    /// Renders many copies of a single mesh via GPU instancing, storing only a
    /// per-instance transform matrix instead of baking duplicated geometry into a
    /// combined mesh asset.  This is what the flora scatter uses so a map with
    /// hundreds of thousands of grass blades costs a few megabytes of matrices
    /// (64 bytes each) rather than hundreds of megabytes of duplicated vertices.
    ///
    /// A simple distance cull (<see cref="maxDrawDistance"/>) acts as a coarse LOD:
    /// far-away flora patches stop being submitted entirely.
    ///
    /// Requires the assigned material's shader to support GPU instancing
    /// (WoT/ObjectPBS and URP/Standard Lit all do).
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("WoT Map Importer/Instanced Renderer")]
    public sealed class WoTInstancedRenderer : MonoBehaviour
    {
        [Tooltip("Mesh instanced at every entry of 'instances'.")]
        public Mesh mesh;

        [Tooltip("Material used for all instances. Its shader must support GPU instancing.")]
        public Material material;

        [Tooltip("Per-instance local-space TRS matrices (relative to this transform).")]
        public Matrix4x4[] instances;

        public ShadowCastingMode shadowCasting = ShadowCastingMode.Off;
        public bool receiveShadows = true;

        [Tooltip("Distance beyond which the whole patch is culled. 0 = never cull (LOD).")]
        public float maxDrawDistance = 0f;

        [Tooltip("Local-space bounds enclosing all instances (used for distance culling).")]
        public Bounds localBounds;

        private const int BatchSize = 1023; // Graphics.DrawMeshInstanced hard limit.

        private readonly List<Matrix4x4[]> _batches = new List<Matrix4x4[]>();
        private Matrix4x4 _lastRoot;
        private bool _dirty = true;

        private void OnEnable() { _dirty = true; }
        private void OnValidate() { _dirty = true; }

        private void RebuildBatches()
        {
            _batches.Clear();
            _dirty = false;
            if (instances == null || instances.Length == 0 || mesh == null) return;

            Matrix4x4 root = transform.localToWorldMatrix;
            _lastRoot = root;
            for (int i = 0; i < instances.Length; i += BatchSize)
            {
                int count = Mathf.Min(BatchSize, instances.Length - i);
                var arr = new Matrix4x4[count];
                for (int k = 0; k < count; k++)
                    arr[k] = root * instances[i + k];
                _batches.Add(arr);
            }
        }

        private void Update()
        {
            if (mesh == null || material == null || instances == null || instances.Length == 0)
                return;

            if (_dirty || transform.localToWorldMatrix != _lastRoot)
                RebuildBatches();

            if (maxDrawDistance > 0f)
            {
                // Coarse LOD: skip the whole patch when the nearest point of its
                // bounds is beyond the draw distance from the active camera.
                Camera cam = Camera.main;
                if (cam != null)
                {
                    Vector3 worldCenter = transform.TransformPoint(localBounds.center);
                    float dist = Vector3.Distance(cam.transform.position, worldCenter) - localBounds.extents.magnitude;
                    if (dist > maxDrawDistance) return;
                }
            }

            int layer = gameObject.layer;
            for (int i = 0; i < _batches.Count; i++)
            {
                Matrix4x4[] batch = _batches[i];
                Graphics.DrawMeshInstanced(mesh, 0, material, batch, batch.Length, null,
                    shadowCasting, receiveShadows, layer);
            }
        }
    }
}
