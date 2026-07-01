using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using WoTMapImporter.Editor.Image;
using WoTMapImporter.Editor.Package;
using WoTMapImporter.Editor.Utils;

namespace WoTMapImporter.Editor.Vegetation
{
    /// <summary>
    /// Best-effort decoder for old BigWorld/WoT 0.8.x compiled SpeedTree (*.ctree) files.
    ///
    /// Important: *.spt is a procedural SpeedTree source file.  Without the original
    /// SpeedTreeRT DLL/modeler it cannot be deterministically evaluated into triangles.
    /// WoT 0.8.x ships an already compiled companion file (*.ctree); this decoder scans
    /// that file for the common runtime geometry blocks seen in 0.8.x clients:
    ///
    ///   u32 vertexCount
    ///   6 * f32 bbox-ish data
    ///   2 * f32 lod/alpha-ish data
    ///   u32 indexCount
    ///   vertexCount * 52 bytes   (uv/wind, normal, position, tangent)
    ///   indexCount * u16         (triangle strip indices, degenerate breaks allowed)
    ///
    /// The exact format was never public and small game builds can differ, so the
    /// decoder is intentionally heuristic.  Unsupported blocks are skipped and the
    /// caller can still fall back to a placeholder.
    /// </summary>
    public static class CtreeMeshDecoder
    {
        private const int DefaultVertexStride = 52;
        private const int DefaultPositionOffset = 28;
        private const int DefaultNormalOffset = 16;
        private const int DefaultUOffset = 0;
        private const int DefaultVOffset = 8;
        private static readonly bool EnableExperimentalCtreeMeshes = true;
        // CTREE meshes are local tree/bush meshes.  Anything beyond this is almost
        // certainly a false-positive block/stride and will break Unity AABBs.
        private const float MaxSaneLocalCoord = 1000f;
        private const float MaxSaneBoundsSize = 1000f;

        public sealed class ImportResult
        {
            public GameObject Prefab;
            public readonly List<string> Warnings = new List<string>();
        }

        private sealed class DecodedCtree
        {
            public readonly List<StringRef> Strings = new List<StringRef>();
            public readonly List<MeshPart> Meshes = new List<MeshPart>();
        }

        private sealed class StringRef
        {
            public int Offset;
            public string Text;
        }

        private sealed class MeshCandidate
        {
            public int HeaderOffset;
            public int VertexOffset;
            public int IndexOffset;
            public int EndOffset;
            public int VertexCount;
            public int IndexCount;
            public int Stride;
            public int IndexSize;
            public int PositionOffset;
            public int NormalOffset;
            public int UOffset;
            public int VOffset;
            public int Score;
        }

        private sealed class MeshPart
        {
            public int SourceOffset;
            public string TextureName;
            public Vector3[] Positions;
            public Vector3[] Normals;
            public Vector2[] Uv;
            public int[] Indices;
        }

        public static bool IsCtree(byte[] data)
        {
            if (!EnableExperimentalCtreeMeshes) return false;
            if (data == null || data.Length < 128) return false;
            if (LooksLikeSpt(data) || LooksLikeSrt(data)) return false;

            var strings = FindLengthPrefixedStrings(data, stopAfter: 32);
            for (int i = 0; i < strings.Count; i++)
            {
                string s = strings[i].Text.ToLowerInvariant().Replace('\\', '/');
                if ((s.Contains("speedtree/") || s.Contains("bark") || s.Contains("leaf") || s.Contains("compositemap")) &&
                    (s.EndsWith(".dds") || s.EndsWith(".tga") || s.EndsWith(".png")))
                    return true;
            }

            // Old .ctree often starts directly with a mesh block.  This catches files
            // where strings were stripped or not present in the first scan window.
            return FindMeshCandidates(data, maxCandidates: 1).Count > 0;
        }

        public static ImportResult ImportToPrefab(
            string outputPath,
            string resourceName,
            byte[] data,
            WoTPackageManager resMgr)
        {
            var result = new ImportResult();
            try
            {
                var decoded = Decode(data);
                if (decoded == null || decoded.Meshes.Count == 0)
                {
                    result.Warnings.Add("CTREE decoded no mesh blocks: " + resourceName);
                    return result;
                }

                WoTLogger.Info($"CTREE decoder: {resourceName}, meshes={decoded.Meshes.Count}, strings={decoded.Strings.Count}");
                result.Prefab = BuildPrefab(outputPath, resourceName, decoded, resMgr, result.Warnings);
                if (result.Prefab == null)
                    result.Warnings.Add("CTREE prefab build failed: " + resourceName);
            }
            catch (Exception e)
            {
                result.Warnings.Add($"CTREE decode failed ({resourceName}): {e.Message}");
                WoTLogger.Warn($"CTREE decode failed ({resourceName}): {e.Message}\n{e.StackTrace}");
            }
            return result;
        }

        private static DecodedCtree Decode(byte[] data)
        {
            if (data == null || data.Length < 128) return null;
            var ctree = new DecodedCtree();
            ctree.Strings.AddRange(FindLengthPrefixedStrings(data, stopAfter: 4096));

            var candidates = FindMeshCandidates(data, maxCandidates: 128);
            candidates = RemoveOverlappingCandidates(candidates);
            candidates.Sort((a, b) => a.HeaderOffset.CompareTo(b.HeaderOffset));

            foreach (var c in candidates)
            {
                var part = DecodeMeshPart(data, c);
                if (part == null) continue;
                part.TextureName = ChooseTextureForBlock(part.SourceOffset, ctree.Strings);
                ctree.Meshes.Add(part);
            }
            return ctree;
        }

