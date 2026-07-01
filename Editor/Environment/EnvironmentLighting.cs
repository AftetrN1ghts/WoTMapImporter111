using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using UnityEditor;
using UnityEngine;
using WoTMapImporter.Editor.Image;
using WoTMapImporter.Editor.Package;
using WoTMapImporter.Editor.Xml;

namespace WoTMapImporter.Editor.EnvLighting
{
    /// <summary>
    /// Parses a WoT/BigWorld sky environment file (system/data/sky&lt;map&gt;.xml,
    /// referenced by space.settings' timeOfDay/skyGradientDome keys) and applies the
    /// original map lighting + skybox to the Unity scene at a chosen time of day.
    ///
    /// The sky xml holds a day_night_cycle with time-keyed sun (lightkey) and ambient
    /// (ambientkey) colours, a base sun orientation (angle/angleZ), height-fog, HDR
    /// luminance multipliers, a vertical sky-gradient texture and a panoramic cloud
    /// skydome. We interpolate the colour keys at the requested hour, place a
    /// Directional Light on the day arc, drive RenderSettings ambient/fog and build a
    /// WoT/Skybox material from the gradient + clouds.
    /// </summary>
    public static class EnvironmentLighting
    {
        // ---------------- data model ----------------

        public struct ColorKey
        {
            public float Time;   // hour 0..24
            public Color Color;  // linear-ish 0..1
        }

        public class EnvironmentData
        {
            public float SunAngle = 0f;    // day_night_cycle/angle (yaw, deg)
            public float SunAngleZ = 0f;   // day_night_cycle/angleZ (arc tilt, deg)
            public Color SunColor = Color.white;
            public float StartTime = 12f;

            public List<ColorKey> LightKeys = new List<ColorKey>();
            public List<ColorKey> AmbientKeys = new List<ColorKey>();

            public float SunLumMultiplier = 1f;
            public float AmbientLumMultiplier = 1f;
            public float SkyLumMultiplier = 1f;

            public bool FogEnabled = false;
            public Color FogColor = new Color(0.6f, 0.7f, 0.8f, 1f);
            public float FogNear = 100f;
            public float FogFar = 1000f;

            public string GradientTexture;  // system/maps/sky_gradient_*.dds
            public string SkyDomeVisual;    // maps/skyboxes/*/skydome/skybox.visual
            public string CloudsTexture;    // clouds.dds (from skydome material)
            public string CloudsMaskTexture;

            public bool HasAnyLightData => LightKeys.Count > 0 || AmbientKeys.Count > 0;
        }

        // ---------------- parsing ----------------

