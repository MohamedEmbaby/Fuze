using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ProjectMerger
{
    /// <summary>
    /// Predefined list of package IDs to skip when an incoming Packages/manifest.json is
    /// merged into the current project. Patterns support exact match (e.g. "com.unity.ads")
    /// and a trailing-asterisk prefix wildcard (e.g. "com.unity.*"). Saved as JSON for
    /// re-use across projects, alongside the asset-exclusion preset.
    /// </summary>
    [Serializable]
    public class ManifestExclusions
    {
        public int version = 1;
        public List<string> packages = new List<string>();

        public const string ProjectDefaultRelPath = "ProjectSettings/ProjectMergerManifestExclusions.json";

        public bool IsExcluded(string packageId)
        {
            if (string.IsNullOrEmpty(packageId)) return false;
            foreach (var raw in packages)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var p = raw.Trim();
                if (p.EndsWith("*"))
                {
                    var prefix = p.Substring(0, p.Length - 1);
                    if (prefix.Length == 0) return true;
                    if (packageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                }
                else if (string.Equals(p, packageId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public bool Contains(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return false;
            var t = pattern.Trim();
            foreach (var raw in packages)
                if (string.Equals((raw ?? "").Trim(), t, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        public void Add(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return;
            var t = pattern.Trim();
            if (!Contains(t)) packages.Add(t);
        }

        public void RemoveAt(int index)
        {
            if (index >= 0 && index < packages.Count) packages.RemoveAt(index);
        }

        public string ToJson() => JsonUtility.ToJson(this, true);

        public static ManifestExclusions FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return new ManifestExclusions();
            try
            {
                var parsed = JsonUtility.FromJson<ManifestExclusions>(json);
                if (parsed == null) return new ManifestExclusions();
                if (parsed.packages == null) parsed.packages = new List<string>();
                return parsed;
            }
            catch { return new ManifestExclusions(); }
        }

        public void SaveTo(string absPath)
        {
            var dir = Path.GetDirectoryName(absPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(absPath, ToJson());
        }

        public static ManifestExclusions LoadFrom(string absPath)
        {
            if (!File.Exists(absPath)) return new ManifestExclusions();
            try { return FromJson(File.ReadAllText(absPath)); }
            catch { return new ManifestExclusions(); }
        }
    }
}