        private static MeshPart DecodeMeshPart(byte[] data, MeshCandidate c)
        {
            if (c.VertexCount <= 0 || c.IndexCount <= 0) return null;

            var positions = new Vector3[c.VertexCount];
            var normals = new Vector3[c.VertexCount];
            var uvs = new Vector2[c.VertexCount];

            for (int i = 0; i < c.VertexCount; i++)
            {
                int b = c.VertexOffset + i * c.Stride;
                Vector3 p = ReadVector3(data, b + c.PositionOffset);
                Vector3 n = ReadVector3(data, b + c.NormalOffset);
                if (!IsFiniteVector(n) || n.sqrMagnitude < 1e-8f) n = Vector3.up;

                float u = ReadSingle(data, b + c.UOffset);
                float v = ReadSingle(data, b + c.VOffset);
                if (!IsFinite(u)) u = 0f;
                if (!IsFinite(v)) v = 0f;

                // WoT/SpeedTree is Y-up in these old files; Unity project code uses Y-up
                // meshes and the map-level root transform later performs the requested
                // WoT->Unity adjustment.  Keep the same local conversion as SptDumper.
                positions[i] = new Vector3(p.x, p.z, p.y);
                normals[i] = new Vector3(n.x, n.z, n.y).normalized;
                uvs[i] = new Vector2(u, 1f - v);
            }

            var strip = new int[c.IndexCount];
            for (int i = 0; i < c.IndexCount; i++)
            {
                int off = c.IndexOffset + i * c.IndexSize;
                strip[i] = c.IndexSize == 4
                    ? unchecked((int)ReadUInt32(data, off))
                    : ReadUInt16(data, off);
            }

            int[] triangles = BuildBestTriangleList(strip, positions, c.VertexCount);
            triangles = FilterInvalidGeometryTriangles(triangles, positions);
            if (triangles.Length == 0) return null;
            if (!SanitizeVerticesForUnityBounds(positions, triangles)) return null;
            if (NeedsWindingFlip(positions, normals, triangles)) SwapTriangleWinding(triangles);

            return new MeshPart
            {
                SourceOffset = c.HeaderOffset,
                Positions = positions,
                Normals = normals,
                Uv = uvs,
                Indices = triangles,
            };
        }

        private static List<MeshCandidate> FindMeshCandidates(byte[] data, int maxCandidates)
        {
            var result = new List<MeshCandidate>();
            if (data == null || data.Length < 128) return result;

            // From probes of WoT 0.8.10 CTREE:
            //   tree/trunk blocks: indexCount at +36, vb starts at +40, stride often 52;
            //   shrub/leaf blocks: several zero dwords, indexCount at +52, vb starts at +56,
            //                     stride often 72/76/64/44.
            // Indices in these CTREE files are 32-bit.  Reading them as uint16 is the
            // main reason for the huge black triangle carpets seen in earlier builds.
            int[] indexFields = { 36, 52, 56, 60, 64 };
            int[] strides = { 52, 72, 76, 64, 60, 56, 48, 44, 40, 36, 32, 28, 24, 20, 16 };

            for (int off = 0; off + 80 < data.Length; off += 4)
            {
                int vCount = unchecked((int)ReadUInt32(data, off));
                if (vCount < 3 || vCount > 100000) continue;

                foreach (int idxField in indexFields)
                {
                    if (off + idxField + 4 > data.Length) continue;
                    int iCount = unchecked((int)ReadUInt32(data, off + idxField));
                    if (iCount < 3 || iCount > 1000000) continue;
                    if (!LooksLikeCtreeMeshHeader(data, off, idxField)) continue;

                    int vb = off + idxField + 4;
                    foreach (int stride in strides)
                    {
                        if (stride < 12 || vb + (long)vCount * stride >= data.Length) continue;
                        long rawIb = (long)vb + (long)vCount * stride;
                        foreach (int ib in new[] { (int)rawIb, Align4((int)rawIb), Align16((int)rawIb) })
                        {
                            if (ib < 0 || ib + (long)iCount * 4 > data.Length) continue;
                            int validIndexScore = ScoreIndexBuffer(data, ib, iCount, 4, vCount);
                            if (validIndexScore < 90) continue;

                            int posOff = GuessVectorOffset(data, vb, vCount, stride, stride >= 40 ? DefaultPositionOffset : 0, preferPosition: true);
                            if (posOff < 0) continue;
                            if (!MeasurePositionCloud(data, vb, vCount, stride, posOff, out var min, out var max, out float diag, out float avgAbs))
                                continue;
                            if (diag < 0.01f || diag > 80f || avgAbs > 80f) continue;

                            int nrmOff = GuessVectorOffset(data, vb, vCount, stride,
                                stride >= 28 ? DefaultNormalOffset : 0,
                                preferPosition: false);
                            if (nrmOff < 0 || nrmOff == posOff) nrmOff = stride >= 28 ? DefaultNormalOffset : 0;
                            if (nrmOff + 12 > stride) nrmOff = 0;

                            int score = validIndexScore;
                            if (idxField == 36) score += 25;
                            if (idxField == 52) score += 20;
                            if (stride == 52) score += 35;
                            if (stride == 72 || stride == 76) score += 25;
                            if (stride == 44 || stride == 64) score += 10;
                            if (posOff == 28) score += 20;
                            if (ib == rawIb) score += 10;
                            if (off == 0) score += 20;
                            score += Mathf.RoundToInt(Mathf.Clamp(80f - diag, 0f, 80f));
                            score -= Mathf.RoundToInt(Mathf.Clamp(avgAbs, 0f, 80f));

                            result.Add(new MeshCandidate
                            {
                                HeaderOffset = off,
                                VertexOffset = vb,
                                IndexOffset = ib,
                                EndOffset = checked(ib + iCount * 4),
                                VertexCount = vCount,
                                IndexCount = iCount,
                                Stride = stride,
                                IndexSize = 4,
                                PositionOffset = posOff,
                                NormalOffset = nrmOff,
                                UOffset = DefaultUOffset < stride ? DefaultUOffset : 0,
                                VOffset = DefaultVOffset < stride ? DefaultVOffset : Math.Max(0, stride - 4),
                                Score = score,
                            });
                        }
                    }
                }
            }
            return result;
        }