        public static EnvironmentData Decode(WoTPackageManager resMgr, string skyXmlResource, string skyDomeVisual, List<string> warnings)
        {
            if (resMgr == null || string.IsNullOrEmpty(skyXmlResource)) return null;
            byte[] bytes = resMgr.ReadBytes(skyXmlResource);
            if (bytes == null)
            {
                warnings?.Add($"Sky env file not found: {skyXmlResource}");
                return null;
            }

            XmlDocument doc;
            try { doc = XmlUnpacker.ReadBytes(bytes); }
            catch (Exception e) { warnings?.Add($"Could not parse sky env {skyXmlResource}: {e.Message}"); return null; }
            if (doc?.DocumentElement == null) return null;

            var root = doc.DocumentElement;
            var env = new EnvironmentData { SkyDomeVisual = skyDomeVisual };

            var dnc = root.SelectSingleNode(".//day_night_cycle") as XmlElement;
            if (dnc != null)
            {
                env.SunAngle = ParseFloat(Text(dnc, "angle"), 0f);
                env.SunAngleZ = ParseFloat(Text(dnc, "angleZ"), 0f);
                env.SunColor = Parse255(Text(dnc, "sunColor"), Color.white);
                env.StartTime = ParseFloat(Text(dnc, "starttime"), 12f);
                foreach (XmlElement lk in dnc.SelectNodes("lightkey"))
                    env.LightKeys.Add(new ColorKey { Time = ParseFloat(Text(lk, "time"), 0f), Color = Parse255(Text(lk, "colour"), Color.white) });
                foreach (XmlElement ak in dnc.SelectNodes("ambientkey"))
                    env.AmbientKeys.Add(new ColorKey { Time = ParseFloat(Text(ak, "time"), 0f), Color = Parse255(Text(ak, "colour"), Color.gray) });
            }

            env.LightKeys.Sort((a, b) => a.Time.CompareTo(b.Time));
            env.AmbientKeys.Sort((a, b) => a.Time.CompareTo(b.Time));

            // HDR luminance multipliers scale the perceived brightness.
            var hdrEnv = root.SelectSingleNode(".//HDR/environment") as XmlElement;
            if (hdrEnv != null)
            {
                env.SunLumMultiplier = ParseFloat(Text(hdrEnv, "sunlightLumMultiplier"), 1f);
                env.AmbientLumMultiplier = ParseFloat(Text(hdrEnv, "ambientLumMultiplier"), 1f);
                env.SkyLumMultiplier = ParseFloat(Text(hdrEnv, "skyLumMultiplier"), 1f);
            }

            // Height fog: use the "forward" block if present, else "deferred".
            var fog = root.SelectSingleNode(".//Fog") as XmlElement;
            if (fog != null)
            {
                env.FogEnabled = ParseBool(Text(fog, "enable"), false);
                var block = (fog.SelectSingleNode("forward") ?? fog.SelectSingleNode("deferred")) as XmlElement;
                if (block != null)
                {
                    env.FogNear = ParseFloat(Text(block, "nearLow"), 100f);
                    env.FogFar = ParseFloat(Text(block, "farLow"), 1000f);
                    env.FogColor = Parse255OrUnit(Text(block, "colorLow"), env.FogColor);
                }
            }

            // Vertical sky gradient (skyGradientDome sub-file references it directly here).
            env.GradientTexture = FindGradient(root);

            // Clouds come from the skydome visual's material properties.
            if (!string.IsNullOrEmpty(skyDomeVisual))
                ExtractSkyDomeTextures(resMgr, skyDomeVisual, env, warnings);

            return env;
        }

