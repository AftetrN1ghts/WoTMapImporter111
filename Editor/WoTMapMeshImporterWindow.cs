using UnityEditor;
using UnityEngine;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Xml;

namespace WoTMapImporter
{
    public class WoTMapMeshImporterWindow : EditorWindow
    {
        private string wotPath = @"C:\Games\World_of_Tanks";
        private string outputPath = "Assets/WoT_Maps_Mesh";
        private List<string> availableMaps = new List<string>();
        private int selectedMapIndex = 0;
        private string SelectedMap => (availableMaps.Count > 0 && selectedMapIndex >= 0) ? availableMaps[selectedMapIndex] : "";

        [MenuItem("Tools/WoT Map Importer (Mesh + Full Port)")]
        public static void ShowWindow()
        {
            GetWindow<WoTMapMeshImporterWindow>("WoT Mesh Importer");
        }

        private void OnGUI()
        {
            GUILayout.Label("WoT Map Importer — Mesh Version + Full C# Port", EditorStyles.boldLabel);

            wotPath = EditorGUILayout.TextField("WoT Path", wotPath);
            outputPath = EditorGUILayout.TextField("Output Folder", outputPath);

            if (GUILayout.Button("Parse Maps (from arena_defs/_list_.xml)"))
            {
                ParseMapsList();
            }

            if (availableMaps.Count > 0)
            {
                selectedMapIndex = EditorGUILayout.Popup("Map", selectedMapIndex, availableMaps.ToArray());
            }
            else
            {
                EditorGUILayout.LabelField("No maps loaded. Click Parse Maps first.");
            }

            EditorGUI.BeginDisabledGroup(string.IsNullOrEmpty(SelectedMap));
            if (GUILayout.Button("Import Selected Map as MESH"))
            {
                ImportAsMesh();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.HelpBox(
                "This version uses meshes instead of Unity Terrain for easier exact texture blending.\n" +
                "Full C# port of terrain_loader.py logic included.",
                MessageType.Info);
        }

        private void ParseMapsList()
        {
            availableMaps.Clear();
            selectedMapIndex = 0;

            // Try several possible locations
            string[] possiblePaths = new string[]
            {
                Path.Combine(wotPath, "res", "scripts", "arena_defs", "_list_.xml"),
                Path.Combine(wotPath, "res", "packages", "scripts.pkg"),           // packed version
                Path.Combine(wotPath, "res", "arena_defs", "_list_.xml"),
            };

            string listXmlPath = null;

            foreach (string path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    if (path.EndsWith(".pkg"))
                    {
                        // Try to extract from .pkg (zip)
                        listXmlPath = ExtractListXmlFromPkg(path);
                    }
                    else
                    {
                        listXmlPath = path;
                    }
                    break;
                }
            }

            if (string.IsNullOrEmpty(listXmlPath) || !File.Exists(listXmlPath))
            {
                Debug.LogWarning("_list_.xml not found in standard locations. Using fallback map folder scan...");

                // Fallback: scan maps folder for any .cdata files
                string mapsRoot = Path.Combine(wotPath, "res", "maps");
                if (Directory.Exists(mapsRoot))
                {
                    foreach (var dir in Directory.GetDirectories(mapsRoot))
                    {
                        if (Directory.GetFiles(dir, "*.cdata", SearchOption.AllDirectories).Length > 0)
                            availableMaps.Add(Path.GetFileName(dir));
                    }
                }

                if (availableMaps.Count > 0)
                    Debug.Log($"Fallback found {availableMaps.Count} maps.");
                else
                    Debug.LogError("No maps found. Check your WoT path.");

                return;
            }

            try
            {
                var doc = new XmlDocument();
                doc.Load(listXmlPath);

                foreach (XmlNode node in doc.SelectNodes("//map"))
                {
                    string name = node.InnerText.Trim();
                    if (!string.IsNullOrEmpty(name))
                        availableMaps.Add(name);
                }

                Debug.Log($"Found {availableMaps.Count} maps from _list_.xml");
            }
            catch (System.Exception e)
            {
                Debug.LogError("Failed to parse _list_.xml: " + e.Message);
            }
        }

        private string ExtractListXmlFromPkg(string pkgPath)
        {
            try
            {
                using (var archive = ZipFile.OpenRead(pkgPath))
                {
                    var entry = archive.GetEntry("scripts/arena_defs/_list_.xml");
                    if (entry != null)
                    {
                        string tempPath = Path.Combine(Path.GetTempPath(), "wot_list.xml");
                        using (var stream = entry.Open())
                        using (var fileStream = File.Create(tempPath))
                        {
                            stream.CopyTo(fileStream);
                        }
                        return tempPath;
                    }
                }
            }
            catch { }
            return null;
        }

        private void ImportAsMesh()
        {
            if (string.IsNullOrEmpty(SelectedMap))
            {
                Debug.LogError("No map selected");
                return;
            }

            string mapFolder = Path.Combine(wotPath, "res", "maps", SelectedMap);
            if (!Directory.Exists(mapFolder))
            {
                Debug.LogError("Map folder not found: " + mapFolder);
                return;
            }

            var chunks = new List<WoTTerrainCDataLoader.TerrainChunk>();
            var files = Directory.GetFiles(mapFolder, "*.cdata", SearchOption.AllDirectories);

            int maxChunks = Mathf.Min(files.Length, 9); // limit for testing
            for (int i = 0; i < maxChunks; i++)
            {
                var chunk = WoTTerrainCDataLoader.LoadChunk(files[i], wotPath);
                if (chunk != null)
                {
                    chunk.chunkX = i % 3;
                    chunk.chunkY = i / 3;
                    chunks.Add(chunk);
                }
            }

            GameObject terrainRoot = WoTTerrainMeshGenerator.CreateTerrainMesh(chunks);
            string prefabPath = Path.Combine(outputPath, SelectedMap + "_Mesh.prefab");
            Directory.CreateDirectory(Path.GetDirectoryName(prefabPath));
            PrefabUtility.SaveAsPrefabAsset(terrainRoot, prefabPath);
            DestroyImmediate(terrainRoot);

            Debug.Log($"Imported {chunks.Count} chunks as mesh terrain for map: {SelectedMap}");
        }
    }
}