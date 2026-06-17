using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ProjectMerger
{
    public class ProjectMergerWindow : EditorWindow
    {
        enum Step { Source, Scan, Resolve, Review }

        // ── Source step ────────────────────────────────────────────────
        string _incomingRoot = "";
        bool   _enablePHash  = true;
        bool   _includeProjectSettings = true;
        bool   _includePackages        = true;

        // Per-import exclusion list — filters BOTH the incoming project and the current project's
        // scans (so e.g. Assets/_Imported staging from a previous merge doesn't pollute the diff).
        ExclusionList _importExclusions;
        bool _filtersFoldout = false;
        string _newExclusionPath = "";
        Vector2 _filtersScroll;

        // ── Scan step ──────────────────────────────────────────────────
        List<AssetRecord> _currentIndex;
        List<AssetRecord> _incomingIndex;
        MergePlan _plan;

        // ── Resolve step ───────────────────────────────────────────────
        Vector2 _listScroll, _leftDiffScroll, _rightDiffScroll;
        int _selectedIndex = -1;
        string _filterText = "";
        bool _showIdentical = false;
        bool _showNew       = false;
        bool _showConflicts = true;
        bool _showNearDup   = true;
        bool _showRemap     = true;
        Texture2D _leftPreview, _rightPreview;
        string _leftText, _rightText;

        // ── Virtualized list state ─────────────────────────────────────
        List<int> _visibleIndices = new List<int>();
        bool   _visibleDirty      = true;
        string _filterTextLast    = null;
        bool _showIdenticalLast, _showNewLast, _showConflictsLast, _showNearDupLast, _showRemapLast;
        const float RowHeight = 38f;

        // ── Review step ────────────────────────────────────────────────
        List<string> _executionLog = new List<string>();
        Vector2 _logScroll;

        Step _step = Step.Source;

        [MenuItem("Tools/Fuze/Open…")]
        public static void ShowWindow()
        {
            var w = GetWindow<ProjectMergerWindow>("Fuze");
            w.minSize = new Vector2(900, 560);
            w.Show();
        }

        void OnEnable()
        {
            var presetPath = Path.Combine(CurrentProjectRoot(), ExclusionList.ImportDefaultRelPath);
            _importExclusions = File.Exists(presetPath)
                ? ExclusionList.LoadFrom(presetPath)
                : new ExclusionList();
            if (_importExclusions.exclusions.Count == 0)
                _importExclusions.SeedBuiltInDefaults();
        }

        static string CurrentProjectRoot() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        void OnGUI()
        {
            DrawHeader();
            EditorGUILayout.Space();
            switch (_step)
            {
                case Step.Source:  DrawSourceStep();  break;
                case Step.Scan:    DrawScanStep();    break;
                case Step.Resolve: DrawResolveStep(); break;
                case Step.Review:  DrawReviewStep();  break;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Header / stepper
        // ─────────────────────────────────────────────────────────────
        void DrawHeader()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Fuze", EditorStyles.boldLabel, GUILayout.Width(140));
                GUILayout.FlexibleSpace();
                DrawStepTab(Step.Source,  "1 · Source");
                DrawStepTab(Step.Scan,    "2 · Scan");
                DrawStepTab(Step.Resolve, "3 · Resolve");
                DrawStepTab(Step.Review,  "4 · Review");
            }
        }

        void DrawStepTab(Step s, string label)
        {
            var active = _step == s;
            var style = new GUIStyle(EditorStyles.toolbarButton);
            if (active) style.fontStyle = FontStyle.Bold;
            bool canJump = s <= _step || (s == Step.Scan && !string.IsNullOrEmpty(_incomingRoot));
            GUI.enabled = canJump;
            if (GUILayout.Button(label, style, GUILayout.Width(110)))
                _step = s;
            GUI.enabled = true;
        }

        // ─────────────────────────────────────────────────────────────
        // Step 1 · Source
        // ─────────────────────────────────────────────────────────────
        void DrawSourceStep()
        {
            EditorGUILayout.LabelField("Source project to import", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Point at any Unity project's root folder (the one containing Assets/ and ProjectSettings/). " +
                "No export step is needed — the source project is read in place. Use the Import filters " +
                "below to skip subfolders/files you don't want. Nothing is written until you press Apply in step 4.",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Project root", GUILayout.Width(100));
                _incomingRoot = EditorGUILayout.TextField(_incomingRoot);
                if (GUILayout.Button("Browse…", GUILayout.Width(90)))
                {
                    var picked = EditorUtility.OpenFolderPanel("Select Unity project folder", _incomingRoot, "");
                    if (!string.IsNullOrEmpty(picked)) _incomingRoot = picked;
                }
            }

            if (!string.IsNullOrEmpty(_incomingRoot))
            {
                var looksLikeProject =
                    Directory.Exists(Path.Combine(_incomingRoot, "Assets")) &&
                    Directory.Exists(Path.Combine(_incomingRoot, "ProjectSettings"));
                if (!looksLikeProject)
                    EditorGUILayout.HelpBox("That folder doesn't look like a Unity project (missing Assets/ or ProjectSettings/).", MessageType.Warning);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Options", EditorStyles.boldLabel);
            _includeProjectSettings = EditorGUILayout.ToggleLeft("Include ProjectSettings/", _includeProjectSettings);
            _includePackages        = EditorGUILayout.ToggleLeft("Include Packages/ (embedded packages + manifest.json merge)", _includePackages);
            _enablePHash            = EditorGUILayout.ToggleLeft("Detect near-duplicate textures (pHash)", _enablePHash);

            EditorGUILayout.Space();
            DrawImportFiltersSection();

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_incomingRoot) || !Directory.Exists(_incomingRoot)))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Merge Package Manifest from this source…", GUILayout.Height(22)))
                    {
                        var w = PackageManifestMergerWindow.ShowWindow();
                        w.SetIncomingFromProjectRoot(_incomingRoot);
                    }
                }

                EditorGUILayout.Space(2);

                if (GUILayout.Button("Start scan →", GUILayout.Height(28)))
                {
                    _step = Step.Scan;
                    EditorApplication.delayCall += RunScan;
                }
            }
        }

        void DrawImportFiltersSection()
        {
            _filtersFoldout = EditorGUILayout.Foldout(_filtersFoldout,
                $"Import filters ({_importExclusions.exclusions.Count} entr" +
                (_importExclusions.exclusions.Count == 1 ? "y" : "ies") + " — applied to incoming AND current scans)",
                toggleOnLabelClick: true);
            if (!_filtersFoldout) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    "Folder/file NAMES to skip on scan — matched anywhere in the tree, " +
                    "case-insensitive. e.g. \"Library\" excludes every Library folder; " +
                    "\"Foo.png\" excludes every file with that exact name.",
                    EditorStyles.miniLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    _newExclusionPath = EditorGUILayout.TextField(_newExclusionPath);
                    using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_newExclusionPath)))
                    {
                        if (GUILayout.Button("Add", GUILayout.Width(60)))
                        {
                            _importExclusions.Add(_newExclusionPath);
                            _newExclusionPath = "";
                        }
                    }
                    if (GUILayout.Button("Pick folder…", GUILayout.Width(110)))
                    {
                        var picked = EditorUtility.OpenFolderPanel(
                            "Pick a folder — only its name will be stored",
                            string.IsNullOrEmpty(_incomingRoot) ? "" : _incomingRoot, "");
                        if (!string.IsNullOrEmpty(picked))
                            _importExclusions.Add(Path.GetFileName(picked.TrimEnd('/', '\\')));
                    }
                    if (GUILayout.Button("Pick file…", GUILayout.Width(95)))
                    {
                        var picked = EditorUtility.OpenFilePanel(
                            "Pick a file — only its name will be stored",
                            string.IsNullOrEmpty(_incomingRoot) ? "" : _incomingRoot, "");
                        if (!string.IsNullOrEmpty(picked))
                            _importExclusions.Add(Path.GetFileName(picked));
                    }
                }

                _filtersScroll = EditorGUILayout.BeginScrollView(_filtersScroll, GUILayout.Height(110));
                if (_importExclusions.exclusions.Count == 0)
                    EditorGUILayout.LabelField("(no exclusions)", EditorStyles.miniLabel);
                else
                {
                    for (int i = 0; i < _importExclusions.exclusions.Count; i++)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            var updated = EditorGUILayout.TextField(_importExclusions.exclusions[i]);
                            if (updated != _importExclusions.exclusions[i])
                                _importExclusions.exclusions[i] = updated;
                            if (GUILayout.Button("✕", GUILayout.Width(28)))
                            {
                                _importExclusions.RemoveAt(i);
                                break;
                            }
                        }
                    }
                }
                EditorGUILayout.EndScrollView();

                using (new EditorGUILayout.HorizontalScope())
                {
                    var defaultPath = Path.Combine(CurrentProjectRoot(), ExclusionList.ImportDefaultRelPath);
                    if (GUILayout.Button("Save to project default", GUILayout.Width(180)))
                        _importExclusions.SaveTo(defaultPath);
                    if (GUILayout.Button("Load project default", GUILayout.Width(160)))
                        _importExclusions = ExclusionList.LoadFrom(defaultPath);
                    if (GUILayout.Button("Reset to built-ins", GUILayout.Width(140)))
                    {
                        _importExclusions = new ExclusionList();
                        _importExclusions.SeedBuiltInDefaults();
                    }
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Save preset…", GUILayout.Width(110)))
                    {
                        var p = EditorUtility.SaveFilePanel("Save import exclusion preset", "",
                            "ProjectMergerImportExclusions", "json");
                        if (!string.IsNullOrEmpty(p)) _importExclusions.SaveTo(p);
                    }
                    if (GUILayout.Button("Load preset…", GUILayout.Width(110)))
                    {
                        var p = EditorUtility.OpenFilePanel("Load import exclusion preset", "", "json");
                        if (!string.IsNullOrEmpty(p)) _importExclusions = ExclusionList.LoadFrom(p);
                    }
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Step 2 · Scan
        // ─────────────────────────────────────────────────────────────
        void DrawScanStep()
        {
            EditorGUILayout.LabelField("Scanning projects…", EditorStyles.boldLabel);
            if (_plan == null)
            {
                EditorGUILayout.HelpBox("Working — watch the progress bar. Both projects are being indexed (GUID + MD5 + optional pHash + script type map).", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField($"Incoming: {_plan.IncomingProjectName}   ({_incomingIndex.Count} files)");
            EditorGUILayout.LabelField($"Current project: {_currentIndex.Count} files");
            EditorGUILayout.Space();

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.BeginVertical();
                EditorGUILayout.LabelField("Identical (skip):",  _plan.Count(EntryStatus.Identical).ToString());
                EditorGUILayout.LabelField("Remap only:",        _plan.Count(EntryStatus.RemapOnly).ToString());
                EditorGUILayout.LabelField("New:",               _plan.Count(EntryStatus.NewAsset).ToString());
                EditorGUILayout.EndVertical();
                EditorGUILayout.BeginVertical();
                EditorGUILayout.LabelField("Conflict (path):",   _plan.Count(EntryStatus.ConflictByPath).ToString());
                EditorGUILayout.LabelField("Conflict (guid):",   _plan.Count(EntryStatus.ConflictByGuid).ToString());
                EditorGUILayout.LabelField("Near-duplicate:",    _plan.Count(EntryStatus.NearDuplicate).ToString());
                EditorGUILayout.LabelField("ProjectSetting:",    _plan.Count(EntryStatus.ProjectSettingConflict).ToString());
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.Space();
            if (GUILayout.Button("Continue to conflict resolution →", GUILayout.Height(28)))
                _step = Step.Resolve;
        }

        void RunScan()
        {
            try
            {
                EditorUtility.DisplayProgressBar("Fuze", "Scanning current project…", 0f);
                var currentRoot = CurrentProjectRoot();
                _currentIndex = ProjectScanner.Scan(
                    currentRoot, _includeProjectSettings, _includePackages, _enablePHash,
                    (p, m) => EditorUtility.DisplayProgressBar("Fuze", "Current: " + m, p * 0.5f),
                    _importExclusions);

                EditorUtility.DisplayProgressBar("Fuze", "Scanning incoming project…", 0.5f);
                _incomingIndex = ProjectScanner.Scan(
                    _incomingRoot, _includeProjectSettings, _includePackages, _enablePHash,
                    (p, m) => EditorUtility.DisplayProgressBar("Fuze", "Incoming: " + m, 0.5f + p * 0.45f),
                    _importExclusions);

                EditorUtility.DisplayProgressBar("Fuze", "Classifying…", 0.96f);
                var name = new DirectoryInfo(_incomingRoot).Name;
                _plan = MergeClassifier.Build(_currentIndex, _incomingIndex, _incomingRoot, name);

                _selectedIndex = FirstUndecidedIndex();
                _visibleDirty  = true;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        int FirstUndecidedIndex()
        {
            if (_plan == null) return -1;
            for (int i = 0; i < _plan.Entries.Count; i++)
                if (_plan.Entries[i].Resolution == Resolution.Undecided) return i;
            return _plan.Entries.Count > 0 ? 0 : -1;
        }

        // ─────────────────────────────────────────────────────────────
        // Step 3 · Resolve  (virtualized list, cached filter)
        // ─────────────────────────────────────────────────────────────
        void DrawResolveStep()
        {
            if (_plan == null) { EditorGUILayout.HelpBox("No plan. Re-run scan.", MessageType.Warning); return; }

            DrawResolveToolbar();

            using (new EditorGUILayout.HorizontalScope())
            {
                DrawConflictList();
                DrawDetailPane();
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label($"Undecided conflicts: {_plan.UndecidedConflicts()}");
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("← Back to scan", GUILayout.Width(120))) _step = Step.Scan;
                if (GUILayout.Button("Review & apply →", GUILayout.Height(24), GUILayout.Width(160)))
                    _step = Step.Review;
            }
        }

        void DrawResolveToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Filter:", EditorStyles.miniLabel, GUILayout.Width(40));
                _filterText = EditorGUILayout.TextField(_filterText, EditorStyles.toolbarSearchField, GUILayout.Width(220));
                _showIdentical = GUILayout.Toggle(_showIdentical, "Identical", EditorStyles.toolbarButton);
                _showNew       = GUILayout.Toggle(_showNew,       "New",       EditorStyles.toolbarButton);
                _showRemap     = GUILayout.Toggle(_showRemap,     "Remap",     EditorStyles.toolbarButton);
                _showConflicts = GUILayout.Toggle(_showConflicts, "Conflicts", EditorStyles.toolbarButton);
                _showNearDup   = GUILayout.Toggle(_showNearDup,   "NearDup",   EditorStyles.toolbarButton);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Bulk: Keep Current", EditorStyles.toolbarButton)) BulkSet(Resolution.KeepCurrent);
                if (GUILayout.Button("Bulk: Overwrite",    EditorStyles.toolbarButton)) BulkSet(Resolution.Overwrite);
                if (GUILayout.Button("Bulk: Keep Both",    EditorStyles.toolbarButton)) BulkSet(Resolution.KeepBoth);
            }
        }

        void BulkSet(Resolution r)
        {
            RebuildVisibleIfDirty();
            foreach (var i in _visibleIndices)
            {
                var e = _plan.Entries[i];
                if (e.Status == EntryStatus.ConflictByPath ||
                    e.Status == EntryStatus.ConflictByGuid ||
                    e.Status == EntryStatus.NearDuplicate ||
                    e.Status == EntryStatus.ProjectSettingConflict)
                    e.Resolution = r;
            }
        }

        void RebuildVisibleIfDirty()
        {
            if (_filterText != _filterTextLast ||
                _showIdentical != _showIdenticalLast ||
                _showNew       != _showNewLast ||
                _showConflicts != _showConflictsLast ||
                _showNearDup   != _showNearDupLast ||
                _showRemap     != _showRemapLast)
            {
                _visibleDirty = true;
                _filterTextLast     = _filterText;
                _showIdenticalLast  = _showIdentical;
                _showNewLast        = _showNew;
                _showConflictsLast  = _showConflicts;
                _showNearDupLast    = _showNearDup;
                _showRemapLast      = _showRemap;
            }

            if (!_visibleDirty || _plan == null) return;

            _visibleIndices.Clear();
            string filter = string.IsNullOrEmpty(_filterText) ? null : _filterText;

            for (int i = 0; i < _plan.Entries.Count; i++)
            {
                var e = _plan.Entries[i];
                bool pass;
                switch (e.Status)
                {
                    case EntryStatus.Identical: pass = _showIdentical; break;
                    case EntryStatus.NewAsset:  pass = _showNew;       break;
                    case EntryStatus.RemapOnly: pass = _showRemap;     break;
                    case EntryStatus.NearDuplicate: pass = _showNearDup; break;
                    default: pass = _showConflicts; break;
                }
                if (!pass) continue;
                if (filter != null &&
                    e.Incoming.RelativePath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                _visibleIndices.Add(i);
            }
            _visibleDirty = false;
        }

        void DrawConflictList()
        {
            RebuildVisibleIfDirty();

            var areaRect = GUILayoutUtility.GetRect(360, 0, GUILayout.Width(360), GUILayout.ExpandHeight(true));
            float totalH = _visibleIndices.Count * RowHeight;
            var viewRect = new Rect(0, 0, areaRect.width - 16, Mathf.Max(totalH, areaRect.height));

            _listScroll = GUI.BeginScrollView(areaRect, _listScroll, viewRect);

            if (_visibleIndices.Count == 0)
            {
                GUI.Label(new Rect(8, 8, viewRect.width - 16, 40),
                          "No entries match the current filters.", EditorStyles.miniLabel);
            }
            else
            {
                int first = Mathf.Max(0, Mathf.FloorToInt(_listScroll.y / RowHeight) - 2);
                int last  = Mathf.Min(_visibleIndices.Count,
                                      first + Mathf.CeilToInt(areaRect.height / RowHeight) + 4);

                for (int i = first; i < last; i++)
                {
                    int entryIdx = _visibleIndices[i];
                    var e = _plan.Entries[entryIdx];
                    var rowRect = new Rect(0, i * RowHeight, viewRect.width, RowHeight - 2);

                    bool selected = _selectedIndex == entryIdx;
                    var prev = GUI.backgroundColor;
                    GUI.backgroundColor = StatusColor(e.Status);
                    GUI.Box(rowRect, GUIContent.none, EditorStyles.helpBox);
                    GUI.backgroundColor = prev;
                    if (selected)
                        EditorGUI.DrawRect(new Rect(rowRect.x, rowRect.y, 3f, rowRect.height), new Color(0.24f, 0.49f, 0.91f));

                    var clickRect = rowRect;
                    var evt = Event.current;
                    if (evt.type == EventType.MouseDown && evt.button == 0 && clickRect.Contains(evt.mousePosition))
                    {
                        _selectedIndex = entryIdx;
                        LoadPreviewForSelection();
                        evt.Use();
                        Repaint();
                    }

                    GUI.Label(new Rect(rowRect.x + 6, rowRect.y + 2, rowRect.width - 12, 16),
                              "[" + ShortStatus(e.Status) + "] " + e.Incoming.FileName,
                              EditorStyles.boldLabel);
                    GUI.Label(new Rect(rowRect.x + 6, rowRect.y + 18, rowRect.width - 12, 16),
                              e.Incoming.RelativePath,
                              EditorStyles.miniLabel);
                }
            }

            GUI.EndScrollView();
        }

        void DrawDetailPane()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_selectedIndex < 0 || _selectedIndex >= _plan.Entries.Count)
                {
                    EditorGUILayout.HelpBox("Select an entry on the left.", MessageType.Info);
                    return;
                }

                var e = _plan.Entries[_selectedIndex];
                EditorGUILayout.LabelField("Incoming:", e.Incoming.RelativePath);
                EditorGUILayout.LabelField("Current:",  e.CurrentMatch?.RelativePath ?? "(none)");
                EditorGUILayout.LabelField("Status:",   e.Status.ToString() + (e.Note != null ? "   · " + e.Note : ""));
                EditorGUILayout.LabelField("Kind:",     e.Incoming.Kind.ToString());
                if (e.Incoming.Guid != null || e.CurrentMatch?.Guid != null)
                    EditorGUILayout.LabelField("GUID:",  $"incoming={e.Incoming.Guid}   current={e.CurrentMatch?.Guid}");
                EditorGUILayout.LabelField("MD5:",      $"incoming={Short(e.Incoming.Md5)}   current={Short(e.CurrentMatch?.Md5)}");
                EditorGUILayout.LabelField("Size:",     $"incoming={e.Incoming.Size}   current={e.CurrentMatch?.Size ?? 0}");

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Resolution", EditorStyles.boldLabel);
                var allowed = AllowedResolutions(e.Status);
                int selected = Mathf.Max(0, Array.IndexOf(allowed, e.Resolution));
                int newSel = EditorGUILayout.Popup(selected, allowed.Select(a => a.ToString()).ToArray());
                if (newSel != selected) e.Resolution = allowed[newSel];

                if (e.Incoming.Kind == AssetKind.Script && e.Resolution == Resolution.KeepBoth)
                {
                    EditorGUILayout.HelpBox(
                        "KeepBoth on a script: the copy will be wrapped in namespace `" + _plan.WrappedNamespace +
                        "` (toggleable in the Review tab) so C# types don't collide.",
                        MessageType.Info);
                }

                EditorGUILayout.Space();
                DrawDiffPreview(e);
            }
        }

        Resolution[] AllowedResolutions(EntryStatus s)
        {
            switch (s)
            {
                case EntryStatus.Identical:
                    return new[] { Resolution.KeepCurrent };
                case EntryStatus.NewAsset:
                    return new[] { Resolution.ImportAsNew, Resolution.KeepCurrent };
                case EntryStatus.RemapOnly:
                    return new[] { Resolution.RemapGuid, Resolution.KeepBoth, Resolution.KeepCurrent };
                case EntryStatus.NearDuplicate:
                    return new[] { Resolution.Undecided, Resolution.KeepCurrent, Resolution.KeepBoth, Resolution.Overwrite, Resolution.RemapGuid };
                default:
                    return new[] { Resolution.Undecided, Resolution.KeepCurrent, Resolution.Overwrite, Resolution.KeepBoth };
            }
        }

        void DrawDiffPreview(MergeEntry e)
        {
            EditorGUILayout.LabelField("Side-by-side", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawSide("Incoming", e.Incoming, ref _leftPreview, ref _leftText, ref _leftDiffScroll);
                DrawSide("Current",  e.CurrentMatch, ref _rightPreview, ref _rightText, ref _rightDiffScroll);
            }
        }

        void DrawSide(string label, AssetRecord rec, ref Texture2D previewCache, ref string textCache, ref Vector2 scroll)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.MinWidth(200)))
            {
                EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
                if (rec == null) { EditorGUILayout.LabelField("(none)"); return; }

                if (rec.Kind == AssetKind.Texture)
                {
                    if (previewCache == null) previewCache = LoadTexPreview(rec.AbsolutePath);
                    if (previewCache != null)
                    {
                        var r = GUILayoutUtility.GetRect(180, 180, GUILayout.ExpandWidth(true));
                        GUI.DrawTexture(r, previewCache, ScaleMode.ScaleToFit);
                    }
                    EditorGUILayout.LabelField($"{previewCache?.width}x{previewCache?.height}  {rec.Size} B");
                    return;
                }

                if (rec.IsTextYaml || rec.Kind == AssetKind.Script || rec.Kind == AssetKind.ProjectSetting ||
                    rec.Kind == AssetKind.Shader)
                {
                    if (textCache == null)
                    {
                        try { textCache = SafeReadHead(rec.AbsolutePath, 16 * 1024); }
                        catch { textCache = "(unreadable)"; }
                    }
                    scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MinHeight(220));
                    EditorGUILayout.TextArea(textCache, GUILayout.ExpandHeight(true));
                    EditorGUILayout.EndScrollView();
                    return;
                }

                EditorGUILayout.LabelField($"{rec.Size} bytes");
                EditorGUILayout.LabelField($"md5 {Short(rec.Md5)}");
            }
        }

        void LoadPreviewForSelection()
        {
            _leftPreview = _rightPreview = null;
            _leftText = _rightText = null;
        }

        static Texture2D LoadTexPreview(string absPath)
        {
            try
            {
                var bytes = File.ReadAllBytes(absPath);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                return tex.LoadImage(bytes) ? tex : null;
            }
            catch { return null; }
        }

        static string SafeReadHead(string absPath, int maxBytes)
        {
            using (var fs = File.OpenRead(absPath))
            {
                var len = (int)Math.Min(fs.Length, maxBytes);
                var buf = new byte[len];
                fs.Read(buf, 0, len);
                var s = System.Text.Encoding.UTF8.GetString(buf);
                if (fs.Length > maxBytes) s += "\n\n… (truncated)";
                return s;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Step 4 · Review — pinned Apply footer, wrap toggle
        // ─────────────────────────────────────────────────────────────
        void DrawReviewStep()
        {
            if (_plan == null) { EditorGUILayout.HelpBox("No plan.", MessageType.Warning); return; }

            EditorGUILayout.LabelField("Review & apply", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                $"Assets/… → staged under Assets/_Imported/{_plan.IncomingProjectName}/.\n" +
                "ProjectSettings/… → mirrored into ProjectSettings/.\n" +
                "Packages/… → mirrored into Packages/ (embedded packages preserved, manifest.json unioned).\n" +
                "Nothing is touched until you hit Apply.",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.BeginVertical();
                EditorGUILayout.LabelField("Identical (skip):",  _plan.Count(EntryStatus.Identical).ToString());
                EditorGUILayout.LabelField("Remap only:",        _plan.Count(EntryStatus.RemapOnly).ToString());
                EditorGUILayout.LabelField("New:",               _plan.Count(EntryStatus.NewAsset).ToString());
                EditorGUILayout.EndVertical();
                EditorGUILayout.BeginVertical();
                int totalConf = _plan.Count(EntryStatus.ConflictByPath) +
                                _plan.Count(EntryStatus.ConflictByGuid) +
                                _plan.Count(EntryStatus.ProjectSettingConflict);
                EditorGUILayout.LabelField("Conflicts (all):",   totalConf.ToString());
                EditorGUILayout.LabelField("Near-duplicates:",   _plan.Count(EntryStatus.NearDuplicate).ToString());
                EditorGUILayout.LabelField("Undecided:",         _plan.UndecidedConflicts().ToString());
                EditorGUILayout.LabelField("GUID remaps:",       _plan.GuidRemap.Count.ToString());
                EditorGUILayout.LabelField("Asmdef renames:",    _plan.AsmdefNameRemap.Count.ToString());
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.Space();

            // Script-isolation toggle. Forced-on when any script is KeepBoth (otherwise CS0101 errors).
            if (_plan.HasScriptKeepBoth()) _plan.WrapImportedScripts = true;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Imported-script isolation", EditorStyles.boldLabel);

                bool forced = _plan.HasScriptKeepBoth();
                using (new EditorGUI.DisabledScope(forced))
                {
                    _plan.WrapImportedScripts = EditorGUILayout.ToggleLeft(
                        forced
                            ? "Wrap imported scripts in namespace (forced — KeepBoth on a script)"
                            : "Wrap imported scripts in namespace (prevents C# type collisions)",
                        _plan.WrapImportedScripts);
                }

                using (new EditorGUI.DisabledScope(!_plan.WrapImportedScripts))
                {
                    var entered = EditorGUILayout.TextField("Namespace", _plan.WrappedNamespace ?? "");
                    if (string.IsNullOrEmpty(entered))
                        _plan.WrappedNamespace = "Imported_" + ScriptNamespaceWrapper.SanitizeNamespace(_plan.IncomingProjectName);
                    else
                        _plan.WrappedNamespace = ScriptNamespaceWrapper.SanitizeNamespace(entered);
                }

                EditorGUILayout.HelpBox(
                    "Scripts in Assembly-CSharp get their namespace rewritten — existing `namespace X` becomes " +
                    "`namespace " + (_plan.WrappedNamespace ?? "") + ".X`; files with no namespace get a new block " +
                    "inserted after the usings. Cross-file references (`using X;`, `X.Foo`) are refactored in a " +
                    "second pass.\n\n" +
                    "Scripts under Packages/ are NOT wrapped — they're already assembly-isolated. Scripts under " +
                    "an .asmdef are skipped TOO unless that asmdef collides with a host asmdef name (see below) — " +
                    "in that case its scripts get wrapped as well so they end up under the renamed assembly's " +
                    "namespace. Unity resolves scene/prefab/SO refs by GUID so those keep working.",
                    MessageType.Info);

                if (_plan.AsmdefNameRemap.Count > 0)
                {
                    var sample = new System.Text.StringBuilder();
                    int shown = 0;
                    foreach (var kv in _plan.AsmdefNameRemap)
                    {
                        if (shown++ >= 4) { sample.Append("\n  …"); break; }
                        sample.Append("\n  ").Append(kv.Key).Append(" → ").Append(kv.Value);
                    }
                    EditorGUILayout.HelpBox(
                        _plan.AsmdefNameRemap.Count + " incoming .asmdef name(s) collide with host asmdefs and " +
                        "will be renamed on apply. Their `name` + `rootNamespace` are rewritten in the staged " +
                        "copy, scripts under them are added to the wrap pass, and any other staged .asmdef " +
                        "referencing them by name has those entries updated too." + sample,
                        MessageType.Warning);
                }
            }

            EditorGUILayout.Space();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Export report (.md)…", GUILayout.Height(24)))
                    SaveReport("md", DryRunReport.BuildMarkdown(_plan));
                if (GUILayout.Button("Export report (.csv)…", GUILayout.Height(24)))
                    SaveReport("csv", DryRunReport.BuildCsv(_plan));
                if (GUILayout.Button("Dry run", GUILayout.Height(24)))
                    RunExecution(dryRun: true);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Execution log", EditorStyles.boldLabel);
            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.Height(140));
            EditorGUILayout.TextArea(string.Join("\n", _executionLog), GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();

            GUILayout.FlexibleSpace();

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                int undecided = _plan.UndecidedConflicts();
                GUILayout.Label(undecided == 0
                        ? "Ready to apply."
                        : $"{undecided} undecided conflict(s) — resolve before Apply.",
                    EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();

                GUI.backgroundColor = undecided == 0 ? new Color(0.6f, 1f, 0.6f) : Color.white;
                using (new EditorGUI.DisabledScope(undecided > 0))
                {
                    if (GUILayout.Button(
                            undecided > 0 ? "Resolve conflicts first" : "APPLY merge",
                            GUILayout.Height(32), GUILayout.Width(220)))
                    {
                        if (EditorUtility.DisplayDialog("Apply merge?",
                                "Stage and apply the merge now? Changes are NOT reversible via this tool — make sure version control is committed.",
                                "Apply", "Cancel"))
                            RunExecution(dryRun: false);
                    }
                }
                GUI.backgroundColor = Color.white;
            }
        }

        void SaveReport(string ext, string content)
        {
            var path = EditorUtility.SaveFilePanel("Export merge report", "", $"merge_{_plan.IncomingProjectName}", ext);
            if (!string.IsNullOrEmpty(path))
            {
                DryRunReport.WriteToFile(path, content);
                EditorUtility.RevealInFinder(path);
            }
        }

        void RunExecution(bool dryRun)
        {
            _executionLog.Clear();
            try
            {
                EditorUtility.DisplayProgressBar("Fuze", dryRun ? "Dry run…" : "Merging…", 0f);
                MergeEngine.Execute(
                    _plan,
                    dryRun,
                    (p, m) => EditorUtility.DisplayProgressBar("Fuze", (dryRun ? "[Dry] " : "") + m, p),
                    _executionLog);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        // ─────────────────────────────────────────────────────────────
        // helpers
        // ─────────────────────────────────────────────────────────────
        static string Short(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length <= 10 ? s : s.Substring(0, 10) + "…");

        static string ShortStatus(EntryStatus s)
        {
            switch (s)
            {
                case EntryStatus.Identical: return "=";
                case EntryStatus.NewAsset:  return "+";
                case EntryStatus.RemapOnly: return "⇄";
                case EntryStatus.ConflictByPath: return "!p";
                case EntryStatus.ConflictByGuid: return "!g";
                case EntryStatus.NearDuplicate:  return "~";
                case EntryStatus.ProjectSettingConflict: return "!ps";
                default: return "?";
            }
        }

        static Color StatusColor(EntryStatus s)
        {
            switch (s)
            {
                case EntryStatus.Identical: return new Color(0.7f, 0.7f, 0.7f);
                case EntryStatus.NewAsset:  return new Color(0.75f, 1f, 0.75f);
                case EntryStatus.RemapOnly: return new Color(0.8f, 0.9f, 1f);
                case EntryStatus.NearDuplicate: return new Color(1f, 0.95f, 0.7f);
                default: return new Color(1f, 0.8f, 0.8f);
            }
        }
    }
}