        private static string FindGradient(XmlElement root)
        {
            // The sky gradient texture is stored under a "texture" node near the top
            // of the sky file (skyGradientDome). Grab the first *sky_gradient* dds.
            foreach (XmlNode n in root.SelectNodes(".//texture"))
            {
                string t = n.InnerText?.Trim();
                if (!string.IsNullOrEmpty(t) && t.IndexOf("gradient", StringComparison.OrdinalIgnoreCase) >= 0)
                    return t.Replace('\\', '/');
            }
            foreach (XmlNode n in root.SelectNodes(".//texture"))
            {
                string t = n.InnerText?.Trim();
                if (!string.IsNullOrEmpty(t) && t.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                    return t.Replace('\\', '/');
            }
            return null;
        }

        private static void ExtractSkyDomeTextures(WoTPackageManager resMgr, string visualResource, EnvironmentData env, List<string> warnings)
        {
            byte[] bytes = resMgr.ReadBytes(visualResource);
            if (bytes == null) { warnings?.Add($"Skydome visual not found: {visualResource}"); return; }
            XmlDocument doc;
            try { doc = XmlUnpacker.ReadBytes(bytes); }
            catch (Exception e) { warnings?.Add($"Could not parse skydome visual: {e.Message}"); return; }
            if (doc?.DocumentElement == null) return;

            foreach (XmlNode prop in doc.DocumentElement.SelectNodes(".//property"))
            {
                var texNode = prop.SelectSingleNode("Texture");
                if (texNode == null) continue;
                string path = texNode.InnerText?.Trim()?.Replace('\\', '/');
                if (string.IsNullOrEmpty(path)) continue;
                if (path.IndexOf("mask", StringComparison.OrdinalIgnoreCase) >= 0)
                    env.CloudsMaskTexture = env.CloudsMaskTexture ?? path;
                else if (path.IndexOf("cloud", StringComparison.OrdinalIgnoreCase) >= 0)
                    env.CloudsTexture = env.CloudsTexture ?? path;
            }
        }

        // ---------------- time-of-day sampling ----------------

        public static Color SampleLight(EnvironmentData env, float hour) => Sample(env.LightKeys, hour, env.SunColor);
        public static Color SampleAmbient(EnvironmentData env, float hour) => Sample(env.AmbientKeys, hour, new Color(0.3f, 0.35f, 0.4f));

        private static Color Sample(List<ColorKey> keys, float hour, Color fallback)
        {
            if (keys == null || keys.Count == 0) return fallback;
            if (keys.Count == 1) return keys[0].Color;
            hour = Mathf.Repeat(hour, 24f);
            if (hour <= keys[0].Time) return LerpWrap(keys, keys.Count - 1, 0, hour);
            if (hour >= keys[keys.Count - 1].Time) return LerpWrap(keys, keys.Count - 1, 0, hour);
            for (int i = 0; i < keys.Count - 1; i++)
            {
                if (hour >= keys[i].Time && hour <= keys[i + 1].Time)
                {
                    float t = Mathf.InverseLerp(keys[i].Time, keys[i + 1].Time, hour);
                    return Color.Lerp(keys[i].Color, keys[i + 1].Color, t);
                }
            }
            return keys[keys.Count - 1].Color;
        }

        // Interpolate across the midnight wrap between the last and first key.
        private static Color LerpWrap(List<ColorKey> keys, int a, int b, float hour)
        {
            float ta = keys[a].Time;
            float tb = keys[b].Time + 24f; // wrap forward
            float h = hour < keys[b].Time ? hour + 24f : hour;
            float span = tb - ta;
            float t = span > 1e-4f ? Mathf.Clamp01((h - ta) / span) : 0f;
            return Color.Lerp(keys[a].Color, keys[b].Color, t);
        }

        // Sun elevation over the day: 0 at 06:00/18:00, peak at 12:00, negative at night.
        public static Quaternion SunRotation(EnvironmentData env, float hour)
        {
            float elevation = Mathf.Sin((hour - 6f) / 12f * Mathf.PI) * 75f; // deg above horizon
            float azimuth = env.SunAngle + (hour - 12f) / 12f * 90f;         // east -> west sweep
            // Directional light forward = direction light travels; sun high => point down.
            return Quaternion.Euler(elevation, azimuth, env.SunAngleZ);
        }

        public static float DayFactor(float hour)
        {
            // 1 in full day, 0 at night (below horizon), smooth around dawn/dusk.
            float elev = Mathf.Sin((hour - 6f) / 12f * Mathf.PI);
            return Mathf.Clamp01(elev * 4f + 0.15f);
        }

        // ---------------- scene apply ----------------

        public static void Apply(EnvironmentData env, float hour, string folder, WoTPackageManager resMgr, GameObject root, List<string> warnings)
        {
            if (env == null) return;

            float dayF = DayFactor(hour);

            // --- Directional (sun) light ---
            var sunGo = new GameObject("WoT Sun");
            sunGo.transform.SetParent(root.transform, false);
            var light = sunGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = SunRotation(env, hour);
            Color sunCol = SampleLight(env, hour);
            light.color = sunCol;
            light.intensity = Mathf.Lerp(0.05f, 1.15f, dayF) * Mathf.Clamp(env.SunLumMultiplier / 6f, 0.5f, 1.5f);
            light.shadows = LightShadows.Soft;

            // --- Ambient ---
            Color amb = SampleAmbient(env, hour);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = amb * Mathf.Clamp(env.AmbientLumMultiplier / 1.5f, 0.5f, 1.5f);
            RenderSettings.sun = light;

            // --- Fog ---
            if (env.FogEnabled)
            {
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.Linear;
                RenderSettings.fogColor = env.FogColor * Mathf.Lerp(0.4f, 1f, dayF);
                RenderSettings.fogStartDistance = env.FogNear;
                RenderSettings.fogEndDistance = env.FogFar;
            }

            // --- Skybox ---
            var skyMat = BuildSkyboxMaterial(env, hour, dayF, sunCol, folder, resMgr, warnings);
            if (skyMat != null)
                RenderSettings.skybox = skyMat;

            DynamicGI.UpdateEnvironment();
        }

        private static Material BuildSkyboxMaterial(EnvironmentData env, float hour, float dayF, Color sunCol, string folder, WoTPackageManager resMgr, List<string> warnings)
        {
            var shader = Shader.Find("WoT/Skybox");
            if (shader == null) { warnings?.Add("WoT/Skybox shader not found; skybox skipped."); return null; }

            var mat = new Material(shader) { name = "WoT_Skybox" };

            var grad = LoadTexture(env.GradientTexture, folder, resMgr, false, TextureWrapMode.Clamp);
            var clouds = LoadTexture(env.CloudsTexture, folder, resMgr, true, TextureWrapMode.Repeat);
            var mask = LoadTexture(env.CloudsMaskTexture, folder, resMgr, true, TextureWrapMode.Repeat);
            if (grad != null) mat.SetTexture("_Gradient", grad);
            if (clouds != null) mat.SetTexture("_Clouds", clouds);
            if (mask != null) mat.SetTexture("_CloudsMask", mask);

            // Tint the whole sky towards the sun colour and darken at night.
            Color tint = Color.Lerp(new Color(0.06f, 0.09f, 0.16f), Color.white, dayF);
            tint = Color.Lerp(tint, sunCol, 0.15f * dayF);
            mat.SetColor("_Tint", tint);
            mat.SetFloat("_Exposure", Mathf.Lerp(0.5f, 1.1f, dayF));
            mat.SetFloat("_CloudOpacity", 1f);

            string matPath = $"{folder}/WoT_Skybox.mat";
            SaveAsset(mat, matPath);
            return AssetDatabase.LoadAssetAtPath<Material>(matPath) ?? mat;
        }

        private static Texture2D LoadTexture(string resource, string folder, WoTPackageManager resMgr, bool linear, TextureWrapMode wrap)
        {
            if (string.IsNullOrEmpty(resource) || resMgr == null) return null;
            byte[] bytes = resMgr.ReadBytes(resource);
            if (bytes == null) return null;
            try
            {
                Texture2D tex;
                if (bytes.Length >= 4 && BitConverter.ToUInt32(bytes, 0) == DdsDecoder.MAGIC)
                {
                    try { tex = DdsDecoder.ReadDecompressed(bytes, PathName(resource), linear, true); }
                    catch { tex = DdsDecoder.Read(bytes, PathName(resource), linear); }
                }
                else
                {
                    tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
                    if (!tex.LoadImage(bytes, false)) { UnityEngine.Object.DestroyImmediate(tex); return null; }
                }
                tex.name = SafeAssetName("sky_" + PathName(resource));
                tex.wrapMode = wrap;
                tex.filterMode = FilterMode.Bilinear;
                tex.anisoLevel = 2;
                string path = $"{folder}/{tex.name}.asset";
                SaveAsset(tex, path);
                return AssetDatabase.LoadAssetAtPath<Texture2D>(path) ?? tex;
            }
            catch { return null; }
        }

        // ---------------- helpers ----------------

        private static string Text(XmlElement e, string child)
        {
            var n = e?.SelectSingleNode(child);
            return n?.InnerText?.Trim();
        }

        private static float ParseFloat(string s, float fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            return float.TryParse(s.Trim().Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        private static bool ParseBool(string s, bool fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            s = s.Trim().ToLowerInvariant();
            return s == "true" || s == "1";
        }

        // Colours in the sky file are 0..255 triples (sometimes with a >1 alpha/intensity).
        private static Color Parse255(string s, Color fallback)
        {
            var p = SplitFloats(s);
            if (p == null || p.Length < 3) return fallback;
            return new Color(p[0] / 255f, p[1] / 255f, p[2] / 255f, 1f);
        }

        // Some fog colours are stored 0..1, some 0..255; detect by magnitude.
        private static Color Parse255OrUnit(string s, Color fallback)
        {
            var p = SplitFloats(s);
            if (p == null || p.Length < 3) return fallback;
            float max = Mathf.Max(p[0], Mathf.Max(p[1], p[2]));
            float d = max > 1.001f ? 255f : 1f;
            return new Color(p[0] / d, p[1] / d, p[2] / d, 1f);
        }

        private static float[] SplitFloats(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var parts = s.Trim().Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            var vals = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out vals[i])) return null;
            return vals;
        }

        private static string PathName(string resource)
        {
            if (string.IsNullOrEmpty(resource)) return "tex";
            string n = resource.Replace('\\', '/');
            int slash = n.LastIndexOf('/');
            if (slash >= 0) n = n.Substring(slash + 1);
            int dot = n.LastIndexOf('.');
            if (dot >= 0) n = n.Substring(0, dot);
            return n;
        }

        private static string SafeAssetName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unnamed";
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s;
        }

        private static void SaveAsset(UnityEngine.Object obj, string path)
        {
            path = path.Replace('\\', '/');
            var old = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (old != null && old.GetType() == obj.GetType()) return;
            AssetDatabase.CreateAsset(obj, path);
            AssetDatabase.ImportAsset(path);
        }
    }
}
