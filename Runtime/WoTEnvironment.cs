using UnityEngine;

namespace WoTMapImporter.Runtime
{
    /// <summary>
    /// Makes an imported map's original lighting environment self-contained.
    ///
    /// RenderSettings (skybox, ambient, fog) are stored per-scene, not inside a
    /// prefab, so a freshly placed map prefab would otherwise lose its skybox and
    /// show a flat grey sky. This component holds the imported environment and
    /// re-applies it to RenderSettings whenever the map is enabled in a scene
    /// (also in the editor via ExecuteAlways).
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class WoTEnvironment : MonoBehaviour
    {
        public Material Skybox;
        public bool ApplyAmbient = true;
        public Color AmbientColor = new Color(0.3f, 0.35f, 0.4f, 1f);
        public bool ApplyFog = false;
        public Color FogColor = new Color(0.6f, 0.7f, 0.8f, 1f);
        public float FogStart = 100f;
        public float FogEnd = 1000f;
        public Light Sun;

        private void OnEnable() { Apply(); }
        private void OnValidate() { Apply(); }

        public void Apply()
        {
            if (Skybox != null)
                RenderSettings.skybox = Skybox;

            if (ApplyAmbient)
            {
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = AmbientColor;
            }

            if (ApplyFog)
            {
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.Linear;
                RenderSettings.fogColor = FogColor;
                RenderSettings.fogStartDistance = FogStart;
                RenderSettings.fogEndDistance = FogEnd;
            }

            if (Sun != null)
                RenderSettings.sun = Sun;

            DynamicGI.UpdateEnvironment();
        }
    }
}
