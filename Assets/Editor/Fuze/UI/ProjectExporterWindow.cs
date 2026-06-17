using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ProjectMerger
{
    /// <summary>
    /// Run inside the SOURCE project. Curate a list of folders/files to exclude,
    /// optionally save/load the list as a JSON preset (reusable across projects),
    /// then export the project either as a plain folder tree (feeds into the
    /// ProjectMerger importer) or as a .unitypackage.
    /// </summary>
    public class ProjectExporterWindow : EditorWindow
    {
        enum Format { Folder, UnityPackage }

        ExclusionList _list;
        Format _format = Format.Folder;
        string _destination = "";
        Vector2 _exclusionScroll;
        Vector2 _logScroll;
        List<string> _log = new List<string>();

        [MenuItem("Tools/Fuze/Export Project…")]
        public static void ShowWindow()
        {
            var w = GetWindow<ProjectExporterWindow>("Fuze · Export");
            w.minSize = new Vector2(760, 560);
            w.Show();
        }

        void OnEnable()
        {
            // Load project default if present; otherwise seed built-ins.
            var defaultPath = Path.Combine(ProjectRoot(), ExclusionList.ProjectDefaultRelPath);
            _list = File.Exists(defaultPath) ? ExclusionList.LoadFrom(defaultPath) : new ExclusionList();
            if (_list.exclusions.Count == 0) _list.SeedBuiltInDefaults();
        }

        void OnGUI()
        {
            DrawHeader();
            EditorGUILayout.Space();
            DrawScopeSection();
            EditorGUILayout.Space();
            DrawExclusionSection();
            EditorGUILayout.Space();
            DrawPresetSection();
            EditorGUILayout.Space();
            DrawDestinationSection();
            EditorGUILayout.Space();
            DrawLog();
            GUILayout.FlexibleSpace();
            DrawApplyBar();
        }

        void DrawHeader()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Fuze · Export", EditorStyles.boldLabel, GUILayout.Width(160));
                GUILayout.FlexibleSpace();
                GUILayout.Label("Source: " + Path.GetFileName(ProjectRoot()), EditorStyles.miniLabel);
            }
            EditorGUILayout.HelpBox(
                "Curate folders to EXCLUDE, then export. Folder export produces a directory tree that the " +
                "Fuze importer can consume; .unitypackage is Assets/-only (Unity's format can't " +
                "hold Packages/ or ProjectSettings/).",
                MessageType.Info);
        }

        void DrawScopeSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Scope", EditorStyles.boldLabel);
                _list.includeAssets          = EditorGUILayout.ToggleLeft("Include Assets/",          _list.includeAssets);
                _list.includePackages        = EditorGUILayout.ToggleLeft("Include Packages/",        _list.includePackages);
                _list.includeProjectSettings = EditorGUILayout.ToggleLeft("Include ProjectSettings/", _list.includeProjectSettings);
            }
        }

        void DrawExclusionSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"Exclusions ({_list.exclusions.Count})", EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Pick folder…", GUILayout.Width(110))) AddFolderViaPicker();
                    if (GUILayout.Button("Pick file…",   GUILayout.Width(95)))  AddFileViaPicker();
                    if (GUILayout.Button("Seed defaults", GUILayout.Width(110))) _list.SeedBuiltInDefaults();
                    using (new EditorGUI.DisabledScope(_list.exclusions.Count == 0))
                    {
                        if (GUILayout.Button("Clear", GUILayout.Width(60))) _list.exclusions.Clear();
                    }
                }
                EditorGUILayout.LabelField(
                    "Matched by NAME, anywhere in the tree (case-insensitive). " +
                    "\"Library\" excludes every Library folder; \"Foo.png\" matches every file with that exact basename.",
                    EditorStyles.miniLabel);

                DrawDropZone();

                _exclusionScroll = EditorGUILayout.BeginScrollView(_exclusionScroll, GUILayout.MinHeight(120), GUILayout.MaxHeight(240));
                int removeAt = -1;
                for (int i = 0; i < _list.exclusions.Count; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        _list.exclusions[i] = EditorGUILayout.TextField(_list.exclusions[i]);
                        if (GUILayout.Button("×", GUILayout.Width(24))) removeAt = i;
                    }
                }
                if (removeAt >= 0) _list.RemoveAt(removeAt);
                EditorGUILayout.EndScrollView();
            }
        }

        void DrawDropZone()
        {
            var rect = GUILayoutUtility.GetRect(0, 34, GUILayout.ExpandWidth(true));
            GUI.Box(rect, "Drop folders or files here to exclude", EditorStyles.helpBox);

            var evt = Event.current;
            if (!rect.Contains(evt.mousePosition)) return;

            if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (evt.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var p in DragAndDrop.paths)
                        _list.Add(Path.GetFileName(p.TrimEnd('/', '\\')));
                    foreach (var obj in DragAndDrop.objectReferences)
                    {
                        var p = AssetDatabase.GetAssetPath(obj);
                        if (!string.IsNullOrEmpty(p))
                            _list.Add(Path.GetFileName(p.TrimEnd('/', '\\')));
                    }
                    evt.Use();
                }
            }
        }

        void AddFolderViaPicker()
        {
            var picked = EditorUtility.OpenFolderPanel("Pick a folder — only its name will be stored", ProjectRoot(), "");
            if (string.IsNullOrEmpty(picked)) return;
            _list.Add(Path.GetFileName(picked.TrimEnd('/', '\\')));
        }

        void AddFileViaPicker()
        {
            var picked = EditorUtility.OpenFilePanel("Pick a file — only its name will be stored", ProjectRoot(), "");
            if (string.IsNullOrEmpty(picked)) return;
            _list.Add(Path.GetFileName(picked));
        }

        void DrawPresetSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Presets", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Load preset…", GUILayout.Height(22)))
                    {
                        var p = EditorUtility.OpenFilePanel("Load exclusion preset", ProjectRoot(), "json");
                        if (!string.IsNullOrEmpty(p))
                        {
                            _list = ExclusionList.LoadFrom(p);
                            _log.Add("PRESET  loaded " + p);
                        }
                    }
                    if (GUILayout.Button("Save preset as…", GUILayout.Height(22)))
                    {
                        var p = EditorUtility.SaveFilePanel("Save exclusion preset", ProjectRoot(),
                            "exclusions", "json");
                        if (!string.IsNullOrEmpty(p))
                        {
                            _list.SaveTo(p);
                            _log.Add("PRESET  saved " + p);
                        }
                    }

                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button("Load project default", GUILayout.Height(22)))
                    {
                        var p = Path.Combine(ProjectRoot(), ExclusionList.ProjectDefaultRelPath);
                        _list = ExclusionList.LoadFrom(p);
                        _log.Add("PRESET  loaded project default " + p);
                    }
                    if (GUILayout.Button("Save as project default", GUILayout.Height(22)))
                    {
                        var p = Path.Combine(ProjectRoot(), ExclusionList.ProjectDefaultRelPath);
                        _list.SaveTo(p);
                        _log.Add("PRESET  saved project default " + p);
                    }
                }
                EditorGUILayout.LabelField(
                    "Project default lives at " + ExclusionList.ProjectDefaultRelPath,
                    EditorStyles.miniLabel);
            }
        }

        void DrawDestinationSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
                _format = (Format)EditorGUILayout.EnumPopup("Format", _format);

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(_format == Format.Folder ? "Destination folder" : "Destination .unitypackage",
                        GUILayout.Width(180));
                    _destination = EditorGUILayout.TextField(_destination);
                    if (GUILayout.Button("Browse…", GUILayout.Width(90)))
                    {
                        if (_format == Format.Folder)
                        {
                            var picked = EditorUtility.OpenFolderPanel("Choose destination folder", _destination, "");
                            if (!string.IsNullOrEmpty(picked)) _destination = picked;
                        }
                        else
                        {
                            var picked = EditorUtility.SaveFilePanel("Save .unitypackage",
                                string.IsNullOrEmpty(_destination) ? "" : Path.GetDirectoryName(_destination),
                                string.IsNullOrEmpty(_destination) ? Path.GetFileName(ProjectRoot()) : Path.GetFileNameWithoutExtension(_destination),
                                "unitypackage");
                            if (!string.IsNullOrEmpty(picked)) _destination = picked;
                        }
                    }
                }

                if (_format == Format.UnityPackage && (_list.includePackages || _list.includeProjectSettings))
                    EditorGUILayout.HelpBox(
                        ".unitypackage can only contain Assets/…  — Packages/ and ProjectSettings/ will not be included.",
                        MessageType.Warning);
            }
        }

        void DrawLog()
        {
            EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.Height(130));
            EditorGUILayout.TextArea(string.Join("\n", _log), GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        void DrawApplyBar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                var scopesOK = _list.includeAssets || _list.includePackages || _list.includeProjectSettings;
                var destOK   = !string.IsNullOrEmpty(_destination);
                var enabled  = scopesOK && destOK;

                if (!scopesOK)      GUILayout.Label("Pick at least one scope.",        EditorStyles.miniLabel);
                else if (!destOK)   GUILayout.Label("Pick a destination.",             EditorStyles.miniLabel);
                else                GUILayout.Label("Ready to export.",                EditorStyles.miniLabel);

                GUILayout.FlexibleSpace();
                GUI.backgroundColor = enabled ? new Color(0.6f, 1f, 0.6f) : Color.white;
                using (new EditorGUI.DisabledScope(!enabled))
                {
                    if (GUILayout.Button("EXPORT", GUILayout.Height(32), GUILayout.Width(180)))
                        RunExport();
                }
                GUI.backgroundColor = Color.white;
            }
        }

        void RunExport()
        {
            _log.Clear();
            try
            {
                if (_format == Format.Folder)
                {
                    EditorUtility.DisplayProgressBar("Fuze · Export", "Exporting folder…", 0f);
                    ProjectExporter.ExportFolder(
                        ProjectRoot(), _destination, _list,
                        (p, m) => EditorUtility.DisplayProgressBar("Fuze · Export", m, p),
                        _log);
                }
                else
                {
                    EditorUtility.DisplayProgressBar("Fuze · Export", "Exporting .unitypackage…", 0f);
                    ProjectExporter.ExportUnityPackage(
                        _destination, _list,
                        (p, m) => EditorUtility.DisplayProgressBar("Fuze · Export", m, p),
                        _log);
                }
                EditorUtility.RevealInFinder(_destination);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        // ── helpers ────────────────────────────────────────────────────
        static string ProjectRoot() => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    }
}
