using System;
using System.Collections.Generic;

namespace ProjectMerger
{
    public static class MergeClassifier
    {
        public const int NearDuplicateThreshold = 6;

        public static MergePlan Build(
            List<AssetRecord> currentProject,
            List<AssetRecord> incomingProject,
            string incomingProjectRoot,
            string incomingProjectName)
        {
            var plan = new MergePlan
            {
                IncomingProjectRoot = incomingProjectRoot,
                IncomingProjectName = incomingProjectName,
                WrappedNamespace = "Imported_" + ScriptNamespaceWrapper.SanitizeNamespace(incomingProjectName),
            };

            var byGuid       = new Dictionary<string, AssetRecord>();
            var byPath       = new Dictionary<string, AssetRecord>();
            var byHash       = new Dictionary<string, AssetRecord>();
            var byNormHash   = new Dictionary<string, AssetRecord>();
            var byScriptType = new Dictionary<string, AssetRecord>();
            var textureHashes = new List<AssetRecord>();

            foreach (var c in currentProject)
            {
                if (!string.IsNullOrEmpty(c.Guid))         byGuid[c.Guid] = c;
                if (!string.IsNullOrEmpty(c.RelativePath)) byPath[c.RelativePath] = c;
                if (!string.IsNullOrEmpty(c.Md5) && !byHash.ContainsKey(c.Md5)) byHash[c.Md5] = c;
                if (!string.IsNullOrEmpty(c.NormalizedMd5) && !byNormHash.ContainsKey(c.NormalizedMd5))
                    byNormHash[c.NormalizedMd5] = c;
                if (c.Kind == AssetKind.Script && c.ScriptTypes != null)
                    foreach (var t in c.ScriptTypes)
                        if (!string.IsNullOrEmpty(t) && !byScriptType.ContainsKey(t))
                            byScriptType[t] = c;
                if (c.Kind == AssetKind.Texture && c.DHash != 0) textureHashes.Add(c);
            }

            foreach (var inc in incomingProject)
            {
                var entry = new MergeEntry
                {
                    Incoming = inc,
                    NearDuplicateHamming = -1,
                    Resolution = Resolution.Undecided
                };

                AssetRecord match;
                bool matchedGuid = !string.IsNullOrEmpty(inc.Guid) && byGuid.TryGetValue(inc.Guid, out match);
                if (matchedGuid)
                {
                    entry.CurrentMatch = byGuid[inc.Guid];
                    if (entry.CurrentMatch.Md5 == inc.Md5)
                    {
                        entry.Status = EntryStatus.Identical;
                        entry.Resolution = Resolution.KeepCurrent;
                    }
                    else
                    {
                        entry.Status = inc.UnderProjectSettings
                            ? EntryStatus.ProjectSettingConflict
                            : EntryStatus.ConflictByGuid;
                    }
                    plan.Entries.Add(entry);
                    continue;
                }

                if (!string.IsNullOrEmpty(inc.Md5) && byHash.TryGetValue(inc.Md5, out match))
                {
                    entry.CurrentMatch = match;
                    entry.Status = EntryStatus.RemapOnly;
                    entry.Resolution = Resolution.RemapGuid;
                    if (!string.IsNullOrEmpty(inc.Guid) && !string.IsNullOrEmpty(match.Guid) && inc.Guid != match.Guid)
                        plan.GuidRemap[inc.Guid] = match.Guid;
                    plan.Entries.Add(entry);
                    continue;
                }

                // Scripts: normalized-text match (ignores BOM, line endings, trailing whitespace).
                if (inc.Kind == AssetKind.Script &&
                    !string.IsNullOrEmpty(inc.NormalizedMd5) &&
                    byNormHash.TryGetValue(inc.NormalizedMd5, out match))
                {
                    entry.CurrentMatch = match;
                    entry.Status = EntryStatus.RemapOnly;
                    entry.Resolution = Resolution.RemapGuid;
                    entry.Note = "script identical after normalization (BOM/CRLF/whitespace)";
                    if (!string.IsNullOrEmpty(inc.Guid) && !string.IsNullOrEmpty(match.Guid) && inc.Guid != match.Guid)
                        plan.GuidRemap[inc.Guid] = match.Guid;
                    plan.Entries.Add(entry);
                    continue;
                }

                // Scripts: C# type name collision → would cause CS0101 "duplicate type" errors if copied as-is.
                if (inc.Kind == AssetKind.Script && inc.ScriptTypes != null)
                {
                    AssetRecord typeMatch = null;
                    string collidedType = null;
                    foreach (var t in inc.ScriptTypes)
                    {
                        if (byScriptType.TryGetValue(t, out typeMatch)) { collidedType = t; break; }
                    }
                    if (typeMatch != null)
                    {
                        entry.CurrentMatch = typeMatch;
                        entry.Status = EntryStatus.ConflictByPath;
                        entry.Resolution = Resolution.KeepCurrent; // safe default
                        entry.Note = "C# type collision: " + collidedType;
                        plan.Entries.Add(entry);
                        continue;
                    }
                }

                if (byPath.TryGetValue(inc.RelativePath, out match))
                {
                    entry.CurrentMatch = match;
                    entry.Status = inc.UnderProjectSettings
                        ? EntryStatus.ProjectSettingConflict
                        : EntryStatus.ConflictByPath;
                    plan.Entries.Add(entry);
                    continue;
                }

                if (inc.Kind == AssetKind.Texture && inc.DHash != 0)
                {
                    AssetRecord bestTex = null;
                    int bestDist = int.MaxValue;
                    foreach (var t in textureHashes)
                    {
                        int d = HashUtil.HammingDistance(inc.DHash, t.DHash);
                        if (d < bestDist) { bestDist = d; bestTex = t; }
                    }
                    if (bestTex != null && bestDist <= NearDuplicateThreshold)
                    {
                        entry.CurrentMatch = bestTex;
                        entry.Status = EntryStatus.NearDuplicate;
                        entry.NearDuplicateHamming = bestDist;
                        entry.Note = $"pHash distance {bestDist}";
                        plan.Entries.Add(entry);
                        continue;
                    }
                }

                entry.Status = EntryStatus.NewAsset;
                entry.Resolution = Resolution.ImportAsNew;
                plan.Entries.Add(entry);
            }

            // ── Always preserve imported-project GUIDs ──────────────────────
            // Scenes / prefabs / ScriptableObjects in the incoming project reference
            // their own assets by GUID. By default several classifier statuses would
            // leave the incoming asset NOT copied (or remap its GUID to the host's),
            // which makes those YAML references resolve to the host's version — or go
            // missing entirely. When the host's equivalent contains nested refs (e.g.
            // a prefab with MonoBehaviours), those refs still point at host scripts,
            // not the wrapped imported scripts the incoming project expects.
            //
            // (a) Identical assets under Assets/ → KeepBoth. MergeEngine's KeepBoth
            //     branch sees the GUID collision, issues a fresh GUID for the stage
            //     copy, and adds a remap. Incoming YAML refs get rewritten to the
            //     stage copy in Phase 3; the stage copy's own nested refs then also
            //     get rewritten via the same remap pass.
            //
            // (b) Scripts with RemapOnly / ConflictByPath / ConflictByGuid → KeepBoth
            //     with the incoming GUID preserved (MergeEngine issues a fresh one
            //     only when host literally shares the GUID).
            //
            // ProjectSettings / Packages files have fixed paths and their own merge
            // semantics (manifest.json is union-merged separately), so we leave them
            // alone here.
            bool promotedAssemblyCSharpScript = false;
            foreach (var e in plan.Entries)
            {
                // (a) Identical → KeepBoth so the incoming copy lands in the stage.
                if (e.Status == EntryStatus.Identical &&
                    !e.Incoming.UnderProjectSettings &&
                    !e.Incoming.UnderPackages &&
                    e.Incoming.Kind != AssetKind.PackageManifest &&
                    (e.Resolution == Resolution.KeepCurrent || e.Resolution == Resolution.Undecided))
                {
                    e.Resolution = Resolution.KeepBoth;
                    e.Note = AppendNote(e.Note, "auto: keep identical for ref integrity");

                    if (e.Incoming.Kind == AssetKind.Script &&
                        !e.Incoming.UnderAsmdef && !e.Incoming.UnderPackages)
                        promotedAssemblyCSharpScript = true;
                    continue;
                }

                // (b) Script entries where the default behaviour would strand the
                // incoming script's GUID (RemapOnly remaps to host; ConflictBy* is
                // Undecided by default so the incoming isn't copied at all).
                if (e.Incoming.Kind != AssetKind.Script) continue;
                if (e.Status != EntryStatus.RemapOnly &&
                    e.Status != EntryStatus.ConflictByPath &&
                    e.Status != EntryStatus.ConflictByGuid)
                    continue;
                if (e.Resolution == Resolution.ImportAsNew ||
                    e.Resolution == Resolution.KeepBoth ||
                    e.Resolution == Resolution.Overwrite)
                    continue;

                if (!string.IsNullOrEmpty(e.Incoming.Guid))
                    plan.GuidRemap.Remove(e.Incoming.Guid);
                e.Resolution = Resolution.KeepBoth;
                e.Note = AppendNote(e.Note, "auto: preserve incoming script GUID");

                if (!e.Incoming.UnderAsmdef && !e.Incoming.UnderPackages)
                    promotedAssemblyCSharpScript = true;
            }

            // Two copies of byte-identical Assembly-CSharp scripts in the same
            // assembly would trigger CS0101 "duplicate type". Enable wrap so the
            // stage copy gets a distinct namespace. Asmdef/Packages scripts are
            // already assembly-isolated, so they don't need wrapping.
            if (promotedAssemblyCSharpScript)
                plan.WrapImportedScripts = true;

            // If any script type collision was detected, pre-enable the auto-wrap option.
            // Users can toggle it off in the Review tab if they prefer manual resolution.
            if (plan.HasScriptTypeCollision())
                plan.WrapImportedScripts = true;

            // Wrap-side effect. When the wrap pass is going to run, EVERY wrap-eligible
            // Assembly-CSharp script must end up in the stage with the wrapped namespace,
            // otherwise references like `Imported_X.Tools.Foo` in other wrapped scripts
            // fail to resolve (the original lives in `Tools`, not `Imported_X.Tools`).
            //
            // NewAsset (ImportAsNew) and explicit KeepBoth already land in stage — fine.
            // Identical (KeepCurrent), RemapOnly (RemapGuid), ConflictByPath/ConflictByGuid
            // (Undecided or KeepCurrent default from type-collision detection) do not —
            // promote all of them to KeepBoth as the default (user can override in Resolve UI).
            if (plan.WrapImportedScripts)
            {
                foreach (var e in plan.Entries)
                {
                    if (e.Incoming.Kind != AssetKind.Script) continue;
                    if (e.Incoming.UnderAsmdef || e.Incoming.UnderPackages) continue;

                    bool alreadyLandsInStage =
                        e.Resolution == Resolution.ImportAsNew ||
                        e.Resolution == Resolution.KeepBoth;
                    if (alreadyLandsInStage) continue;

                    // RemapOnly queued a GUID remap in the earlier pass. Drop it so the
                    // stage copy keeps its own incoming GUID (and incoming YAML refs hit it).
                    if (e.Status == EntryStatus.RemapOnly && !string.IsNullOrEmpty(e.Incoming.Guid))
                        plan.GuidRemap.Remove(e.Incoming.Guid);

                    e.Resolution = Resolution.KeepBoth;
                    e.Note = AppendNote(e.Note, "auto: wrap-compat copy");
                }
            }

            // ── Asmdef name collisions ──────────────────────────────────────
            // Two .asmdef files with the same "name" can't coexist — Unity treats it as
            // the assembly name and bails out of compilation. Detection runs after the
            // main classifier so it can see the resolutions chosen so far and override
            // them only when needed. Matching is by name (not path/guid) because asmdefs
            // can sit in different folders and still collide.
            var currentAsmdefNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in currentProject)
            {
                if (c.Kind == AssetKind.Asmdef && !string.IsNullOrEmpty(c.AsmdefName))
                    currentAsmdefNames.Add(c.AsmdefName);
            }

