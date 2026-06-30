using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace WoTMapImporter
{
    /// <summary>
    /// Option B: Mesh-based terrain (easier to control UV + exact blending)
    /// </summary>
    public static class WoTTerrainMeshGenerator
    {
        public static GameObject CreateTerrainMesh(
            List<WoTTerrainCDataLoader.TerrainChunk> chunks,
            float chunkSize = 64f,
            int resolution = 65)
        {
            GameObject root = new GameObject("WoT_Terrain_Mesh");
            root.isStatic = true;

            foreach (var chunk in chunks)
            {
                if (chunk.heights == null) continue;

                int size = chunk.heights.GetLength(0);
                Vector3[] vertices = new Vector3[size * size];
                Vector2[] uvs = new Vector2[size * size];
                int[] triangles = new int[(size - 1) * (size - 1) * 6];

                float scale = chunkSize / (size - 1);

                // Build vertices
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int index = y * size + x;
                        float h = chunk.heights[x, y];
                        vertices[index] = new Vector3(x * scale, h, y * scale);

                        // Simple planar UV (can be improved with layer tileSize)
                        uvs[index] = new Vector2(x / (float)(size - 1), y / (float)(size - 1));
                    }
                }

                // Build triangles
                int t = 0;
                for (int y = 0; y < size - 1; y++)
                {
                    for (int x = 0; x < size - 1; x++)
                    {
                        int i0 = y * size + x;
                        int i1 = i0 + 1;
                        int i2 = i0 + size;
                        int i3 = i2 + 1;

                        triangles[t++] = i0;
                        triangles[t++] = i2;
                        triangles[t++] = i1;
                        triangles[t++] = i1;
                        triangles[t++] = i2;
                        triangles[t++] = i3;
                    }
                }

                Mesh mesh = new Mesh
                {
                    name = $"Chunk_{chunk.chunkX}_{chunk.chunkY}",
                    vertices = vertices,
                    uv = uvs,
                    triangles = triangles
                };
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();

                GameObject chunkGO = new GameObject(mesh.name);
                chunkGO.transform.SetParent(root.transform);
                chunkGO.transform.position = new Vector3(chunk.chunkX * chunkSize, 0, chunk.chunkY * chunkSize);

                MeshFilter mf = chunkGO.AddComponent<MeshFilter>();
                mf.mesh = mesh;

                MeshRenderer mr = chunkGO.AddComponent<MeshRenderer>();
                mr.sharedMaterial = GetDefaultTerrainMaterial();
            }

            return root;
        }

        private static Material GetDefaultTerrainMaterial()
        {
            // You can replace this with your custom multilayer shader
            return new Material(Shader.Find("Standard"));
        }
    }
}