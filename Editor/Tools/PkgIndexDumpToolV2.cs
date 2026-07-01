using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// Put into any Assets/.../Editor folder.
// Menu: WoT Tools/Package Index Dump/Map + Shared Vegetation...
// Scans shared*.pkg and a selected map pkg (e.g. 10_hills.pkg), writes Assets/WoTPkgIndexReport_v2.txt.
public static class PkgIndexDumpToolV2
{
    [MenuItem("WoT Tools/Package Index Dump/Map + Shared Vegetation...")]
    public static void Dump()
    {
        string wot = EditorUtility.OpenFolderPanel("Select WoT 0.8.10 root folder", "C:/", "");
        if (string.IsNullOrEmpty(wot)) return;

        string packages = Path.Combine(wot, "res", "packages");
        if (!Directory.Exists(packages)) packages = Path.Combine(wot, "res");
        if (!Directory.Exists(packages))
        {
            EditorUtility.DisplayDialog("WoT package dump", "res/packages or res folder not found", "OK");
            return;
        }

        string mapPkg = EditorUtility.OpenFilePanel("Select map pkg too, e.g. 10_hills.pkg", packages, "pkg");

        var pkgFiles = Directory.GetFiles(packages, "*.pkg")
            .Where(p => Path.GetFileName(p).StartsWith("shared", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(p).IndexOf("content", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        Path.GetFileName(p).IndexOf("flora", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        Path.GetFileName(p).IndexOf("speedtree", StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
        if (!string.IsNullOrEmpty(mapPkg) && File.Exists(mapPkg)) pkgFiles.Insert(0, mapPkg);
        pkgFiles = pkgFiles.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();

        var sb = new StringBuilder(1024 * 1024);
        sb.AppendLine("WOT PACKAGE INDEX REPORT V2");
        sb.AppendLine("WoT: " + wot.Replace('\\', '/'));
        sb.AppendLine("Packages dir: " + packages.Replace('\\', '/'));
        sb.AppendLine("Packages scanned: " + pkgFiles.Count);
        sb.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine(new string('=', 120));

        foreach (string pkg in pkgFiles) DumpPackage(pkg, sb);

        string outPath = "Assets/WoTPkgIndexReport_v2.txt";
        File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
        AssetDatabase.Refresh();
        EditorUtility.DisplayDialog("WoT package dump", "Report written:\n" + outPath, "OK");
        EditorUtility.RevealInFinder(outPath);
    }

    private static void DumpPackage(string pkgPath, StringBuilder sb)
    {
        sb.AppendLine("PKG: " + Path.GetFileName(pkgPath));
        try
        {
            using (var zip = ZipFile.OpenRead(pkgPath))
            {
                var all = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).Where(n => !string.IsNullOrEmpty(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                sb.AppendLine("  entries=" + all.Count);

                var speedtree = all.Where(n => n.IndexOf("speedtree/", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                var flora = all.Where(n => n.IndexOf("flora/", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                var prims = all.Where(IsPrimitive).ToList();

                sb.AppendLine($"  speedtree entries={speedtree.Count}, flora entries={flora.Count}, primitives all={prims.Count}, flora primitives={flora.Count(IsPrimitive)}");

                sb.AppendLine("  SPEEDTREE all paths (first 600):");
                foreach (var n in speedtree.Take(600)) sb.AppendLine("    " + n);

                sb.AppendLine("  PRIMITIVES all paths containing vegetation-ish words (first 600):");
                string[] words = { "apple", "birch", "bush", "cedar", "fir", "flora", "grass", "grape", "linden", "maple", "olive", "poplar", "sedge", "shrub", "spruce", "tree", "wheat" };
                foreach (var n in prims.Where(n => words.Any(w => n.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)).Take(600))
                    sb.AppendLine("    " + n);

                sb.AppendLine("  FLORA all paths (first 600):");
                foreach (var n in flora.Take(600)) sb.AppendLine("    " + n);
            }
        }
        catch (Exception e) { sb.AppendLine("  ERROR: " + e.Message); }
        sb.AppendLine(new string('=', 120));
    }

    private static bool IsPrimitive(string n) => n.EndsWith(".primitives", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".primitives_processed", StringComparison.OrdinalIgnoreCase);
}