        private static bool MeasurePositionCloud(byte[] data, int vb, int vertexCount, int stride, int posOff,
            out Vector3 min, out Vector3 max, out float diag, out float avgAbs)
        {
            int samples = Math.Min(vertexCount, 512);
            int ok = 0;
            min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            double sumAbs = 0.0;
            for (int i = 0; i < samples; i++)
            {
                Vector3 v = ReadVector3(data, vb + i * stride + posOff);
                if (!IsFiniteVector(v) || Mathf.Abs(v.x) > 10000f || Mathf.Abs(v.y) > 10000f || Mathf.Abs(v.z) > 10000f) continue;
                ok++;
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
                sumAbs += Mathf.Abs(v.x) + Mathf.Abs(v.y) + Mathf.Abs(v.z);
            }
            if (ok < Math.Max(3, samples * 9 / 10)) { diag = 0; avgAbs = 0; return false; }
            diag = (max - min).magnitude;
            avgAbs = (float)(sumAbs / Math.Max(1, ok));
            return IsFinite(diag) && IsFinite(avgAbs);
        }

        private static bool LooksLikeCtreeMeshHeader(byte[] data, int off, int indexField)
        {
            int good = 0;
            int meaningful = 0;
            for (int o = 4; o < indexField; o += 4)
            {
                float f = ReadSingle(data, off + o);
                if (!IsFinite(f) || Mathf.Abs(f) > 100000f) return false;
                good++;
                if (Mathf.Abs(f) > 0.001f) meaningful++;
            }
            if (good < 6 || meaningful < 4) return false;

            // Reject index-array false positives like: 58 00 00 00 54 00 00 00 ...
            // Interpreted as floats these are all denormals near zero.
            float maxAbs = 0f;
            for (int o = 4; o < Math.Min(indexField, 36); o += 4)
                maxAbs = Mathf.Max(maxAbs, Mathf.Abs(ReadSingle(data, off + o)));
            return maxAbs > 0.01f;
        }

        private static int Align16(int v) => (v + 15) & ~15;

        private static List<MeshCandidate> RemoveOverlappingCandidates(List<MeshCandidate> candidates)
        {
            candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
            var kept = new List<MeshCandidate>();
            foreach (var c in candidates)
            {
                bool overlaps = false;
                for (int i = 0; i < kept.Count; i++)
                {
                    var k = kept[i];
                    if (c.HeaderOffset < k.EndOffset && k.HeaderOffset < c.EndOffset)
                    {
                        overlaps = true;
                        break;
                    }
                }
                if (!overlaps) kept.Add(c);
                if (kept.Count >= 64) break;
            }
            return kept;
        }

        private static bool LooksLikeFiniteHeader(byte[] data, int off)
        {
            return LooksLikeCtreeMeshHeader(data, off, 36);
        }

        private static int ScoreIndexBuffer(byte[] data, int offset, int count, int indexSize, int vertexCount)
        {
            int samples = Math.Min(count, 1024);
            int valid = 0;
            int varied = 0;
            int prev = -1;
            for (int i = 0; i < samples; i++)
            {
                int idx = indexSize == 4
                    ? unchecked((int)ReadUInt32(data, offset + i * 4))
                    : ReadUInt16(data, offset + i * 2);
                if (idx >= 0 && idx < vertexCount) valid++;
                if (idx != prev) varied++;
                prev = idx;
            }
            if (samples == 0) return 0;
            int pct = valid * 100 / samples;
            if (varied < Math.Max(3, samples / 20)) pct -= 30;
            return pct;
        }

