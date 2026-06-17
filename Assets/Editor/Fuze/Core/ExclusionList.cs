using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ProjectMerger
{
    /// <summary>
    /// List of folder/file NAMES to exclude when exporting or scanning, plus scope toggles
    /// (Assets/ProjectSettings/Packages). Serializable as JSON for re-use across projects.
    ///
    /// Matching is by NAME, not path: each entry matches a file when ANY segment of the
    /// project-relative path equals the entry (case-insensitive). For example, the entry
    /// "Library" excludes both "Library/foo" and "Assets/Plugins/Library/bar". File-name
    /// entries like "Foo.png" match any file with that exact basename anywhere in the tree.
    ///
    /// If an entry is given as a multi-segment path (e.g. "Assets/_Imported"), only the
    /// final segment is used — the rest is dropped on match. Use plain names for clarity.
    /// </summary>
    [Serializable]
    public class ExclusionList
    {
        public int version = 1;
        public bool includeAssets          = true;
        public bool includePackages        = true;
        public bool includeProjectSettings = true;

        public List<string> exclusions = new List<string>();

        public const string ProjectDefaultRelPath = "ProjectSettings/ProjectMergerExclusions.json";

        /// <summary>Preset path used by the importer (Fuze main window) — lives next to the
        /// exporter's preset but is a separate file so import filters can differ from export filters
        /// when both tools are used in the same project.</summary>
        public const string ImportDefaultRelPath = "ProjectSettings/ProjectMergerImportExclusions.json";

        public static readonly string[] BuiltInDefaults =
        {
            "Library",
            "Temp",
            "Logs",
            "obj",
            "Build",
            "Builds",
            "UserSettings",
            ".git",
            ".vs",
            ".idea",
            "_Imported",
        };

        public void SeedBuiltInDefaults()
        {
            foreach (var d in BuiltInDefaults)
                if (!ContainsNormalized(d)) exclusions.Add(d);
        }

        public bool ContainsNormalized(string path)
        {
            var n = Normalize(path);
            foreach (var e in exclusions)
                if (string.Equals(Normalize(e), n, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        public void Add(string path)
        {
            // Stored entries are leaf names — keeps the UI tidy and avoids duplicate semantics
            // between "Foo" and "Bar/Foo" (both match the same files under name-based matching).
            var n = LeafName(path);
            if (string.IsNullOrEmpty(n)) return;
            if (!ContainsNormalized(n)) exclusions.Add(n);
        }

        public void RemoveAt(int i)
        {
            if (i >= 0 && i < exclusions.Count) exclusions.RemoveAt(i);
        }

        /// <summary>True if any segment of <paramref name="projectRelativePath"/> matches an
        /// exclusion entry by name (case-insensitive).</summary>
        public bool IsExcluded(string projectRelativePath)
        {
            if (string.IsNullOrEmpty(projectRelativePath)) return false;
            var segments = projectRelativePath.Replace('\\', '/').Split('/');
            foreach (var raw in exclusions)
            {
                var name = LeafName(raw);
                if (string.IsNullOrEmpty(name)) continue;
                foreach (var seg in segments)
                {
                    if (string.Equals(seg, name, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        /// <summary>Strip directory components from a stored entry — entries are matched by
        /// NAME, so "Assets/_Imported" and "_Imported" behave identically.</summary>
        public static string LeafName(string entry)
        {
            var n = Normalize(entry);
            if (string.IsNullOrEmpty(n)) return string.Empty;
            int slash = n.LastIndexOf('/');
            return slash < 0 ? n : n.Substring(slash + 1);
        }

        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            return path.Replace('\\', '/').Trim().TrimStart('/').TrimEnd('/');
        }

        // ── JSON ─────────────────────────────────────────────────────

        public string ToJson() => JsonUtility.ToJson(this, true);

        public static ExclusionList FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return new ExclusionList();
            try
            {
                var parsed = JsonUtility.FromJson<ExclusionList>(json);
                if (parsed == null) return new ExclusionList();
                if (parsed.exclusions == null) parsed.exclusions = new List<string>();
                return parsed;
            }
            catch
            {
                return new ExclusionList();
            }
        }

        public void SaveTo(string absPath)
        {
            var dir = Path.GetDirectoryName(absPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(absPath, ToJson());
        }

        public static ExclusionList LoadFrom(string absPath)
        {
            if (!File.Exists(absPath)) return new ExclusionList();
            try { return FromJson(File.ReadAllText(absPath)); }
            catch { return new ExclusionList(); }
        }
    }
}
