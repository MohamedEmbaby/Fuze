using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ProjectMerger
{
    /// <summary>
    /// Executes a MergePlan:
    ///   1. Stage          — copy each approved asset to its destination
    ///                       (Assets/_Imported/&lt;Name&gt;/, ProjectSettings/, or Packages/).
    ///   2. Wrap / rename  — for .cs files that need it, rename their existing namespace
    ///                       or insert a new namespace block. Scripts under Packages/ are
    ///                       always skipped (already isolated); scripts under an .asmdef
    ///                       are skipped UNLESS that asmdef itself is being renamed to
    ///                       resolve a name collision — those scripts still need wrapping
    ///                       so their types move under the imported namespace too.
    ///   3. Refactor refs  — rewrite `using X;` and `X.Foo` across all wrapped .cs.
    ///   4. Rewrite asmdef — rename collided "name"+"rootNamespace" in staged .asmdef
    ///                       files and update name-style entries in every staged asmdef's
    ///                       "references" array.
    ///   5. GUID remap     — rewrite cross-asset GUID refs inside copied YAML/text assets.
    ///   6. Type qualifiers — rewrite assembly-qualified type names in YAML (UnityEvent
    ///                       m_TargetAssemblyTypeName / m_ObjectArgumentAssemblyTypeName,
    ///                       SerializeReference type descriptors) so scenes' method binds
    ///                       and enum int args still resolve after a wrap or asmdef rename.
    ///   7. manifest.json  — union dependencies from incoming into current.
    /// </summary>
    public static class MergeEngine
    {
        /// <summary>Leaf folder name under Assets/ that holds every staged import.</summary>
        public const string StageFolderName = "_Imported";
        public const string StageRoot       = "Assets/" + StageFolderName;

        public static void Execute(
            MergePlan plan,
            bool dryRun,
            Action<float, string> progress,
            List<string> log)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            log = log ?? new List<string>();

            var currentRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var stageRel    = $"{StageRoot}/{SanitizeFolderName(plan.IncomingProjectName)}";
            var stageAbs    = Path.Combine(currentRoot, stageRel.Replace('/', Path.DirectorySeparatorChar));

            if (!dryRun) Directory.CreateDirectory(stageAbs);

            var copiedFiles       = new List<string>();     // every file written (for GUID-remap pass)
            var wrapCandidates    = new List<string>();     // .cs files eligible for namespace wrapping
            var copiedAsmdefs     = new List<string>();     // .asmdef files written into stage (for rewrite pass)
            AssetRecord incomingManifest = null;            // Packages/manifest.json
            int total = plan.Entries.Count;

            // Snapshot every GUID already claimed under Assets/_Imported/ before this merge
            // started. The host-side scan typically excludes _Imported (the import filter
            // default), so the classifier never sees that a prior merge already owns some
            // GUID — an incoming asset that happens to share a GUID with a previously
            // imported project (very common when both forks descend from the same starting
            // state) falls through to NewAsset, preserves its GUID, and lands as a SECOND
            // file at the same GUID. Unity then binds every scene ref to whichever import
            // got there first, so the newly imported project's scenes start pointing at the
            // PREVIOUS import's scripts.
            //
            // This index is a cheap last line of defense in the engine: on each ImportAsNew /
            // KeepBoth copy we check it and, if the incoming GUID already lives somewhere
            // else under _Imported/, mint a fresh GUID + add a remap so YAML refs across
            // the new stage all retarget onto the new copy.
            var stageGuidIndex = BuildStageGuidIndex(currentRoot);

            for (int i = 0; i < total; i++)
            {
                var e = plan.Entries[i];
                if (progress != null && (i % 10 == 0))
                    progress((float)i / Mathf.Max(1, total), (dryRun ? "[Dry] " : "") + e.Incoming.RelativePath);

                // manifest.json is merged specially at the end; skip regular copy semantics.
                if (e.Incoming.Kind == AssetKind.PackageManifest)
                {
                    if (e.Resolution != Resolution.KeepCurrent) incomingManifest = e.Incoming;
                    log.Add($"MANIFEST queued for merge: {e.Incoming.RelativePath}");
                    continue;
                }

                switch (e.Resolution)
                {
                    case Resolution.KeepCurrent:
                        log.Add($"SKIP   {e.Status}  {e.Incoming.RelativePath}");
                        break;

                    case Resolution.RemapGuid:
                        log.Add($"REMAP  guid {e.Incoming.Guid} -> {e.CurrentMatch?.Guid}  ({e.Incoming.RelativePath})");
                        break;

                    case Resolution.ImportAsNew:
                    {
                        var dest = DestinationFor(e.Incoming, stageAbs, currentRoot);
                        if (!dryRun)
                        {
                            CopyWithMeta(e.Incoming, dest, copiedFiles, wrapCandidates, copiedAsmdefs, plan.AsmdefNameRemap, log);
                            if (TryResolveStageGuidCollision(plan, e.Incoming, dest, stageGuidIndex, currentRoot, out var freshNew, out var collidedNew))
                            {
                                log.Add($"NEW    {RelOf(currentRoot, dest)}  (stage-guid collision w/ {collidedNew} → fresh {freshNew})");
                                break;
                            }
                        }
                        log.Add($"NEW    {RelOf(currentRoot, dest)}");
                        break;
                    }

                    case Resolution.KeepBoth:
                    {
                        // Stage path is unique within a single merge run (each incoming file has a
                        // unique relative path under the project's stage subfolder), so we copy
                        // straight into the computed destination. Re-runs of the same merge land
                        // on the same path and idempotently overwrite — UniquifyName used to spawn
                        // `_imported1`, `_imported2`, … on every re-run, which both cluttered the
                        // tree AND, because the wrap pass renamed each duplicate to the same
                        // wrapped type, produced CS0101 "duplicate type" errors that blanked
                        // scene script refs out as Missing.
                        //
                        // To keep prior YAML rewrites stable across re-runs, we also reuse the
                        // existing stage .meta's GUID (when present) instead of minting a fresh
                        // one — otherwise every re-run would invalidate any GUID remap a previous
                        // merge had baked into still-living stage scenes/prefabs.
                        var dest = DestinationFor(e.Incoming, stageAbs, currentRoot);
                        if (!dryRun)
                        {
                            string priorStageGuid = ReadMetaGuid(dest + ".meta");

                            CopyWithMeta(e.Incoming, dest, copiedFiles, wrapCandidates, copiedAsmdefs, plan.AsmdefNameRemap, log);

                            // A fresh GUID is only needed when the incoming GUID would literally
                            // collide with an existing asset's GUID. That's true for Identical
                            // (by definition — same GUID) and ConflictByGuid (same GUID, different
                            // content). RemapOnly has a *different* GUID from the current match,
                            // so no collision — we keep the incoming GUID so incoming YAML refs
                            // point at the new stage copy.
                            bool guidCollision =
                                e.CurrentMatch != null &&
                                !string.IsNullOrEmpty(e.Incoming.Guid) &&
                                !string.IsNullOrEmpty(e.CurrentMatch.Guid) &&
                                string.Equals(e.Incoming.Guid, e.CurrentMatch.Guid, StringComparison.OrdinalIgnoreCase);
                            if (guidCollision)
                            {
                                var fresh = !string.IsNullOrEmpty(priorStageGuid) &&
                                            !string.Equals(priorStageGuid, e.Incoming.Guid, StringComparison.OrdinalIgnoreCase)
                                    ? priorStageGuid
                                    : NewGuid32();
                                if (GuidRemapper.SetMetaGuid(dest + ".meta", fresh))
                                {
                                    plan.GuidRemap[e.Incoming.Guid] = fresh;
                                    stageGuidIndex[fresh] = dest + ".meta";
                                    log.Add($"BOTH   {RelOf(currentRoot, dest)}  ({(string.IsNullOrEmpty(priorStageGuid) ? "new" : "kept")} guid {fresh})");
                                    break;
                                }
                            }

                            // Host-side collision didn't trigger — but the incoming GUID may
                            // still clash with an asset that a previous merge left under
                            // _Imported/. See the comment on stageGuidIndex above.
                            if (TryResolveStageGuidCollision(plan, e.Incoming, dest, stageGuidIndex, currentRoot, out var freshBoth, out var collidedBoth))
                            {
                                log.Add($"BOTH   {RelOf(currentRoot, dest)}  (stage-guid collision w/ {collidedBoth} → fresh {freshBoth})");
                                break;
                            }
                        }
                        log.Add($"BOTH   {RelOf(currentRoot, dest)}");
                        break;
                    }

                    case Resolution.Overwrite:
                    {
                        if (e.CurrentMatch == null)
                        {
                            log.Add($"WARN   Overwrite requested but no CurrentMatch for {e.Incoming.RelativePath}");
                            break;
                        }
                        var dest = e.CurrentMatch.AbsolutePath;
                        if (!dryRun) CopyWithMeta(e.Incoming, dest, copiedFiles, wrapCandidates, copiedAsmdefs, plan.AsmdefNameRemap, log);
                        log.Add($"OVER   {e.CurrentMatch.RelativePath}");
                        break;
                    }

                    case Resolution.Undecided:
                        log.Add($"UNDEC  {e.Status}  {e.Incoming.RelativePath}  (left as-is)");
                        break;
                }
            }

            // Phase 2a — wrap / rename namespaces on eligible scripts only.
            var discoveredRoots = new HashSet<string>();
            var discoveredTopLevelTypes = new HashSet<string>();
            if (!dryRun && plan.WrapImportedScripts && !string.IsNullOrEmpty(plan.WrappedNamespace) && wrapCandidates.Count > 0)
            {
                progress?.Invoke(0.85f, "Wrapping / renaming namespaces under " + plan.WrappedNamespace + "…");
                int wrappedNew = 0, renamedExisting = 0;
                foreach (var cs in wrapCandidates)
                {
                    if (WrapScriptFile(cs, plan.WrappedNamespace, discoveredRoots, discoveredTopLevelTypes, out bool hadNs, log))
                    {
                        if (hadNs) renamedExisting++; else wrappedNew++;
                    }
                }
                log.Add($"NAMESPACES  wrapped {wrappedNew} / renamed {renamedExisting} under {plan.WrappedNamespace}  (roots: {discoveredRoots.Count}, top-level types: {discoveredTopLevelTypes.Count})");

                // Phase 2b — rewrite references, but ONLY in the files we actually wrapped.
                // Asmdef-isolated scripts and files under Packages/ kept their original namespace,
                // so rewriting their references would point them at a type that doesn't exist in
                // their assembly's dependency graph.
                if (discoveredRoots.Count > 0 || discoveredTopLevelTypes.Count > 0)
                {
                    progress?.Invoke(0.90f, $"Refactoring {discoveredRoots.Count} namespace root(s) and {discoveredTopLevelTypes.Count} top-level type(s)…");
                    int refactored = 0;
                    foreach (var cs in wrapCandidates)
                    {
                        if (RewriteReferencesInFile(cs, discoveredRoots, discoveredTopLevelTypes, plan.WrappedNamespace, log))
                            refactored++;
                    }
                    log.Add($"REFACTOR    {refactored} wrapped file(s) had prefixed references  (roots: {string.Join(", ", discoveredRoots)} | types: {string.Join(", ", discoveredTopLevelTypes)})");
                }
            }

            // Phase 2c — rewrite staged .asmdef files. Two reasons we touch them:
            //   1. Renamed asmdefs (entries in plan.AsmdefNameRemap) need their "name" +
            //      "rootNamespace" updated so the assembly compiles under a unique name
            //      and future scripts pick up the wrapped namespace by default.
            //   2. Every staged asmdef — renamed or not — may reference a renamed asmdef
            //      by NAME (the older "FooAsm" form, not "GUID:..."). Those entries in the
            //      "references" array must be rewritten too, otherwise the assembly fails
            //      to resolve the dependency.
            if (!dryRun && copiedAsmdefs.Count > 0 && plan.AsmdefNameRemap.Count > 0)
            {
                progress?.Invoke(0.92f, "Rewriting .asmdef files…");
                int renamedFiles = 0, refUpdatedFiles = 0;
                foreach (var asmdefAbs in copiedAsmdefs)
                {
                    var originalName = AsmdefRefactor.ReadName(asmdefAbs);
                    string rewriteName   = null;
                    string rewriteRootNs = null;
                    bool willRename = false;
                    if (!string.IsNullOrEmpty(originalName) &&
                        plan.AsmdefNameRemap.TryGetValue(originalName, out var renamed))
                    {
                        willRename    = true;
                        rewriteName   = renamed;
                        // Mirror rootNamespace onto the new name so newly-created scripts
                        // in this asmdef inherit the wrapped namespace by default.
                        rewriteRootNs = renamed;
                    }

                    if (AsmdefRefactor.RewriteFile(asmdefAbs, rewriteName, rewriteRootNs, plan.AsmdefNameRemap))
                    {
                        if (willRename) renamedFiles++;
                        else            refUpdatedFiles++;
                        log.Add(willRename
                            ? $"ASMDEF rename {originalName} → {rewriteName}  ({RelOf(currentRoot, asmdefAbs)})"
                            : $"ASMDEF refs   updated  ({RelOf(currentRoot, asmdefAbs)})");
                    }
                }
                log.Add($"ASMDEF  renamed {renamedFiles} / updated references in {refUpdatedFiles} (remap entries: {plan.AsmdefNameRemap.Count})");
            }

            // Phase 3 — cross-asset GUID remap.
            if (!dryRun && plan.GuidRemap.Count > 0 && copiedFiles.Count > 0)
            {
                progress?.Invoke(0.94f, "Rewriting GUID references…");
                int rewrittenFiles = 0, totalReplacements = 0;
                foreach (var f in copiedFiles)
                {
                    if (!GuidRemapper.IsRewritable(f)) continue;
                    var n = GuidRemapper.RewriteFile(f, plan.GuidRemap);
                    if (n > 0) { rewrittenFiles++; totalReplacements += n; }
                }
                log.Add($"REMAPPED {totalReplacements} guid references across {rewrittenFiles} files");
            }

            // Phase 3b — assembly-qualified type names embedded in YAML (UnityEvent
            // m_TargetAssemblyTypeName / m_ObjectArgumentAssemblyTypeName + SerializeReference
            // type descriptors). These are NOT GUID-keyed, so Phase 3 missed them: when wrap
            // moves a script's namespace from `MyNs` to `Imported_X.MyNs` (or asmdef rename
            // shifts its assembly), every scene/prefab UnityEvent that named the type by
            // string keeps the stale qualifier and Unity drops the binding — m_MethodName
            // shows blank in the inspector and m_IntArgument enum args bind to nothing.
            bool needsTypeRefactor =
                !dryRun && copiedFiles.Count > 0 &&
                ((discoveredRoots.Count + discoveredTopLevelTypes.Count) > 0 ||
                 plan.AsmdefNameRemap.Count > 0);
            if (needsTypeRefactor)
            {
                progress?.Invoke(0.96f, "Rewriting assembly-qualified type names…");
                int touchedFiles = 0, totalChanges = 0;
                foreach (var f in copiedFiles)
                {
                    if (!GuidRemapper.IsRewritable(f)) continue;
                    var n = YamlTypeRefactor.RewriteFile(
                        f,
                        plan.WrappedNamespace,
                        discoveredRoots,
                        discoveredTopLevelTypes,
                        plan.AsmdefNameRemap);
                    if (n > 0) { touchedFiles++; totalChanges += n; }
                }
                log.Add($"TYPENAMES rewrote {totalChanges} qualifier(s) across {touchedFiles} files (UnityEvent + SerializeReference)");
            }

            // Phase 4 — Packages/manifest.json: union dependencies, honoring the predefined
            // removal preset so a package can be excluded once and stay excluded across imports.
            if (!dryRun && incomingManifest != null)
            {
                progress?.Invoke(0.97f, "Merging Packages/manifest.json…");
                var targetManifest = Path.Combine(currentRoot, "Packages", "manifest.json");
                try
                {
                    var exclusionsPath = Path.Combine(currentRoot, ManifestExclusions.ProjectDefaultRelPath);
                    var exclusions = File.Exists(exclusionsPath) ? ManifestExclusions.LoadFrom(exclusionsPath) : null;
                    int added = ManifestMerger.UnionInto(targetManifest, incomingManifest.AbsolutePath, exclusions, log);
                    log.Add($"MANIFEST  merged, {added} new dependency entries");
                }
                catch (Exception ex)
                {
                    log.Add($"ERROR  manifest merge failed: {ex.Message}");
                }
            }

            if (!dryRun) AssetDatabase.Refresh();
            progress?.Invoke(1f, dryRun ? "Dry run complete" : "Merge complete");
        }

        // ── Namespace wrap / refactor ────────────────────────────────────────
        static bool WrapScriptFile(
            string csAbsPath,
            string wrapperNs,
            HashSet<string> rootsOut,
            HashSet<string> topLevelTypesOut,
            out bool hadNamespace,
            List<string> log)
        {
            hadNamespace = false;
            try
            {
                var src = File.ReadAllText(csAbsPath, Encoding.UTF8);
                var result = ScriptNamespaceWrapper.Wrap(src, wrapperNs);
                hadNamespace = result.HadNamespace;
                if (result.Roots != null)
                    foreach (var r in result.Roots) rootsOut.Add(r);
                // Top-level types from namespace-less files are now under `wrapperNs`, so
                // short-name references to them in OUTSIDE a type body (e.g. `using static X;`)
                // need prefixing too. Tracked separately from namespace roots so we don't
                // rewrite field types, locals, or like-named variables inside class bodies.
                if (result.TopLevelTypes != null)
                    foreach (var t in result.TopLevelTypes) topLevelTypesOut.Add(t);

                if (result.Source == src) return false;
                File.WriteAllText(csAbsPath, result.Source, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                log.Add($"ERROR  wrap failed for {csAbsPath}: {ex.Message}");
                return false;
            }
        }

        static bool RewriteReferencesInFile(
            string csAbsPath,
            HashSet<string> roots,
            HashSet<string> topLevelTypes,
            string prefix,
            List<string> log)
        {
            try
            {
                var src = File.ReadAllText(csAbsPath, Encoding.UTF8);
                var updated = ScriptNamespaceWrapper.RewriteReferences(src, roots, topLevelTypes, prefix);
                if (updated == src) return false;
                File.WriteAllText(csAbsPath, updated, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                log.Add($"ERROR  reference rewrite failed for {csAbsPath}: {ex.Message}");
                return false;
            }
        }

        // ── Destination & copy helpers ──────────────────────────────────────
        static string DestinationFor(AssetRecord inc, string stageAbs, string currentRoot)
        {
            // ProjectSettings mirror into ProjectSettings/.
            if (inc.UnderProjectSettings)
                return Path.Combine(currentRoot, inc.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            // Packages mirror into Packages/ (embedded packages are preserved by path).
            if (inc.UnderPackages)
                return Path.Combine(currentRoot, inc.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            // Assets/… go under stage folder, mirroring the source tree under Assets/.
            string rel = inc.RelativePath;
            if (rel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring("Assets/".Length);

            return Path.Combine(stageAbs, rel.Replace('/', Path.DirectorySeparatorChar));
        }

        static void CopyWithMeta(
            AssetRecord inc,
            string destAbs,
            List<string> copiedFiles,
            List<string> wrapCandidates,
            List<string> copiedAsmdefs,
            IReadOnlyDictionary<string, string> asmdefNameRemap,
            List<string> log)
        {
            try
            {
                var destDir = Path.GetDirectoryName(destAbs);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                File.Copy(inc.AbsolutePath, destAbs, overwrite: true);
                copiedFiles.Add(destAbs);

                // A script gets the namespace wrapper if (a) it lives in Assembly-CSharp
                // (no .asmdef, not under Packages/), or (b) its owning asmdef is being
                // renamed to dodge a name collision — its types must move under the
                // wrapper namespace so they don't ambiguity-clash with same-named host
                // types when both assemblies are referenced.
                if (inc.Kind == AssetKind.Script && !inc.UnderPackages)
                {
                    bool underRemappedAsmdef =
                        inc.UnderAsmdef &&
                        !string.IsNullOrEmpty(inc.AsmdefName) &&
                        asmdefNameRemap != null &&
                        asmdefNameRemap.ContainsKey(inc.AsmdefName);
                    if (!inc.UnderAsmdef || underRemappedAsmdef)
                        wrapCandidates.Add(destAbs);
                }

                if (inc.Kind == AssetKind.Asmdef)
                    copiedAsmdefs.Add(destAbs);

                var srcMeta = inc.AbsolutePath + ".meta";
                if (File.Exists(srcMeta))
                {
                    var destMeta = destAbs + ".meta";
                    File.Copy(srcMeta, destMeta, overwrite: true);
                    copiedFiles.Add(destMeta);
                }
            }
            catch (Exception ex)
            {
                log.Add($"ERROR  copy failed {inc.AbsolutePath} -> {destAbs}: {ex.Message}");
            }
        }

        static readonly System.Text.RegularExpressions.Regex MetaGuidRegex =
            new System.Text.RegularExpressions.Regex(
                @"^guid:\s*([0-9a-fA-F]{32})",
                System.Text.RegularExpressions.RegexOptions.Multiline);

        static string ReadMetaGuid(string metaAbsPath)
        {
            if (string.IsNullOrEmpty(metaAbsPath) || !File.Exists(metaAbsPath)) return null;
            try
            {
                var text = File.ReadAllText(metaAbsPath);
                var m = MetaGuidRegex.Match(text);
                return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Walks every .meta under Assets/_Imported/ once and indexes guid → owning meta path.
        /// When the index has multiple metas for the same guid (corrupt prior state), the
        /// first one encountered wins — the index only needs to PROVE a collision exists.
        /// </summary>
        static Dictionary<string, string> BuildStageGuidIndex(string currentRoot)
        {
            var idx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var stageRootAbs = Path.Combine(currentRoot, StageRoot.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(stageRootAbs)) return idx;
            try
            {
                foreach (var meta in Directory.EnumerateFiles(stageRootAbs, "*.meta", SearchOption.AllDirectories))
                {
                    var g = ReadMetaGuid(meta);
                    if (!string.IsNullOrEmpty(g) && !idx.ContainsKey(g))
                        idx[g] = meta;
                }
            }
            catch { }
            return idx;
        }

        /// <summary>
        /// If <paramref name="inc"/>'s incoming GUID is already claimed by a meta file
        /// elsewhere under _Imported/ (i.e. a prior merge already owns this GUID), mint
        /// a fresh GUID for the just-copied dest, update its meta, register the remap on
        /// <paramref name="plan"/>, and seed the index with the new guid so a later entry
        /// in the same merge can't collide with it either. Returns true when a fresh GUID
        /// was issued.
        /// </summary>
        static bool TryResolveStageGuidCollision(
            MergePlan plan,
            AssetRecord inc,
            string destAbs,
            Dictionary<string, string> stageGuidIndex,
            string currentRoot,
            out string freshGuid,
            out string collidingRelPath)
        {
            freshGuid = null;
            collidingRelPath = null;
            if (inc == null || string.IsNullOrEmpty(inc.Guid)) return false;

            if (!stageGuidIndex.TryGetValue(inc.Guid, out var collidingMeta)) return false;

            var destMeta = destAbs + ".meta";
            // The colliding meta IS our dest — that's not a collision, it's the prior
            // run's copy of the same asset (re-run idempotency). Leave the GUID alone.
            if (string.Equals(collidingMeta, destMeta, StringComparison.OrdinalIgnoreCase))
                return false;

            var fresh = NewGuid32();
            if (!GuidRemapper.SetMetaGuid(destMeta, fresh)) return false;

            plan.GuidRemap[inc.Guid] = fresh;
            // Subsequent files in this merge see the fresh guid as taken so we never
            // hand the same fresh value to two different stage files in one run.
            stageGuidIndex[fresh] = destMeta;

            freshGuid = fresh;
            collidingRelPath = RelOf(currentRoot, collidingMeta);
            return true;
        }

        static string RelOf(string root, string abs)
        {
            var r = root.Replace('\\','/').TrimEnd('/') + "/";
            var a = abs.Replace('\\','/');
            return a.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? a.Substring(r.Length) : a;
        }

        static string NewGuid32() => Guid.NewGuid().ToString("N").ToLowerInvariant();

        static string SanitizeFolderName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Imported";
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
