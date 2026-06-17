using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ProjectMerger
{
    public enum ManifestEntryStatus
    {
        Identical,
        OnlyInCurrent,
        OnlyInIncoming,
        Conflict
    }

    public enum ManifestAction
    {
        Skip,
        Add,
        UseIncoming,
        RemoveCurrent
    }

    public class ManifestEntry
    {
        public string PackageId;
        public string CurrentVersion;
        public string IncomingVersion;
        public ManifestEntryStatus Status;
        public ManifestAction Action;
        public bool ExcludedByPreset;
    }

    public class ManifestMergePlan
    {
        public string CurrentManifestPath;
        public string IncomingManifestPath;
        public List<ManifestEntry> Entries = new List<ManifestEntry>();

        public int Count(ManifestEntryStatus s)
        {
            int n = 0;
            foreach (var e in Entries) if (e.Status == s) n++;
            return n;
        }
    }

    /// <summary>
    /// Parses Packages/manifest.json into a flat dictionary of {packageId: version}, builds a
    /// comparison plan against an incoming manifest (optionally honoring a predefined
    /// exclusion preset), and writes the result back into the original JSON's `dependencies`
    /// block. Other top-level fields like `scopedRegistries` are preserved verbatim — only
    /// the `dependencies` block is rewritten.
    /// </summary>
    public static class ManifestMerger
    {
        // The dependencies block is flat (`"id": "version"`), so a non-greedy `[^}]*` capture
        // is sufficient — there are no nested braces inside `dependencies`.
        static readonly Regex DepBlockRegex =
            new Regex("\"dependencies\"\\s*:\\s*\\{([^}]*)\\}", RegexOptions.Singleline);
        static readonly Regex DepEntryRegex =
            new Regex("\"([^\"]+)\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Singleline);

        public static Dictionary<string, string> ReadDeps(string manifestJson)
        {
            var deps = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(manifestJson)) return deps;
            var block = DepBlockRegex.Match(manifestJson);
            if (!block.Success) return deps;
            foreach (Match m in DepEntryRegex.Matches(block.Groups[1].Value))
                deps[m.Groups[1].Value] = m.Groups[2].Value;
            return deps;
        }

        public static Dictionary<string, string> ReadDepsFromFile(string absPath)
        {
            if (string.IsNullOrEmpty(absPath) || !File.Exists(absPath))
                return new Dictionary<string, string>(StringComparer.Ordinal);
            return ReadDeps(File.ReadAllText(absPath, Encoding.UTF8));
        }

        public static string WriteDeps(string originalJson, IDictionary<string, string> deps)
        {
            var sb = new StringBuilder();
            sb.Append("\"dependencies\": {");
            bool first = true;
            foreach (var kv in deps)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("\n    \"").Append(kv.Key).Append("\": \"").Append(kv.Value).Append("\"");
            }
            sb.Append("\n  }");

            if (string.IsNullOrEmpty(originalJson) || !DepBlockRegex.IsMatch(originalJson))
                return "{\n  " + sb + "\n}\n";
            return DepBlockRegex.Replace(originalJson, sb.ToString(), 1);
        }

        public static ManifestMergePlan BuildPlan(
            string currentManifestAbs,
            string incomingManifestAbs,
            ManifestExclusions exclusions)
        {
            var plan = new ManifestMergePlan
            {
                CurrentManifestPath  = currentManifestAbs,
                IncomingManifestPath = incomingManifestAbs
            };

            var current  = ReadDepsFromFile(currentManifestAbs);
            var incoming = ReadDepsFromFile(incomingManifestAbs);

            // Stable order: current entries in their original file order, then incoming-only
            // entries sorted alphabetically so the table doesn't shuffle between rebuilds.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in current)
            {
                seen.Add(kv.Key);
                var entry = new ManifestEntry
                {
                    PackageId       = kv.Key,
                    CurrentVersion  = kv.Value,
                    IncomingVersion = incoming.TryGetValue(kv.Key, out var iv) ? iv : null
                };
                if (entry.IncomingVersion == null)
                {
                    entry.Status = ManifestEntryStatus.OnlyInCurrent;
                    entry.Action = ManifestAction.Skip;
                }
                else if (entry.CurrentVersion == entry.IncomingVersion)
                {
                    entry.Status = ManifestEntryStatus.Identical;
                    entry.Action = ManifestAction.Skip;
                }
                else
                {
                    entry.Status = ManifestEntryStatus.Conflict;
                    entry.Action = ManifestAction.Skip;
                }
                entry.ExcludedByPreset = exclusions != null && exclusions.IsExcluded(entry.PackageId);
                plan.Entries.Add(entry);
            }

            var incomingOnly = new List<KeyValuePair<string, string>>();
            foreach (var kv in incoming)
                if (!seen.Contains(kv.Key)) incomingOnly.Add(kv);
            incomingOnly.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            foreach (var kv in incomingOnly)
            {
                bool excluded = exclusions != null && exclusions.IsExcluded(kv.Key);
                plan.Entries.Add(new ManifestEntry
                {
                    PackageId        = kv.Key,
                    CurrentVersion   = null,
                    IncomingVersion  = kv.Value,
                    Status           = ManifestEntryStatus.OnlyInIncoming,
                    Action           = excluded ? ManifestAction.Skip : ManifestAction.Add,
                    ExcludedByPreset = excluded
                });
            }

            return plan;
        }

        public static string ApplyToText(
            ManifestMergePlan plan,
            string currentJson,
            out int added, out int removed, out int replaced,
            List<string> log = null)
        {
            added = removed = replaced = 0;
            var deps = ReadDeps(currentJson);

            foreach (var e in plan.Entries)
            {
                switch (e.Action)
                {
                    case ManifestAction.Skip:
                        break;

                    case ManifestAction.Add:
                        if (!deps.ContainsKey(e.PackageId))
                        {
                            deps[e.PackageId] = e.IncomingVersion;
                            added++;
                            log?.Add($"MANIFEST  add     {e.PackageId} {e.IncomingVersion}");
                        }
                        break;

                    case ManifestAction.UseIncoming:
                        if (deps.TryGetValue(e.PackageId, out var existing) && existing != e.IncomingVersion)
                        {
                            log?.Add($"MANIFEST  replace {e.PackageId} {existing} -> {e.IncomingVersion}");
                            deps[e.PackageId] = e.IncomingVersion;
                            replaced++;
                        }
                        break;

                    case ManifestAction.RemoveCurrent:
                        if (deps.Remove(e.PackageId))
                        {
                            removed++;
                            log?.Add($"MANIFEST  remove  {e.PackageId}");
                        }
                        break;
                }
            }

            return WriteDeps(currentJson, deps);
        }

        /// <summary>
        /// Read the current manifest, apply the plan, and write back. Returns the total
        /// number of changes (added + removed + replaced).
        /// </summary>
        public static int ApplyAndSave(ManifestMergePlan plan, List<string> log = null)
        {
            var current = File.Exists(plan.CurrentManifestPath)
                ? File.ReadAllText(plan.CurrentManifestPath, Encoding.UTF8)
                : "";
            var merged = ApplyToText(plan, current, out int added, out int removed, out int replaced, log);
            var dir = Path.GetDirectoryName(plan.CurrentManifestPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(plan.CurrentManifestPath, merged, new UTF8Encoding(false));
            return added + removed + replaced;
        }

        /// <summary>
        /// Convenience for the existing MergeEngine flow: union incoming deps into the current
        /// manifest, skipping anything matched by <paramref name="exclusions"/>. Conflicts keep
        /// the current version (logged). Returns the count of newly added entries.
        /// </summary>
        public static int UnionInto(
            string currentManifestPath,
            string incomingManifestPath,
            ManifestExclusions exclusions,
            List<string> log)
        {
            if (!File.Exists(currentManifestPath))
            {
                var dir = Path.GetDirectoryName(currentManifestPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (exclusions == null || !HasAnyExclusion(exclusions))
                {
                    File.Copy(incomingManifestPath, currentManifestPath, overwrite: true);
                    return 0;
                }
                // Filter on copy.
                var incomingOnly = ReadDepsFromFile(incomingManifestPath);
                var filtered = new Dictionary<string, string>(StringComparer.Ordinal);
                int skipped = 0;
                foreach (var kv in incomingOnly)
                {
                    if (exclusions.IsExcluded(kv.Key)) { skipped++; log?.Add($"MANIFEST  skip(preset) {kv.Key}"); continue; }
                    filtered[kv.Key] = kv.Value;
                }
                File.WriteAllText(currentManifestPath,
                    WriteDeps(File.ReadAllText(incomingManifestPath, Encoding.UTF8), filtered),
                    new UTF8Encoding(false));
                if (skipped > 0) log?.Add($"MANIFEST  preset filtered {skipped} package(s) on initial copy");
                return filtered.Count;
            }

            var currentText  = File.ReadAllText(currentManifestPath,  Encoding.UTF8);
            var incomingText = File.ReadAllText(incomingManifestPath, Encoding.UTF8);
            var currentDeps  = ReadDeps(currentText);
            var incomingDeps = ReadDeps(incomingText);

            int added = 0, presetSkipped = 0;
            foreach (var kv in incomingDeps)
            {
                if (exclusions != null && exclusions.IsExcluded(kv.Key))
                {
                    presetSkipped++;
                    log?.Add($"MANIFEST  skip(preset) {kv.Key}");
                    continue;
                }
                if (!currentDeps.ContainsKey(kv.Key))
                {
                    currentDeps[kv.Key] = kv.Value;
                    added++;
                }
                else if (currentDeps[kv.Key] != kv.Value)
                {
                    log?.Add($"MANIFEST  conflict {kv.Key}: keeping current {currentDeps[kv.Key]} (incoming {kv.Value})");
                }
            }

            if (presetSkipped > 0) log?.Add($"MANIFEST  preset filtered {presetSkipped} incoming package(s)");
            File.WriteAllText(currentManifestPath, WriteDeps(currentText, currentDeps), new UTF8Encoding(false));
            return added;
        }

        static bool HasAnyExclusion(ManifestExclusions e)
        {
            if (e == null || e.packages == null) return false;
            foreach (var s in e.packages)
                if (!string.IsNullOrWhiteSpace(s)) return true;
            return false;
        }
    }
}
