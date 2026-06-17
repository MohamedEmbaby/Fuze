using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ProjectMerger
{
    /// <summary>
    /// Exports the current Unity project (or a chosen subset) to either:
    ///   • a plain folder tree (consumable by the ProjectMerger importer), OR
    ///   • a .unitypackage (Unity-native, Assets/ only — Unity's package format
    ///     cannot represent Packages/ or ProjectSettings/).
    ///
    /// Respects an <see cref="ExclusionList"/>: any path that starts with an
    /// excluded entry is skipped. .meta files are treated as siblings of their
    /// parent asset — if the asset is excluded, its .meta is too.
    /// </summary>
    public static class ProjectExporter
    {
        public class Result
        {
            public int CopiedCount;
            public int SkippedCount;
            public long BytesWritten;
            public string Destination;
        }

        public static Result ExportFolder(
            string projectRoot,
            string destinationRoot,
            ExclusionList ex,
            Action<float, string> progress,
            List<string> log)
        {
            var r = new Result { Destination = destinationRoot };
            if (ex == null) ex = new ExclusionList();
            log = log ?? new List<string>();

            if (Directory.Exists(destinationRoot) && !IsDirEmpty(destinationRoot))
            {
                log.Add($"WARN   destination is not empty: {destinationRoot}");
            }
            Directory.CreateDirectory(destinationRoot);

            var scopes = new List<string>();
            if (ex.includeAssets)          scopes.Add("Assets");
            if (ex.includePackages)        scopes.Add("Packages");
            if (ex.includeProjectSettings) scopes.Add("ProjectSettings");

            if (scopes.Count == 0)
            {
                log.Add("ERROR  no scopes selected — nothing to export.");
                return r;
            }

            foreach (var scope in scopes)
            {
                var scopeAbs = Path.Combine(projectRoot, scope);
                if (!Directory.Exists(scopeAbs)) continue;

                var files = Directory.GetFiles(scopeAbs, "*.*", SearchOption.AllDirectories);
                int total = files.Length;
                for (int i = 0; i < total; i++)
                {
                    var abs = files[i];
                    var rel = MakeRelative(projectRoot, abs).Replace('\\', '/');

                    if (ex.IsExcluded(rel))
                    {
                        r.SkippedCount++;
                        continue;
                    }

                    var destAbs = Path.Combine(destinationRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                    var destDir = Path.GetDirectoryName(destAbs);
                    if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

                    try
                    {
                        File.Copy(abs, destAbs, overwrite: true);
                        r.CopiedCount++;
                        try { r.BytesWritten += new FileInfo(destAbs).Length; } catch { }
                    }
                    catch (Exception ex2)
                    {
                        log.Add($"ERROR  copy failed: {rel} → {ex2.Message}");
                    }

                    if (i % 25 == 0 && progress != null)
                        progress((float)i / Mathf.Max(1, total), scope + "/… " + Path.GetFileName(abs));
                }
            }

            log.Add($"FOLDER  {r.CopiedCount} files written, {r.SkippedCount} excluded, {r.BytesWritten / 1024} KB, -> {destinationRoot}");
            progress?.Invoke(1f, "Folder export complete");
            return r;
        }

        public static Result ExportUnityPackage(
            string destinationFile,
            ExclusionList ex,
            Action<float, string> progress,
            List<string> log)
        {
            var r = new Result { Destination = destinationFile };
            if (ex == null) ex = new ExclusionList();
            log = log ?? new List<string>();

            progress?.Invoke(0.05f, "Collecting asset paths…");
            var all = AssetDatabase.GetAllAssetPaths();
            var picked = new List<string>(all.Length);

            foreach (var p in all)
            {
                // .unitypackage only supports paths under Assets/.
                if (!p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) continue;
                if (ex.IsExcluded(p)) { r.SkippedCount++; continue; }
                picked.Add(p);
            }

            if (picked.Count == 0)
            {
                log.Add("ERROR  nothing to export after exclusions.");
                return r;
            }

            progress?.Invoke(0.25f, $"Exporting {picked.Count} assets to .unitypackage…");
            // Recurse so selecting a folder includes its children; no IncludeDependencies so
            // exclusions remain authoritative.
            AssetDatabase.ExportPackage(picked.ToArray(), destinationFile, ExportPackageOptions.Recurse);

            try { r.BytesWritten = new FileInfo(destinationFile).Length; } catch { }
            r.CopiedCount = picked.Count;
            log.Add($"PACKAGE {r.CopiedCount} assets, {r.SkippedCount} excluded, {r.BytesWritten / 1024} KB, -> {destinationFile}");
            if (ex.includePackages || ex.includeProjectSettings)
                log.Add("NOTE    .unitypackage can only contain Assets/… — Packages/ and ProjectSettings/ were not included. Use Folder export for those.");

            progress?.Invoke(1f, ".unitypackage export complete");
            return r;
        }

        static string MakeRelative(string root, string abs)
        {
            var r = root.Replace('\\', '/').TrimEnd('/') + "/";
            var a = abs.Replace('\\', '/');
            return a.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? a.Substring(r.Length) : a;
        }

        static bool IsDirEmpty(string dir)
        {
            try { return Directory.GetFileSystemEntries(dir).Length == 0; }
            catch { return false; }
        }
    }
}
