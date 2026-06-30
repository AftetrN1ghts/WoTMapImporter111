using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WoTMapImporter.Editor.Vegetation
{
    public class SptDumper
    {
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct SIndexed
        {
            public int m_nDiscreteLodLevel;
            public ushort m_usNumStrips;
            public ushort m_usPad;
            public IntPtr m_pStripLengths;
            public IntPtr m_pStrips;

            public ushort m_usVertexCount;
            public ushort m_usPad2;
            public IntPtr m_pColors;
            public IntPtr m_pNormals;
            public IntPtr m_pBinormals;
            public IntPtr m_pTangents;
            public IntPtr m_pCoords;
            public IntPtr m_pTexCoords0;
            public IntPtr m_pTexCoords1;
            public IntPtr m_pWindWeights;
            public IntPtr m_pWindMatrixIndices;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct SLeaf
        {
            public bool m_bIsActive;
            public byte m_bPad1, m_bPad2, m_bPad3;
            public float m_fAlphaTestValue;
            public int m_nDiscreteLodLevel;
            public ushort m_usLeafCount;
            public ushort m_usPad;

            public IntPtr m_pLeafMapIndices;
            public IntPtr m_pLeafClusterIndices;
            public IntPtr m_pCenterCoords;
            public IntPtr m_pLeafMapTexCoords;
            public IntPtr m_pLeafMapCoords;

            public IntPtr m_pColors;
            public IntPtr m_pNormals;
            public IntPtr m_pBinormals;
            public IntPtr m_pTangents;
            public IntPtr m_pWindWeights;
            public IntPtr m_pWindMatrixIndices;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct SBillboard
        {
            public bool m_bIsActive;
            public byte m_bPad1, m_bPad2, m_bPad3;
            public IntPtr m_pTexCoords;
            public IntPtr m_pCoords;
            public float m_fAlphaTestValue;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct SGeometry
        {
            public SIndexed m_sBranches;
            public float m_fBranchAlphaTestValue;
            public SIndexed m_sFronds;
            public float m_fFrondAlphaTestValue;
            public SLeaf m_sLeaves0;
            public SLeaf m_sLeaves1;
            public SBillboard m_sBillboard0;
            public SBillboard m_sBillboard1;
            public SBillboard m_sHorizontalBillboard;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct STextures
        {
            public IntPtr m_pBranchTextureFilename;
            public uint m_uiLeafTextureCount;
            public IntPtr m_pLeafTextureFilenames;
            public uint m_uiFrondTextureCount;
            public IntPtr m_pFrondTextureFilenames;
            public IntPtr m_pCompositeFilename;
            public IntPtr m_pSelfShadowFilename;
        }

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        public delegate void CSpeedTreeRT_Constructor(IntPtr pThis);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        public delegate bool CSpeedTreeRT_LoadTree(IntPtr pThis, [MarshalAs(UnmanagedType.LPStr)] string pFilename);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        public delegate bool CSpeedTreeRT_Compute(IntPtr pThis, IntPtr pTransform, uint nSeed, bool bCompositeStrips);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        public delegate void CSpeedTreeRT_GetGeometry(IntPtr pThis, IntPtr pGeom, uint ulBitVector, short nBranchLodOverride, short nFrondLodOverride, short nLeafLodOverride);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        public delegate void CSpeedTreeRT_GetTextures(IntPtr pThis, IntPtr pTextures);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        public delegate void CSpeedTreeRT_Destructor(IntPtr pThis);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        public delegate void SGeometry_Constructor(IntPtr pGeom);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        public delegate void STextures_Constructor(IntPtr pTextures);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        public static extern IntPtr LoadLibrary(string dllToLoad);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        public static extern IntPtr GetProcAddress(IntPtr hModule, string procedureName);

        public static float GetFloat(IntPtr ptr, int offset)
        {
            if (ptr == IntPtr.Zero) return 0f;
            float[] arr = new float[1];
            Marshal.Copy(new IntPtr(ptr.ToInt64() + offset * 4), arr, 0, 1);
            return arr[0];
        }

        public static void Main(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: SptDumper <sptPath> <objPath> <speedTreeDllPath>");
                return;
            }

            string sptPath = args[0];
            string objPath = args[1];
            string dllPath = args[2];

            Console.WriteLine("SptDumper: Loading DLL " + dllPath);
            IntPtr hMod = LoadLibrary(dllPath);
            if (hMod == IntPtr.Zero)
            {
                Console.WriteLine("SptDumper: Failed to load DLL " + dllPath);
                return;
            }

            IntPtr pConstruct = GetProcAddress(hMod, "??0CSpeedTreeRT@@QAE@XZ");
            IntPtr pLoadTree  = GetProcAddress(hMod, "?LoadTree@CSpeedTreeRT@@QAE_NPBD@Z");
            IntPtr pCompute   = GetProcAddress(hMod, "?Compute@CSpeedTreeRT@@QAE_NPBMI_N@Z");
            IntPtr pGetGeom   = GetProcAddress(hMod, "?GetGeometry@CSpeedTreeRT@@QAEXAAUSGeometry@1@KFFF@Z");
            IntPtr pGetTex    = GetProcAddress(hMod, "?GetTextures@CSpeedTreeRT@@QBEXAAUSTextures@1@@Z");
            IntPtr pDestruct  = GetProcAddress(hMod, "??1CSpeedTreeRT@@QAE@XZ");
            IntPtr pGeomConst = GetProcAddress(hMod, "??0SGeometry@CSpeedTreeRT@@QAE@XZ");
            IntPtr pTexConst  = GetProcAddress(hMod, "??0STextures@CSpeedTreeRT@@QAE@XZ");

            if (pConstruct == IntPtr.Zero || pLoadTree == IntPtr.Zero || pCompute == IntPtr.Zero || pGetGeom == IntPtr.Zero)
            {
                Console.WriteLine("SptDumper: Failed to find CSpeedTreeRT exports in DLL");
                return;
            }

            var fnConstruct = (CSpeedTreeRT_Constructor)Marshal.GetDelegateForFunctionPointer(pConstruct, typeof(CSpeedTreeRT_Constructor));
            var fnLoadTree  = (CSpeedTreeRT_LoadTree)Marshal.GetDelegateForFunctionPointer(pLoadTree, typeof(CSpeedTreeRT_LoadTree));
            var fnCompute   = (CSpeedTreeRT_Compute)Marshal.GetDelegateForFunctionPointer(pCompute, typeof(CSpeedTreeRT_Compute));
            var fnGetGeom   = (CSpeedTreeRT_GetGeometry)Marshal.GetDelegateForFunctionPointer(pGetGeom, typeof(CSpeedTreeRT_GetGeometry));
            var fnGetTex    = (CSpeedTreeRT_GetTextures)Marshal.GetDelegateForFunctionPointer(pGetTex, typeof(CSpeedTreeRT_GetTextures));
            var fnDestruct  = (CSpeedTreeRT_Destructor)Marshal.GetDelegateForFunctionPointer(pDestruct, typeof(CSpeedTreeRT_Destructor));
            var fnGeomConst = (SGeometry_Constructor)Marshal.GetDelegateForFunctionPointer(pGeomConst, typeof(SGeometry_Constructor));
            var fnTexConst  = (STextures_Constructor)Marshal.GetDelegateForFunctionPointer(pTexConst, typeof(STextures_Constructor));

            IntPtr pTree = Marshal.AllocHGlobal(4096);
            fnConstruct(pTree);

            Console.WriteLine("SptDumper: Loading tree " + sptPath);
            bool loaded = fnLoadTree(pTree, sptPath);
            if (!loaded)
            {
                Console.WriteLine("SptDumper: LoadTree failed for " + sptPath);
                fnDestruct(pTree);
                Marshal.FreeHGlobal(pTree);
                return;
            }

            Console.WriteLine("SptDumper: Computing geometry");
            fnCompute(pTree, IntPtr.Zero, 1, true);

            IntPtr pGeom = Marshal.AllocHGlobal(4096);
            fnGeomConst(pGeom);
            fnGetGeom(pTree, pGeom, 0xFFFF, -1, -1, -1);
            SGeometry sGeom = (SGeometry)Marshal.PtrToStructure(pGeom, typeof(SGeometry));

            IntPtr pTex = Marshal.AllocHGlobal(1024);
            fnTexConst(pTex);
            fnGetTex(pTree, pTex);
            STextures sTex = (STextures)Marshal.PtrToStructure(pTex, typeof(STextures));

            string branchTex = sTex.m_pBranchTextureFilename != IntPtr.Zero ? Marshal.PtrToStringAnsi(sTex.m_pBranchTextureFilename) : "branch_diffuse.dds";
            string frondTex  = sTex.m_uiFrondTextureCount > 0 && sTex.m_pFrondTextureFilenames != IntPtr.Zero ? Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(sTex.m_pFrondTextureFilenames, 0)) : "frond_diffuse.dds";
            string leafTex   = sTex.m_uiLeafTextureCount > 0 && sTex.m_pLeafTextureFilenames != IntPtr.Zero ? Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(sTex.m_pLeafTextureFilenames, 0)) : "leaf_diffuse.dds";

            Console.WriteLine("SptDumper: Textures: Branch=" + branchTex + ", Frond=" + frondTex + ", Leaf=" + leafTex);

            string mtlPath = Path.ChangeExtension(objPath, ".mtl");
            string mtlName = Path.GetFileName(mtlPath);

            using (var swMtl = new StreamWriter(mtlPath, false, Encoding.UTF8))
            {
                swMtl.WriteLine("newmtl " + Path.GetFileNameWithoutExtension(branchTex));
                swMtl.WriteLine("map_Kd " + Path.GetFileName(branchTex));
                swMtl.WriteLine();
                swMtl.WriteLine("newmtl " + Path.GetFileNameWithoutExtension(frondTex));
                swMtl.WriteLine("map_Kd " + Path.GetFileName(frondTex));
                swMtl.WriteLine();
                swMtl.WriteLine("newmtl " + Path.GetFileNameWithoutExtension(leafTex));
                swMtl.WriteLine("map_Kd " + Path.GetFileName(leafTex));
            }

            using (var swObj = new StreamWriter(objPath, false, Encoding.UTF8))
            {
                swObj.WriteLine("mtllib " + mtlName);
                int vOffset = 1;
                int vtOffset = 1;
                int vnOffset = 1;

                // 1. Branches
                if (sGeom.m_sBranches.m_usVertexCount > 0 && sGeom.m_sBranches.m_usNumStrips > 0)
                {
                    swObj.WriteLine("g " + Path.GetFileNameWithoutExtension(objPath) + "_branches");
                    swObj.WriteLine("usemtl " + Path.GetFileNameWithoutExtension(branchTex));
                    int vCount = sGeom.m_sBranches.m_usVertexCount;
                    for (int v = 0; v < vCount; v++)
                    {
                        float x = GetFloat(sGeom.m_sBranches.m_pCoords, v * 3 + 0);
                        float y = GetFloat(sGeom.m_sBranches.m_pCoords, v * 3 + 1);
                        float z = GetFloat(sGeom.m_sBranches.m_pCoords, v * 3 + 2);
                        swObj.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "v {0:F6} {1:F6} {2:F6}", x, z, y));
                    }
                    for (int v = 0; v < vCount; v++)
                    {
                        float u = GetFloat(sGeom.m_sBranches.m_pTexCoords0, v * 2 + 0);
                        float val = GetFloat(sGeom.m_sBranches.m_pTexCoords0, v * 2 + 1);
                        swObj.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "vt {0:F6} {1:F6}", u, 1f - val));
                    }
                    for (int v = 0; v < vCount; v++)
                    {
                        float nx = GetFloat(sGeom.m_sBranches.m_pNormals, v * 3 + 0);
                        float ny = GetFloat(sGeom.m_sBranches.m_pNormals, v * 3 + 1);
                        float nz = GetFloat(sGeom.m_sBranches.m_pNormals, v * 3 + 2);
                        swObj.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "vn {0:F6} {1:F6} {2:F6}", nx, nz, ny));
                    }

                    int nStrips = sGeom.m_sBranches.m_usNumStrips;
                    for (int s = 0; s < nStrips; s++)
                    {
                        ushort len = (ushort)Marshal.ReadInt16(sGeom.m_sBranches.m_pStripLengths, s * 2);
                        IntPtr pIndices = Marshal.ReadIntPtr(sGeom.m_sBranches.m_pStrips, s * IntPtr.Size);
                        for (int i = 0; i < len - 2; i++)
                        {
                            int i0 = (ushort)Marshal.ReadInt16(pIndices, i * 2);
                            int i1 = (ushort)Marshal.ReadInt16(pIndices, (i + 1) * 2);
                            int i2 = (ushort)Marshal.ReadInt16(pIndices, (i + 2) * 2);
                            if (i % 2 != 0) { int tmp = i1; i1 = i2; i2 = tmp; }
                            if (i0 != i1 && i1 != i2 && i2 != i0)
                            {
                                swObj.WriteLine(string.Format("f {0}/{1}/{2} {3}/{4}/{5} {6}/{7}/{8}", i0 + vOffset, i0 + vtOffset, i0 + vnOffset, i2 + vOffset, i2 + vtOffset, i2 + vnOffset, i1 + vOffset, i1 + vtOffset, i1 + vnOffset));
                            }
                        }
                    }
                    vOffset += vCount; vtOffset += vCount; vnOffset += vCount;
                }

                // 2. Fronds
                if (sGeom.m_sFronds.m_usVertexCount > 0 && sGeom.m_sFronds.m_usNumStrips > 0)
                {
                    swObj.WriteLine("g " + Path.GetFileNameWithoutExtension(objPath) + "_fronds");
                    swObj.WriteLine("usemtl " + Path.GetFileNameWithoutExtension(frondTex));
                    int vCount = sGeom.m_sFronds.m_usVertexCount;
                    for (int v = 0; v < vCount; v++)
                    {
                        float x = GetFloat(sGeom.m_sFronds.m_pCoords, v * 3 + 0);
                        float y = GetFloat(sGeom.m_sFronds.m_pCoords, v * 3 + 1);
                        float z = GetFloat(sGeom.m_sFronds.m_pCoords, v * 3 + 2);
                        swObj.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "v {0:F6} {1:F6} {2:F6}", x, z, y));
                    }
                    for (int v = 0; v < vCount; v++)
                    {
                        float u = GetFloat(sGeom.m_sFronds.m_pTexCoords0, v * 2 + 0);
                        float val = GetFloat(sGeom.m_sFronds.m_pTexCoords0, v * 2 + 1);
                        swObj.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "vt {0:F6} {1:F6}", u, 1f - val));
                    }
                    for (int v = 0; v < vCount; v++)
                    {
                        float nx = GetFloat(sGeom.m_sFronds.m_pNormals, v * 3 + 0);
                        float ny = GetFloat(sGeom.m_sFronds.m_pNormals, v * 3 + 1);
                        float nz = GetFloat(sGeom.m_sFronds.m_pNormals, v * 3 + 2);
                        swObj.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "vn {0:F6} {1:F6} {2:F6}", nx, nz, ny));
                    }

                    int nStrips = sGeom.m_sFronds.m_usNumStrips;
                    for (int s = 0; s < nStrips; s++)
                    {
                        ushort len = (ushort)Marshal.ReadInt16(sGeom.m_sFronds.m_pStripLengths, s * 2);
                        IntPtr pIndices = Marshal.ReadIntPtr(sGeom.m_sFronds.m_pStrips, s * IntPtr.Size);
                        for (int i = 0; i < len - 2; i++)
                        {
                            int i0 = (ushort)Marshal.ReadInt16(pIndices, i * 2);
                            int i1 = (ushort)Marshal.ReadInt16(pIndices, (i + 1) * 2);
                            int i2 = (ushort)Marshal.ReadInt16(pIndices, (i + 2) * 2);
                            if (i % 2 != 0) { int tmp = i1; i1 = i2; i2 = tmp; }
                            if (i0 != i1 && i1 != i2 && i2 != i0)
                            {
                                swObj.WriteLine(string.Format("f {0}/{1}/{2} {3}/{4}/{5} {6}/{7}/{8}", i0 + vOffset, i0 + vtOffset, i0 + vnOffset, i2 + vOffset, i2 + vtOffset, i2 + vnOffset, i1 + vOffset, i1 + vtOffset, i1 + vnOffset));
                            }
                        }
                    }
                    vOffset += vCount; vtOffset += vCount; vnOffset += vCount;
                }

                // 3. Leaves
                if (sGeom.m_sLeaves0.m_usLeafCount > 0)
                {
                    swObj.WriteLine("g " + Path.GetFileNameWithoutExtension(objPath) + "_leaves");
                    swObj.WriteLine("usemtl " + Path.GetFileNameWithoutExtension(leafTex));
                    int lCount = sGeom.m_sLeaves0.m_usLeafCount;
                    for (int l = 0; l < lCount; l++)
                    {
                        float cx = GetFloat(sGeom.m_sLeaves0.m_pCenterCoords, l * 3 + 0);
                        float cy = GetFloat(sGeom.m_sLeaves0.m_pCenterCoords, l * 3 + 1);
                        float cz = GetFloat(sGeom.m_sLeaves0.m_pCenterCoords, l * 3 + 2);

                        IntPtr pCoordsPtr = Marshal.ReadIntPtr(sGeom.m_sLeaves0.m_pLeafMapCoords, l * IntPtr.Size);
                        for (int c = 0; c < 4; c++)
                        {
                            float x = cx + GetFloat(pCoordsPtr, c * 4 + 0);
                            float y = cy + GetFloat(pCoordsPtr, c * 4 + 1);
                            float z = cz + GetFloat(pCoordsPtr, c * 4 + 2);
                            swObj.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "v {0:F6} {1:F6} {2:F6}", x, z, y));
                        }

                        IntPtr pTexPtr = Marshal.ReadIntPtr(sGeom.m_sLeaves0.m_pLeafMapTexCoords, l * IntPtr.Size);
                        for (int c = 0; c < 4; c++)
                        {
                            float u = GetFloat(pTexPtr, c * 2 + 0);
                            float val = GetFloat(pTexPtr, c * 2 + 1);
                            swObj.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "vt {0:F6} {1:F6}", u, 1f - val));
                        }

                        float nx = GetFloat(sGeom.m_sLeaves0.m_pNormals, l * 3 + 0);
                        float ny = GetFloat(sGeom.m_sLeaves0.m_pNormals, l * 3 + 1);
                        float nz = GetFloat(sGeom.m_sLeaves0.m_pNormals, l * 3 + 2);
                        swObj.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "vn {0:F6} {1:F6} {2:F6}", nx, nz, ny));
                        swObj.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "vn {0:F6} {1:F6} {2:F6}", nx, nz, ny));
                        swObj.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "vn {0:F6} {1:F6} {2:F6}", nx, nz, ny));
                        swObj.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "vn {0:F6} {1:F6} {2:F6}", nx, nz, ny));

                        int curV = vOffset + l * 4;
                        swObj.WriteLine(string.Format("f {0}/{0}/{0} {1}/{1}/{1} {2}/{2}/{2}", curV + 0, curV + 1, curV + 2));
                        swObj.WriteLine(string.Format("f {0}/{0}/{0} {1}/{1}/{1} {2}/{2}/{2}", curV + 0, curV + 2, curV + 3));
                    }
                    vOffset += lCount * 4; vtOffset += lCount * 4; vnOffset += lCount * 4;
                }
            }

            Console.WriteLine("SptDumper: Successfully saved OBJ to " + objPath);
            fnDestruct(pTree);
            Marshal.FreeHGlobal(pTree);
            Marshal.FreeHGlobal(pGeom);
            Marshal.FreeHGlobal(pTex);
        }
    }
}