        private static int GuessVectorOffset(byte[] data, int vb, int vertexCount, int stride, int preferred, bool preferPosition)
        {
            var offsets = new List<int>();
            if (preferred >= 0 && preferred + 12 <= stride) offsets.Add(preferred);
            for (int o = 0; o + 12 <= stride; o += 4)
                if (!offsets.Contains(o)) offsets.Add(o);

            int bestOff = -1;
            float bestScore = float.NegativeInfinity;
            int samples = Math.Min(vertexCount, 32);
            foreach (int o in offsets)
            {
                float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
                float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;
                int finite = 0;
                float lenSum = 0f;
                for (int i = 0; i < samples; i++)
                {
                    int b = vb + i * stride + o;
                    Vector3 v = ReadVector3(data, b);
                    if (!IsFiniteVector(v) || Math.Abs(v.x) > 10000f || Math.Abs(v.y) > 10000f || Math.Abs(v.z) > 10000f) continue;
                    finite++;
                    lenSum += v.magnitude;
                    if (v.x < minX) minX = v.x; if (v.x > maxX) maxX = v.x;
                    if (v.y < minY) minY = v.y; if (v.y > maxY) maxY = v.y;
                    if (v.z < minZ) minZ = v.z; if (v.z > maxZ) maxZ = v.z;
                }
                if (finite < Math.Max(3, samples / 2)) continue;

                float range = (maxX - minX) + (maxY - minY) + (maxZ - minZ);
                float avgLen = lenSum / Math.Max(1, finite);
                float score;
                if (preferPosition)
                {
                    // Positions should have some spatial spread and usually lengths
                    // of centimeters/meters, not a unit normal cloud.
                    score = range + Mathf.Clamp(avgLen, 0f, 100f) * 0.05f;
                    if (o == preferred) score += 4f;
                }
                else
                {
                    // Normals should be near unit length and not too far spread as
                    // absolute coordinates.
                    score = -Math.Abs(avgLen - 1f) * 10f + range * 0.05f;
                    if (o == preferred) score += 2f;
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    bestOff = o;
                }
            }
            return bestOff;
        }

        private static int[] BuildBestTriangleList(int[] indices, Vector3[] positions, int vertexCount)
        {
            // Old CTREE blocks are usually triangle strips.  Some files store several
            // strips back-to-back; if we stitch them as one strip, Unity shows huge
            // black carpets between leaves/bushes.  Remove stretched bridge triangles
            // and also try plain triangle-list interpretation as a fallback.
            int[] strip = FilterStretchedTriangles(TriangleStripToList(indices, vertexCount), positions);
            int[] list = FilterStretchedTriangles(TriangleListToList(indices, vertexCount), positions);

            if (strip.Length == 0) return list;
            if (list.Length == 0) return strip;

            float stripBad = TriangleBadness(strip, positions);
            float listBad = TriangleBadness(list, positions);

            // Prefer strips unless triangle-list interpretation is clearly cleaner.
            if (list.Length >= strip.Length / 3 && listBad < stripBad * 0.65f)
                return list;
            return strip;
        }

        private static int[] TriangleStripToList(int[] strip, int vertexCount)
        {
            var tris = new List<int>(Math.Max(0, (strip.Length - 2) * 3));
            for (int i = 0; i + 2 < strip.Length; i++)
            {
                int i0 = strip[i];
                int i1 = strip[i + 1];
                int i2 = strip[i + 2];
                if (!ValidTri(i0, i1, i2, vertexCount)) continue;

                if ((i & 1) == 0)
                {
                    tris.Add(i0); tris.Add(i2); tris.Add(i1);
                }
                else
                {
                    tris.Add(i0); tris.Add(i1); tris.Add(i2);
                }
            }
            return tris.ToArray();
        }

        private static int[] TriangleListToList(int[] src, int vertexCount)
        {
            var tris = new List<int>(src.Length);
            for (int i = 0; i + 2 < src.Length; i += 3)
            {
                int i0 = src[i];
                int i1 = src[i + 1];
                int i2 = src[i + 2];
                if (!ValidTri(i0, i1, i2, vertexCount)) continue;
                tris.Add(i0); tris.Add(i1); tris.Add(i2);
            }
            return tris.ToArray();
        }

        private static bool ValidTri(int i0, int i1, int i2, int vertexCount)
        {
            return i0 >= 0 && i1 >= 0 && i2 >= 0 &&
                   i0 < vertexCount && i1 < vertexCount && i2 < vertexCount &&
                   i0 != i1 && i1 != i2 && i2 != i0;
        }

        private static int[] FilterStretchedTriangles(int[] triangles, Vector3[] positions)
        {
            if (triangles == null || positions == null || triangles.Length < 3) return Array.Empty<int>();

            var maxEdges = new List<float>(triangles.Length / 3);
            Bounds b = new Bounds(positions[0], Vector3.zero);
            for (int i = 1; i < positions.Length; i++) b.Encapsulate(positions[i]);
            float diag = Mathf.Max(0.001f, b.size.magnitude);

            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int a = triangles[i], c = triangles[i + 1], d = triangles[i + 2];
                if (!ValidTri(a, c, d, positions.Length)) continue;
                if (!SanePosition(positions[a]) || !SanePosition(positions[c]) || !SanePosition(positions[d])) continue;
                maxEdges.Add(MaxEdge(positions[a], positions[c], positions[d]));
            }
            if (maxEdges.Count == 0) return Array.Empty<int>();
            maxEdges.Sort();
            float median = Mathf.Max(0.001f, maxEdges[maxEdges.Count / 2]);
            float threshold = Mathf.Max(median * 8f, diag * 0.35f);

