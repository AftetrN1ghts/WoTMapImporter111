using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using WoTMapImporter.Editor.Data;
using WoTMapImporter.Editor.Package;
using WoTMapImporter.Editor.Utils;
using WoTMapImporter.Editor.Xml;

namespace WoTMapImporter.Editor
{
    /// <summary>
    /// EditorWindow for selecting a WoT installation, parsing the map list,
    /// and importing maps into the current Unity scene.
    ///
    /// IMPORTANT: This file deliberately uses ONLY the most basic
    /// EditorGUILayout overloads (no GUILayoutOption arguments anywhere)
    /// because some Unity versions have alternative overloads like
    /// Toggle(bool, Texture, ...) that the compiler prefers over the
    /// params GUILayoutOption[] overload. Avoiding the GUILayoutOption[]
    /// overloads altogether eliminates the ambiguity.
    /// </summary>
    public class WoTMapImporterWindow : EditorWindow
    {
        private string _wotPath = @"C:\Games\World_of_Tanks";
        private string _outputFolder = "Assets/WoTImported";
        private bool _loadTerrain = true;
        private bool _loadObjects = true;
        private bool _loadVegetation = true;
        private bool _loadFlora = true;
        private float _floraDensity = 0.25f;
        private bool _floraGpuInstancing = true;
        private float _floraDrawDistance = 250f;
        private bool _loadNormals = true;
        private bool _loadWetness = false;
        private int _maxResolution = 4097;
        private int _terrainBakeResolution = 2048;
        private bool _terrainLiveSplat = true;
        private WoTMapImporter.TerrainImportMode _terrainMode = WoTMapImporter.TerrainImportMode.MeshChunks;
        private bool _mapsParsed;
        private Vector2 _scroll;
        private List<MapInfo> _maps = new List<MapInfo>();
        private int _selectedMap = -1;
        private string _filter = "";
        private string _status = "";
        private bool _isImporting;

        [MenuItem("Tools/WoT Map Importer")]
        public static void Open()
        {
            var w = GetWindow<WoTMapImporterWindow>(true, "WoT Map Importer", true);
            w.minSize = new Vector2(520, 600);
            w.Show();
        }

        private void OnEnable()
        {
            _wotPath = EditorPrefs.GetString("WoTMapImporter.wotPath", _wotPath);
            _outputFolder = EditorPrefs.GetString("WoTMapImporter.outputFolder", _outputFolder);
            _loadTerrain = EditorPrefs.GetBool("WoTMapImporter.loadTerrain", _loadTerrain);
            _loadObjects = EditorPrefs.GetBool("WoTMapImporter.loadObjects", _loadObjects);
            _loadVegetation = EditorPrefs.GetBool("WoTMapImporter.loadVegetation", _loadVegetation);
            _loadFlora = EditorPrefs.GetBool("WoTMapImporter.loadFlora", _loadFlora);
            _floraDensity = EditorPrefs.GetFloat("WoTMapImporter.floraDensity", _floraDensity);
            _floraGpuInstancing = EditorPrefs.GetBool("WoTMapImporter.floraGpuInstancing", _floraGpuInstancing);
            _floraDrawDistance = EditorPrefs.GetFloat("WoTMapImporter.floraDrawDistance", _floraDrawDistance);
            _terrainMode = (WoTMapImporter.TerrainImportMode)EditorPrefs.GetInt("WoTMapImporter.terrainMode", (int)_terrainMode);
            _terrainBakeResolution = EditorPrefs.GetInt("WoTMapImporter.terrainBakeResolution", _terrainBakeResolution);
            _terrainLiveSplat = EditorPrefs.GetBool("WoTMapImporter.terrainLiveSplat", _terrainLiveSplat);
        }