            string asmdefPrefix = string.IsNullOrEmpty(plan.WrappedNamespace)
                ? "Imported_" + ScriptNamespaceWrapper.SanitizeNamespace(incomingProjectName)
                : plan.WrappedNamespace;

            foreach (var e in plan.Entries)
            {
                if (e.Incoming.Kind != AssetKind.Asmdef) continue;
                if (string.IsNullOrEmpty(e.Incoming.AsmdefName)) continue;
                if (!currentAsmdefNames.Contains(e.Incoming.AsmdefName)) continue;

                var renamed = asmdefPrefix + "." + e.Incoming.AsmdefName;
                plan.AsmdefNameRemap[e.Incoming.AsmdefName] = renamed;

                // The asmdef MUST land in stage so MergeEngine can rewrite its body.
                // KeepCurrent / RemapGuid / Undecided would leave it un-copied.
                if (e.Resolution == Resolution.KeepCurrent ||
                    e.Resolution == Resolution.RemapGuid ||
                    e.Resolution == Resolution.Undecided)
                {
                    e.Resolution = Resolution.KeepBoth;
                }
                e.Note = AppendNote(e.Note, "asmdef name collision → " + renamed);
            }

            // Renamed asmdef → its scripts get wrapped (so types under the assembly move
            // into the imported namespace too). Force-enable wrap; MergeEngine reads the
            // remap via plan.AsmdefNameRemap when deciding wrap candidates.
            if (plan.AsmdefNameRemap.Count > 0)
                plan.WrapImportedScripts = true;

            return plan;
        }

        static string AppendNote(string existing, string add)
        {
            if (string.IsNullOrEmpty(existing)) return "[" + add + "]";
            return existing + "  [" + add + "]";
        }
    }
}