            var kept = new List<int>(triangles.Length);
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int a = triangles[i], c = triangles[i + 1], d = triangles[i + 2];
                if (!ValidTri(a, c, d, positions.Length)) continue;
                if (!SanePosition(positions[a]) || !SanePosition(positions[c]) || !SanePosition(positions[d])) continue;
                float e = MaxEdge(positions[a], positions[c], positions[d]);
                // Remove only obvious strip bridges.  Real long branch triangles are
                // allowed unless they are also large relative to the median triangle.
                if (e > threshold && e > median * 4f) continue;
                kept.Add(a); kept.Add(c); kept.Add(d);
            }
            return kept.ToArray();
        }

        private static float TriangleBadness(int[] triangles, Vector3[] positions)
        {
            if (triangles == null || positions == null || triangles.Length < 3) return float.PositiveInfinity;
            double sum = 0.0;
            int n = 0;
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                if (!ValidTri(a, b, c, positions.Length)) continue;
                if (!SanePosition(positions[a]) || !SanePosition(positions[b]) || !SanePosition(positions[c])) continue;
                sum += MaxEdge(positions[a], positions[b], positions[c]);
                n++;
            }
            return n > 0 ? (float)(sum / n) : float.PositiveInfinity;
        }

        private static int[] FilterInvalidGeometryTriangles(int[] triangles, Vector3[] positions)
        {
            if (triangles == null || positions == null || triangles.Length < 3) return Array.Empty<int>();
            var kept = new List<int>(triangles.Length);
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                if (!ValidTri(a, b, c, positions.Length)) continue;
                if (!SanePosition(positions[a]) || !SanePosition(positions[b]) || !SanePosition(positions[c])) continue;
                float area2 = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]).magnitude;
                if (!IsFinite(area2) || area2 < 1e-10f) continue;
                kept.Add(a); kept.Add(b); kept.Add(c);
            }
            return kept.ToArray();
        }

        private static bool SanitizeVerticesForUnityBounds(Vector3[] positions, int[] triangles)
        {
            if (positions == null || positions.Length == 0 || triangles == null || triangles.Length < 3) return false;

            var used = new bool[positions.Length];
            for (int i = 0; i < triangles.Length; i++)
            {
                int idx = triangles[i];
                if (idx >= 0 && idx < used.Length) used[idx] = true;
            }

            bool has = false;
            Bounds bounds = new Bounds(Vector3.zero, Vector3.zero);
            for (int i = 0; i < positions.Length; i++)
            {
                if (!used[i]) continue;
                if (!SanePosition(positions[i])) return false;
                if (!has) { bounds = new Bounds(positions[i], Vector3.zero); has = true; }
                else bounds.Encapsulate(positions[i]);
            }
            if (!has) return false;
            if (!IsFiniteVector(bounds.min) || !IsFiniteVector(bounds.max)) return false;
            if (bounds.size.magnitude > MaxSaneBoundsSize) return false;

            // Unity calculates bounds from every vertex, including vertices not used by
            // any triangle.  False-positive CTREE candidates may leave unused garbage
            // vertices with +/-inf/NaN.  Collapse unused/invalid vertices to the valid
            // bounds center so they cannot poison mesh.bounds/worldAABB.
            Vector3 center = bounds.center;
            for (int i = 0; i < positions.Length; i++)
            {
                if (!used[i] || !SanePosition(positions[i]))
                    positions[i] = center;
            }
            return true;
        }

        private static bool SanePosition(Vector3 v)
        {
            return IsFiniteVector(v) &&
                   Mathf.Abs(v.x) <= MaxSaneLocalCoord &&
                   Mathf.Abs(v.y) <= MaxSaneLocalCoord &&
                   Mathf.Abs(v.z) <= MaxSaneLocalCoord;
        }

        private static float MaxEdge(Vector3 a, Vector3 b, Vector3 c)
        {
            return Mathf.Max((a - b).magnitude, Mathf.Max((b - c).magnitude, (c - a).magnitude));
        }

        private static string ChooseTextureForBlock(int blockOffset, List<StringRef> strings)
        {
            string firstDiffuse = null;
            string bark = null;
            string composite = null;
            int firstCompositeOffset = int.MaxValue;

            for (int i = 0; i < strings.Count; i++)
            {
                string s = strings[i].Text;
                if (!IsLikelyDiffuse(s)) continue;
                if (firstDiffuse == null) firstDiffuse = s;

                string n = s.ToLowerInvariant();
                if (bark == null && (n.Contains("bark") || n.Contains("branch") || n.Contains("trunk")))
                    bark = s;
                if (composite == null && (n.Contains("compositemap") || n.Contains("leaf") || n.Contains("leaves") || n.Contains("frond")))
                {
                    composite = s;
                    firstCompositeOffset = strings[i].Offset;
                }
            }

            if (blockOffset < firstCompositeOffset && !string.IsNullOrEmpty(bark)) return bark;
            if (!string.IsNullOrEmpty(composite)) return composite;
            if (!string.IsNullOrEmpty(bark)) return bark;
            return firstDiffuse;
        }

        private static List<StringRef> FindLengthPrefixedStrings(byte[] data, int stopAfter)
        {
            var result = new List<StringRef>();
            if (data == null) return result;

            for (int off = 0; off + 8 <= data.Length; off++)
            {
                int len = unchecked((int)ReadUInt32(data, off));
                if (len <= 0 || len > 512 || off + 4 + len > data.Length) continue;
                if (!LooksAscii(data, off + 4, len)) continue;

                int actual = len;
                while (actual > 0 && data[off + 4 + actual - 1] == 0) actual--;
                if (actual <= 0) continue;
                string s = CleanResourcePath(Encoding.UTF8.GetString(data, off + 4, actual));
                if (string.IsNullOrEmpty(s) || !LooksUsefulString(s)) continue;

                result.Add(new StringRef { Offset = off, Text = s });
                if (stopAfter > 0 && result.Count >= stopAfter) break;
            }
            return result;
        }

        private static bool LooksAscii(byte[] data, int off, int len)
        {
            int last = len - 1;
            while (last >= 0 && data[off + last] == 0) last--;
            if (last < 0) return false;

            int useful = 0;
            for (int i = 0; i <= last; i++)
            {
                byte b = data[off + i];
                // Internal NUL/control chars later make System.IO.Path throw
                // "Illegal characters in path".  Only trailing NUL padding is valid.
                if (b < 0x20 || b > 0x7e) return false;
                if ((b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'a' && b <= (byte)'z') || b == (byte)'/' || b == (byte)'\\' || b == (byte)'.')
                    useful++;
            }
            return useful >= Math.Min(4, last + 1);
        }

        private static bool LooksUsefulString(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string n = s.ToLowerInvariant();
            return n.Contains("speedtree") || n.Contains(".dds") || n.Contains(".tga") || n.Contains(".png") ||
                   n.Contains("bark") || n.Contains("leaf") || n.Contains("frond") || n.Contains("compositemap");
        }

        private static GameObject BuildPrefab(
            string outputPath,
            string resourceName,
            DecodedCtree ctree,
            WoTPackageManager resMgr,
            List<string> warnings)
        {
            string baseName = SafeAssetName(PathName(resourceName));
            string rootDir = $"{outputPath}/VegetationAssets/_DecodedCTREE/{baseName}_{StableHash32(resourceName):X8}".Replace('\\', '/');
            EnsureFolder(rootDir);

            var tempRoot = new GameObject(baseName + "_CTREE");
            var materialCache = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < ctree.Meshes.Count; i++)
            {
                var part = ctree.Meshes[i];
                var mesh = new UnityEngine.Mesh
                {
                    name = SafeAssetName($"{baseName}_ctree_{i:D2}_{part.SourceOffset:X}"),
                    indexFormat = part.Positions.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
                };
                mesh.vertices = part.Positions;
                mesh.normals = part.Normals;
                mesh.uv = part.Uv;
                mesh.triangles = part.Indices;
                if (mesh.normals == null || mesh.normals.Length != mesh.vertexCount) mesh.RecalculateNormals();
                try { mesh.RecalculateTangents(); } catch { }
                mesh.RecalculateBounds();

                string meshPath = $"{rootDir}/Meshes/{mesh.name}_{StableHash32(resourceName + i + part.SourceOffset):X8}.asset";
                SaveAsset(mesh, meshPath);
                var meshAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Mesh>(meshPath) ?? mesh;

                var go = new GameObject($"mesh_{i:D2}_{part.SourceOffset:X}");
                go.transform.SetParent(tempRoot.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = meshAsset;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = GetOrCreateMaterial(rootDir, resourceName, part.TextureName, resMgr, materialCache, warnings);
            }

            RemoveAllColliders(tempRoot);
            string prefabPath = $"{rootDir}/{baseName}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(tempRoot, prefabPath);
            UnityEngine.Object.DestroyImmediate(tempRoot);
            return prefab;
        }

        private static Material GetOrCreateMaterial(
            string rootDir,
            string resourceName,
            string textureName,
            WoTPackageManager resMgr,
            Dictionary<string, Material> cache,
            List<string> warnings)
        {
            string key = textureName ?? "__none__";
            if (cache.TryGetValue(key, out var cached)) return cached;

            Texture2D tex = LoadTexture(rootDir, resourceName, textureName, resMgr, warnings);
            Shader shader = Shader.Find("WoT/ObjectPBS") ??
                            Shader.Find("Universal Render Pipeline/Lit") ??
                            Shader.Find("HDRP/Lit") ??
                            Shader.Find("Standard") ??
                            Shader.Find("Sprites/Default");
            var mat = new Material(shader)
            {
                name = SafeAssetName($"{PathName(resourceName)}_{PathName(textureName ?? "ctree")}"),
            };
            if (tex != null)
            {
                SetTextureIfExists(mat, "_BaseMap", tex);
                SetTextureIfExists(mat, "_MainTex", tex);
            }
            else
            {
                Color tint = GuessTint(textureName);
                SetColorIfExists(mat, "_BaseColor", tint);
                SetColorIfExists(mat, "_Color", tint);
            }

            SetFloatIfExists(mat, "_Cutoff", 0.35f);
            SetFloatIfExists(mat, "_AlphaClip", 1f);
            mat.SetOverrideTag("RenderType", "TransparentCutout");
            mat.SetOverrideTag("Queue", "AlphaTest");
            mat.renderQueue = (int)RenderQueue.AlphaTest;
            mat.EnableKeyword("_ALPHATEST_ON");
            DisableBackfaceCulling(mat);
            mat.doubleSidedGI = true;

            string matPath = $"{rootDir}/Materials/{mat.name}_{StableHash32(key):X8}.mat";
            SaveAsset(mat, matPath);
            var matAsset = AssetDatabase.LoadAssetAtPath<Material>(matPath) ?? mat;
            cache[key] = matAsset;
            return matAsset;
        }

        private static Texture2D LoadTexture(string rootDir, string resourceName, string textureName, WoTPackageManager resMgr, List<string> warnings)
        {
            if (string.IsNullOrEmpty(textureName) || resMgr == null) return null;
            foreach (string candidate in TextureCandidates(resourceName, textureName))
            {
                byte[] bytes = null;
                try { bytes = resMgr.ReadBytes(candidate); }
                catch (Exception e)
                {
                    warnings?.Add($"CTREE texture candidate skipped ({candidate}): {e.Message}");
                    continue;
                }
                if (bytes == null) continue;

                try
                {
                    Texture2D tex;
                    if (bytes.Length >= 4 && BitConverter.ToUInt32(bytes, 0) == DdsDecoder.MAGIC)
                    {
                        try { tex = DdsDecoder.ReadDecompressed(bytes, Path.GetFileNameWithoutExtension(candidate), false, true); }
                        catch { tex = DdsDecoder.Read(bytes, Path.GetFileNameWithoutExtension(candidate), false); }
                    }
                    else
                    {
                        tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
                        {
                            name = Path.GetFileNameWithoutExtension(candidate),
                            wrapMode = TextureWrapMode.Repeat,
                        };
                        if (!tex.LoadImage(bytes, false))
                        {
                            UnityEngine.Object.DestroyImmediate(tex);
                            continue;
                        }
                    }

                    tex.name = SafeAssetName(PathName(candidate) + "_" + StableHash32(candidate).ToString("X8"));
                    tex.wrapMode = TextureWrapMode.Repeat;
                    tex.filterMode = FilterMode.Bilinear;
                    string texPath = $"{rootDir}/Textures/{tex.name}.asset";
                    SaveAsset(tex, texPath);
                    return AssetDatabase.LoadAssetAtPath<Texture2D>(texPath) ?? tex;
                }
                catch (Exception e)
                {
                    warnings?.Add($"CTREE texture load failed ({candidate}): {e.Message}");
                }
            }
            return null;
        }

        private static IEnumerable<string> TextureCandidates(string resourceName, string textureName)
        {
            string cleaned = CleanResourcePath(textureName);
            if (string.IsNullOrEmpty(cleaned)) yield break;
            string tex = Normalize(cleaned);
            string resDir = Path.GetDirectoryName(Normalize(resourceName))?.Replace('\\', '/') ?? string.Empty;
            yield return tex;
            if (!tex.StartsWith("content/", StringComparison.OrdinalIgnoreCase)) yield return "content/" + tex;

            if (!string.IsNullOrEmpty(resDir) && tex.IndexOf('/') < 0)
            {
                yield return resDir + "/" + tex;
                yield return "content/" + resDir + "/" + tex;
            }

            if (Path.HasExtension(tex))
            {
                string dds = Path.ChangeExtension(tex, ".dds").Replace('\\', '/');
                yield return dds;
                if (!dds.StartsWith("content/", StringComparison.OrdinalIgnoreCase)) yield return "content/" + dds;
            }
            else
            {
                foreach (string ext in new[] { ".dds", ".tga", ".png" })
                {
                    yield return tex + ext;
                    if (!tex.StartsWith("content/", StringComparison.OrdinalIgnoreCase)) yield return "content/" + tex + ext;
                }
            }
        }

        private static bool IsLikelyDiffuse(string name)
        {
            name = CleanResourcePath(name);
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            bool tex = n.EndsWith(".dds") || n.EndsWith(".tga") || n.EndsWith(".png") || n.EndsWith(".jpg") || n.EndsWith(".jpeg");
            if (!tex) return false;
            return !(n.Contains("normal") || n.Contains("_nm") || n.Contains("_n.") || n.Contains("spec") || n.Contains("rough") || n.Contains("height"));
        }

        private static Color GuessTint(string textureName)
        {
            string n = (textureName ?? string.Empty).ToLowerInvariant();
            if (n.Contains("bark") || n.Contains("branch") || n.Contains("trunk")) return new Color(0.45f, 0.32f, 0.20f, 1f);
            if (n.Contains("leaf") || n.Contains("leaves") || n.Contains("frond") || n.Contains("compositemap")) return new Color(0.45f, 0.65f, 0.32f, 1f);
            return new Color(0.55f, 0.55f, 0.55f, 1f);
        }

        private static bool NeedsWindingFlip(Vector3[] positions, Vector3[] normals, int[] indices)
        {
            if (positions == null || normals == null || indices == null || positions.Length == 0) return false;
            double sum = 0.0;
            int samples = 0;
            int step = Math.Max(3, (indices.Length / 300) * 3);
            for (int i = 0; i + 2 < indices.Length; i += step)
            {
                int i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
                if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= positions.Length || i1 >= positions.Length || i2 >= positions.Length) continue;
                Vector3 face = Vector3.Cross(positions[i1] - positions[i0], positions[i2] - positions[i0]);
                if (face.sqrMagnitude < 1e-12f) continue;
                face.Normalize();
                Vector3 n = normals[i0] + normals[i1] + normals[i2];
                if (n.sqrMagnitude < 1e-12f) continue;
                n.Normalize();
                sum += Vector3.Dot(face, n);
                samples++;
            }
            return samples > 0 && sum / samples < 0.0;
        }

        private static void SwapTriangleWinding(int[] indices)
        {
            if (indices == null) return;
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int t = indices[i];
                indices[i] = indices[i + 2];
                indices[i + 2] = t;
            }
        }

        private static void RemoveAllColliders(GameObject root)
        {
            if (root == null) return;
            var colliders = root.GetComponentsInChildren<Collider>(true);
            foreach (var c in colliders)
                if (c != null) UnityEngine.Object.DestroyImmediate(c);
        }

        private static void DisableBackfaceCulling(Material mat)
        {
            if (mat == null) return;
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
            if (mat.HasProperty("_CullMode")) mat.SetFloat("_CullMode", 0f);
            if (mat.HasProperty("_DoubleSidedEnable")) mat.SetFloat("_DoubleSidedEnable", 1f);
            mat.SetInt("_Cull", 0);
        }

        private static void SetTextureIfExists(Material mat, string prop, Texture tex)
        {
            if (tex != null && mat.HasProperty(prop)) mat.SetTexture(prop, tex);
            if (tex != null && (prop == "_MainTex" || prop == "_BaseMap")) mat.mainTexture = tex;
        }

        private static void SetColorIfExists(Material mat, string prop, Color color)
        {
            if (mat.HasProperty(prop)) mat.SetColor(prop, color);
        }

        private static void SetFloatIfExists(Material mat, string prop, float value)
        {
            if (mat.HasProperty(prop)) mat.SetFloat(prop, value);
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            if (data == null || offset < 0 || offset + 4 > data.Length) return 0;
            return data[offset] |
                   ((uint)data[offset + 1] << 8) |
                   ((uint)data[offset + 2] << 16) |
                   ((uint)data[offset + 3] << 24);
        }

        private static ushort ReadUInt16(byte[] data, int offset)
        {
            if (data == null || offset < 0 || offset + 2 > data.Length) return 0;
            return (ushort)(data[offset] | (data[offset + 1] << 8));
        }

        private static float ReadSingle(byte[] data, int offset)
        {
            uint u = ReadUInt32(data, offset);
            return BitConverter.ToSingle(BitConverter.GetBytes(u), 0);
        }

        private static Vector3 ReadVector3(byte[] data, int offset)
        {
            return new Vector3(ReadSingle(data, offset), ReadSingle(data, offset + 4), ReadSingle(data, offset + 8));
        }

        private static bool IsFinite(float f)
        {
            return !float.IsNaN(f) && !float.IsInfinity(f);
        }

        private static bool IsFiniteVector(Vector3 v)
        {
            return IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);
        }

        private static int Align4(int v) => (v + 3) & ~3;

        private static bool LooksLikeSpt(byte[] data)
        {
            if (data == null || data.Length < 20) return false;
            return ReadUInt32(data, 0) == 1000 && ReadUInt32(data, 4) == 12 &&
                   Encoding.ASCII.GetString(data, 8, 12) == "__IdvSpt_02_";
        }

        private static bool LooksLikeSrt(byte[] data)
        {
            if (data == null || data.Length < 16) return false;
            string h = Encoding.ASCII.GetString(data, 0, 16).TrimEnd('\0', ' ');
            return h == "SRT 06.0.0" || h == "SRT 07.0.0";
        }

        private static string CleanResourcePath(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim().Replace('\\', '/');
            int nul = s.IndexOf('\0');
            if (nul >= 0) s = s.Substring(0, nul);
            if (string.IsNullOrEmpty(s)) return null;

            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsControl(s[i])) return null;
            }

            try
            {
                // Validate with the runtime's path parser.  WoT resources are virtual
                // paths, but this still catches NUL and other illegal characters before
                // WoTPackageManager/PkgFile reaches Path.GetFileName().
                Path.GetFileName(s);
            }
            catch { return null; }

            return s.TrimStart('/');
        }

        private static string Normalize(string s)
        {
            return (s ?? string.Empty).Trim().TrimStart('/').Replace('\\', '/').ToLowerInvariant();
        }

        private static string PathName(string s)
        {
            string n = (s ?? string.Empty).Replace('\\', '/');
            int idx = n.LastIndexOf('/');
            string baseN = idx >= 0 ? n.Substring(idx + 1) : n;
            return Path.GetFileNameWithoutExtension(baseN);
        }

        private static string SafeAssetName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unnamed";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
        }

        private static uint StableHash32(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                if (s != null)
                {
                    for (int i = 0; i < s.Length; i++)
                    {
                        h ^= char.ToLowerInvariant(s[i]);
                        h *= 16777619u;
                    }
                }
                return h;
            }
        }

        private static void SaveAsset(UnityEngine.Object obj, string path)
        {
            path = path.Replace('\\', '/');
            EnsureFolder(Path.GetDirectoryName(path)?.Replace('\\', '/'));
            var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (existing != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(obj, path);
            AssetDatabase.ImportAsset(path);
        }

        private static void EnsureFolder(string folderPath)
        {
            folderPath = folderPath?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folderPath) || AssetDatabase.IsValidFolder(folderPath)) return;
            string parent = Path.GetDirectoryName(folderPath)?.Replace('\\', '/') ?? "Assets";
            string leaf = Path.GetFileName(folderPath);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            if (!AssetDatabase.IsValidFolder(folderPath)) AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
