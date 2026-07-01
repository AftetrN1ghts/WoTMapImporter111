using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// Put into any Assets/.../Editor folder.
// Menu: WoT Tools/Package Index Dump/Shared Content Flora+SpeedTree...
// It writes Assets/WoTPkgIndexReport.txt. Send/paste that text report.
public static class PkgIndexDumpTool
{
    [MenuItem("WoT Tools/Package Index Dump/Shared Content Flora+SpeedTree...")]
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

        var pkgFiles = Directory.GetFiles(packages, "*.pkg")
            .Where(p => Path.GetFileName(p).StartsWith("shared", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(p).IndexOf("content", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        Path.GetFileName(p).IndexOf("flora", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        Path.GetFileName(p).IndexOf("speedtree", StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var sb = new StringBuilder(1024 * 512);
        sb.AppendLine("WOT PACKAGE INDEX REPORT");
        sb.AppendLine("WoT: " + wot.Replace('\\', '/'));
        sb.AppendLine("Packages dir: " + packages.Replace('\\', '/'));
        sb.AppendLine("Packages scanned: " + pkgFiles.Length);
        sb.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine(new string('=', 120));

        foreach (string pkg in pkgFiles)
        {
            DumpPackage(pkg, sb);
        }

        string outPath = "Assets/WoTPkgIndexReport.txt";
        File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
        AssetDatabase.Refresh();
        EditorUtility.DisplayDialog("WoT package dump", "Report written:\n" + outPath + "\n\nPaste/send this text report.", "OK");
        EditorUtility.RevealInFinder(outPath);
    }

    private static void DumpPackage(string pkgPath, StringBuilder sb)
    {
        sb.AppendLine("PKG: " + Path.GetFileName(pkgPath));
        try
        {
            using (var zip = ZipFile.OpenRead(pkgPath))
            {
                var all = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).Where(n => !string.IsNullOrEmpty(n)).ToList();
                sb.AppendLine("  entries=" + all.Count);

                var flora = all.Where(n => n.IndexOf("flora/", StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                var floraPrims = flora.Where(IsPrimitive).ToList();
                var floraDds = flora.Where(IsTexture).ToList();
                var speedtree = all.Where(n => n.IndexOf("speedtree/", StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                var speedtree10Hills = speedtree.Where(n => n.IndexOf("10_hills", StringComparison.OrdinalIgnoreCase) >= 0).ToList();

                sb.AppendLine($"  flora entries={flora.Count}, flora primitives={floraPrims.Count}, flora textures={floraDds.Count}");
                sb.AppendLine($"  speedtree entries={speedtree.Count}, speedtree/10_hills entries={speedtree10Hills.Count}");

                sb.AppendLine("  FLORA primitive folders (first 300):");
                foreach (var g in floraPrims.GroupBy(ParentFolder).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Take(300))
                {
                    sb.AppendLine($"    {g.Key}  files={g.Count()}");
                    foreach (var n in g.Take(5)) sb.AppendLine("      " + n);
                }

                sb.AppendLine("  FLORA primitive paths containing common tree words:");
                string[] words = { "apple", "birch", "bush", "cedar", "fir", "grape", "linden", "maple", "olive", "poplar", "sedge", "shrub", "spruce", "tree" };
                foreach (string n in floraPrims.Where(n => words.Any(w => n.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)).Take(500))
                    sb.AppendLine("    " + n);

                sb.AppendLine("  SPEEDTREE/10_hills paths (first 300):");
                foreach (string n in speedtree10Hills.Take(300)) sb.AppendLine("    " + n);

                sb.AppendLine("  SPEEDTREE paths containing common tree words (first 300):");
                foreach (string n in speedtree.Where(n => words.Any(w => n.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)).Take(300))
                    sb.AppendLine("    " + n);
            }
        }
        catch (Exception e)
        {
            sb.AppendLine("  ERROR: " + e.Message);
        }
        sb.AppendLine(new string('=', 120));
    }

    private static bool IsPrimitive(string n)
    {
        return n.EndsWith(".primitives", StringComparison.OrdinalIgnoreCase) ||
               n.EndsWith(".primitives_processed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTexture(string n)
    {
        return n.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ||
               n.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) ||
               n.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
    }

    private static string ParentFolder(string n)
    {
        n = n.Replace('\\', '/');
        int slash = n.LastIndexOf('/');
        if (slash <= 0) return "";
        return n.Substring(0, slash);
    }
}