        private void OnDisable()
        {
            EditorPrefs.SetString("WoTMapImporter.wotPath", _wotPath);
            EditorPrefs.SetString("WoTMapImporter.outputFolder", _outputFolder);
            EditorPrefs.SetBool("WoTMapImporter.loadTerrain", _loadTerrain);
            EditorPrefs.SetBool("WoTMapImporter.loadObjects", _loadObjects);
            EditorPrefs.SetBool("WoTMapImporter.loadVegetation", _loadVegetation);
            EditorPrefs.SetBool("WoTMapImporter.loadFlora", _loadFlora);
            EditorPrefs.SetFloat("WoTMapImporter.floraDensity", _floraDensity);
            EditorPrefs.SetBool("WoTMapImporter.floraGpuInstancing", _floraGpuInstancing);
            EditorPrefs.SetFloat("WoTMapImporter.floraDrawDistance", _floraDrawDistance);
            EditorPrefs.SetInt("WoTMapImporter.terrainMode", (int)_terrainMode);
            EditorPrefs.SetInt("WoTMapImporter.terrainBakeResolution", _terrainBakeResolution);
            EditorPrefs.SetBool("WoTMapImporter.terrainLiveSplat", _terrainLiveSplat);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("World of Tanks - Map Importer", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            DrawSettings();
            EditorGUILayout.Space();

            DrawParseSection();
            EditorGUILayout.Space();

            DrawMapsList();
            EditorGUILayout.Space();

            DrawImportButton();
            EditorGUILayout.Space();

            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, MessageType.Info);
        }

        private void DrawSettings()
        {
            EditorGUILayout.LabelField("Paths", EditorStyles.boldLabel);
            _wotPath = EditorGUILayout.TextField("WoT Installation", _wotPath);
            if (GUILayout.Button("Browse..."))
            {
                string sel = EditorUtility.OpenFolderPanel("Select World of Tanks folder", _wotPath, "");
                if (!string.IsNullOrEmpty(sel)) _wotPath = sel;
            }
            _outputFolder = EditorGUILayout.TextField("Output Folder", _outputFolder);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Options", EditorStyles.boldLabel);
            _loadTerrain = EditorGUILayout.Toggle("Import terrain", _loadTerrain);
            _loadObjects = EditorGUILayout.Toggle("Load static objects", _loadObjects);
            _loadVegetation = EditorGUILayout.Toggle("Load SpeedTree vegetation", _loadVegetation);
            _loadFlora = EditorGUILayout.Toggle("Load ground flora/grass", _loadFlora);
            using (new EditorGUI.DisabledScope(!_loadFlora))
            {
                _floraDensity = EditorGUILayout.Slider("Flora density (per m²)", _floraDensity, 0.02f, 2f);
                _floraGpuInstancing = EditorGUILayout.Toggle(
                    new GUIContent("Flora GPU instancing",
                        "Store per-instance matrices and render with GPU instancing instead of baking a combined mesh per chunk. Much smaller on disk."),
                    _floraGpuInstancing);
                using (new EditorGUI.DisabledScope(!_floraGpuInstancing))
                    _floraDrawDistance = EditorGUILayout.Slider(
                        new GUIContent("Flora draw distance (m)", "Cull flora patches farther than this from the camera. 0 = never cull."),
                        _floraDrawDistance, 0f, 1000f);
            }
            using (new EditorGUI.DisabledScope(!_loadTerrain))
            {
                _loadNormals = EditorGUILayout.Toggle("Load terrain normals", _loadNormals);
                _loadWetness = EditorGUILayout.Toggle("Load wetness/roughness map", _loadWetness);
                _terrainMode = (WoTMapImporter.TerrainImportMode)EditorGUILayout.EnumPopup("Terrain mode", _terrainMode);
                using (new EditorGUI.DisabledScope(_terrainMode == WoTMapImporter.TerrainImportMode.MeshChunks))
                {
                    _maxResolution = EditorGUILayout.IntPopup(
                        "Max heightmap resolution",
                        _maxResolution,
                        new[] { "1025", "2049", "4097" },
                        new[] { 1025, 2049, 4097 });
                }
                using (new EditorGUI.DisabledScope(_terrainMode != WoTMapImporter.TerrainImportMode.MeshChunks))
                {
                    _terrainLiveSplat = EditorGUILayout.Toggle("Live-splat terrain (recommended)", _terrainLiveSplat);
                    using (new EditorGUI.DisabledScope(_terrainLiveSplat))
                    {
                        _terrainBakeResolution = EditorGUILayout.IntPopup(
                            "Baked terrain resolution",
                            _terrainBakeResolution,
                            new[] { "512", "1024", "2048", "4096" },
                            new[] { 512, 1024, 2048, 4096 });
                    }
                }
            }
            if (!_loadTerrain)
                EditorGUILayout.HelpBox("Terrain import is disabled. The importer will still load space.bin static objects if 'Load static objects' is enabled.", MessageType.Info);
            else if (_terrainMode == WoTMapImporter.TerrainImportMode.MeshChunks)
                EditorGUILayout.HelpBox("MeshChunks uses one MeshRenderer per WoT .cdata chunk and samples original blend textures without Unity Terrain alphamap normalization.", MessageType.Info);
        }

