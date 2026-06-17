using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ProjectMerger
{
    /// <summary>
    /// Finds .cs files that duplicate another .cs file in the project:
    ///  - by normalized content (BOM/CRLF/whitespace-insensitive MD5)
    ///  - by C# type-name collision (same fully-qualified type declared in multiple files)
    /// Lets the user delete picks per group. Useful to clean up after an earlier merge that
    /// produced duplicates because of line-ending differences.
    /// </summary>
    public class ScriptDuplicateFinderWindow : EditorWindow
    {
        class Group
        {
            public string Key;
            public string Reason;
            public List<string> Paths = new List<string>();
            public HashSet<string> ToDelete = new HashSet<string>();
        }

        List<Group> _groups = new List<Group>();
        Vector2 _scroll;

        [MenuItem("Tools/Fuze/Find duplicate scripts…")]
        public static void ShowWindow()
        {
            var w = GetWindow<ScriptDuplicateFinderWindow>("Fuze · Duplicates");
            w.minSize = new Vector2(720, 480);
            w.Show();
        }

        void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Scan Assets/", EditorStyles.toolbarButton, GUILayout.Width(110)))
                    Scan();
                GUILayout.Label($"Groups: {_groups.Count}", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(_groups.Sum(g => g.ToDelete.Count) == 0))
                {
                    if (GUILayout.Button("Delete selected", EditorStyles.toolbarButton, GUILayout.Width(120)))
                        DeleteSelected();
                }
            }

            if (_groups.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Press Scan. Duplicate .cs files will be grouped by (a) normalized content and (b) colliding C# type names.",
                    MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var g in _groups)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField($"[{g.Reason}] {g.Key}   ({g.Paths.Count} files)", EditorStyles.boldLabel);
                    foreach (var p in g.Paths)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            bool marked = g.ToDelete.Contains(p);
                            bool now = EditorGUILayout.ToggleLeft("delete", marked, GUILayout.Width(60));
                            if (now && !marked) g.ToDelete.Add(p);
                            if (!now && marked) g.ToDelete.Remove(p);

                            if (GUILayout.Button(p, EditorStyles.linkLabel))
                            {
                                var asset = AssetDatabase.LoadMainAssetAtPath(p);
                                if (asset != null) EditorGUIUtility.PingObject(asset);
                            }
                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button("Open", GUILayout.Width(50)))
                                AssetDatabase.OpenAsset(AssetDatabase.LoadMainAssetAtPath(p));
                        }
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }

        void Scan()
        {
            _groups.Clear();
            var assetsDir = Application.dataPath;
            var files = Directory.GetFiles(assetsDir, "*.cs", SearchOption.AllDirectories);

            var byHash = new Dictionary<string, List<string>>();
            var byType = new Dictionary<string, List<string>>();

            int total = files.Length;
            for (int i = 0; i < total; i++)
            {
                if (i % 20 == 0)
                    EditorUtility.DisplayProgressBar("Fuze · Duplicates", files[i], (float)i / total);

                var abs = files[i];
                var rel = ("Assets" + abs.Substring(assetsDir.Length)).Replace('\\', '/');
                var nhash = HashUtil.Md5NormalizedText(abs);
                if (!string.IsNullOrEmpty(nhash))
                {
                    if (!byHash.TryGetValue(nhash, out var list))
                        byHash[nhash] = list = new List<string>();
                    list.Add(rel);
                }

                var types = new List<string>();
                ExtractTypes(abs, types);
                foreach (var t in types)
                {
                    if (!byType.TryGetValue(t, out var list))
                        byType[t] = list = new List<string>();
                    list.Add(rel);
                }
            }
            EditorUtility.ClearProgressBar();

            foreach (var kv in byHash)
            {
                if (kv.Value.Count > 1)
                    _groups.Add(new Group { Key = kv.Key.Substring(0, 10) + "…", Reason = "Content", Paths = kv.Value.Distinct().ToList() });
            }
            foreach (var kv in byType)
            {
                if (kv.Value.Distinct().Count() > 1)
                    _groups.Add(new Group { Key = kv.Key, Reason = "Type", Paths = kv.Value.Distinct().ToList() });
            }

            _groups = _groups
                .OrderByDescending(g => g.Reason == "Content")
                .ThenBy(g => g.Key)
                .ToList();
        }

        void DeleteSelected()
        {
            var toDelete = _groups.SelectMany(g => g.ToDelete).Distinct().ToList();
            if (toDelete.Count == 0) return;
            if (!EditorUtility.DisplayDialog("Delete scripts?",
                    $"Delete {toDelete.Count} script file(s)? This will also remove their .meta files.",
                    "Delete", "Cancel")) return;

            foreach (var p in toDelete)
                AssetDatabase.DeleteAsset(p);
            AssetDatabase.Refresh();
            Scan();
        }

        static void ExtractTypes(string csPath, List<string> into)
        {
            string text;
            try { text = File.ReadAllText(csPath); } catch { return; }

            text = System.Text.RegularExpressions.Regex.Replace(text, @"//.*?$", "",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"/\*.*?\*/", "",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            string ns = null;
            var nsMatch = System.Text.RegularExpressions.Regex.Match(text, @"\bnamespace\s+([A-Za-z_][\w\.]*)");
            if (nsMatch.Success) ns = nsMatch.Groups[1].Value;

            var typeRegex = new System.Text.RegularExpressions.Regex(
                @"\b(?:public\s+|internal\s+|private\s+|protected\s+|static\s+|sealed\s+|abstract\s+|partial\s+)*" +
                @"(?:class|struct|interface|enum|record)\s+([A-Za-z_]\w*)");
            foreach (System.Text.RegularExpressions.Match m in typeRegex.Matches(text))
            {
                var name = m.Groups[1].Value;
                into.Add(string.IsNullOrEmpty(ns) ? name : ns + "." + name);
            }
        }
    }
}
