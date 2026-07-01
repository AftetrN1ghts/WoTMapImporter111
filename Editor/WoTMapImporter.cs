using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEditor;
using UnityEngine;
using WoTMapImporter.Editor.Data;
using WoTMapImporter.Editor.Package;
using WoTMapImporter.Editor.Terrain;
using WoTMapImporter.Editor.Vegetation;
using WoTMapImporter.Editor.Utils;
using WoTMapImporter.Editor.Xml;

namespace WoTMapImporter.Editor
{
    /// <summary>
    /// Orchestrates the full WoT map import flow.
    ///
    /// Architecture (mirrors Simi4's Blender addon):
    ///   1. EXTRACT the relevant .pkg files to a temp directory on disk.
    ///      This is how the original addon reads everything - it never reads
    ///      from ZipFile directly for terrain.
    ///   2. Read space.settings (old) or space.bin (new) from the extracted dir.
    ///   3. Open every *.cdata file in spaces/&lt;map_name&gt;/ (terrain chunks).
    ///   4. Open every *.chunk file (static model placements).
    ///   5. Read textures/models from the extracted dir + any shared*.pkg.
    /// </summary>
    public static class WoTMapImporter
    {
        public enum TerrainImportMode
        {
            UnityTerrain = 0,
            MeshChunks = 1,
        }

        public class ImportSettings
        {
            public bool LoadTerrain = true;
            public bool LoadObjects = true;
            public bool LoadVegetation = true;
            public bool LoadNormals = true;
            public bool LoadWetness = false;
            public int MaxHeightmapResolution = 4097;
            public TerrainImportMode TerrainMode = TerrainImportMode.MeshChunks;
        }

        public class ImportResult
        {
            public GameObject Root;
            public TerrainData TerrainData;
            public List<string> Warnings = new List<string>();
            public List<string> Errors = new List<string>();
            public TimeSpan Duration;
        }

        public static ImportResult ImportMap(
            MapInfo mapInfo,
            string wotResPath,
            string outputFolder,
            ImportSettings settings,
            Action<float, string> progress = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new ImportResult();
            WoTLogger.Info($"=== Importing {mapInfo.Name} ===");

            string geometry = string.IsNullOrEmpty(mapInfo.Geometry)
                ? $"spaces/{mapInfo.Name}" : mapInfo.Geometry;
            string spaceName = geometry.IndexOf('/') >= 0
                ? geometry.Substring(geometry.IndexOf('/') + 1) : geometry;

            progress?.Invoke(0.05f, "Extracting .pkg files...");

            // 1. Extract relevant .pkg files to a temp directory.
            string extractDir = Path.Combine(Path.GetTempPath(), "WoTMapImporter", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(extractDir);
            WoTLogger.Info($"Extract dir: {extractDir}");

            WoTPackageManager pkgMgr = null;
            try
            {
                ExtractMapPackages(wotResPath, spaceName, extractDir, progress);

                // The actual map content lives in <extractDir>/spaces/<map_name>/
                string spaceDir = Path.Combine(extractDir, geometry.Replace('/', Path.DirectorySeparatorChar));
                WoTLogger.Info($"Space dir: {spaceDir} (exists: {Directory.Exists(spaceDir)})");

                if (!Directory.Exists(spaceDir) || Directory.GetFiles(spaceDir, "*", SearchOption.AllDirectories).Length == 0)
                {
                    // In older WoT versions, spaces/<map_name> is unpacked directly in res/spaces/<map_name>
                    string resSpaceDir = Path.Combine(Path.GetDirectoryName(wotResPath), geometry.Replace('/', Path.DirectorySeparatorChar));
                    if (!Directory.Exists(resSpaceDir))
                        resSpaceDir = Path.Combine(wotResPath, geometry.Replace('/', Path.DirectorySeparatorChar));

                    if (Directory.Exists(resSpaceDir))
                    {
                        spaceDir = resSpaceDir;
                        WoTLogger.Info($"Using unpacked space dir from res: {spaceDir}");
                    }
                }

                // Open map packages + shared*.pkg for resource lookups (textures/models).
                // Terrain layer textures can live in the map package itself, so using
                // shared*.pkg only is not enough for the mesh-terrain renderer.
                var resourcePackages = GetSharedPackageNames(wotResPath);
                resourcePackages.Insert(0, "particles.pkg");
                resourcePackages.Insert(0, $"{spaceName}_bin.pkg");
                resourcePackages.Insert(0, $"{spaceName}.pkg");
                AddOptionalResourcePackages(wotResPath, resourcePackages, spaceName);
                pkgMgr = new WoTPackageManager(wotResPath, resourcePackages);

                progress?.Invoke(0.15f, "Loading space settings...");
                UniversalTerrain universalTerrain = LoadTerrainMetadata(spaceDir, pkgMgr, geometry);

                var folder = $"{outputFolder}/{mapInfo.Name}";
                EnsureAssetFolder(folder);

                GameObject terrainObject = null;
                if (settings.LoadTerrain)
                {
                    progress?.Invoke(0.3f, "Loading cdata chunks...");
                    var chunks = LoadAllChunks(spaceDir, universalTerrain);

                    if (chunks.Count == 0)
                    {
                        result.Errors.Add("No terrain chunks found at " + spaceDir);
                        return result;
                    }

                    progress?.Invoke(0.7f, settings.TerrainMode == TerrainImportMode.MeshChunks
                        ? "Building WoT mesh terrain chunks..."
                        : "Building Unity Terrain...");

                    if (settings.TerrainMode == TerrainImportMode.MeshChunks)
                    {
                        var meshResult = TerrainMeshBuilder.Build(folder, mapInfo, universalTerrain, chunks, pkgMgr,
                                                                  settings.LoadWetness);
                        terrainObject = meshResult.TerrainObject;
                        result.Warnings.AddRange(meshResult.Warnings);
                    }
                    else
                    {
                        var buildResult = TerrainBuilder.Build(folder, mapInfo, universalTerrain, chunks, pkgMgr,
                                                               settings.MaxHeightmapResolution);
                        terrainObject = buildResult.TerrainObject;
                        result.TerrainData = buildResult.TerrainData;
                        result.Warnings.AddRange(buildResult.Warnings);
                    }
                }
                else
                {
                    progress?.Invoke(0.7f, "Terrain import disabled...");
                    result.Warnings.Add("Terrain import disabled");
                }

                progress?.Invoke(settings.LoadObjects ? 0.9f : 0.95f, "Creating root object...");
                var root = new GameObject($"WoTMap_{mapInfo.Name}");
                if (terrainObject != null)
                    terrainObject.transform.SetParent(root.transform, false);

                // ---- Static objects and SpeedTree vegetation (from compiled space.bin) ----
                if (settings.LoadObjects || settings.LoadVegetation)
                {
                    try
                    {
                        progress?.Invoke(0.92f, "Loading compiled space content...");
                        var compiledSpace = LoadCompiledSpace(spaceDir, pkgMgr, result);

                        if (settings.LoadObjects && compiledSpace != null)
                        {
                            progress?.Invoke(0.94f, "Loading static objects...");
                            LoadStaticObjects(compiledSpace, $"{outputFolder}/{mapInfo.Name}", pkgMgr, root, result);
                        }

                        if (settings.LoadVegetation && compiledSpace != null)
                        {
                            progress?.Invoke(0.97f, "Loading SpeedTree vegetation...");
                            LoadVegetation(compiledSpace, $"{outputFolder}/{mapInfo.Name}", pkgMgr, root, result);
                        }
                    }
                    catch (Exception oe)
                    {
                        WoTLogger.Warn($"Compiled-space content loading failed: {oe.Message}\n{oe.StackTrace}");
                        result.Warnings.Add("Compiled-space content loading failed: " + oe.Message);
                    }
                }

                result.Root = root;
                string prefabPath = $"{folder}/WoTMap_{mapInfo.Name}.prefab";
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);

                progress?.Invoke(1.0f, "Done.");
            }
            catch (Exception e)
            {
                WoTLogger.Error($"Import failed: {e.Message}\n{e.StackTrace}");
                result.Errors.Add(e.Message);
            }
            finally
            {
                pkgMgr?.Dispose();
                // Clean up extract dir asynchronously
                try { Directory.Delete(extractDir, recursive: true); }
                catch (Exception ex) { WoTLogger.Warn($"Could not clean up extract dir: {ex.Message}"); }
            }

            sw.Stop();
            result.Duration = sw.Elapsed;
            WoTLogger.Info($"=== Import finished in {sw.Elapsed.TotalSeconds:F2}s ===");
            return result;
        }

