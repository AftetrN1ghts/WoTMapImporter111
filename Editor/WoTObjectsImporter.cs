using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;

public class WoTObjectsImporter : EditorWindow
{
    [MenuItem("WoT/Import Objects (from Blender data)")]
    static void Init()
    {
        var window = GetWindow<WoTObjectsImporter>("WoT Objects Importer");
        window.Show();
    }

    private string blenderExportPath = "Assets/WoTMapData/Objects";
    private string outputPrefabPath = "Assets/WoTMapData/Prefabs";

    void OnGUI()
    {
        GUILayout.Label("WoT Objects Importer", EditorStyles.boldLabel);
        blenderExportPath = EditorGUILayout.TextField("Blender Export Folder", blenderExportPath);
        outputPrefabPath = EditorGUILayout.TextField("Output Prefabs", outputPrefabPath);

        if (GUILayout.Button("Import Objects + LODs + Destroyed"))
        {
            ImportAllObjects();
        }
    }

    void ImportAllObjects()
    {
        if (!Directory.Exists(blenderExportPath))
        {
            Debug.LogError("Folder not found: " + blenderExportPath);
            return;
        }

        Directory.CreateDirectory(outputPrefabPath);

        var modelFiles = Directory.GetFiles(blenderExportPath, "*.fbx", SearchOption.AllDirectories);

        foreach (var fbxPath in modelFiles)
        {
            string assetPath = fbxPath.Replace(Application.dataPath, "Assets");
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (model == null) continue;

            string name = Path.GetFileNameWithoutExtension(fbxPath);

            // Создаём префаб с LOD Group
            GameObject go = new GameObject(name);
            LODGroup lodGroup = go.AddComponent<LODGroup>();

            // Ищем все LOD-варианты по имени (lod0, lod1, destroyed и т.д.)
            List<LOD> lods = new List<LOD>();

            // Основной LOD0
            GameObject lod0 = Instantiate(model);
            lod0.name = name + "_LOD0";
            lod0.transform.SetParent(go.transform);
            ApplyCorrectTransform(lod0);

            Renderer[] renderers0 = lod0.GetComponentsInChildren<Renderer>();
            lods.Add(new LOD(0.6f, renderers0));

            // Ищем другие лоды (пример: имя содержит _LOD1)
            string dir = Path.GetDirectoryName(fbxPath);
            string baseName = Path.GetFileNameWithoutExtension(fbxPath);

            for (int i = 1; i <= 3; i++)
            {
                string lodName = baseName + "_LOD" + i;
                string lodPath = Path.Combine(dir, lodName + ".fbx");
                if (File.Exists(lodPath))
                {
                    string lodAssetPath = lodPath.Replace(Application.dataPath, "Assets");
                    GameObject lodModel = AssetDatabase.LoadAssetAtPath<GameObject>(lodAssetPath);
                    if (lodModel != null)
                    {
                        GameObject lodGO = Instantiate(lodModel);
                        lodGO.name = name + "_LOD" + i;
                        lodGO.transform.SetParent(go.transform);
                        ApplyCorrectTransform(lodGO);

                        Renderer[] r = lodGO.GetComponentsInChildren<Renderer>();
                        float screenSize = Mathf.Max(0.05f, 0.6f - i * 0.2f);
                        lods.Add(new LOD(screenSize, r));
                    }
                }
            }

            // Разрушаемая версия (destroyed / broken)
            string destroyedPath = Path.Combine(dir, baseName + "_destroyed.fbx");
            if (File.Exists(destroyedPath))
            {
                string dAsset = destroyedPath.Replace(Application.dataPath, "Assets");
                GameObject dModel = AssetDatabase.LoadAssetAtPath<GameObject>(dAsset);
                if (dModel != null)
                {
                    GameObject dGO = Instantiate(dModel);
                    dGO.name = name + "_Destroyed";
                    dGO.transform.SetParent(go.transform);
                    ApplyCorrectTransform(dGO);
                    dGO.SetActive(false); // по умолчанию выкл

                    // Можно добавить скрипт для переключения на destroyed
                }
            }

            lodGroup.SetLODs(lods.ToArray());
            lodGroup.RecalculateBounds();

            string prefabPath = Path.Combine(outputPrefabPath, name + ".prefab");
            PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
            DestroyImmediate(go);

            Debug.Log("Imported: " + name);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    void ApplyCorrectTransform(GameObject go)
    {
        // Требуемое исправление ориентации
        go.transform.localRotation = Quaternion.Euler(-90, 180, 0);
        go.transform.localScale = new Vector3(-1, 1, 1);
    }
}