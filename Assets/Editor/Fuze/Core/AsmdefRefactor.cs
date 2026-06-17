using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ProjectMerger
{
    /// <summary>
    /// Minimal JSON rewrite for .asmdef files. Used by <see cref="MergeEngine"/> when an
    /// incoming asmdef's "name" collides with a host asmdef:
    ///   • the staged copy gets a unique "name" + a wrapped "rootNamespace";
    ///   • any other staged .asmdef whose "references" array names the renamed asmdef
    ///     by name (not by GUID) gets that entry rewritten so resolution survives.
    ///
    /// Stays string-based on purpose — Unity asmdefs are tiny, hand-edited JSON; pulling
    /// in JsonUtility would lose unknown fields and reformat the file on every save.
    /// </summary>
    public static class AsmdefRefactor
    {
        static readonly Regex NameRegex =
            new Regex("\"name\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled);

        static readonly Regex RootNamespaceRegex =
            new Regex("\"rootNamespace\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled);

        static readonly Regex ReferencesArrayRegex =
            new Regex("\"references\"\\s*:\\s*\\[(?<body>[^\\]]*)\\]",
                RegexOptions.Compiled | RegexOptions.Singleline);

        static readonly Regex StringLiteralRegex =
            new Regex("\"([^\"]*)\"", RegexOptions.Compiled);

        public static string ReadName(string asmdefAbsPath)
        {
            try { return NameRegex.Match(File.ReadAllText(asmdefAbsPath)).Groups[1].Value; }
            catch { return string.Empty; }
        }

        public static string ReadRootNamespace(string asmdefAbsPath)
        {
            try { return RootNamespaceRegex.Match(File.ReadAllText(asmdefAbsPath)).Groups[1].Value; }
            catch { return string.Empty; }
        }

        /// <summary>
        /// Rewrites a single .asmdef file in place. Returns true if any field changed.
        ///   <paramref name="newName"/>      — replaces "name" (skipped when null/empty).
        ///   <paramref name="newRootNs"/>    — replaces or inserts "rootNamespace" (skipped when null).
        ///                                     Pass empty string to clear the value but keep the key.
        ///   <paramref name="nameRemap"/>    — every entry in "references" whose value matches a key
        ///                                     is replaced with the mapped value. GUID:… entries are
        ///                                     left alone (their target's meta GUID didn't change).
        /// </summary>
        public static bool RewriteFile(
            string asmdefAbsPath,
            string newName,
            string newRootNs,
            IReadOnlyDictionary<string, string> nameRemap)
        {
            string text;
            try { text = File.ReadAllText(asmdefAbsPath, Encoding.UTF8); }
            catch { return false; }

            var updated = Rewrite(text, newName, newRootNs, nameRemap);
            if (updated == text) return false;

            try { File.WriteAllText(asmdefAbsPath, updated, new UTF8Encoding(false)); }
            catch { return false; }
            return true;
        }

        public static string Rewrite(
            string source,
            string newName,
            string newRootNs,
            IReadOnlyDictionary<string, string> nameRemap)
        {
            if (string.IsNullOrEmpty(source)) return source;

            if (!string.IsNullOrEmpty(newName))
            {
                var nameMatch = NameRegex.Match(source);
                if (nameMatch.Success)
                {
                    source = source.Substring(0, nameMatch.Index)
                           + "\"name\": \"" + newName + "\""
                           + source.Substring(nameMatch.Index + nameMatch.Length);
                }
            }

            if (newRootNs != null)
            {
                var rootMatch = RootNamespaceRegex.Match(source);
                if (rootMatch.Success)
                {
                    source = source.Substring(0, rootMatch.Index)
                           + "\"rootNamespace\": \"" + newRootNs + "\""
                           + source.Substring(rootMatch.Index + rootMatch.Length);
                }
                else
                {
                    // Insert right after "name" so the field appears in a predictable spot.
                    var nameMatch = NameRegex.Match(source);
                    if (nameMatch.Success)
                    {
                        int insertAt = nameMatch.Index + nameMatch.Length;
                        source = source.Substring(0, insertAt)
                               + ",\n    \"rootNamespace\": \"" + newRootNs + "\""
                               + source.Substring(insertAt);
                    }
                }
            }

            if (nameRemap != null && nameRemap.Count > 0)
            {
                source = ReferencesArrayRegex.Replace(source, m =>
                {
                    var body = m.Groups["body"].Value;
                    var rewrittenBody = StringLiteralRegex.Replace(body, sm =>
                    {
                        var v = sm.Groups[1].Value;
                        if (v.StartsWith("GUID:")) return sm.Value;
                        if (nameRemap.TryGetValue(v, out var renamed))
                            return "\"" + renamed + "\"";
                        return sm.Value;
                    });
                    return "\"references\": [" + rewrittenBody + "]";
                });
            }

            return source;
        }
    }
}
