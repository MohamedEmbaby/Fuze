using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ProjectMerger
{
    /// <summary>
    /// Standalone tool: import a Packages/manifest.json from another Unity project, compare
    /// dependency-for-dependency against the current project, and merge selectively. A
    /// reusable "predefined removals" preset filters packages out of the import (skipping
    /// them by default) — saved next to the asset-exclusion preset so it carries between
    /// projects.
    /// </summary>
    public class PackageManifestMergerWindow : EditorWindow
    {
        // ── Source ─────────────────────────────────────────────────────
        string _incomingManifestPath = "";

        // ── Predefined removals ────────────────────────────────────────
        ManifestExclusions _exclusions;
        string _newExclusion = "";
        Vector2 _exclusionScroll;

        // ── Plan ───────────────────────────────────────────────────────
        ManifestMergePlan _plan;
        Vector2 _planScroll;
        string _filter = "";
        bool _showIdentical    = false;
        bool _showOnlyCurrent  = true;
        bool _showOnlyIncoming = true;
        bool _showConflict     = true;

        // ── Log ────────────────────────────────────────────────────────
        List<string> _log = new List<string>();
        Vector2 _logScroll;

        const float RowHeight = 22f;

        [MenuItem("Tools/Fuze/Merge Package Manifest…")]
        public static PackageManifestMergerWindow ShowWindow()
        {
            var w = GetWindow<PackageManifestMergerWindow>("Fuze · Manifest");
            w.minSize = new Vector2(820, 560);
            w.Show();
            return w;
        }

        /// <summary>Pre-load the incoming manifest from a Unity project root and run Compare.
        /// Used by the main Fuze window's Source step so the user doesn't have to
        /// re-pick the same project.</summary>
        public void SetIncomingFromProjectRoot(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot)) return;
            var candidate = Path.Combine(projectRoot, "Packages", "manifest.json");
            if (!File.Exists(candidate))
            {
                EditorUtility.DisplayDialog("Fuze · Manifest",
                    $"No Packages/manifest.json under {projectRoot}.", "OK");
                return;
            }
            _incomingManifestPath = candidate;
            BuildPlan();
        }

        void OnEnable()
        {
            var defaultPath = Path.Combine(ProjectRoot(), ManifestExclusions.ProjectDefaultRelPath);
            _exclusions = File.Exists(defaultPath)
                ? ManifestExclusions.LoadFrom(defaultPath)
                : new ManifestExclusions();
        }

        void OnGUI()
        {
            DrawHeader();
            EditorGUILayout.Space();
            DrawSourceSection();
            EditorGUILayout.Space();
            DrawExclusionsSection();
            EditorGUILayout.Space();
            DrawPlanSection();
            EditorGUILayout.Space();
            DrawLogSection();
            GUILayout.FlexibleSpace();
            DrawApplyBar();
        }

        // ─────────────────────────────────────────────────────────────
        void DrawHeader()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Fuze · Manifest Merger", EditorStyles.boldLabel, GUILayout.Width(220));
                GUILayout.FlexibleSpace();
                GUILayout.Label("Current: " + Path.GetFileName(ProjectRoot()), EditorStyles.miniLabel);
            }
            EditorGUILayout.HelpBox(
                "Import an incoming Packages/manifest.json, compare dependencies, and pick what gets " +
                "added/replaced/removed in this project's manifest. Predefined-removal entries below " +
                "skip those packages on import (and let you remove them from the current manifest in one click).",
                MessageType.Info);
        }

        // ─────────────────────────────────────────────────────────────
        void DrawSourceSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Incoming manifest", EditorStyles.boldLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Path", GUILayout.Width(40));
                    _incomingManifestPath = EditorGUILayout.TextField(_incomingManifestPath);

                    if (GUILayout.Button("Manifest…", GUILayout.Width(90)))
                    {
                        var picked = EditorUtility.OpenFilePanel("Select Packages/manifest.json",
                            string.IsNullOrEmpty(_incomingManifestPath) ? "" : Path.GetDirectoryName(_incomingManifestPath),
                            "json");
                        if (!string.IsNullOrEmpty(picked)) _incomingManifestPath = picked;
                    }
                    if (GUILayout.Button("Project…", GUILayout.Width(90)))
                    {
                        var picked = EditorUtility.OpenFolderPanel("Select Unity project folder", "", "");
                        if (!string.IsNullOrEmpty(picked))
                        {
                            var candidate = Path.Combine(picked, "Packages", "manifest.json");
                            if (File.Exists(candidate)) _incomingManifestPath = candidate;
                            else EditorUtility.DisplayDialog("Fuze · Manifest",
                                "No Packages/manifest.json under that folder.", "OK");
                        }
                    }
                }

                bool exists = !string.IsNullOrEmpty(_incomingManifestPath) && File.Exists(_incomingManifestPath);
                if (!string.IsNullOrEmpty(_incomingManifestPath) && !exists)
                    EditorGUILayout.HelpBox("File not found.", MessageType.Warning);

                EditorGUILayout.Space();
                using (new EditorGUI.DisabledScope(!exists))
                {
                    if (GUILayout.Button("Compare against current project →", GUILayout.Height(24)))
                        BuildPlan();
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        void DrawExclusionsSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Predefined removals (skip these packages on import)", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    "Patterns: exact id (com.unity.ads) or trailing-* prefix (com.unity.*). " +
                    "Matched packages default to Skip on Add and can be Remove'd from current.",
                    EditorStyles.miniLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    _newExclusion = EditorGUILayout.TextField(_newExclusion);
                    using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_newExclusion)))
                    {
                        if (GUILayout.Button("Add", GUILayout.Width(60)))
                        {
                            _exclusions.Add(_newExclusion);
                            _newExclusion = "";
                            ReapplyExclusionsToPlan();
                        }
                    }
                }

                _exclusionScroll = EditorGUILayout.BeginScrollView(_exclusionScroll, GUILayout.Height(96));
                if (_exclusions.packages.Count == 0)
                    EditorGUILayout.LabelField("(no predefined removals)", EditorStyles.miniLabel);
                else
                {
                    for (int i = 0; i < _exclusions.packages.Count; i++)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            var updated = EditorGUILayout.TextField(_exclusions.packages[i]);
                            if (updated != _exclusions.packages[i])
                            {
                                _exclusions.packages[i] = updated;
                                ReapplyExclusionsToPlan();
                            }
                            if (GUILayout.Button("✕", GUILayout.Width(28)))
                            {
                                _exclusions.RemoveAt(i);
                                ReapplyExclusionsToPlan();
                                break;
                            }
                        }
                    }
                }
                EditorGUILayout.EndScrollView();

                using (new EditorGUILayout.HorizontalScope())
                {
                    var defaultPath = Path.Combine(ProjectRoot(), ManifestExclusions.ProjectDefaultRelPath);
                    if (GUILayout.Button("Save to project default", GUILayout.Width(180)))
                    {
                        _exclusions.SaveTo(defaultPath);
                        _log.Add($"PRESET  saved to {ManifestExclusions.ProjectDefaultRelPath}");
                    }
                    if (GUILayout.Button("Load project default", GUILayout.Width(160)))
                    {
                        _exclusions = ManifestExclusions.LoadFrom(defaultPath);
                        ReapplyExclusionsToPlan();
                    }
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Save preset…", GUILayout.Width(110)))
                    {
                        var p = EditorUtility.SaveFilePanel("Save manifest exclusion preset", "",
                            "ManifestExclusions", "json");
                        if (!string.IsNullOrEmpty(p)) _exclusions.SaveTo(p);
                    }
                    if (GUILayout.Button("Load preset…", GUILayout.Width(110)))
                    {
                        var p = EditorUtility.OpenFilePanel("Load manifest exclusion preset", "", "json");
                        if (!string.IsNullOrEmpty(p))
                        {
                            _exclusions = ManifestExclusions.LoadFrom(p);
                            ReapplyExclusionsToPlan();
                        }
                    }
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        void DrawPlanSection()
        {
            if (_plan == null)
            {
                EditorGUILayout.HelpBox("Pick an incoming manifest and press Compare to build the plan.", MessageType.None);
                return;
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Comparison", EditorStyles.boldLabel);

                using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                {
                    GUILayout.Label("Filter:", EditorStyles.miniLabel, GUILayout.Width(40));
                    _filter = EditorGUILayout.TextField(_filter, EditorStyles.toolbarSearchField, GUILayout.Width(220));
                    _showIdentical    = GUILayout.Toggle(_showIdentical,    "Identical",    EditorStyles.toolbarButton);
                    _showOnlyCurrent  = GUILayout.Toggle(_showOnlyCurrent,  "OnlyCurrent",  EditorStyles.toolbarButton);
                    _showOnlyIncoming = GUILayout.Toggle(_showOnlyIncoming, "OnlyIncoming", EditorStyles.toolbarButton);
                    _showConflict     = GUILayout.Toggle(_showConflict,     "Conflict",     EditorStyles.toolbarButton);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Add all incoming",     EditorStyles.toolbarButton)) BulkSet(ManifestEntryStatus.OnlyInIncoming, ManifestAction.Add);
                    if (GUILayout.Button("Skip all incoming",    EditorStyles.toolbarButton)) BulkSet(ManifestEntryStatus.OnlyInIncoming, ManifestAction.Skip);
                    if (GUILayout.Button("Use incoming versions",EditorStyles.toolbarButton)) BulkSet(ManifestEntryStatus.Conflict,       ManifestAction.UseIncoming);
                    if (GUILayout.Button("Remove preset matches",EditorStyles.toolbarButton)) RemovePresetMatchesInCurrent();
                }

                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField($"Identical: {_plan.Count(ManifestEntryStatus.Identical)}", GUILayout.Width(120));
                    EditorGUILayout.LabelField($"OnlyCurrent: {_plan.Count(ManifestEntryStatus.OnlyInCurrent)}", GUILayout.Width(140));
                    EditorGUILayout.LabelField($"OnlyIncoming: {_plan.Count(ManifestEntryStatus.OnlyInIncoming)}", GUILayout.Width(150));
                    EditorGUILayout.LabelField($"Conflict: {_plan.Count(ManifestEntryStatus.Conflict)}", GUILayout.Width(110));
                    GUILayout.FlexibleSpace();
                }

                DrawColumnHeader();
                _planScroll = EditorGUILayout.BeginScrollView(_planScroll, GUILayout.MinHeight(260));
                int shown = 0;
                foreach (var e in _plan.Entries)
                {
                    if (!PassesFilter(e)) continue;
                    DrawEntryRow(e);
                    shown++;
                }
                if (shown == 0)
                    EditorGUILayout.LabelField("No entries match the current filters.", EditorStyles.miniLabel);
                EditorGUILayout.EndScrollView();
            }
        }

        bool PassesFilter(ManifestEntry e)
        {
            switch (e.Status)
            {
                case ManifestEntryStatus.Identical:      if (!_showIdentical)    return false; break;
                case ManifestEntryStatus.OnlyInCurrent:  if (!_showOnlyCurrent)  return false; break;
                case ManifestEntryStatus.OnlyInIncoming: if (!_showOnlyIncoming) return false; break;
                case ManifestEntryStatus.Conflict:       if (!_showConflict)     return false; break;
            }
            if (!string.IsNullOrEmpty(_filter) &&
                e.PackageId.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            return true;
        }

        void DrawColumnHeader()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Status",   EditorStyles.miniBoldLabel, GUILayout.Width(110));
                GUILayout.Label("Package",  EditorStyles.miniBoldLabel, GUILayout.Width(280));
                GUILayout.Label("Current",  EditorStyles.miniBoldLabel, GUILayout.Width(140));
                GUILayout.Label("Incoming", EditorStyles.miniBoldLabel, GUILayout.Width(140));
                GUILayout.Label("Action",   EditorStyles.miniBoldLabel);
            }
        }

        void DrawEntryRow(ManifestEntry e)
        {
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = StatusColor(e.Status, e.ExcludedByPreset);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox, GUILayout.Height(RowHeight)))
            {
                GUI.backgroundColor = prevBg;
                GUILayout.Label(StatusLabel(e.Status), GUILayout.Width(110));
                var label = e.PackageId + (e.ExcludedByPreset ? "  (preset)" : "");
                GUILayout.Label(label, GUILayout.Width(280));
                GUILayout.Label(e.CurrentVersion  ?? "—", GUILayout.Width(140));
                GUILayout.Label(e.IncomingVersion ?? "—", GUILayout.Width(140));

                var allowed = AllowedActions(e.Status);
                int idx = Array.IndexOf(allowed, e.Action);
                if (idx < 0) idx = 0;
                var labels = new string[allowed.Length];
                for (int i = 0; i < allowed.Length; i++) labels[i] = allowed[i].ToString();
                int next = EditorGUILayout.Popup(idx, labels);
                if (next != idx) e.Action = allowed[next];
            }
            GUI.backgroundColor = prevBg;
        }

        static ManifestAction[] AllowedActions(ManifestEntryStatus s)
        {
            switch (s)
            {
                case ManifestEntryStatus.Identical:      return new[] { ManifestAction.Skip, ManifestAction.RemoveCurrent };
                case ManifestEntryStatus.OnlyInCurrent:  return new[] { ManifestAction.Skip, ManifestAction.RemoveCurrent };
                case ManifestEntryStatus.OnlyInIncoming: return new[] { ManifestAction.Add,  ManifestAction.Skip };
                case ManifestEntryStatus.Conflict:       return new[] { ManifestAction.Skip, ManifestAction.UseIncoming, ManifestAction.RemoveCurrent };
                default: return new[] { ManifestAction.Skip };
            }
        }

        void BulkSet(ManifestEntryStatus status, ManifestAction action)
        {
            if (_plan == null) return;
            var allowed = AllowedActions(status);
            bool valid = false;
            foreach (var a in allowed) if (a == action) { valid = true; break; }
            if (!valid) return;
            foreach (var e in _plan.Entries)
            {
                if (e.Status != status) continue;
                if (action == ManifestAction.Add && e.ExcludedByPreset) continue; // honor preset
                e.Action = action;
            }
        }

        void RemovePresetMatchesInCurrent()
        {
            if (_plan == null) return;
            int n = 0;
            foreach (var e in _plan.Entries)
            {
                if (!e.ExcludedByPreset) continue;
                if (e.Status == ManifestEntryStatus.OnlyInCurrent ||
                    e.Status == ManifestEntryStatus.Identical ||
                    e.Status == ManifestEntryStatus.Conflict)
                {
                    e.Action = ManifestAction.RemoveCurrent;
                    n++;
                }
            }
            _log.Add($"PRESET  marked {n} preset-matched current package(s) for removal");
        }

        void ReapplyExclusionsToPlan()
        {
            if (_plan == null) return;
            foreach (var e in _plan.Entries)
            {
                bool excluded = _exclusions.IsExcluded(e.PackageId);
                if (excluded == e.ExcludedByPreset) continue;
                e.ExcludedByPreset = excluded;
                // Re-apply default for OnlyInIncoming so newly-excluded packages flip to Skip
                // and newly-allowed packages flip back to Add.
                if (e.Status == ManifestEntryStatus.OnlyInIncoming)
                    e.Action = excluded ? ManifestAction.Skip : ManifestAction.Add;
            }
        }

        // ─────────────────────────────────────────────────────────────
        void DrawLogSection()
        {
            EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.Height(110));
            EditorGUILayout.TextArea(string.Join("\n", _log), GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        void DrawApplyBar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                int pending = CountPendingChanges();
                GUILayout.Label(_plan == null
                    ? "No plan yet."
                    : pending == 0 ? "No changes selected." : $"{pending} change(s) pending.",
                    EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(_plan == null))
                {
                    if (GUILayout.Button("Dry run", GUILayout.Width(100), GUILayout.Height(28)))
                        Apply(dryRun: true);
                }

                GUI.backgroundColor = pending > 0 ? new Color(0.6f, 1f, 0.6f) : Color.white;
                using (new EditorGUI.DisabledScope(_plan == null || pending == 0))
                {
                    if (GUILayout.Button("APPLY merge", GUILayout.Width(160), GUILayout.Height(28)))
                    {
                        if (EditorUtility.DisplayDialog("Apply manifest merge?",
                                "Rewrite this project's Packages/manifest.json with the selected actions?\n" +
                                "A backup is written next to it (manifest.json.bak).",
                                "Apply", "Cancel"))
                            Apply(dryRun: false);
                    }
                }
                GUI.backgroundColor = Color.white;
            }
        }

        int CountPendingChanges()
        {
            if (_plan == null) return 0;
            int n = 0;
            foreach (var e in _plan.Entries)
                if (e.Action != ManifestAction.Skip) n++;
            return n;
        }

        // ─────────────────────────────────────────────────────────────
        void BuildPlan()
        {
            try
            {
                EditorUtility.DisplayProgressBar("Fuze · Manifest", "Reading manifests…", 0.5f);
                var current = Path.Combine(ProjectRoot(), "Packages", "manifest.json");
                _plan = ManifestMerger.BuildPlan(current, _incomingManifestPath, _exclusions);
                _log.Add($"COMPARE  {_plan.Entries.Count} package(s) — " +
                         $"identical {_plan.Count(ManifestEntryStatus.Identical)}, " +
                         $"onlyCurrent {_plan.Count(ManifestEntryStatus.OnlyInCurrent)}, " +
                         $"onlyIncoming {_plan.Count(ManifestEntryStatus.OnlyInIncoming)}, " +
                         $"conflict {_plan.Count(ManifestEntryStatus.Conflict)}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        void Apply(bool dryRun)
        {
            if (_plan == null) return;
            try
            {
                EditorUtility.DisplayProgressBar("Fuze · Manifest", dryRun ? "Dry run…" : "Applying…", 0.5f);
                var currentPath = _plan.CurrentManifestPath;
                var currentText = File.Exists(currentPath) ? File.ReadAllText(currentPath) : "";
                var merged = ManifestMerger.ApplyToText(_plan, currentText, out int added, out int removed, out int replaced, _log);

                if (dryRun)
                {
                    _log.Add($"[Dry] would add {added}, replace {replaced}, remove {removed}");
                    return;
                }

                if (File.Exists(currentPath))
                    File.Copy(currentPath, currentPath + ".bak", overwrite: true);
                File.WriteAllText(currentPath, merged, new System.Text.UTF8Encoding(false));
                _log.Add($"APPLIED  added {added}, replaced {replaced}, removed {removed} → {currentPath}");
                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                _log.Add($"ERROR  {ex.Message}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        // ─────────────────────────────────────────────────────────────
        static string ProjectRoot() => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        static string StatusLabel(ManifestEntryStatus s)
        {
            switch (s)
            {
                case ManifestEntryStatus.Identical:      return "= identical";
                case ManifestEntryStatus.OnlyInCurrent:  return "← only current";
                case ManifestEntryStatus.OnlyInIncoming: return "→ only incoming";
                case ManifestEntryStatus.Conflict:       return "≠ conflict";
                default: return s.ToString();
            }
        }

        static Color StatusColor(ManifestEntryStatus s, bool excluded)
        {
            if (excluded) return new Color(0.95f, 0.85f, 0.55f);
            switch (s)
            {
                case ManifestEntryStatus.Identical:      return new Color(0.78f, 0.78f, 0.78f);
                case ManifestEntryStatus.OnlyInCurrent:  return new Color(0.80f, 0.90f, 1f);
                case ManifestEntryStatus.OnlyInIncoming: return new Color(0.78f, 1f, 0.78f);
                case ManifestEntryStatus.Conflict:       return new Color(1f, 0.78f, 0.78f);
                default: return Color.white;
            }
        }
    }
}
