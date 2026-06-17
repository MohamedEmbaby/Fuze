using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ProjectMerger
{
    /// <summary>
    /// Rewrites Unity GUID references inside text/YAML assets and .meta files.
    /// Unity references appear as `guid: xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx` (32 hex chars).
    /// </summary>
    public static class GuidRemapper
    {
        static readonly Regex GuidRefRegex =
            new Regex(@"\b([0-9a-fA-F]{32})\b", RegexOptions.Compiled);

        static readonly HashSet<string> RewritableExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".prefab", ".unity", ".asset", ".mat", ".controller", ".anim", ".overrideController",
            ".mask", ".mixer", ".guiskin", ".physicMaterial", ".physicsMaterial2D",
            ".lighting", ".shadervariants", ".spriteatlas", ".spriteatlasv2",
            ".preset", ".terrainlayer", ".playable", ".signal", ".renderTexture",
            ".meta"
        };

        public static bool IsRewritable(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var ext = Path.GetExtension(path);
            return RewritableExt.Contains(ext) || path.Replace('\\','/').Contains("/ProjectSettings/");
        }

        public static int RewriteFile(string absolutePath, IReadOnlyDictionary<string, string> remap)
        {
            if (remap == null || remap.Count == 0) return 0;
            if (!File.Exists(absolutePath)) return 0;

            string text;
            try { text = File.ReadAllText(absolutePath, Encoding.UTF8); }
            catch { return 0; }

            int replacements = 0;
            var rewritten = GuidRefRegex.Replace(text, m =>
            {
                var key = m.Value.ToLowerInvariant();
                if (remap.TryGetValue(key, out var repl))
                {
                    replacements++;
                    return repl;
                }
                return m.Value;
            });

            if (replacements > 0)
            {
                try { File.WriteAllText(absolutePath, rewritten, new UTF8Encoding(false)); }
                catch { }
            }
            return replacements;
        }

        public static bool SetMetaGuid(string metaPath, string newGuid)
        {
            if (!File.Exists(metaPath) || string.IsNullOrEmpty(newGuid)) return false;
            string text;
            try { text = File.ReadAllText(metaPath); } catch { return false; }

            var updated = Regex.Replace(
                text,
                @"^guid:\s*[0-9a-fA-F]{32}",
                "guid: " + newGuid,
                RegexOptions.Multiline);

            if (updated == text) return false;
            try { File.WriteAllText(metaPath, updated, new UTF8Encoding(false)); }
            catch { return false; }
            return true;
        }
    }
}