        // =================== EXTRACTION ===================

        private static void ExtractMapPackages(
            string wotResPath, string spaceName, string extractDir,
            Action<float, string> progress)
        {
            // Extract the map's main package + _bin package + particles package.
            // This matches Simi4's loader.extract_space_pkg().
            string[] packagesToExtract = {
                $"{spaceName}.pkg",
                $"{spaceName}_bin.pkg",
                "particles.pkg",
            };

            int extracted = 0;
            foreach (var pkgName in packagesToExtract)
            {
                string pkgPath = Path.Combine(wotResPath, pkgName);
                if (File.Exists(pkgPath))
                {
                    ExtractZip(pkgPath, extractDir);
                    WoTLogger.Info($"Extracted {pkgName}");
                }
                else
                {
                    WoTLogger.Warn($"pkg not found (skipping): {pkgName}");
                }
                extracted++;
                progress?.Invoke(0.05f + 0.05f * extracted, $"Extracted {extracted}/{packagesToExtract.Length}...");
            }
        }

        private static List<string> GetSharedPackageNames(string wotResPath)
        {
            var list = new List<string>();
            string pkgsDir = wotResPath;
            if (!Directory.Exists(pkgsDir)) return list;
            foreach (var f in Directory.GetFiles(pkgsDir, "shared*.pkg"))
            {
                string fname = Path.GetFileName(f);
                if (fname.Contains("_hd-")) continue;
                list.Add(fname);
            }
            return list;
        }

        private static void AddOptionalResourcePackages(string wotResPath, List<string> packages, string spaceName)
        {
            if (!Directory.Exists(wotResPath) || packages == null) return;
            var seen = new HashSet<string>(packages, StringComparer.OrdinalIgnoreCase);

            void AddIfExists(string pkgName)
            {
                if (string.IsNullOrEmpty(pkgName) || seen.Contains(pkgName)) return;
                if (!File.Exists(Path.Combine(wotResPath, pkgName))) return;
                packages.Add(pkgName);
                seen.Add(pkgName);
            }

            // The map's own package(s) can hold the map-specific SpeedTree/flora
            // resources (older maps sometimes split them across <map>_*.pkg), so pull
            // in every package named after the map, not just <map>.pkg / <map>_bin.pkg.
            if (!string.IsNullOrEmpty(spaceName))
            {
                foreach (var f in Directory.GetFiles(wotResPath, $"{spaceName}*.pkg"))
                    AddIfExists(Path.GetFileName(f));
            }

            // SpeedTree resources are normally in shared*.pkg, but some clients/modded
            // installs use separate content/vegetation packages.  Add them if present
            // without forcing the importer to open every game package.
            AddIfExists("content.pkg");
            AddIfExists("misc.pkg");
            AddIfExists("vegetation.pkg");
            AddIfExists("speedtree.pkg");

            foreach (var f in Directory.GetFiles(wotResPath, "vegetation*.pkg"))
                AddIfExists(Path.GetFileName(f));
            foreach (var f in Directory.GetFiles(wotResPath, "speedtree*.pkg"))
                AddIfExists(Path.GetFileName(f));
        }

