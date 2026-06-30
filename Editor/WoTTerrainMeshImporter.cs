using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace WoTMapImporter
{
    /// <summary>
    /// Option B: Mesh-based terrain importer (alternative to Unity Terrain)
    /// Uses the same CData data as the original Terrain importer.
    /// </summary>
    public static class WoTTerrainMeshImporter
    {
        public static GameObject CreateMeshTerrainFromChunks(
            List<WoTTerrainCDataDumper.TerrainChunkData> chunkDataList,
            float chunkWorldSize = 64f)
        {
            GameObject root = new GameObject("WoT_Terrain_Meshes");
            root.isStatic = true;

            foreach (var chunkData in chunkDataList)
            {
                if (chunkData.heights == null || chunkData.heights.Length == 0)
                    continue;

                int res = (int)Mathf.Sqrt(chunkData.heights.Length);
                Mesh mesh = CreateChunkMesh(chunkData, res, chunkWorldSize);

                GameObject chunkGO = new GameObject($"Chunk_{chunkData.chunkX}_{chunkData.chunkY}");
                chunkGO.transform.SetParent(root.transform);
                chunkGO.transform.position = new Vector3(
                    chunkData.chunkX * chunkWorldSize,
                    0,
                    chunkData.chunkY * chunkWorldSize);

                MeshFilter mf = chunkGO.AddComponent<MeshFilter>();
                mf.sharedMesh = mesh;

                MeshRenderer mr = chunkGO.AddComponent<MeshRenderer>();
                mr.sharedMaterial = GetTerrainMaterial(chunkData);

                chunkGO.isStatic = true;
            }

            return root;
        }

        private static Mesh CreateChunkMesh(WoTTerrainCDataDumper.TerrainChunkData data, int resolution, float worldSize)
        {
            int size = resolution;
            Vector3[] verts = new Vector3[size * size];
            Vector2[] uvs = new Vector2[size * size];
            int[] tris = new int[(size - 1) * (size - 1) * 6];

            float scale = worldSize / (size - 1);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = y * size + x;
                    float h = data.heights[i];
                    verts[i] = new Vector3(x * scale, h, y * scale);
                    uvs[i] = new Vector2(x / (float)(size - 1), y / (float)(size - 1));
                }
            }

            int t = 0;
            for (int y = 0; y < size - 1; y++)
            {
                for (int x = 0; x < size - 1; x++)
                {
                    int i0 = y * size + x;
                    int i1 = i0 + 1;
                    int i2 = i0 + size;
                    int i3 = i2 + 1;

                    tris[t++] = i0; tris[t++] = i2; tris[t++] = i1;
                    tris[t++] = i1; tris[t++] = i2; tris[t++] = i3;
                }
            }

            Mesh mesh = new Mesh();
            mesh.name = $"Chunk_{data.chunkX}_{data.chunkY}";
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Material GetTerrainMaterial(WoTTerrainCDataDumper.TerrainChunkData data)
        {
            // You can assign your WoTTerrainMultilayer shader here
            Material mat = new Material(Shader.Find("WoT/TerrainMultilayer"));
            if (mat.shader == null)
                mat.shader = Shader.Find("Standard");

            // TODO: assign blend textures and layer textures from data
            return mat;
        }
    }
}