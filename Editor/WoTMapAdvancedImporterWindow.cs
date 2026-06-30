using UnityEditor;
using UnityEngine;
using System.Collections.Generic;
using System.IO;

namespace WoTMapImporter
{
    public class WoTMapAdvancedImporterWindow : EditorWindow
    {
        private string wotPath = @"C:\Games\World_of_Tanks";
        private string outputPath = "Assets/WoT_Imported";
        private List<string> maps = new List<string>();
        private int selectedIndex = 0;
        private bool useMeshInsteadOfTerrain = true;

        [MenuItem("Tools/WoT Map Importer (Advanced)")]
        public static void ShowWindow()
        {
            GetWindow<WoTMapAdvancedImporterWindow>("WoT Advanced Importer");
        }

        private void OnGUI()
        {
            GUILayout.Label("WoT Map Importer — Advanced (Mesh + Terrain)", EditorStyles.boldLabel);

            wotPath = EditorGUILayout.TextField("WoT Installation Path", wotPath);
            outputPath = EditorGUILayout.TextField("Output Folder", outputPath);
            useMeshInsteadOfTerrain = EditorGUILayout.Toggle("Use Mesh instead of Terrain", useMeshInsteadOfTerrain);

            if (GUILayout.Button("Scan Maps"))
            {
                ScanMaps();
            }

            if (maps.Count > 0)
            {
                selectedIndex = EditorGUILayout.Popup("Map", selectedIndex, maps.ToArray());
            }

            EditorGUI.BeginDisabledGroup(maps.Count == 0);
            if (GUILayout.Button("Import Selected Map"))
            {
                ImportSelectedMap();
            }
            EditorGUI.EndDisabledGroup();
        }

        private void ScanMaps()
        {
            maps.Clear();

            // Try multiple possible locations
            string[] possibleMapRoots = new string[]
            {
                Path.Combine(wotPath, "res", "maps"),
                Path.Combine(wotPath, "res", "packages"),
                Path.Combine(wotPath, "content", "maps"),
                wotPath
            };

            string mapsDir = null;

            foreach (string candidate in possibleMapRoots)
            {
                if (Directory.Exists(candidate))
                {
                    // Check if this folder or its subfolders contain .cdata
                    if (Directory.GetFiles(candidate, "*.cdata", SearchOption.AllDirectories).Length > 0 ||
                        Directory.GetDirectories(candidate).Length > 0)
                    {
                        mapsDir = candidate;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(mapsDir) || !Directory.Exists(mapsDir))
            {
                Debug.LogError("Maps folder not found. Please check your WoT path.\n" +
                               "Typical locations: res/maps or res/packages");
                return;
            }

            // Scan for folders containing .cdata files
            foreach (string dir in Directory.GetDirectories(mapsDir, "*", SearchOption.AllDirectories))
            {
                if (Directory.GetFiles(dir, "*.cdata", SearchOption.TopDirectoryOnly).Length > 0 ||
                    Directory.GetFiles(dir, "*.cdata", SearchOption.AllDirectories).Length > 0)
                {
                    string mapName = Path.GetFileName(dir);
                    if (!maps.Contains(mapName))
                        maps.Add(mapName);
                }
            }

            // Also check the root if it has .cdata directly
            if (Directory.GetFiles(mapsDir, "*.cdata", SearchOption.TopDirectoryOnly).Length > 0)
            {
                maps.Add(Path.GetFileName(mapsDir));
            }

            if (maps.Count == 0)
            {
                Debug.LogWarning("No maps with .cdata found. Try a different path or check if the game is fully installed.");
            }
            else
            {
                Debug.Log($"Found {maps.Count} maps with terrain data.");
            }
        }

        private void ImportSelectedMap()
        {
            if (maps.Count == 0) return;

            string mapName = maps[selectedIndex];
            string mapFolder = Path.Combine(wotPath, "res", "maps", mapName);

            var chunks = WoTTerrainCDataDumper.DumpAllChunks(mapFolder);

            GameObject root;

            if (useMeshInsteadOfTerrain)
            {
                root = WoTTerrainMeshImporter.CreateMeshTerrainFromChunks(chunks);
                Debug.Log($"Created MESH terrain with {chunks.Count} chunks");
            }
            else
            {
                // Original Terrain logic (your existing code)
                root = WoTTerrainCDataDumper.CreateTerrainFromChunks(chunks);
                Debug.Log($"Created TERRAIN with {chunks.Count} chunks");
            }

            string prefabPath = Path.Combine(outputPath, mapName + (useMeshInsteadOfTerrain ? "_Mesh" : "_Terrain") + ".prefab");
            Directory.CreateDirectory(Path.GetDirectoryName(prefabPath));
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            DestroyImmediate(root);

            Debug.Log("Import complete: " + prefabPath);
        }
    }
}