        private static void ExtractZip(string zipPath, string destDir)
        {
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    string outPath = Path.Combine(destDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                    try
                    {
                        using var fs = entry.Open();
                        using var outFs = File.Create(outPath);
                        fs.CopyTo(outFs);
                    }
                    catch (NotSupportedException)
                    {
                        ExtractEntryViaSharpZipLib(zipPath, entry.FullName, outPath);
                    }
                }
            }
            catch (Exception e)
            {
                WoTLogger.Warn($"Error extracting {zipPath}: {e.Message}");
            }
        }

        private static void ExtractEntryViaSharpZipLib(string zipPath, string entryName, string outPath)
        {
            try
            {
                System.Type zipFileType = null;
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    zipFileType = asm.GetType("ICSharpCode.SharpZipLib.Zip.ZipFile");
                    if (zipFileType != null) break;
                }
                if (zipFileType == null) return;

                var zipObj = System.Activator.CreateInstance(zipFileType, new object[] { zipPath });
                var getEntryMethod = zipFileType.GetMethod("GetEntry", new System.Type[] { typeof(string) });
                var entryObj = getEntryMethod.Invoke(zipObj, new object[] { entryName });

                if (entryObj != null)
                {
                    var getInputStreamMethod = zipFileType.GetMethod("GetInputStream", new System.Type[] { entryObj.GetType() });
                    var stream = (Stream)getInputStreamMethod.Invoke(zipObj, new object[] { entryObj });
                    using var outFs = File.Create(outPath);
                    stream.CopyTo(outFs);
                }

                if (zipObj is IDisposable disp) disp.Dispose();
            }
            catch (Exception ex)
            {
                WoTLogger.Warn($"Failed SharpZipLib fallback for {entryName}: {ex.Message}");
            }
        }

        // =================== TERRAIN METADATA ===================

        private static UniversalTerrain LoadTerrainMetadata(
            string spaceDir, WoTPackageManager pkgMgr, string geometry)
        {
            var ut = new UniversalTerrain { ChunkSize = 100f };

            // Old format: space.settings XML
            string settingsPath = Path.Combine(spaceDir, "space.settings");
            if (File.Exists(settingsPath))
            {
                try
                {
                    var doc = XmlUnpacker.ReadBytes(File.ReadAllBytes(settingsPath));
                    ut.MinX = int.Parse(doc.SelectSingleNode("/root/bounds/minX").InnerText.Trim());
                    ut.MaxX = int.Parse(doc.SelectSingleNode("/root/bounds/maxX").InnerText.Trim());
                    ut.MinY = int.Parse(doc.SelectSingleNode("/root/bounds/minY").InnerText.Trim());
                    ut.MaxY = int.Parse(doc.SelectSingleNode("/root/bounds/maxY").InnerText.Trim());
                    WoTLogger.Info($"space.settings bounds: x[{ut.MinX}..{ut.MaxX}] y[{ut.MinY}..{ut.MaxY}]");
                    return ut;
                }
                catch (Exception e)
                {
                    WoTLogger.Warn($"Could not parse space.settings: {e.Message}");
                }
            }

            // New format: space.bin (compiled space).  We only need BWT2.settings
            // here: chunk_size, bounds, normal_map_fnv, global_map_fnv, noise_fnv.
            string spaceBinPath = Path.Combine(spaceDir, "space.bin");
            if (File.Exists(spaceBinPath))
            {
                try
                {
                    if (TryReadCompiledTerrainMetadata(File.ReadAllBytes(spaceBinPath), ut))
                    {
                        WoTLogger.Info($"space.bin BWT2 terrain: chunkSize={ut.ChunkSize} bounds x[{ut.MinX}..{ut.MaxX}] y[{ut.MinY}..{ut.MaxY}] globalMap='{ut.GlobalMap}'");
                        return ut;
                    }
                }
                catch (Exception e)
                {
                    WoTLogger.Warn($"Could not parse BWT2 terrain metadata: {e.Message}");
                }
            }

            // Fallback: derive from cdata files later; keep zero bounds for now.
            ut.MinX = ut.MinY = 0;
            ut.MaxX = ut.MaxY = 0;
            return ut;
        }

        private struct SpaceRow
        {
            public string Header;
            public uint Position;
            public uint Length;
        }

        private static bool TryReadCompiledTerrainMetadata(byte[] bin, UniversalTerrain ut)
        {
            using var ms = new MemoryStream(bin, false);
            using var br = new BinaryReader(ms);

            var rows = ReadSpaceRows(br);
            if (!rows.TryGetValue("BWT2", out var bwt2))
                return false;

            var strings = rows.TryGetValue("BWST", out var bwst)
                ? ReadSpaceStringTable(br, bwst)
                : new Dictionary<uint, string>();

            br.BaseStream.Position = bwt2.Position;
            uint settingsSize = br.ReadUInt32();
            if (settingsSize < 32)
                return false;

            ut.ChunkSize = br.ReadSingle();
            ut.MinX = br.ReadInt32();
            ut.MaxX = br.ReadInt32();
            ut.MinY = br.ReadInt32();
            ut.MaxY = br.ReadInt32();
            uint normalMapFnv = br.ReadUInt32();
            uint globalMapFnv = br.ReadUInt32();
            uint noiseFnv = br.ReadUInt32();

            if (strings.TryGetValue(globalMapFnv, out var globalMap))
                ut.GlobalMap = globalMap.ToLowerInvariant();
            else if (globalMapFnv != 0)
                WoTLogger.Warn($"BWT2 global_map_fnv 0x{globalMapFnv:X8} was not found in BWST");

            return ut.ChunkSize > 0.01f;
        }

        private static Dictionary<string, SpaceRow> ReadSpaceRows(BinaryReader br)
        {
            br.BaseStream.Position = 0;
            string rootHeader = ReadSpaceHeader(br);
            if (rootHeader != "BWTB") throw new Exception($"Not a compiled space, root={rootHeader}");
            br.ReadUInt32();
            br.ReadUInt32();
            br.ReadUInt32();
            br.ReadUInt32();
            uint rowsNum = br.ReadUInt32();

            var rows = new Dictionary<string, SpaceRow>();
            for (uint i = 0; i < rowsNum; i++)
            {
                string h = ReadSpaceHeader(br);
                br.ReadUInt32();
                uint pos = br.ReadUInt32();
                br.ReadUInt32();
                uint len = br.ReadUInt32();
                br.ReadUInt32();
                rows[h] = new SpaceRow { Header = h, Position = pos, Length = len };
            }
            return rows;
        }

        private static string ReadSpaceHeader(BinaryReader br)
        {
            return System.Text.Encoding.ASCII.GetString(br.ReadBytes(4));
        }

        private static Dictionary<uint, string> ReadSpaceStringTable(BinaryReader br, SpaceRow row)
        {
            var result = new Dictionary<uint, string>();
            if (row.Length == 0) return result;
            br.BaseStream.Position = row.Position;

            uint elemSize = br.ReadUInt32();
            uint count = br.ReadUInt32();
            var entries = new (uint hash, uint offset, uint length)[count];
            for (uint i = 0; i < count; i++)
                entries[i] = (br.ReadUInt32(), br.ReadUInt32(), br.ReadUInt32());

            uint stringsSize = br.ReadUInt32();
            long stringsStart = br.BaseStream.Position;
            foreach (var e in entries)
            {
                br.BaseStream.Position = stringsStart + e.offset;
                var bytes = br.ReadBytes((int)e.length);
                result[e.hash] = System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes);
            }
            return result;
        }

        // =================== CHUNK LOADING ===================

        private static List<TerrainChunk> LoadAllChunks(
            string spaceDir, UniversalTerrain ut)
        {
            var chunks = new List<TerrainChunk>();

            if (!Directory.Exists(spaceDir))
            {
                WoTLogger.Error($"spaceDir does not exist: {spaceDir}");
                return chunks;
            }

            // Diagnostic: dump the contents of spaceDir so we know what's there.
            WoTLogger.Info($"=== Contents of {spaceDir} (first 40 files) ===");
            try
            {
                var allFiles = Directory.GetFiles(spaceDir, "*", SearchOption.AllDirectories);
                WoTLogger.Info($"Total files: {allFiles.Length}");
                for (int i = 0; i < Math.Min(40, allFiles.Length); i++)
                {
                    string rel = allFiles[i].Substring(spaceDir.Length + 1).Replace('\\', '/');
                    long size = new FileInfo(allFiles[i]).Length;
                    WoTLogger.Info($"  [{size,8} B] {rel}");
                }
                if (allFiles.Length > 40)
                    WoTLogger.Info($"  ... and {allFiles.Length - 40} more");

                // Summary of file extensions present - this is the single most useful
                // line for diagnosing "No terrain chunks found".
                var extCounts = allFiles
                    .GroupBy(f =>
                    {
                        string n = Path.GetFileName(f);
                        int firstDot = n.IndexOf('.');
                        return firstDot >= 0 ? n.Substring(firstDot).ToLowerInvariant() : "(no ext)";
                    })
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Key} x{g.Count()}");
                WoTLogger.Info($"Extension histogram: {string.Join(", ", extCounts)}");
            }
            catch (Exception e)
            {
                WoTLogger.Warn($"Could not list spaceDir contents: {e.Message}");
            }

            // Find all cdata files in the extracted space directory.
            // Files can be:
            //   - XXXXXXXX.cdata          (old format, unprocessed)
            //   - XXXXXXXXo.cdata_processed (new format, processed/optimized)
            //   - XXXXXXXX.cdata_processed (also seen)
            // Simi4 uses glob('*.cdata') which catches all three.
            var cdataPaths = Directory.GetFiles(spaceDir, "*.cdata*");
            // Filter to actual cdata files (not just any *.cdata* prefix)
            cdataPaths = cdataPaths.Where(p => {
                string n = Path.GetFileName(p).ToLowerInvariant();
                return n.EndsWith(".cdata") || n.EndsWith(".cdata_processed");
            }).ToArray();
            WoTLogger.Info($"Found {cdataPaths.Length} .cdata/.cdata_processed files in {spaceDir}");
            if (cdataPaths.Length == 0)
            {
                cdataPaths = Directory.GetFiles(spaceDir, "*.cdata*", SearchOption.AllDirectories);
                cdataPaths = cdataPaths.Where(p => {
                    string n = Path.GetFileName(p).ToLowerInvariant();
                    return n.EndsWith(".cdata") || n.EndsWith(".cdata_processed");
                }).ToArray();
                WoTLogger.Info($"Recursive search: {cdataPaths.Length} .cdata files");

                // Last resort: find any file matching XXXXYYY pattern (with optional
                // 'o' suffix and optional extension).
                if (cdataPaths.Length == 0)
                {
                    WoTLogger.Info("Falling back to hex-name pattern search...");
                    var allFiles = Directory.GetFiles(spaceDir, "*", SearchOption.AllDirectories);
                    var hexFiles = new List<string>();
                    foreach (var f in allFiles)
                    {
                        string baseN = Path.GetFileNameWithoutExtension(f);
                        // Strip optional trailing 'o' (e.g. "00000000o" -> "00000000")
                        if (baseN.EndsWith("o")) baseN = baseN.Substring(0, baseN.Length - 1);
                        if (baseN.Length != 8) continue;
                        bool isHex = true;
                        for (int i = 0; i < 8; i++)
                        {
                            char c = baseN[i];
                            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                            { isHex = false; break; }
                        }
                        if (isHex) hexFiles.Add(f);
                    }
                    cdataPaths = hexFiles.ToArray();
                    WoTLogger.Info($"Hex-name search: {cdataPaths.Length} files matching XXXXYYYY[o] pattern");
                }
            }

            // Sort for deterministic processing
            Array.Sort(cdataPaths, StringComparer.OrdinalIgnoreCase);

            // Discover bounds from chunk names if not set
            if (ut.MinX == ut.MaxX && ut.MinY == ut.MaxY && cdataPaths.Length > 0)
            {
                int minX = int.MaxValue, maxX = int.MinValue;
                int minY = int.MaxValue, maxY = int.MinValue;
                foreach (var path in cdataPaths)
                {
                    ParseChunkName(Path.GetFileName(path), out int hexX, out int hexY);
                    if (hexX < minX) minX = hexX;
                    if (hexX > maxX) maxX = hexX;
                    if (hexY < minY) minY = hexY;
                    if (hexY > maxY) maxY = hexY;
                }
                ut.MinX = minX; ut.MaxX = maxX; ut.MinY = minY; ut.MaxY = maxY;
                WoTLogger.Info($"Discovered terrain bounds from cdata: x[{minX}..{maxX}] y[{minY}..{maxY}]");
            }

            int skippedTooSmall = 0, skippedNull = 0, skippedException = 0;
            foreach (var path in cdataPaths)
            {
                string baseName = Path.GetFileName(path);
                ParseChunkName(baseName, out int hexX, out int hexY);
                Vector2 chunkPos = new Vector2(hexX * ut.ChunkSize, hexY * ut.ChunkSize);

                try
                {
                    byte[] data = File.ReadAllBytes(path);
                    if (data.Length < 4) { skippedTooSmall++; continue; }

                    var chunk = TerrainChunkDecoder.Decode(data, baseName, chunkPos);
                    if (chunk != null)
                        chunks.Add(chunk);
                    else
                        skippedNull++;
                }
                catch (Exception e)
                {
                    skippedException++;
                    WoTLogger.Warn($"Failed to decode chunk {baseName}: {e.Message}");
                }
            }
            WoTLogger.Info($"Loaded {chunks.Count}/{cdataPaths.Length} terrain chunks " +
                           $"(skipped: {skippedNull} null, {skippedException} errors, {skippedTooSmall} too small)");
            if (chunks.Count == 0 && cdataPaths.Length > 0)
            {
                WoTLogger.Error(
                    "Found .cdata files but decoded 0 chunks. The most common reason on modern " +
                    "clients is that the chunks are NOT plain ZIP archives (processed/packed format). " +
                    "Check the 'Decoding chunk ... isZip=' lines above.");
            }
            AlignChunkLayers(chunks);
            return chunks;
        }

        private static void AlignChunkLayers(List<TerrainChunk> chunks)
        {
            if (chunks == null || chunks.Count == 0) return;

            // 1. Collect all unique layers across all chunks in a consistent order.
            var globalLayers = new List<TerrainLayerDef>();
            var globalLayerNames = new List<string>();

            foreach (var chunk in chunks)
            {
                if (chunk == null || chunk.Layers == null) continue;
                foreach (var layer in chunk.Layers)
                {
                    if (layer == null || string.IsNullOrEmpty(layer.Name)) continue;
                    string nameKey = layer.Name.ToLowerInvariant();
                    if (!globalLayerNames.Contains(nameKey))
                    {
                        globalLayerNames.Add(nameKey);
                        globalLayers.Add(layer);
                    }
                }
            }

            WoTLogger.Info($"Aligned global terrain layers ({globalLayers.Count}): {string.Join(", ", globalLayerNames)}");

            // 2. Rebuild each chunk's Layers list to match the global list exactly.
            foreach (var chunk in chunks)
            {
                if (chunk == null || chunk.Layers == null) continue;

                var oldLayers = chunk.Layers;
                var newLayers = new List<TerrainLayerDef>();
                var globalToLocal = new List<int>();

                for (int i = 0; i < globalLayers.Count; i++)
                {
                    string targetName = globalLayerNames[i];
                    int localIdx = -1;
                    for (int j = 0; j < oldLayers.Count; j++)
                    {
                        if (oldLayers[j] != null && !string.IsNullOrEmpty(oldLayers[j].Name) &&
                            oldLayers[j].Name.ToLowerInvariant() == targetName)
                        {
                            localIdx = j;
                            break;
                        }
                    }

                    if (localIdx != -1)
                    {
                        newLayers.Add(oldLayers[localIdx]);
                        globalToLocal.Add(localIdx);
                    }
                    else
                    {
                        newLayers.Add(globalLayers[i]);
                        globalToLocal.Add(-1);
                    }
                }

                chunk.Layers = newLayers;
                chunk.GlobalToLocalLayerIndices = globalToLocal;
            }
        }

        // =================== COMPILED SPACE CONTENT ===================

        private static CompiledSpace LoadCompiledSpace(string spaceDir, WoTPackageManager pkgMgr, ImportResult result)
        {
            string spaceBinPath = Path.Combine(spaceDir, "space.bin");
            if (File.Exists(spaceBinPath))
            {
                byte[] bin = File.ReadAllBytes(spaceBinPath);
                return Package.CompiledSpace.Parse(bin);
            }

            WoTLogger.Info("space.bin not found; parsing old-format BigWorld .chunk XML files...");
            var cs = new Package.CompiledSpace();
            if (!Directory.Exists(spaceDir))
            {
                WoTLogger.Warn($"Space directory not found: {spaceDir}");
                return cs;
            }

            var chunkFiles = Directory.GetFiles(spaceDir, "*.chunk", SearchOption.AllDirectories);
            WoTLogger.Info($"Found {chunkFiles.Length} .chunk files in {spaceDir}");

            int modelInstanceIndex = 0;
            int sptrInstanceIndex = 0;
            var uniqueModels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var chunkPath in chunkFiles)
            {
                try
                {
                    byte[] data = File.ReadAllBytes(chunkPath);
                    if (data.Length == 0) continue;

                    System.Xml.XmlDocument doc = null;
                    try
                    {
                        if (data.Length >= 4 && BitConverter.ToUInt32(data, 0) == XmlUnpacker.PACKED_HEADER)
                        {
                            doc = XmlUnpacker.ReadBytes(data);
                        }
                        else
                        {
                            doc = new System.Xml.XmlDocument();
                            using var ms = new MemoryStream(data);
                            doc.Load(ms);
                        }
                    }
                    catch (Exception ex)
                    {
                        WoTLogger.Warn($"Failed to unpack XML in chunk {Path.GetFileName(chunkPath)}: {ex.Message}");
                        continue;
                    }

                    if (doc == null) continue;

                    ParseChunkName(Path.GetFileName(chunkPath), out int hexX, out int hexY);
                    float chunkTx = hexX * 100f;
                    float chunkTy = 0f;
                    float chunkTz = hexY * 100f;

                    if (chunkTx == 0f && chunkTz == 0f && doc.DocumentElement != null)
                    {
                        var cTransNode = doc.DocumentElement.SelectSingleNode("transform");
                        if (cTransNode != null)
                        {
                            float[] cTrans = ParseTransformNode(cTransNode);
                            if (cTrans[12] != 0f || cTrans[14] != 0f)
                            {
                                chunkTx = cTrans[12];
                                chunkTy = cTrans[13];
                                chunkTz = cTrans[14];
                            }
                        }
                    }

                    var modelNodes = doc.SelectNodes("//model");
                    if (modelNodes != null)
                    {
                        foreach (System.Xml.XmlNode mNode in modelNodes)
                        {
                            string res = mNode.SelectSingleNode("resource")?.InnerText?.Trim();
                            if (string.IsNullOrEmpty(res)) continue;

                            string prims = res;
                            if (prims.EndsWith(".model", StringComparison.OrdinalIgnoreCase))
                                prims = prims.Substring(0, prims.Length - 6) + ".primitives";
                            else if (prims.EndsWith(".visual", StringComparison.OrdinalIgnoreCase))
                                prims = prims.Substring(0, prims.Length - 7) + ".primitives";
                            else if (!prims.EndsWith(".primitives", StringComparison.OrdinalIgnoreCase))
                                prims = prims + ".primitives";

                            float[] transform = ParseTransformNode(mNode.SelectSingleNode("transform"));
                            transform[12] += chunkTx;
                            transform[13] += chunkTy;
                            transform[14] += chunkTz;

                            if (!uniqueModels.TryGetValue(prims, out int modelId))
                            {
                                modelId = uniqueModels.Count;
                                uniqueModels[prims] = modelId;
                            }

                            var placement = new Package.CompiledSpace.ModelPlacement
                            {
                                InstanceIndex = modelInstanceIndex++,
                                ModelId = modelId,
                                Transform = transform,
                            };

                            var lod = new Package.CompiledSpace.ModelLod { LodIndex = 0, Distance = 0f };
                            bool parsedVisual = false;

                            try
                            {
                                string vPath = res;
                                if (vPath.EndsWith(".model", StringComparison.OrdinalIgnoreCase))
                                    vPath = vPath.Substring(0, vPath.Length - 6) + ".visual";
                                else if (vPath.EndsWith(".primitives", StringComparison.OrdinalIgnoreCase))
                                    vPath = vPath.Substring(0, vPath.Length - 11) + ".visual";
                                else if (!vPath.EndsWith(".visual", StringComparison.OrdinalIgnoreCase))
                                    vPath = vPath + ".visual";

                                byte[] vData = pkgMgr.ReadBytes(vPath);
                                if (vData != null)
                                {
                                    System.Xml.XmlDocument vDoc = null;
                                    if (vData.Length >= 4 && BitConverter.ToUInt32(vData, 0) == XmlUnpacker.PACKED_HEADER)
                                        vDoc = XmlUnpacker.ReadBytes(vData);
                                    else
                                    {
                                        vDoc = new System.Xml.XmlDocument();
                                        using var vms = new MemoryStream(vData);
                                        vDoc.Load(vms);
                                    }

                                    if (vDoc != null)
                                    {
                                        var rsetNodes = vDoc.SelectNodes("//renderSet");
                                        if (rsetNodes != null && rsetNodes.Count > 0)
                                        {
                                            int rsetId = 0;
                                            int matIndex = 0;
                                            foreach (System.Xml.XmlNode rsetNode in rsetNodes)
                                            {
                                                string vertsData = rsetNode.SelectSingleNode("geometry/vertices")?.InnerText?.Trim() ?? "";
                                                string primsData = rsetNode.SelectSingleNode("geometry/primitive")?.InnerText?.Trim() ?? "";
                                                if (vertsData.Contains(".primitives"))
                                                {
                                                    string[] vSplit = vertsData.Split('/');
                                                    vertsData = vSplit[vSplit.Length - 1];
                                                }
                                                if (primsData.Contains(".primitives"))
                                                {
                                                    string[] pSplit = primsData.Split('/');
                                                    primsData = pSplit[pSplit.Length - 1];
                                                }

                                                var pgNodes = rsetNode.SelectNodes("geometry/primitiveGroup");
                                                if (pgNodes != null && pgNodes.Count > 0)
                                                {
                                                    for (int pgIdx = 0; pgIdx < pgNodes.Count; pgIdx++)
                                                    {
                                                        System.Xml.XmlNode pgNode = pgNodes[pgIdx];
                                                        int primGroupIndex = pgIdx;
                                                        string firstText = pgNode.ChildNodes.Count > 0 && pgNode.ChildNodes[0].NodeType == System.Xml.XmlNodeType.Text ? pgNode.ChildNodes[0].InnerText?.Trim() : "";
                                                        if (!string.IsNullOrEmpty(firstText) && int.TryParse(firstText, out int parsedPg))
                                                            primGroupIndex = parsedPg;

                                                        var rMesh = new Package.CompiledSpace.RenderMesh
                                                        {
                                                            RenderSetId = rsetId,
                                                            PrimitiveGroup = primGroupIndex,
                                                            MaterialIndex = matIndex++,
                                                            PrimsName = prims,
                                                            VertsDataName = vertsData,
                                                            PrimsDataName = primsData,
                                                        };

                                                        var matNode = pgNode.SelectSingleNode("material");
                                                        if (matNode != null)
                                                        {
                                                            string id = matNode.SelectSingleNode("identifier")?.InnerText?.Trim();
                                                            if (!string.IsNullOrEmpty(id)) rMesh.Identifier = id;

                                                            string fx = matNode.SelectSingleNode("fx")?.InnerText?.Trim();
                                                            if (!string.IsNullOrEmpty(fx)) rMesh.FxName = fx;

                                                            var propNodes = matNode.SelectNodes("property");
                                                            if (propNodes != null)
                                                            {
                                                                foreach (System.Xml.XmlNode pNode in propNodes)
                                                                {
                                                                    string pName = pNode.Attributes?["name"]?.Value?.Trim();
                                                                    if (string.IsNullOrEmpty(pName) && pNode.ChildNodes.Count > 0 && pNode.ChildNodes[0].NodeType == System.Xml.XmlNodeType.Text)
                                                                        pName = pNode.ChildNodes[0].InnerText?.Trim();
                                                                    if (string.IsNullOrEmpty(pName)) pName = pNode.InnerText?.Trim() ?? "";
                                                                    if (string.IsNullOrEmpty(pName)) continue;

                                                                    string pTex = pNode.SelectSingleNode("Texture")?.InnerText?.Trim();
                                                                    if (!string.IsNullOrEmpty(pTex))
                                                                    {
                                                                        rMesh.Props[pName] = new Package.CompiledSpace.MaterialProperty { Name = pName, ValueType = 6, StringValue = pTex };
                                                                    }
                                                                    else
                                                                    {
                                                                        string pVec = pNode.SelectSingleNode("Vector4")?.InnerText?.Trim() ?? pNode.SelectSingleNode("Vector3")?.InnerText?.Trim();
                                                                        if (!string.IsNullOrEmpty(pVec))
                                                                        {
                                                                            string[] parts = pVec.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                                                                            float[] vec = new float[parts.Length];
                                                                            for (int i = 0; i < parts.Length; i++)
                                                                            {
                                                                                if (float.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float val))
                                                                                    vec[i] = val;
                                                                            }
                                                                            rMesh.Props[pName] = new Package.CompiledSpace.MaterialProperty { Name = pName, ValueType = 2, VectorValue = vec };
                                                                        }
                                                                        else
                                                                        {
                                                                            string pVal = pNode.SelectSingleNode("Bool")?.InnerText?.Trim() ?? pNode.SelectSingleNode("Int")?.InnerText?.Trim() ?? pNode.SelectSingleNode("Float")?.InnerText?.Trim();
                                                                            if (string.IsNullOrEmpty(pVal)) pVal = pNode.InnerText?.Trim() ?? "";
                                                                            if (!string.IsNullOrEmpty(pVal)) rMesh.Props[pName] = new Package.CompiledSpace.MaterialProperty { Name = pName, ValueType = 1, StringValue = pVal };
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                        }
                                                        lod.Meshes.Add(rMesh);
                                                        parsedVisual = true;
                                                    }
                                                }
                                                rsetId++;
                                            }
                                        }
                                    }
                                }
                            }
                            catch { /* ignore visual parse errors */ }

                            if (!parsedVisual)
                            {
                                var rMesh = new Package.CompiledSpace.RenderMesh
                                {
                                    RenderSetId = 0,
                                    PrimitiveGroup = -1,
                                    MaterialIndex = 0,
                                    PrimsName = prims,
                                    VertsDataName = "",
                                    PrimsDataName = "",
                                };
                                lod.Meshes.Add(rMesh);
                            }

                            placement.Lods.Add(lod);

                            cs.Placements.Add(placement);
                        }
                    }

                    var sptrNodes = doc.SelectNodes("//speedtree | //speedTree");
                    if (sptrNodes != null)
                    {
                        foreach (System.Xml.XmlNode sNode in sptrNodes)
                        {
                            string spt = sNode.SelectSingleNode("spt")?.InnerText?.Trim();
                            if (string.IsNullOrEmpty(spt)) continue;

                            uint seed = 0;
                            uint.TryParse(sNode.SelectSingleNode("seed")?.InnerText?.Trim() ?? "0", out seed);

                            float[] transform = ParseTransformNode(sNode.SelectSingleNode("transform"));
                            transform[12] += chunkTx;
                            transform[13] += chunkTy;
                            transform[14] += chunkTz;

                            cs.SpeedTrees.Add(new Package.CompiledSpace.SpeedTreePlacement
                            {
                                InstanceIndex = sptrInstanceIndex++,
                                ResourceName = spt,
                                ResourceFnv = Package.CompiledSpace.Fnv1a32(spt),
                                Seed = seed,
                                CastsShadow = true,
                                CastsLocalShadow = true,
                                AlwaysDynamic = false,
                                VisibilityMask = 1u,
                                Transform = transform,
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    WoTLogger.Warn($"Error processing chunk {Path.GetFileName(chunkPath)}: {ex.Message}");
                }
            }

            WoTLogger.Info($"Old-format chunk parsing finished: {cs.Placements.Count} static models, {cs.SpeedTrees.Count} SpeedTrees");
            return cs;
        }

        private static float[] ParseTransformNode(System.Xml.XmlNode tNode)
        {
            float[] m = new float[16];
            m[0] = 1f; m[5] = 1f; m[10] = 1f; m[15] = 1f; // identity default
            if (tNode == null) return m;

            ParseRowNode(tNode.SelectSingleNode("row0"), m, 0);
            ParseRowNode(tNode.SelectSingleNode("row1"), m, 4);
            ParseRowNode(tNode.SelectSingleNode("row2"), m, 8);
            ParseRowNode(tNode.SelectSingleNode("row3"), m, 12);
            return m;
        }

        private static void ParseRowNode(System.Xml.XmlNode rowNode, float[] m, int offset)
        {
            if (rowNode == null) return;
            string text = rowNode.InnerText?.Trim();
            if (string.IsNullOrEmpty(text)) return;
            string[] parts = text.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length && i < 4; i++)
            {
                if (float.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float val))
                {
                    m[offset + i] = val;
                }
            }
        }

        private static void LoadStaticObjects(
            CompiledSpace space, string outputPath, WoTPackageManager pkgMgr,
            GameObject root, ImportResult result)
        {
            if (space.Placements.Count == 0)
            {
                WoTLogger.Warn("CompiledSpace produced 0 visible model placements");
                return;
            }

            var objectBuild = Mesh.ObjectBuilder.Build(outputPath, space, pkgMgr);
            if (objectBuild.Root != null)
            {
                objectBuild.Root.transform.SetParent(root.transform, false);
                ApplyRequestedObjectsRootTransform(objectBuild.Root.transform);
            }

            foreach (var w in objectBuild.Warnings)
            {
                WoTLogger.Info(w);
                result.Warnings.Add(w);
            }

            WoTLogger.Info($"Static objects: {space.Placements.Count} placements, {space.Models.Count} LOD0 render instances (legacy count)");
        }

        private static void LoadVegetation(
            CompiledSpace space, string outputPath, WoTPackageManager pkgMgr,
            GameObject root, ImportResult result)
        {
            if (space.SpeedTrees.Count == 0)
            {
                WoTLogger.Warn("CompiledSpace produced 0 SpeedTree vegetation placements");
                return;
            }

            var vegetationBuild = VegetationBuilder.Build(outputPath, space, pkgMgr);
            if (vegetationBuild.Root != null)
            {
                vegetationBuild.Root.transform.SetParent(root.transform, false);
                ApplyRequestedObjectsRootTransform(vegetationBuild.Root.transform);
            }

            foreach (var w in vegetationBuild.Warnings)
            {
                WoTLogger.Info(w);
                result.Warnings.Add(w);
            }

            WoTLogger.Info($"Vegetation: {space.SpeedTrees.Count} SpTr placements");
        }

        private static void ApplyRequestedObjectsRootTransform(Transform objectsRoot)
        {
            if (objectsRoot == null) return;

            // User-facing coordinate adjustment for static objects only.
            // Terrain stays untouched; all imported objects, destructible hierarchy
            // and trigger colliders are under StaticObjects with this transform.
            objectsRoot.localRotation = Quaternion.Euler(-90f, 180f, 0f);
            objectsRoot.localScale = new Vector3(-1f, 1f, 1f);
        }

        private static void EnsureAssetFolder(string folderPath)
        {
            folderPath = folderPath.Replace('\\', '/');
            if (string.IsNullOrEmpty(folderPath) || AssetDatabase.IsValidFolder(folderPath)) return;

            string parent = Path.GetDirectoryName(folderPath)?.Replace('\\', '/') ?? "Assets";
            string leaf = Path.GetFileName(folderPath);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureAssetFolder(parent);
            if (!AssetDatabase.IsValidFolder(folderPath)) AssetDatabase.CreateFolder(parent, leaf);
        }

        private static void ParseChunkName(string name, out int hexX, out int hexY)
        {
            // A chunk file is named like "XXXXYYYY.cdata" / "XXXXYYYYo.cdata_processed".
            // We only care about the first 8 hex chars (matches Blender: chunk_name = item.name[:8]).
            // Strip every extension first (the file may have a double extension).
            string baseName = name;
            int dot = baseName.IndexOf('.');
            if (dot >= 0) baseName = baseName.Substring(0, dot);

            hexX = 0; hexY = 0;
            if (baseName.Length < 8) return;
            try
            {
                // NOTE: Substring's 2nd arg is LENGTH, not end index. Both halves are 4 chars.
                hexX = unchecked((short)Convert.ToInt32(baseName.Substring(0, 4), 16));
                hexY = unchecked((short)Convert.ToInt32(baseName.Substring(4, 4), 16));
            }
            catch (Exception e)
            {
                WoTLogger.Warn($"ParseChunkName failed for '{name}' (base '{baseName}'): {e.Message}");
            }
        }
    }
}
