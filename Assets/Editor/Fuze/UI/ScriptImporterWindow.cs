using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ProjectMerger
{
    /// <summary>
    /// Focused, scripts-only importer. Pick any folder (typically inside another Unity
    /// project, but anywhere works), tick the .cs files you want, and Fuze copies them
    /// into this project under a chosen destination, then wraps every imported script in
    /// a chosen namespace using the same Wrap + RewriteReferences passes as the full
    /// project merge. No GUID remap, no scene/prefab hookups — just isolated types.
    /// </summary>
    public class ScriptImporterWindow : EditorWindow
    {
        class ScriptEntry
        {
            public string AbsPath;
            public string RelToSource;   // forward-slash, relative to _sourceRoot
            public bool   Selected = true;
        }

        // ── Source ─────────────────────────────────────────────────────
        string _sourceRoot = "";

        // ── Discovered scripts ─────────────────────────────────────────
        readonly List<ScriptEntry> _entries = new List<ScriptEntry>();
        Vector2 _listScroll;
        string _filter = "";

        // ── Destination & wrapping ─────────────────────────────────────
        string _destRoot = "";
        string _wrapNamespace = "";
        bool   _preserveGuids = false;
        bool   _userEditedDest = false;
        bool   _userEditedNs = false;

        // ── Log ────────────────────────────────────────────────────────
        readonly List<string> _log = new List<string>();
        Vector2 _logScroll;

        [MenuItem("Tools/Fuze/Import Scripts…")]
        public static void ShowWindow()
        {
            var w = GetWindow<ScriptImporterWindow>("Fuze · Scripts");
            w.minSize = new Vector2(760, 580);
            w.Show();
        }

        void OnGUI()
        {
            DrawHeader();
            EditorGUILayout.Space();
            DrawSourceSection();
            EditorGUILayout.Space();
            DrawSelectionSection();
            EditorGUILayout.Space();
            DrawDestinationSection();
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
                GUILayout.Label("Fuze · Script Importer", EditorStyles.boldLabel, GUILayout.Width(220));
                GUILayout.FlexibleSpace();
                GUILayout.Label("Target: " + Path.GetFileName(ProjectRoot()), EditorStyles.miniLabel);
            }
            EditorGUILayout.HelpBox(
                "Pick individual .cs files from a folder and copy them into this project, with " +
                "every imported type wrapped in a chosen namespace so it can't collide with existing " +
                "code. Namespaced files get their existing namespace renamed under the wrapper; " +
                "no-namespace files get a wrapper namespace inserted around their declarations. " +
                "Cross-file references between imported scripts are refactored automatically.",
                MessageType.Info);
        }

        // ─────────────────────────────────────────────────────────────
        void DrawSourceSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Source folder", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    var typed = EditorGUILayout.TextField(_sourceRoot);
                    if (typed != _sourceRoot)
                    {
                        _sourceRoot = typed;
                        ApplyDefaultsFromSource();
                    }
                    if (GUILayout.Button("Browse…", GUILayout.Width(90)))
                    {
                        var picked = EditorUtility.OpenFolderPanel("Pick folder containing .cs files", _sourceRoot, "");
                        if (!string.IsNullOrEmpty(picked))
                        {
                            _sourceRoot = picked;
                            ApplyDefaultsFromSource();
                            Scan();
                        }
                    }
                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_sourceRoot) || !Directory.Exists(_sourceRoot)))
                    {
                        if (GUILayout.Button("Rescan", GUILayout.Width(80))) Scan();
                    }
                }

                if (!string.IsNullOrEmpty(_sourceRoot) && !Directory.Exists(_sourceRoot))
                    EditorGUILayout.HelpBox("Folder does not exist.", MessageType.Warning);
            }
        }

        void ApplyDefaultsFromSource()
        {
            if (string.IsNullOrEmpty(_sourceRoot)) return;
            var leaf = Path.GetFileName(_sourceRoot.TrimEnd('/', '\\'));
            var sanitized = ScriptNamespaceWrapper.SanitizeNamespace(leaf);
            if (!_userEditedNs || string.IsNullOrEmpty(_wrapNamespace))
                _wrapNamespace = "Imported_" + sanitized;
            if (!_userEditedDest || string.IsNullOrEmpty(_destRoot))
                _destRoot = "Assets/_Imported/" + sanitized;
        }

        void Scan()
        {
            _entries.Clear();
            if (string.IsNullOrEmpty(_sourceRoot) || !Directory.Exists(_sourceRoot))
            {
                Repaint();
                return;
            }

            try
            {
                EditorUtility.DisplayProgressBar("Fuze · Scripts", "Scanning…", 0.5f);
                var files = Directory.GetFiles(_sourceRoot, "*.cs", SearchOption.AllDirectories);
                foreach (var f in files)
                {
                    _entries.Add(new ScriptEntry
                    {
                        AbsPath     = f,
                        RelToSource = MakeRel(_sourceRoot, f),
                        Selected    = true
                    });
                }
                _entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelToSource, b.RelToSource));
                _log.Add($"SCAN  {_entries.Count} .cs file(s) under {_sourceRoot}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        // ─────────────────────────────────────────────────────────────
        void DrawSelectionSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                {
                    GUILayout.Label("Filter:", EditorStyles.miniLabel, GUILayout.Width(40));
                    _filter = EditorGUILayout.TextField(_filter, EditorStyles.toolbarSearchField, GUILayout.Width(220));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Select all (filtered)",  EditorStyles.toolbarButton)) SetSelection(true);
                    if (GUILayout.Button("Select none (filtered)", EditorStyles.toolbarButton)) SetSelection(false);
                    if (GUILayout.Button("Invert (filtered)",      EditorStyles.toolbarButton)) InvertSelection();
                }

                int totalSel = 0;
                foreach (var e in _entries) if (e.Selected) totalSel++;
                EditorGUILayout.LabelField($"Selected: {totalSel} / {_entries.Count}", EditorStyles.miniLabel);

                _listScroll = EditorGUILayout.BeginScrollView(_listScroll, GUILayout.MinHeight(220));
                if (_entries.Count == 0)
                    EditorGUILayout.LabelField("(no scripts — pick a source folder above)", EditorStyles.miniLabel);
                else
                {
                    int shown = 0;
                    foreach (var e in _entries)
                    {
                        if (!Passes(e)) continue;
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            e.Selected = EditorGUILayout.Toggle(e.Selected, GUILayout.Width(18));
                            EditorGUILayout.LabelField(e.RelToSource, EditorStyles.miniLabel);
                        }
                        shown++;
                    }
                    if (shown == 0)
                        EditorGUILayout.LabelField("No entries match the current filter.", EditorStyles.miniLabel);
                }
                EditorGUILayout.EndScrollView();
            }
        }

        bool Passes(ScriptEntry e)
        {
            if (string.IsNullOrEmpty(_filter)) return true;
            return e.RelToSource.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void SetSelection(bool sel)
        {
            foreach (var e in _entries) if (Passes(e)) e.Selected = sel;
        }

        void InvertSelection()
        {
            foreach (var e in _entries) if (Passes(e)) e.Selected = !e.Selected;
        }

        // ─────────────────────────────────────────────────────────────
        void DrawDestinationSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Destination & wrapping", EditorStyles.boldLabel);

                var destTyped = EditorGUILayout.TextField(
                    new GUIContent("Dest folder", "Project-relative, e.g. Assets/_Imported/MyScripts"),
                    _destRoot);
                if (destTyped != _destRoot)
                {
                    _destRoot = destTyped;
                    _userEditedDest = true;
                }

                var nsTyped = EditorGUILayout.TextField(
                    new GUIContent("Namespace", "Imported types are wrapped in this namespace"),
                    _wrapNamespace);
                if (nsTyped != _wrapNamespace)
                {
                    _wrapNamespace = nsTyped;
                    _userEditedNs = true;
                }

                _preserveGuids = EditorGUILayout.ToggleLeft(
                    new GUIContent("Preserve script GUIDs (copy .meta files)",
                        "Off (default): Unity assigns fresh GUIDs on import. " +
                        "On: copy each .cs.meta so existing prefab/scene references in the source project keep working."),
                    _preserveGuids);

                if (!string.IsNullOrEmpty(_destRoot) &&
                    !_destRoot.Replace('\\', '/').StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                    !_destRoot.Replace('\\', '/').StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                {
                    EditorGUILayout.HelpBox(
                        "Destination should usually live under Assets/ or Packages/ so Unity compiles the imported scripts.",
                        MessageType.Warning);
                }
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
            int sel = 0;
            foreach (var e in _entries) if (e.Selected) sel++;

            string sanitizedNs = ScriptNamespaceWrapper.SanitizeNamespace(_wrapNamespace ?? "");
            bool nsValid       = !string.IsNullOrEmpty(_wrapNamespace) && sanitizedNs.Length > 0;
            bool destValid     = !string.IsNullOrEmpty(_destRoot);
            bool ready         = sel > 0 && nsValid && destValid;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label(
                    !ready
                        ? (sel == 0 ? "Select at least one script."
                                    : !nsValid ? "Set a valid namespace."
                                               : "Set a destination folder.")
                        : $"Ready — will import {sel} script(s) into {_destRoot} under namespace {sanitizedNs}.",
                    EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();

                GUI.backgroundColor = ready ? new Color(0.6f, 1f, 0.6f) : Color.white;
                using (new EditorGUI.DisabledScope(!ready))
                {
                    if (GUILayout.Button($"Import {sel} script(s) →", GUILayout.Width(220), GUILayout.Height(28)))
                    {
                        if (EditorUtility.DisplayDialog("Fuze · Import Scripts",
                                $"Copy {sel} .cs file(s) into {_destRoot} and wrap their types in namespace {sanitizedNs}?",
                                "Import", "Cancel"))
                            RunImport(sanitizedNs);
                    }
                }
                GUI.backgroundColor = Color.white;
            }
        }

        // ─────────────────────────────────────────────────────────────
        void RunImport(string ns)
        {
            try
            {
                EditorUtility.DisplayProgressBar("Fuze · Scripts", "Preparing…", 0f);

                var projectRoot = ProjectRoot();
                var destRel     = _destRoot.Replace('\\', '/').Trim().TrimEnd('/');
                var destAbs     = Path.GetFullPath(Path.Combine(projectRoot, destRel.Replace('/', Path.DirectorySeparatorChar)));

                if (IsPathInside(destAbs, _sourceRoot))
                {
                    _log.Add("ERROR  destination is inside the source folder — refusing to copy");
                    return;
                }
                Directory.CreateDirectory(destAbs);

                // Phase 1 — copy selected files (and optional .meta).
                var copied = new List<string>();
                int total  = 0; foreach (var e in _entries) if (e.Selected) total++;
                int idx    = 0;
                foreach (var e in _entries)
                {
                    if (!e.Selected) continue;
                    idx++;
                    EditorUtility.DisplayProgressBar("Fuze · Scripts", "Copying " + e.RelToSource, idx / Mathf.Max(1f, total) * 0.4f);

                    var dst = Path.Combine(destAbs, e.RelToSource.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    var finalDst = UniquifyName(dst);

                    try
                    {
                        File.Copy(e.AbsPath, finalDst, overwrite: false);
                        copied.Add(finalDst);
                        if (_preserveGuids)
                        {
                            var srcMeta = e.AbsPath + ".meta";
                            if (File.Exists(srcMeta))
                                File.Copy(srcMeta, finalDst + ".meta", overwrite: true);
                        }
                        _log.Add("COPY   " + RelOf(projectRoot, finalDst));
                    }
                    catch (Exception ex)
                    {
                        _log.Add($"ERROR  copy {e.AbsPath} → {finalDst}: {ex.Message}");
                    }
                }

                // Phase 2 — wrap each copy. Collects roots / top-level type names across the set
                // so the cross-file refactor pass below knows what to prefix.
                var roots         = new HashSet<string>();
                var topLevelTypes = new HashSet<string>();
                int wrappedNew    = 0, renamedExisting = 0;
                for (int i = 0; i < copied.Count; i++)
                {
                    EditorUtility.DisplayProgressBar("Fuze · Scripts",
                        "Wrapping " + Path.GetFileName(copied[i]),
                        0.4f + (i / Mathf.Max(1f, copied.Count)) * 0.3f);
                    try
                    {
                        var src = File.ReadAllText(copied[i], Encoding.UTF8);
                        var r   = ScriptNamespaceWrapper.Wrap(src, ns);
                        if (r.Roots != null)
                            foreach (var x in r.Roots) roots.Add(x);
                        if (r.TopLevelTypes != null)
                            foreach (var x in r.TopLevelTypes) topLevelTypes.Add(x);

                        if (r.Source != src)
                            File.WriteAllText(copied[i], r.Source, new UTF8Encoding(false));

                        if (r.HadNamespace) renamedExisting++;
                        else                 wrappedNew++;
                    }
                    catch (Exception ex)
                    {
                        _log.Add($"ERROR  wrap {copied[i]}: {ex.Message}");
                    }
                }
                _log.Add($"WRAP   {copied.Count} file(s); namespaced renamed {renamedExisting}, no-namespace wrapped {wrappedNew} (roots: {roots.Count}, top-level types: {topLevelTypes.Count})");

                // Phase 3 — rewrite cross-file references in the imported set so `using OldNs;`
                // and `OldNs.Foo` references between two imported files keep resolving after
                // their namespaces were renamed.
                if (roots.Count > 0 || topLevelTypes.Count > 0)
                {
                    int refactored = 0;
                    for (int i = 0; i < copied.Count; i++)
                    {
                        EditorUtility.DisplayProgressBar("Fuze · Scripts",
                            "Refactoring " + Path.GetFileName(copied[i]),
                            0.7f + (i / Mathf.Max(1f, copied.Count)) * 0.25f);
                        try
                        {
                            var src     = File.ReadAllText(copied[i], Encoding.UTF8);
                            var updated = ScriptNamespaceWrapper.RewriteReferences(src, roots, topLevelTypes, ns);
                            if (updated != src)
                            {
                                File.WriteAllText(copied[i], updated, new UTF8Encoding(false));
                                refactored++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.Add($"ERROR  refactor {copied[i]}: {ex.Message}");
                        }
                    }
                    _log.Add($"REFACTOR  {refactored} file(s) had cross-file references prefixed under {ns}");
                }

                EditorUtility.DisplayProgressBar("Fuze · Scripts", "Refreshing AssetDatabase…", 0.97f);
                AssetDatabase.Refresh();
                _log.Add("DONE");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        // ─────────────────────────────────────────────────────────────
        static string ProjectRoot() => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        static string MakeRel(string root, string abs)
        {
            var r = root.Replace('\\', '/').TrimEnd('/') + "/";
            var a = abs.Replace('\\', '/');
            return a.StartsWith(r, StringComparison.OrdinalIgnoreCase)
                ? a.Substring(r.Length)
                : Path.GetFileName(abs);
        }

        static string RelOf(string root, string abs)
        {
            var r = root.Replace('\\', '/').TrimEnd('/') + "/";
            var a = abs.Replace('\\', '/');
            return a.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? a.Substring(r.Length) : a;
        }

        static string UniquifyName(string absPath)
        {
            if (!File.Exists(absPath)) return absPath;
            var dir  = Path.GetDirectoryName(absPath);
            var name = Path.GetFileNameWithoutExtension(absPath);
            var ext  = Path.GetExtension(absPath);
            for (int i = 1; i < 1000; i++)
            {
                var candidate = Path.Combine(dir, $"{name}_imported{i}{ext}");
                if (!File.Exists(candidate)) return candidate;
            }
            return absPath;
        }

        static bool IsPathInside(string candidate, string root)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(root)) return false;
            var c = Path.GetFullPath(candidate).Replace('\\', '/').TrimEnd('/') + "/";
            var r = Path.GetFullPath(root).Replace('\\', '/').TrimEnd('/') + "/";
            return c.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
    }
}
