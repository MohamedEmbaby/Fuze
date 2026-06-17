using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ProjectMerger
{
    /// <summary>
    /// Rewrites assembly-qualified type names embedded in Unity YAML so UnityEvent
    /// callbacks and SerializeReference fields keep resolving after a merge has
    /// renamed a script's namespace (wrap pass) or its owning asmdef.
    ///
    /// Patterns handled:
    ///   • <c>m_TargetAssemblyTypeName: &lt;Namespace&gt;.&lt;Class&gt;, &lt;Asm&gt;</c>
    ///   • <c>m_ObjectArgumentAssemblyTypeName: &lt;Namespace&gt;.&lt;Class&gt;, &lt;Asm&gt;</c>
    ///   • SerializeReference: <c>type: {class: X, ns: Y, asm: Z}</c>
    ///
    /// The sibling fields <c>m_MethodName</c> and <c>m_IntArgument</c> don't need any
    /// rewriting — method names survive a namespace wrap and enum values are stored
    /// as raw ints — but they only RESOLVE when the assembly-qualified type name
    /// above still points at a real type. Wrapping a script without rewriting these
    /// fields is what makes scene UnityEvents come back as Missing, with method
    /// dropdowns blank and enum int args binding to nothing.
    /// </summary>
    public static class YamlTypeRefactor
    {
        // Whole-line match so we can preserve leading indentation and emit a
        // properly-formed `key: value` regardless of the original spacing.
        static readonly Regex AssemblyTypeNameRegex = new Regex(
            @"^(?<lead>\s*)(?<key>m_TargetAssemblyTypeName|m_ObjectArgumentAssemblyTypeName):[ \t]*(?<value>[^\r\n]*)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        // SerializeReference type descriptor. Class can legitimately contain a `+`
        // (nested types serialize as Outer+Inner), so we accept anything that's
        // not `,` or `}` for each field.
        static readonly Regex SerializeReferenceTypeRegex = new Regex(
            @"type:\s*\{\s*class:\s*(?<class>[^,}]*?)\s*,\s*ns:\s*(?<ns>[^,}]*?)\s*,\s*asm:\s*(?<asm>[^,}]*?)\s*\}",
            RegexOptions.Compiled);

        /// <summary>
        /// Rewrites every <c>m_*AssemblyTypeName</c> and SerializeReference type entry
        /// in <paramref name="absolutePath"/>'s YAML. Returns the number of fields
        /// changed; a non-zero return implies the file was rewritten on disk.
        ///
        /// A field changes when either its type name or its assembly name needs
        /// rewriting:
        ///   • TYPE: prefix with <paramref name="wrapPrefix"/> when the first segment
        ///     is in <paramref name="wrappedNamespaceRoots"/>, or when the value is a
        ///     no-namespace top-level type listed in <paramref name="wrappedTopLevelTypes"/>.
        ///   • ASM:  replaced via <paramref name="asmdefNameRemap"/> when its current
        ///     value is a key in that map (i.e. the asmdef was renamed for collision
        ///     resolution). Assembly-CSharp and asmdefs that weren't renamed pass through.
        /// </summary>
        public static int RewriteFile(
            string absolutePath,
            string wrapPrefix,
            HashSet<string> wrappedNamespaceRoots,
            HashSet<string> wrappedTopLevelTypes,
            IReadOnlyDictionary<string, string> asmdefNameRemap)
        {
            int wrappedCount = (wrappedNamespaceRoots?.Count ?? 0) + (wrappedTopLevelTypes?.Count ?? 0);
            int asmdefMaps   = asmdefNameRemap?.Count ?? 0;
            // Nothing to do if the wrap pass produced no roots/types and there are no
            // asmdef renames either.
            if (wrappedCount == 0 && asmdefMaps == 0) return 0;

            if (!File.Exists(absolutePath)) return 0;
            string text;
            try { text = File.ReadAllText(absolutePath, Encoding.UTF8); }
            catch { return 0; }

            int changes = 0;

            var rewritten = AssemblyTypeNameRegex.Replace(text, m =>
            {
                var lead  = m.Groups["lead"].Value;
                var key   = m.Groups["key"].Value;
                var value = m.Groups["value"].Value.TrimEnd();
                if (string.IsNullOrEmpty(value)) return m.Value;

                int comma = value.IndexOf(',');
                string typeName, asmName;
                if (comma < 0)
                {
                    typeName = value.Trim();
                    asmName  = null;
                }
                else
                {
                    typeName = value.Substring(0, comma).Trim();
                    asmName  = value.Substring(comma + 1).Trim();
                }

                string newType = RewrapTypeName(typeName, wrapPrefix, wrappedNamespaceRoots, wrappedTopLevelTypes);
                string newAsm  = RewrapAssemblyName(asmName, asmdefNameRemap);

                bool changed = newType != typeName || (asmName != null && newAsm != asmName);
                if (!changed) return m.Value;
                changes++;
                return lead + key + ": " + newType + (newAsm != null ? ", " + newAsm : "");
            });

            rewritten = SerializeReferenceTypeRegex.Replace(rewritten, m =>
            {
                var className = m.Groups["class"].Value.Trim();
                var nsValue   = m.Groups["ns"].Value.Trim();
                var asmValue  = m.Groups["asm"].Value.Trim();
                if (string.IsNullOrEmpty(className) && string.IsNullOrEmpty(asmValue))
                    return m.Value;

                string fullType = string.IsNullOrEmpty(nsValue) ? className : nsValue + "." + className;
                string newFull  = RewrapTypeName(fullType, wrapPrefix, wrappedNamespaceRoots, wrappedTopLevelTypes);
                string newClass = className;
                string newNs    = nsValue;
                if (newFull != fullType)
                {
                    int lastDot = newFull.LastIndexOf('.');
                    newClass = lastDot < 0 ? newFull : newFull.Substring(lastDot + 1);
                    newNs    = lastDot < 0 ? string.Empty : newFull.Substring(0, lastDot);
                }

                string newAsm = RewrapAssemblyName(asmValue, asmdefNameRemap);

                bool changed = newClass != className || newNs != nsValue || newAsm != asmValue;
                if (!changed) return m.Value;
                changes++;
                return "type: {class: " + newClass + ", ns: " + newNs + ", asm: " + newAsm + "}";
            });

            if (changes > 0)
            {
                try { File.WriteAllText(absolutePath, rewritten, new UTF8Encoding(false)); }
                catch { return 0; }
            }
            return changes;
        }

        static string RewrapTypeName(
            string typeName,
            string wrapPrefix,
            HashSet<string> wrappedNamespaceRoots,
            HashSet<string> wrappedTopLevelTypes)
        {
            if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(wrapPrefix)) return typeName;

            // Idempotent on re-runs: don't double-prefix a value we already wrapped.
            if (typeName == wrapPrefix || typeName.StartsWith(wrapPrefix + ".")) return typeName;

            // Nested types serialize as `Outer+Inner`; the namespace lookup keys off
            // the OUTER type's qualifier, so split the `+` chain and only inspect the
            // head when looking up the namespace root.
            int firstDot = typeName.IndexOf('.');
            if (firstDot < 0)
            {
                // No-namespace top-level type. Only prefix when the wrap pass
                // actually saw this name in a namespace-less file it wrapped.
                string head = typeName;
                int plus = head.IndexOf('+');
                if (plus >= 0) head = head.Substring(0, plus);
                if (wrappedTopLevelTypes != null && wrappedTopLevelTypes.Contains(head))
                    return wrapPrefix + "." + typeName;
                return typeName;
            }

            string root = typeName.Substring(0, firstDot);
            if (wrappedNamespaceRoots != null && wrappedNamespaceRoots.Contains(root))
                return wrapPrefix + "." + typeName;
            return typeName;
        }

        static string RewrapAssemblyName(string asmName, IReadOnlyDictionary<string, string> asmdefNameRemap)
        {
            if (string.IsNullOrEmpty(asmName)) return asmName;
            if (asmdefNameRemap != null && asmdefNameRemap.TryGetValue(asmName, out var renamed))
                return renamed;
            return asmName;
        }
    }
}
