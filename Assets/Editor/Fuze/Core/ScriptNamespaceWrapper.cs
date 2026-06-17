using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ProjectMerger
{
    /// <summary>
    /// Isolates imported C# source files from the current project so their types
    /// can't collide with existing ones.
    ///
    /// Two behaviours:
    ///   • File has existing namespace(s) (plugin / package code): each
    ///     `namespace X[.Y.Z]` declaration is rewritten to `namespace PREFIX.X[.Y.Z]`.
    ///     File-scoped `namespace X;` is handled in place too.
    ///   • File has no namespace: a `namespace PREFIX { … }` block is inserted
    ///     AFTER the using directives and before the type declarations, wrapping
    ///     only the body.
    ///
    /// The set of unique top-level roots encountered (X, Y, Z, …) is returned so
    /// that callers can run <see cref="RewriteReferences"/> across every copied
    /// script to fix up `using X;`, `using static X.Y;` and `X.Foo` references.
    ///
    /// Unity scene/prefab/ScriptableObject references are GUID-based and unaffected
    /// by namespace changes — Unity re-reads m_Namespace / m_ClassName from the
    /// updated MonoScript on import.
    /// </summary>
    public static class ScriptNamespaceWrapper
    {
        public struct WrapResult
        {
            public string Source;
            public HashSet<string> Roots;           // original top-level namespace roots detected in the file
            public HashSet<string> TopLevelTypes;   // top-level type names from files with no namespace
                                                    // (those types are now under `PREFIX`, so short-name
                                                    // references to them in other wrapped files must be
                                                    // prefixed too — e.g. `using static script2;`)
            public bool   HadNamespace;             // false → we inserted a new namespace block
        }

        public static string SanitizeNamespace(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "Imported";
            var sb = new StringBuilder();
            foreach (var c in raw)
            {
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
                else if (c == '.' || c == '/' || c == '\\' || c == '-' || c == ' ') sb.Append('_');
            }
            var s = sb.ToString().Trim('_');
            if (s.Length == 0) return "Imported";
            if (char.IsDigit(s[0])) s = "_" + s;
            return s;
        }

        // Block-form namespace declarations: `namespace A.B.C {`
        static readonly Regex NsBlockRegex =
            new Regex(@"(^|\n)(\s*)namespace\s+([A-Za-z_][\w\.]*)(\s*)\{", RegexOptions.Compiled);

        // File-scoped namespace declarations (C# 10+): `namespace A.B.C;`
        static readonly Regex NsFileRegex =
            new Regex(@"(^|\n)(\s*)namespace\s+([A-Za-z_][\w\.]*)\s*;", RegexOptions.Compiled);

        // Top-level namespace roots that belong to Unity, .NET, or well-known third-party SDKs.
        // Classes under these roots already exist in the current project, so prefixing them
        // with the wrapper namespace would turn valid references into broken ones
        // (e.g. `UnityEngine.UI.Button` → `PREFIX.UnityEngine.UI.Button`, which doesn't exist).
        static readonly HashSet<string> WellKnownRoots = new HashSet<string>
        {
            "System", "Microsoft", "Mono",
            "UnityEngine", "UnityEditor", "Unity", "UnityEngineInternal", "UnityEditorInternal",
            "TMPro", "Cinemachine", "TreeEditor",
            "JetBrains", "NUnit", "NuGet",
            "Newtonsoft",
            "Google",
        };

        static bool IsWellKnownRoot(string root) => WellKnownRoots.Contains(root);

        public static WrapResult Wrap(string source, string prefixNamespace)
        {
            var result = new WrapResult
            {
                Source         = source,
                Roots          = new HashSet<string>(),
                TopLevelTypes  = new HashSet<string>(),
                HadNamespace   = false
            };

            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(prefixNamespace))
                return result;

            string bom = "";
            if (source.Length > 0 && source[0] == '\uFEFF')
            {
                bom = "\uFEFF";
                source = source.Substring(1);
            }

            string eol = source.Contains("\r\n") ? "\r\n" : "\n";

            // Collect roots from any existing namespace declarations — but only for roots that
            // belong to the imported project. Unity / .NET / third-party SDK roots are skipped
            // (their classes already exist in the current project; wrapping their references
            // would break resolution).
            bool hadAny = false;
            bool hadWrappable = false;
            foreach (Match m in NsBlockRegex.Matches(source))
            {
                var full = m.Groups[3].Value;
                // Skip if already prefixed (idempotent when re-running).
                if (full == prefixNamespace || full.StartsWith(prefixNamespace + ".")) continue;
                hadAny = true;
                var root = full.Split('.')[0];
                if (IsWellKnownRoot(root)) continue;
                result.Roots.Add(root);
                hadWrappable = true;
            }
            foreach (Match m in NsFileRegex.Matches(source))
            {
                var full = m.Groups[3].Value;
                if (full == prefixNamespace || full.StartsWith(prefixNamespace + ".")) continue;
                hadAny = true;
                var root = full.Split('.')[0];
                if (IsWellKnownRoot(root)) continue;
                result.Roots.Add(root);
                hadWrappable = true;
            }

            if (hadAny)
            {
                // Rename existing namespaces in place — but leave well-known roots alone
                // so `namespace UnityEngine.X { … }` stays resolvable against Unity.
                source = NsBlockRegex.Replace(source, m =>
                {
                    var full = m.Groups[3].Value;
                    if (full == prefixNamespace || full.StartsWith(prefixNamespace + ".")) return m.Value;
                    if (IsWellKnownRoot(full.Split('.')[0])) return m.Value;
                    return m.Groups[1].Value + m.Groups[2].Value +
                           "namespace " + prefixNamespace + "." + full + m.Groups[4].Value + "{";
                });
                source = NsFileRegex.Replace(source, m =>
                {
                    var full = m.Groups[3].Value;
                    if (full == prefixNamespace || full.StartsWith(prefixNamespace + ".")) return m.Value;
                    if (IsWellKnownRoot(full.Split('.')[0])) return m.Value;
                    return m.Groups[1].Value + m.Groups[2].Value +
                           "namespace " + prefixNamespace + "." + full + ";";
                });
                result.HadNamespace = hadWrappable;
            }
            else
            {
                // No namespace: wrap the body in `namespace PREFIX { … }` AFTER usings.
                // Also record the file's top-level type names so that short-name references
                // to them in OTHER wrapped files (e.g. `using static script2;`) get prefixed.
                CollectTopLevelTypeNames(source, result.TopLevelTypes);
                source = InsertNamespaceAfterUsings(source, prefixNamespace, eol);
                result.HadNamespace = false;
            }

            result.Source = bom + source;
            return result;
        }

        // Matches actual C# type declarations (not `using ...;` / attributes / strings).
        static readonly Regex TypeDeclLineRegex =
            new Regex(@"\b(class|struct|interface|enum|record)\s+[A-Za-z_]\w*", RegexOptions.Compiled);

        /// <summary>
        /// Walks `source` and records the names of all type declarations at brace depth 0.
        /// Skips nested types, strings, char literals, and line/block comments so that
        /// `// class Foo` or `"class Foo"` don't produce false positives. Runs only on
        /// files that had no namespace — after wrapping, those top-level types live
        /// under PREFIX, so other wrapped files referencing them by short name need
        /// the prefix applied.
        /// </summary>
        static void CollectTopLevelTypeNames(string source, HashSet<string> into)
        {
            if (string.IsNullOrEmpty(source) || into == null) return;

            int i = 0, len = source.Length;
            int depth = 0;

            while (i < len)
            {
                char c = source[i];

                // Line comment
                if (c == '/' && i + 1 < len && source[i + 1] == '/')
                {
                    while (i < len && source[i] != '\n') i++;
                    continue;
                }
                // Block comment
                if (c == '/' && i + 1 < len && source[i + 1] == '*')
                {
                    i += 2;
                    while (i < len)
                    {
                        if (source[i] == '*' && i + 1 < len && source[i + 1] == '/') { i += 2; break; }
                        i++;
                    }
                    continue;
                }
                // Verbatim / interpolated verbatim string: @"…" / $@"…" / @$"…"
                if ((c == '@' || c == '$') && i + 1 < len)
                {
                    bool isVerbatim = (c == '@' && source[i + 1] == '"');
                    bool isInterpVerbatim =
                        (c == '$' && i + 2 < len && source[i + 1] == '@' && source[i + 2] == '"') ||
                        (c == '@' && i + 2 < len && source[i + 1] == '$' && source[i + 2] == '"');

                    if (isVerbatim || isInterpVerbatim)
                    {
                        int openLen = isInterpVerbatim ? 3 : 2;
                        i += openLen;
                        while (i < len)
                        {
                            if (source[i] == '"' && i + 1 < len && source[i + 1] == '"') { i += 2; continue; }
                            if (source[i] == '"') { i++; break; }
                            i++;
                        }
                        continue;
                    }
                }
                // Regular string
                if (c == '"')
                {
                    i++;
                    while (i < len)
                    {
                        if (source[i] == '\\' && i + 1 < len) { i += 2; continue; }
                        if (source[i] == '"') { i++; break; }
                        i++;
                    }
                    continue;
                }
                // Char literal
                if (c == '\'')
                {
                    i++;
                    while (i < len)
                    {
                        if (source[i] == '\\' && i + 1 < len) { i += 2; continue; }
                        if (source[i] == '\'') { i++; break; }
                        i++;
                    }
                    continue;
                }

                if (c == '{') { depth++; i++; continue; }
                if (c == '}') { if (depth > 0) depth--; i++; continue; }

                // Only consider identifiers at top level (depth 0).
                if (depth == 0 && IsIdentStart(c) && (i == 0 || !IsIdentCont(source[i - 1])))
                {
                    int kwStart = i;
                    while (i < len && IsIdentCont(source[i])) i++;
                    var kw = source.Substring(kwStart, i - kwStart);

                    if (kw == "class" || kw == "struct" || kw == "interface" || kw == "enum" || kw == "record")
                    {
                        // Skip whitespace between keyword and the type name.
                        while (i < len && (source[i] == ' ' || source[i] == '\t' || source[i] == '\r' || source[i] == '\n')) i++;
                        if (i < len && IsIdentStart(source[i]))
                        {
                            int nameStart = i;
                            while (i < len && IsIdentCont(source[i])) i++;
                            var name = source.Substring(nameStart, i - nameStart);
                            if (!IsWellKnownRoot(name)) into.Add(name);
                        }
                    }
                    continue;
                }

                i++;
            }
        }

        static string InsertNamespaceAfterUsings(string source, string prefix, string eol)
        {
            var lines = source.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None);

            // Walk top-of-file lines, treating only real using DIRECTIVES (not `using (...)` statements
            // or `using var x = ...;` statements inside methods) as preamble. Stop at the first actual
            // type declaration — any `using` keyword after that point must be inside a method body.
            int lastPreambleIdx = -1;
            bool inAssemblyAttrSpan = false;
            int bracketDepth = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].TrimStart();

                // Strip the `// …` tail so e.g. `// class X` can't falsely trigger the type detector.
                var u = StripLineComment(t);

                // Track open `[…]` spans so multi-line attributes don't prematurely end the preamble.
                bracketDepth += CountChar(u, '[') - CountChar(u, ']');
                if (bracketDepth < 0) bracketDepth = 0;

                if (inAssemblyAttrSpan)
                {
                    lastPreambleIdx = i;
                    if (u.Contains("]")) inAssemblyAttrSpan = false;
                    continue;
                }

                if (u.StartsWith("[assembly:") || u.StartsWith("[module:"))
                {
                    lastPreambleIdx = i;
                    if (!u.Contains("]")) inAssemblyAttrSpan = true;
                    continue;
                }

                // First real type declaration → stop. This is the guard that keeps
                // `using (StreamWriter w = …)` inside a method from being misread.
                if (bracketDepth == 0 && TypeDeclLineRegex.IsMatch(u))
                    break;

                if (IsUsingDirective(u) || u.StartsWith("extern alias"))
                {
                    lastPreambleIdx = i;
                    continue;
                }

                // Other lines (blank, comments, `[CreateAssetMenu]` on a type, `#if`, `#pragma`) are
                // allowed between usings and the class — we neither advance the insertion point nor stop.
            }

            int insertAt = lastPreambleIdx + 1;

            // The namespace must open and close at the SAME #if/#endif nesting level, with
            // both braces balanced inside whatever conditional blocks span them. Two
            // failure modes to avoid for editor scripts shaped like
            //     #if UNITY_EDITOR
            //     using UnityEditor;
            //     [#endif]
            //     public class Foo { }
            //     [#endif]
            //
            //   • `namespace {` left inside an open #if while `}` is appended past EOF →
            //     the } leaks outside the #endif (CS1022 in builds without the symbol).
            //   • `namespace {` emitted between the using and an #endif that closes right
            //     after it → `{` ends up inside the #if while the body sits outside.
            //
            // Fix in two steps: (1) measure the preprocessor depth at the insertion point
            // and, if it sits inside an open #if, slide forward past blank lines and the
            // #endif(s) that close those preamble #if blocks so the namespace opens at
            // baseline depth; (2) close the namespace after the LAST body line still at
            // that depth, leaving trailing #endif(s) unwrapped.
            int depthAtInsert = 0;
            for (int i = 0; i < insertAt; i++)
                depthAtInsert += PreprocessorIfDelta(lines[i]);

            // (1) Slide the insertion point out of any preamble #if block. Blank lines and
            // the #endif(s) closing those blocks are absorbed into the preamble; we stop at
            // the first real content line (comment / attribute / type / a fresh #if) —
            // that belongs WITH the type declaration, inside the namespace.
            while (depthAtInsert > 0 && insertAt < lines.Length)
            {
                int delta  = PreprocessorIfDelta(lines[insertAt]);
                bool blank = lines[insertAt].TrimStart().Length == 0;
                if (!blank && delta >= 0)
                    break;
                depthAtInsert += delta;
                insertAt++;
            }

            // (2) Find where the closing brace goes.
            int closeAfter   = insertAt - 1;
            int runningDepth = depthAtInsert;
            for (int i = insertAt; i < lines.Length; i++)
            {
                runningDepth += PreprocessorIfDelta(lines[i]);
                if (runningDepth == depthAtInsert)
                    closeAfter = i;
            }
            // Degenerate / unbalanced input (depth never returns to the insertion level):
            // fall back to wrapping through end-of-file.
            if (closeAfter < insertAt)
                closeAfter = lines.Length - 1;

            var sb = new StringBuilder();
            for (int i = 0; i < insertAt; i++) sb.Append(lines[i]).Append(eol);

            // Blank line between preamble and the new namespace for readability.
            if (insertAt > 0) sb.Append(eol);

            sb.Append("namespace ").Append(prefix).Append(eol);
            sb.Append("{").Append(eol);
            for (int i = insertAt; i <= closeAfter; i++)
                sb.Append(lines[i]).Append(eol);
            sb.Append("}").Append(eol);

            // Trailing lines past the closing brace — the #endif(s) that close preamble
            // #if blocks, plus any final blank line — are emitted unwrapped.
            for (int i = closeAfter + 1; i < lines.Length; i++)
            {
                sb.Append(lines[i]);
                if (i < lines.Length - 1) sb.Append(eol);
            }
            return sb.ToString();
        }

        /// <summary>
        /// +1 when <paramref name="line"/> is a <c>#if</c> directive, -1 for <c>#endif</c>,
        /// 0 for anything else (<c>#else</c> / <c>#elif</c> leave nesting depth unchanged).
        /// Tolerates whitespace between <c>#</c> and the keyword. Line-based heuristic: a
        /// <c>#if</c> token buried inside a block comment would be miscounted, but real
        /// Unity source keeps preprocessor directives on their own lines, so this holds in
        /// practice — and matches the cheap line-based approach used elsewhere here.
        /// </summary>
        static int PreprocessorIfDelta(string line)
        {
            if (string.IsNullOrEmpty(line)) return 0;
            int i = 0, n = line.Length;
            while (i < n && (line[i] == ' ' || line[i] == '\t')) i++;
            if (i >= n || line[i] != '#') return 0;
            i++;
            while (i < n && (line[i] == ' ' || line[i] == '\t')) i++;
            int kwStart = i;
            while (i < n && char.IsLetter(line[i])) i++;
            var kw = line.Substring(kwStart, i - kwStart);
            if (kw == "if")    return +1;
            if (kw == "endif") return -1;
            return 0;
        }

        /// <summary>
        /// True only for actual using DIRECTIVES:
        ///   • `using X;`, `using X.Y.Z;`
        ///   • `using static X.Y;`
        ///   • `using Foo = X.Y;`
        ///   • `global using …;`
        /// Rejects using STATEMENTS like `using (resource) { … }` (C# 8+ `using var` is always
        /// inside a method body, so it's reached only after the type-declaration guard trips).
        /// </summary>
        static bool IsUsingDirective(string t)
        {
            if (t.StartsWith("global ")) t = t.Substring(7).TrimStart();
            if (!t.StartsWith("using")) return false;
            if (t.Length < 6) return false;
            if (t[5] != ' ' && t[5] != '\t') return false;

            int i = 6;
            while (i < t.Length && (t[i] == ' ' || t[i] == '\t')) i++;
            if (i >= t.Length) return false;

            // `using (` is a statement, not a directive.
            if (t[i] == '(') return false;
            return true;
        }

        static string StripLineComment(string s)
        {
            // Cheap — doesn't understand `//` appearing inside a string literal, but those are
            // very uncommon at file-scope preamble lines.
            int idx = s.IndexOf("//", System.StringComparison.Ordinal);
            return idx < 0 ? s : s.Substring(0, idx);
        }

        static int CountChar(string s, char c)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++) if (s[i] == c) n++;
            return n;
        }

        /// <summary>
        /// Prefixes standalone occurrences of imported-project identifiers with <paramref name="prefix"/>:
        ///   `using Vendor.Lib;`        → `using PREFIX.Vendor.Lib;`
        ///   `Vendor.Lib.Foo`           → `PREFIX.Vendor.Lib.Foo`
        ///   `using static script2;`    → `using static PREFIX.script2;`   (top-level type only)
        ///
        /// Namespace roots (<paramref name="namespaceRoots"/>) are prefixed everywhere — they
        /// qualify paths, so a fully-qualified `OtherNs.Foo` inside a method body must still
        /// become `PREFIX.OtherNs.Foo` after the namespace was renamed.
        ///
        /// Top-level type names (<paramref name="topLevelTypes"/>, from files that had no
        /// namespace and got wrapped into PREFIX) are only prefixed OUTSIDE type bodies —
        /// inside a class/struct/interface/enum/record, short-name references resolve through
        /// the enclosing namespace, and prefixing would wrongly rewrite fields, locals, and
        /// variables that happen to share a type name.
        ///
        /// Implemented as a small hand-rolled tokenizer so that matches inside
        /// string literals, verbatim strings, interpolated strings, char literals,
        /// line comments and block comments are NOT rewritten. Also skips:
        ///   • identifiers preceded by `.` (already qualified)
        ///   • identifiers followed by `::` (alias qualifier — invalid on multi-part namespaces)
        ///   • the declaration name in `class Foo { … }` / `struct` / `interface` / `enum` / `record`
        /// </summary>
        public static string RewriteReferences(
            string source,
            IReadOnlyCollection<string> namespaceRoots,
            IReadOnlyCollection<string> topLevelTypes,
            string prefix)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(prefix)) return source;
            int rootCount = namespaceRoots?.Count ?? 0;
            int typeCount = topLevelTypes?.Count ?? 0;
            if (rootCount == 0 && typeCount == 0) return source;

            // Defense-in-depth: even if the caller passes a well-known root (UnityEngine, System, …),
            // never rewrite references to it. Those classes already exist in the host project, so
            // prefixing them would break compilation.
            var rootSet = new HashSet<string>();
            if (namespaceRoots != null)
                foreach (var r in namespaceRoots)
                    if (!IsWellKnownRoot(r)) rootSet.Add(r);
            var typeSet = new HashSet<string>();
            if (topLevelTypes != null)
                foreach (var t in topLevelTypes)
                    if (!IsWellKnownRoot(t)) typeSet.Add(t);
            if (rootSet.Count == 0 && typeSet.Count == 0) return source;
            var sb = new StringBuilder(source.Length + 64);
            int i = 0, len = source.Length;

            // Scope tracking — we only rewrite identifiers that are OUTSIDE any type body
            // (class/struct/interface/enum/record). Once we enter a type brace, everything
            // inside (fields, properties, method signatures, method bodies, nested blocks,
            // nested types) is left untouched. Namespace braces are tracked too but don't
            // suppress rewriting — `using` / base-type references at namespace scope still
            // need the prefix applied.
            var braceStack = new Stack<bool>(); // true = this brace opened a type body
            int typeScopeDepth = 0;
            bool pendingTypeDecl = false;       // set when `class`/`struct`/`interface`/`enum`/`record` was just seen

            while (i < len)
            {
                char c = source[i];

                // Line comment ------------------------------------------------
                if (c == '/' && i + 1 < len && source[i + 1] == '/')
                {
                    while (i < len && source[i] != '\n') sb.Append(source[i++]);
                    continue;
                }
                // Block comment -----------------------------------------------
                if (c == '/' && i + 1 < len && source[i + 1] == '*')
                {
                    sb.Append(source[i++]); sb.Append(source[i++]);
                    while (i < len)
                    {
                        if (source[i] == '*' && i + 1 < len && source[i + 1] == '/')
                        {
                            sb.Append(source[i++]); sb.Append(source[i++]);
                            break;
                        }
                        sb.Append(source[i++]);
                    }
                    continue;
                }
                // Verbatim / interpolated verbatim string: @"..." or $@"..." or @$"..."
                if ((c == '@' || c == '$') && i + 1 < len)
                {
                    bool isVerbatim = (c == '@' && source[i + 1] == '"');
                    bool isInterpVerbatim =
                        (c == '$' && i + 2 < len && source[i + 1] == '@' && source[i + 2] == '"') ||
                        (c == '@' && i + 2 < len && source[i + 1] == '$' && source[i + 2] == '"');

                    if (isVerbatim || isInterpVerbatim)
                    {
                        // Append the prefix chars ($@ / @$ / @) and opening quote
                        int openLen = isInterpVerbatim ? 3 : 2;
                        for (int k = 0; k < openLen; k++) sb.Append(source[i++]);
                        while (i < len)
                        {
                            if (source[i] == '"' && i + 1 < len && source[i + 1] == '"')
                            {
                                sb.Append(source[i++]); sb.Append(source[i++]);
                                continue;
                            }
                            if (source[i] == '"') { sb.Append(source[i++]); break; }
                            sb.Append(source[i++]);
                        }
                        continue;
                    }
                }
                // Regular string ----------------------------------------------
                if (c == '"')
                {
                    sb.Append(source[i++]);
                    while (i < len)
                    {
                        if (source[i] == '\\' && i + 1 < len)
                        {
                            sb.Append(source[i++]);
                            sb.Append(source[i++]);
                            continue;
                        }
                        if (source[i] == '"') { sb.Append(source[i++]); break; }
                        sb.Append(source[i++]);
                    }
                    continue;
                }
                // Char literal ------------------------------------------------
                if (c == '\'')
                {
                    sb.Append(source[i++]);
                    while (i < len)
                    {
                        if (source[i] == '\\' && i + 1 < len)
                        {
                            sb.Append(source[i++]);
                            sb.Append(source[i++]);
                            continue;
                        }
                        if (source[i] == '\'') { sb.Append(source[i++]); break; }
                        sb.Append(source[i++]);
                    }
                    continue;
                }

                // Brace tracking ---------------------------------------------
                if (c == '{')
                {
                    braceStack.Push(pendingTypeDecl);
                    if (pendingTypeDecl) typeScopeDepth++;
                    pendingTypeDecl = false;
                    sb.Append(c);
                    i++;
                    continue;
                }
                if (c == '}')
                {
                    if (braceStack.Count > 0 && braceStack.Pop()) typeScopeDepth--;
                    sb.Append(c);
                    i++;
                    continue;
                }
                // `;` ends a statement/declaration — a pending type decl that never saw
                // its `{` (e.g. `record Foo;`) is done and should not leak into the next brace.
                if (c == ';')
                {
                    pendingTypeDecl = false;
                    sb.Append(c);
                    i++;
                    continue;
                }

                // Identifier? -------------------------------------------------
                if (IsIdentStart(c))
                {
                    int start = i;
                    while (i < len && IsIdentCont(source[i])) i++;
                    var ident = source.Substring(start, i - start);

                    // Flag type-decl keywords so the next `{` is recognized as a type body.
                    if (ident == "class" || ident == "struct" || ident == "interface" ||
                        ident == "enum"  || ident == "record")
                    {
                        pendingTypeDecl = true;
                        sb.Append(ident);
                        continue;
                    }

                    bool afterDot = start > 0 && source[start - 1] == '.';
                    bool followedByDoubleColon = i + 1 < len && source[i] == ':' && source[i + 1] == ':';

                    // Avoid double-prefixing: look back one segment for `<prefix>.`
                    bool alreadyPrefixed = false;
                    string probe = prefix + ".";
                    if (start >= probe.Length &&
                        source.Substring(start - probe.Length, probe.Length) == probe)
                        alreadyPrefixed = true;

                    // The identifier immediately after `class`/`struct`/`interface`/`enum`/`record`
                    // is a type *declaration*, not a reference — never prefix it, otherwise we'd
                    // emit invalid syntax like `class PREFIX.Foo { … }`.
                    bool isTypeDeclarationName = IsPrecededByTypeDeclKeyword(source, start);

                    bool gateOk = !afterDot && !followedByDoubleColon && !alreadyPrefixed && !isTypeDeclarationName;
                    bool insideTypeScope = typeScopeDepth > 0;

                    // Namespace roots: always prefix when gated — qualified paths like
                    // `OtherNs.Foo` must become `PREFIX.OtherNs.Foo` even inside method bodies.
                    // Top-level types: only prefix at file/namespace scope — inside a type body
                    // short names resolve via the enclosing namespace, and prefixing would
                    // wrongly rewrite fields, locals, and like-named variables.
                    bool prefixMe =
                        gateOk &&
                        ( rootSet.Contains(ident)
                        || (!insideTypeScope && typeSet.Contains(ident)) );

                    if (prefixMe)
                        sb.Append(prefix).Append('.').Append(ident);
                    else
                        sb.Append(ident);
                    continue;
                }

                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }

        static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
        static bool IsIdentCont(char c)  => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>
        /// True when the identifier starting at <paramref name="identStart"/> is the NAME
        /// of a type being declared — i.e. the immediately-preceding token (skipping
        /// whitespace) is one of `class`, `struct`, `interface`, `enum`, `record`.
        /// Used by <see cref="RewriteReferences"/> to avoid prefixing declaration names.
        /// </summary>
        static bool IsPrecededByTypeDeclKeyword(string source, int identStart)
        {
            int j = identStart - 1;
            while (j >= 0 && (source[j] == ' ' || source[j] == '\t' || source[j] == '\r' || source[j] == '\n'))
                j--;
            if (j < 0) return false;
            if (!IsIdentCont(source[j])) return false;

            int kwEnd = j + 1;
            while (j >= 0 && IsIdentCont(source[j])) j--;
            int kwStart = j + 1;
            if (j >= 0 && source[j] == '.') return false; // e.g. `a.class` (invalid, but defensive)

            var kw = source.Substring(kwStart, kwEnd - kwStart);
            return kw == "class" || kw == "struct" || kw == "interface" || kw == "enum" || kw == "record";
        }
    }
}
