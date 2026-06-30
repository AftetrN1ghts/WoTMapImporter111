using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace WoTMapImporter
{
    /// <summary>
    /// Existing dumper logic (simplified version for compatibility)
    /// </summary>
    public static class WoTTerrainCDataDumper
    {
        public class TerrainChunkData
        {
            public int chunkX;
            public int chunkY;
            public float[] heights;
            public Texture2D blendTexture;
            public List<Texture2D> layerTextures = new List<Texture2D>();
        }

        public static List<TerrainChunkData> DumpAllChunks(string mapFolder)
        {
            var result = new List<TerrainChunkData>();
            var files = Directory.GetFiles(mapFolder, "*.cdata", SearchOption.AllDirectories);

            for (int i = 0; i < Mathf.Min(files.Length, 16); i++)
            {
                var chunk = new TerrainChunkData
                {
                    chunkX = i % 4,
                    chunkY = i / 4,
                    heights = new float[65 * 65]
                };

                // Fill with dummy height data for now
                for (int h = 0; h < chunk.heights.Length; h++)
                    chunk.heights[h] = Random.Range(0f, 5f);

                result.Add(chunk);
            }

            return result;
        }

        public static GameObject CreateTerrainFromChunks(List<TerrainChunkData> chunks)
        {
            GameObject go = new GameObject("WoT_Terrain_Original");
            // TODO: implement real Terrain creation here (your old logic)
            return go;
        }

        /// <summary>
        /// New: Import as Mesh (recommended for accurate texture blending)
        /// </summary>
        public static GameObject ImportMapAsMesh(string mapFolder, float chunkSize = 64f)
        {
            var chunks = DumpAllChunks(mapFolder);
            return WoTTerrainMeshImporter.CreateMeshTerrainFromChunks(chunks, chunkSize);
        }
    }
}