        private void DrawParseSection()
        {
            if (GUILayout.Button("Parse WoT (load maps)"))
                Parse();
            if (_mapsParsed)
                EditorGUILayout.LabelField("Maps found: " + _maps.Count);
        }

        private void DrawMapsList()
        {
            if (!_mapsParsed || _maps.Count == 0) return;

            EditorGUILayout.LabelField("Maps", EditorStyles.boldLabel);
            _filter = EditorGUILayout.TextField("Filter", _filter);

            // Most basic BeginScrollView overload - just a Vector2. No options.
            // The window itself has minSize 600 height so the scrollview is naturally
            // tall enough.
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            for (int i = 0; i < _maps.Count; i++)
            {
                var m = _maps[i];
                if (!string.IsNullOrEmpty(_filter))
                {
                    string lowerFilter = _filter.ToLowerInvariant();
                    string lowerName = m.Name.ToLowerInvariant();
                    string lowerLocalized = m.LocalizedName.ToLowerInvariant();
                    if (lowerName.IndexOf(lowerFilter, System.StringComparison.Ordinal) < 0 &&
                        lowerLocalized.IndexOf(lowerFilter, System.StringComparison.Ordinal) < 0)
                        continue;
                }

                EditorGUILayout.BeginHorizontal();
                bool wasSelected = _selectedMap == i;
                bool nowSelected = EditorGUILayout.Toggle(wasSelected);
                if (nowSelected != wasSelected)
                    _selectedMap = nowSelected ? i : -1;
                EditorGUILayout.LabelField(m.LogName + "  (" + m.Name + ")");
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawImportButton()
        {
            using (new EditorGUI.DisabledScope(_isImporting || _selectedMap < 0))
            {
                if (GUILayout.Button("Import Selected Map"))
                    DoImport();
            }
        }

        // ================== actions ==================

        private void Parse()
        {
            _status = "";
            if (!Directory.Exists(_wotPath))
            {
                _status = "Path does not exist: " + _wotPath + "\nCheck 'WoT Installation' field.";
                WoTLogger.Error(_status);
                return;
            }
            string resPath = Path.Combine(_wotPath, "res");
            string pkgsPath = Path.Combine(resPath, "packages");
            string versionXml = Path.Combine(_wotPath, "version.xml");
            if (!Directory.Exists(resPath))
            {
                _status = "'res' folder not found in " + _wotPath +
                          "\nThis is not a valid WoT installation (expected res/packages/scripts.pkg or res/scripts).";
                WoTLogger.Error(_status);
                return;
            }
            if (!Directory.Exists(pkgsPath))
            {
                if (!Directory.Exists(Path.Combine(resPath, "scripts")))
                {
                    _status = "'res/packages' and 'res/scripts' folders not found.\nWoT may not be fully installed.";
                    WoTLogger.Error(_status);
                    return;
                }
                pkgsPath = resPath; // Fallback so package manager has a valid directory
            }

            string scriptsPkg = Path.Combine(pkgsPath, "scripts.pkg");
            string scriptsDir = Path.Combine(resPath, "scripts");
            if (!File.Exists(scriptsPkg) && !Directory.Exists(scriptsDir))
            {
                // Show first few .pkg files we found, so user knows what's there.
                var pkgFiles = Directory.Exists(pkgsPath) ? Directory.GetFiles(pkgsPath, "*.pkg") : new string[0];
                string fileList = pkgFiles.Length == 0
                    ? "(no .pkg files found)"
                    : string.Join(", ", System.Linq.Enumerable.Take(pkgFiles, 5)
                        .Select(System.IO.Path.GetFileName));
                _status = "scripts.pkg not found in res/packages and res/scripts not found.\nFound: " + fileList;
                WoTLogger.Error(_status);
                return;
            }

            WoTLogger.Info("Parsing WoT at " + _wotPath);
            try
            {
                WoTVersion version = default;
                if (File.Exists(versionXml))
                {
                    var versionDoc = new System.Xml.XmlDocument();
                    versionDoc.Load(versionXml);
                    var versionText = versionDoc.SelectSingleNode("/version")?.InnerText
                                      ?? versionDoc.DocumentElement?.InnerText ?? "";
                    string realm = versionDoc.SelectSingleNode("/version/meta/realm")?.InnerText?.Trim() ?? "RU";
                    WoTVersion.TryParse(versionText, realm, out version);
                    WoTLogger.Info("WoT version: " + version + " (" + realm + ")");
                }
                else
                {
                    WoTLogger.Warn("version.xml not found, proceeding without version info");
                }

                var pkgMgr = new WoTPackageManager(pkgsPath, new[] { "scripts.pkg" });
                try
                {
                    _maps = MapListParser.ParseMaps(pkgMgr);
                    _mapsParsed = true;
                    if (_maps.Count == 0)
                    {
                        _status = "Found 0 maps in scripts.pkg. Check that:\n" +
                                  "  - scripts.pkg is the correct file\n" +
                                  "  - It contains scripts/arena_defs/_list_.xml\n" +
                                  "  - Logs: %TEMP%/WoTMapImporter.log";
                    }
                    else
                    {
                        _status = "Found " + _maps.Count + " maps";
                    }
                    WoTLogger.Info(_status);
                }
                finally
                {
                    pkgMgr.Dispose();
                }
            }
            catch (System.Exception e)
            {
                _status = "Parse failed: " + e.Message + "\nLogs: %TEMP%/WoTMapImporter.log";
                WoTLogger.Error(_status);
                WoTLogger.Error(e.StackTrace);
            }
        }

        private void DoImport()
        {
            if (_selectedMap < 0 || _selectedMap >= _maps.Count) return;
            var mapInfo = _maps[_selectedMap];
            string wotResPath = Path.Combine(_wotPath, "res", "packages");
            if (!Directory.Exists(wotResPath))
            {
                wotResPath = Path.Combine(_wotPath, "res");
                if (!Directory.Exists(wotResPath))
                {
                    _status = "res/packages and res not found";
                    return;
                }
            }

            // The map list only fills Name/LocalizedName. We must read the per-map
            // arena_defs/<map>.xml to get the real geometry path + bounding box,
            // otherwise we silently fall back to "spaces/<name>" and lose bounds.
            try
            {
                using var infoMgr = new WoTPackageManager(wotResPath, new[] { "scripts.pkg" });
                var full = MapListParser.ParseMapInfo(infoMgr, mapInfo.Name);
                full.LocalizedName = mapInfo.LocalizedName;
                mapInfo = full;
                WoTLogger.Info($"Map info: name={mapInfo.Name} geometry='{mapInfo.Geometry}' " +
                               $"bl={mapInfo.BottomLeft} ur={mapInfo.UpperRight}");
            }
            catch (System.Exception e)
            {
                WoTLogger.Warn($"Could not read arena_defs/{mapInfo.Name}.xml " +
                               $"(falling back to spaces/{mapInfo.Name}): {e.Message}");
            }
            _isImporting = true;
            try
            {
                var settings = new WoTMapImporter.ImportSettings
                {
                    LoadTerrain = _loadTerrain,
                    LoadObjects = _loadObjects,
                    LoadVegetation = _loadVegetation,
                    LoadFlora = _loadFlora,
                    FloraDensity = _floraDensity,
                    FloraGpuInstancing = _floraGpuInstancing,
                    FloraDrawDistance = _floraDrawDistance,
                    LoadNormals = _loadNormals,
                    LoadWetness = _loadWetness,
                    MaxHeightmapResolution = _maxResolution,
                    TerrainBakeResolution = _terrainBakeResolution,
                    TerrainLiveSplat = _terrainLiveSplat,
                    TerrainMode = _terrainMode,
                };

                EditorUtility.DisplayProgressBar("WoT Map Importer", "Starting import...", 0f);
                var result = WoTMapImporter.ImportMap(mapInfo, wotResPath, _outputFolder, settings,
                    (p, msg) =>
                    {
                        EditorUtility.DisplayProgressBar("WoT Map Importer", msg, p);
                    });

                if (result.Errors.Count > 0)
                {
                    _status = "Errors: " + string.Join("; ", result.Errors);
                }
                else
                {
                    _status = "Imported " + mapInfo.LogName + " in " +
                              result.Duration.TotalSeconds.ToString("F2") + "s. " +
                              "Warnings: " + result.Warnings.Count;
                }
                if (result.Root != null)
                    Selection.activeGameObject = result.Root;
                AssetDatabase.SaveAssets();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _isImporting = false;
            }
        }
    }
}
