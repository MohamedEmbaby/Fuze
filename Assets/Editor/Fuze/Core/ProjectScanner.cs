using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ProjectMerger
{
    public class ProjectScanner
    {
        static readonly Regex MetaGuidRegex =
            new Regex(@"^guid:\s*([0-9a-fA-F]{32})", RegexOptions.Multiline);

        static readonly Regex NamespaceRegex =
            new Regex(@"\bnamespace\s+([A-Za-z_][\w\.]*)", RegexOptions.Compiled);

        static readonly Regex TypeDeclRegex =
            new Regex(@"\b(?:public\s+|internal\s+|private\s+|protected\s+|static\s+|sealed\s+|abstract\s+|partial\s+)*" +
                      @"(?:class|struct|interface|enum|record)\s+([A-Za-z_]\w*)",
                      RegexOptions.Compiled);

        // Very small JSON-ish matchers tailored for .asmdef / manifest.json — avoids a library dependency.
        static readonly Regex AsmdefNameRegex =
            new Regex("\"name\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
        static readonly Regex AsmdefRootNsRegex =
            new Regex("\"rootNamespace\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled);

        static readonly HashSet<string> TextYamlExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".prefab", ".unity", ".asset", ".mat", ".controller", ".anim", ".overrideController",
            ".mask", ".mixer", ".guiskin", ".physicMaterial", ".physicsMaterial2D",
            ".lighting", ".shadervariants", ".spriteatlas", ".spriteatlasv2",
            ".preset", ".terrainlayer", ".playable", ".signal", ".renderTexture"
        };

        static readonly HashSet<string> TextureExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".psd", ".gif", ".tif", ".tiff", ".exr", ".hdr"
        };

        class AsmdefInfo
        {
            public string DirAbs;          // absolute path of the folder that contains the .asmdef
            public string Name;
            public string RootNamespace;
        }

        public static List<AssetRecord> Scan(
            string projectRoot,
            bool includeProjectSettings,
            bool includePackages,
            bool enablePHash,
            Action<float, string> progress,
            ExclusionList exclusions = null)
        {
            var records = new List<AssetRecord>();
            if (string.IsNullOrEmpty(projectRoot) || !Directory.Exists(projectRoot))
                return records;

            var roots = new List<string>();
            var assetsDir = Path.Combine(projectRoot, "Assets");
            if (Directory.Exists(assetsDir)) roots.Add(assetsDir);
            if (includeProjectSettings)
            {
                var ps = Path.Combine(projectRoot, "ProjectSettings");
                if (Directory.Exists(ps)) roots.Add(ps);
            }
            if (includePackages)
            {
                var pk = Path.Combine(projectRoot, "Packages");
                if (Directory.Exists(pk)) roots.Add(pk);
            }

            // Walk with directory-level pruning instead of GetFiles(AllDirectories) + a
            // post-filter. Enumerating everything first means the merge tool's own stage
            // folder — which holds a full copy of every prior import and grows on each
            // iteration — gets pulled into memory on every scan even though nothing under
            // it is ever classified. Pruning whole subtrees at traversal time keeps the
            // scan's footprint bounded to the live project regardless of import count.
            var files = new List<string>();
            foreach (var root in roots)
                CollectFilesPruned(root, files, exclusions);

            bool IsExcluded(string abs)
            {
                if (exclusions == null) return false;
                var rel = MakeRelative(projectRoot, abs).Replace('\\', '/');
                return exclusions.IsExcluded(rel);
            }

            // Pre-pass: build a list of .asmdef folders so every .cs can know its owning assembly.
            var asmdefs = new List<AsmdefInfo>();
            foreach (var f in files)
            {
                if (!f.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsExcluded(f)) continue;
                try
                {
                    var text = File.ReadAllText(f);
                    var info = new AsmdefInfo
                    {
                        DirAbs = NormalizeDir(Path.GetDirectoryName(f)),
                        Name   = AsmdefNameRegex.Match(text).Groups[1].Value,
                        RootNamespace = AsmdefRootNsRegex.Match(text).Groups[1].Value
                    };
                    asmdefs.Add(info);
                }
                catch { }
            }

            int total = files.Count;
            for (int i = 0; i < total; i++)
            {
                var abs = files[i];
                if (progress != null && (i % 25 == 0))
                    progress((float)i / Mathf.Max(1, total), "Scanning " + Path.GetFileName(abs));

                if (abs.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;

                var rel = MakeRelative(projectRoot, abs);
                rel = rel.Replace('\\', '/');

                if (exclusions != null && exclusions.IsExcluded(rel)) continue;

                bool underPS = rel.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase);
                bool underPk = rel.StartsWith("Packages/",        StringComparison.OrdinalIgnoreCase);

                var rec = new AssetRecord
                {
                    RelativePath = rel,
                    AbsolutePath = abs,
                    UnderProjectSettings = underPS,
                    UnderPackages = underPk,
                    Kind = ClassifyKind(abs, underPS, underPk),
                };

                try
                {
                    var fi = new FileInfo(abs);
                    rec.Size = fi.Length;
                    rec.Md5  = HashUtil.Md5File(abs);
                }
                catch { continue; }

                rec.Guid = TryReadGuidFromMeta(abs);
                rec.IsTextYaml = TextYamlExt.Contains(Path.GetExtension(abs)) || underPS;

                var ownerAsmdef = FindOwningAsmdef(abs, asmdefs);
                if (ownerAsmdef != null)
                {
                    rec.UnderAsmdef  = true;
                    rec.AsmdefName   = ownerAsmdef.Name;
                    rec.AsmdefRootNs = ownerAsmdef.RootNamespace;
                }

                if (enablePHash && rec.Kind == AssetKind.Texture)
                    rec.DHash = HashUtil.DHashImage(abs);

                if (rec.Kind == AssetKind.Script)
                {
                    rec.NormalizedMd5 = HashUtil.Md5NormalizedText(abs);
                    ExtractScriptTypes(abs, rec.ScriptTypes);
                    // If a script has no explicit namespace but its asmdef declares a rootNamespace,
                    // Unity implicitly puts its types in that namespace — record it so the classifier
                    // can still detect type collisions.
                    if (rec.ScriptTypes.Count > 0 && !string.IsNullOrEmpty(rec.AsmdefRootNs))
                    {
                        var qualified = new List<string>();
                        foreach (var t in rec.ScriptTypes)
                            qualified.Add(t.Contains(".") ? t : rec.AsmdefRootNs + "." + t);
                        rec.ScriptTypes = qualified;
                    }
                }

                records.Add(rec);
            }

            progress?.Invoke(1f, "Scan complete");
            return records;
        }

        static AssetKind ClassifyKind(string abs, bool underPS, bool underPk)
        {
            if (underPS) return AssetKind.ProjectSetting;
            var ext = Path.GetExtension(abs).ToLowerInvariant();
            if (ext == ".asmdef") return AssetKind.Asmdef;
            if (underPk && abs.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                return AssetKind.PackageManifest;
            if (TextureExt.Contains(ext)) return AssetKind.Texture;
            switch (ext)
            {
                case ".cs":     return AssetKind.Script;
                case ".prefab": return AssetKind.Prefab;
                case ".unity":  return AssetKind.Scene;
                case ".mat":    return AssetKind.Material;
                case ".asset":  return AssetKind.ScriptableObject;
                case ".wav": case ".mp3": case ".ogg": case ".aif": case ".aiff": return AssetKind.Audio;
                case ".fbx": case ".obj": case ".blend": case ".dae": case ".3ds": return AssetKind.Model;
                case ".shader": case ".cginc": case ".hlsl": case ".shadergraph": return AssetKind.Shader;
                case ".anim": case ".controller": return AssetKind.Animation;
                case ".dll":    return AssetKind.Dll;
                default: return AssetKind.Other;
            }
        }

        static AsmdefInfo FindOwningAsmdef(string absPath, List<AsmdefInfo> asmdefs)
        {
            var dir = NormalizeDir(Path.GetDirectoryName(absPath));
            AsmdefInfo best = null;
            int bestLen = -1;
            foreach (var a in asmdefs)
            {
                if (dir.StartsWith(a.DirAbs, StringComparison.OrdinalIgnoreCase) && a.DirAbs.Length > bestLen)
                {
                    best = a;
                    bestLen = a.DirAbs.Length;
                }
            }
            return best;
        }

        static string NormalizeDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return "";
            return dir.Replace('\\', '/').TrimEnd('/') + "/";
        }

        static void ExtractScriptTypes(string csPath, List<string> into)
        {
            string text;
            try { text = File.ReadAllText(csPath); } catch { return; }

            text = Regex.Replace(text, @"//.*?$", "", RegexOptions.Multiline);
            text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);

            string ns = null;
            var nsMatch = NamespaceRegex.Match(text);
            if (nsMatch.Success) ns = nsMatch.Groups[1].Value;

            foreach (Match m in TypeDeclRegex.Matches(text))
            {
                var name = m.Groups[1].Value;
                into.Add(string.IsNullOrEmpty(ns) ? name : ns + "." + name);
            }
        }

        static string TryReadGuidFromMeta(string assetAbsPath)
        {
            var meta = assetAbsPath + ".meta";
            if (!File.Exists(meta)) return null;
            try
            {
                var text = File.ReadAllText(meta);
                var m = MetaGuidRegex.Match(text);
                return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
            }
            catch { return null; }
        }

        static string MakeRelative(string root, string abs)
        {
            var r = root.Replace('\\', '/').TrimEnd('/') + "/";
            var a = abs.Replace('\\', '/');
            return a.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? a.Substring(r.Length) : a;
        }

        /// <summary>
        /// Iterative file walk that prunes whole directory subtrees up front instead of
        /// enumerating everything and filtering paths afterwards. Two kinds of pruning:
        ///   • the merge tool's own stage folder (<see cref="MergeEngine.StageFolderName"/>)
        ///     is ALWAYS skipped — re-indexing a pile of already-imported projects that
        ///     grows on every iteration is the single biggest, and entirely avoidable,
        ///     memory cost of a repeated import session, and nothing under it is ever
        ///     classified anyway (cross-import GUID safety is handled separately by
        ///     MergeEngine's stage-GUID index);
        ///   • any directory whose name matches an <see cref="ExclusionList"/> entry —
        ///     Library/Temp/obj/Build/etc. never get walked into memory at all.
        /// File-name exclusions are still applied per-file by the caller.
        /// </summary>
        static void CollectFilesPruned(string root, List<string> into, ExclusionList exclusions)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;

            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var dir = pending.Pop();

                try { into.AddRange(Directory.GetFiles(dir)); }
                catch { }

                string[] subdirs;
                try { subdirs = Directory.GetDirectories(dir); }
                catch { continue; }

                foreach (var sub in subdirs)
                {
                    var name = Path.GetFileName(sub);
                    if (string.Equals(name, MergeEngine.StageFolderName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (exclusions != null && exclusions.IsExcluded(name))
                        continue;
                    pending.Push(sub);
                }
            }
        }
    }
}
